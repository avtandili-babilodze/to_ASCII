using System.Diagnostics;
using System.Text;

namespace AsciiVision.Core.Media;

/// <summary>
/// Wraps an ffmpeg/ffprobe child process. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
/// so paths with spaces or Unicode characters are quoted correctly. stderr is drained continuously
/// into a bounded tail buffer: an undrained diagnostic pipe would eventually block ffmpeg (deadlock).
/// </summary>
public sealed class FfmpegProcess : IDisposable
{
    private const int MaxTailLines = 40;

    private readonly Process _process;
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stderrPump;
    private int _disposed;

    private FfmpegProcess(Process process)
    {
        _process = process;
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public Stream? StandardInput => _process.StartInfo.RedirectStandardInput ? _process.StandardInput.BaseStream : null;

    public Stream? StandardOutput => _process.StartInfo.RedirectStandardOutput ? _process.StandardOutput.BaseStream : null;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int ExitCode => _process.ExitCode;

    public static FfmpegProcess Start(string executable, IEnumerable<string> arguments, bool redirectInput, bool redirectOutput)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FfmpegException($"Could not start '{Path.GetFileName(executable)}': {ex.Message}", ex);
        }

        if (process is null)
        {
            throw new FfmpegException($"Could not start '{Path.GetFileName(executable)}'.");
        }

        ChildProcessJob.Track(process);
        return new FfmpegProcess(process);
    }

    /// <summary>The last diagnostic lines ffmpeg printed (for error messages).</summary>
    public string ErrorTail
    {
        get
        {
            lock (_stderrTail)
            {
                return string.Join(Environment.NewLine, _stderrTail);
            }
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        return _process.ExitCode;
    }

    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var reader = _process.StandardError;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > MaxTailLines)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();
        try
        {
            _process.WaitForExit(2000);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}

public sealed class FfmpegException : Exception
{
    public FfmpegException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    /// <summary>Builds a message from ffmpeg's diagnostics without dumping the whole log on the user.</summary>
    public static FfmpegException FromProcess(string what, FfmpegProcess process, int? exitCode = null)
    {
        var tail = process.ErrorTail;
        var lastLines = string.Join(" ", tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3).Select(l => l.Trim()));
        var code = exitCode is { } c ? $" (exit code {c})" : string.Empty;
        return new FfmpegException(string.IsNullOrWhiteSpace(lastLines) ? $"{what} failed{code}." : $"{what} failed{code}: {lastLines}");
    }
}
