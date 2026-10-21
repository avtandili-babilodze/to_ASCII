using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Rendering.Console;

/// <summary>
/// App-side half of the console output: launches the console host process, streams frames to it
/// over a named pipe and tracks the terminal size it reports. A single-slot mailbox sits in front of
/// the pipe, so a slow terminal receives the newest frame rather than a backlog.
/// </summary>
public sealed class ConsoleRenderer : ITerminalRenderer, IAsyncDisposable
{
    private readonly ISettingsService _settings;
    private readonly PerformanceMetrics _metrics;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Session? _session;

    public ConsoleRenderer(ISettingsService settings, PerformanceMetrics metrics, ILogger<ConsoleRenderer>? logger = null)
    {
        _settings = settings;
        _metrics = metrics;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Executable that implements <c>--console-host</c>; defaults to the running app.</summary>
    public string? HostExecutable { get; set; } = Environment.ProcessPath;

    public string Name => "Console";

    public bool IsOpen => _session is { IsAlive: true };

    public bool IsActive => IsOpen;

    public GridSize? TerminalSize => _session?.TerminalSize;

    public bool? SupportsVirtualTerminal => _session?.VirtualTerminal;

    /// <summary>Raised (on a worker thread) when the console window was closed or the host died.</summary>
    public event EventHandler? Closed;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsOpen)
            {
                return;
            }

            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }

            if (string.IsNullOrEmpty(HostExecutable) || !File.Exists(HostExecutable))
            {
                throw new InvalidOperationException("The console host executable could not be located.");
            }

            var session = new Session(this, HostExecutable);
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            _session = session;
            _logger.LogInformation("Console output opened");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                _logger.LogInformation("Console output closed");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Submit(AsciiFrame frame)
    {
        var session = _session;
        if (session is null || !session.IsAlive)
        {
            frame.Dispose();
            return;
        }

        session.Post(frame);
    }

    public void ShowStatus(string message) => _session?.PostStatus(message);

    private void OnSessionEnded(Session session)
    {
        if (ReferenceEquals(_session, session))
        {
            _logger.LogInformation("Console host ended");
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly ConsoleRenderer _owner;
        private readonly string _executable;
        private readonly LatestItemMailbox<AsciiFrame> _mailbox;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _statusQueue = new();
        private NamedPipeServerStream? _pipe;
        private Process? _process;
        private Task? _writer;
        private Task? _reader;
        private volatile bool _alive;
        private int _ended;
        private GridSize? _terminalSize;

        public Session(ConsoleRenderer owner, string executable)
        {
            _owner = owner;
            _executable = executable;
            _mailbox = new LatestItemMailbox<AsciiFrame>(() => _owner._metrics.AddDropped());
        }

        public bool IsAlive => _alive;

        public GridSize? TerminalSize => _terminalSize;

        public bool? VirtualTerminal { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            string pipeName = $"AsciiVision.Console.{Environment.ProcessId}.{Guid.NewGuid():N}";
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                256 * 1024, 4 * 1024);

            var psi = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add(ConsoleHost.Argument);
            psi.ArgumentList.Add(pipeName);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the console window.");
            Core.Media.ChildProcessJob.Track(_process);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillProcess();
                throw new TimeoutException("The console window did not start within 15 seconds.");
            }

            // Wait briefly for the first size report so the first frame already fits the window.
            var first = await ReadMessageAsync(timeout.Token).ConfigureAwait(false);
            if (first is { Type: ConsoleProtocol.TerminalInfo })
            {
                ApplyTerminalInfo(first.Value.Payload);
            }

            _alive = true;
            _reader = Task.Run(ReadLoopAsync);
            _writer = Task.Factory.StartNew(WriteLoopAsync, TaskCreationOptions.LongRunning).Unwrap();
        }

        public void Post(AsciiFrame frame) => _mailbox.Post(frame);

        public void PostStatus(string message)
        {
            _statusQueue.Enqueue(message);
            _mailbox.Signal();
        }

        private async Task WriteLoopAsync()
        {
            var buffer = new byte[256 * 1024];
            var token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await _mailbox.WaitAsync(token).ConfigureAwait(false);
                    while (_statusQueue.TryDequeue(out var status))
                    {
                        var message = ConsoleProtocol.WriteText(ConsoleProtocol.Status, status);
                        await _pipe!.WriteAsync(message, token).ConfigureAwait(false);
                    }

                    using var frame = _mailbox.TryTake();
                    if (frame is null)
                    {
                        continue;
                    }

                    long start = Stopwatch.GetTimestamp();
                    int length = ConsoleProtocol.WriteFrame(ref buffer, frame, _owner._settings.Current.ConsoleColorDepth);
                    await _pipe!.WriteAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
                    _owner._metrics.ConsoleEncodeMilliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    _owner._metrics.ConsoleRate.Tick();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                End();
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var message = await ReadMessageAsync(_cts.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        break;
                    }

                    if (message.Value.Type == ConsoleProtocol.TerminalInfo)
                    {
                        ApplyTerminalInfo(message.Value.Payload);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException)
            {
            }
            finally
            {
                End();
            }
        }

        private void ApplyTerminalInfo(byte[] payload)
        {
            var (columns, rows, vt) = ConsoleProtocol.ReadTerminalInfo(payload);
            _terminalSize = new GridSize(Math.Max(1, columns), Math.Max(1, rows));
            VirtualTerminal = vt;
        }

        private async Task<(byte Type, byte[] Payload)?> ReadMessageAsync(CancellationToken token)
        {
            var header = new byte[ConsoleProtocol.HeaderSize];
            if (!await ReadExactlyAsync(header, token).ConfigureAwait(false))
            {
                return null;
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 0 or > 1024)
            {
                throw new InvalidDataException("Unexpected message from the console host.");
            }

            var payload = new byte[length];
            if (!await ReadExactlyAsync(payload, token).ConfigureAwait(false))
            {
                return null;
            }

            return (header[4], payload);
        }

        private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken token)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await _pipe!.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }

        private void End()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
            {
                return;
            }

            _alive = false;
            _mailbox.Clear();
            _owner.OnSessionEnded(this);
        }

        private void KillProcess()
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _ended, 1); // a deliberate close should not raise Closed
            _alive = false;
            try
            {
                if (_pipe is { IsConnected: true })
                {
                    var message = ConsoleProtocol.WriteText(ConsoleProtocol.Shutdown, string.Empty);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _pipe.WriteAsync(message, timeout.Token).ConfigureAwait(false);
                    await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }

            _cts.Cancel();
            if (_process is not null)
            {
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillProcess();
                }

                _process.Dispose();
            }

            foreach (var task in new[] { _writer, _reader })
            {
                if (task is not null)
                {
                    try
                    {
                        await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            _pipe?.Dispose();
            _mailbox.Dispose();
            _cts.Dispose();
        }
    }
}
