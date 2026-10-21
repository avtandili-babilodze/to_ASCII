using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using AsciiVision.Core.Configuration;

namespace AsciiVision.Rendering.Console;

/// <summary>
/// Entry point of the console output process (<c>AsciiVision.exe --console-host &lt;pipe&gt;</c>).
///
/// Running the terminal output in a child process with its own console is deliberate: when the user
/// closes a console window, Windows terminates the process that owns it. Isolating the console means
/// closing it can never take down the main application; the app just sees the pipe disconnect.
/// The host renders through VT sequences in the alternate screen buffer (no scrollback), or falls back
/// to WriteConsoleOutput with 16-color attributes on consoles without VT support.
/// </summary>
public static class ConsoleHost
{
    public const string Argument = "--console-host";

    private static ConsoleNative.HandlerRoutine? _ctrlHandler;

    public static int Run(string[] args)
    {
        int index = Array.IndexOf(args, Argument);
        if (index < 0 || index + 1 >= args.Length)
        {
            return 2;
        }

        string pipeName = args[index + 1];
        if (ConsoleNative.GetConsoleWindow() == IntPtr.Zero && !ConsoleNative.AllocConsole())
        {
            return 3;
        }

        ConsoleNative.SetConsoleTitle("ASCII Vision — Console Output");
        IntPtr output = ConsoleNative.CreateFile("CONOUT$", ConsoleNative.GENERIC_READ | ConsoleNative.GENERIC_WRITE,
            ConsoleNative.FILE_SHARE_READ | ConsoleNative.FILE_SHARE_WRITE, IntPtr.Zero, ConsoleNative.OPEN_EXISTING, 0, IntPtr.Zero);
        IntPtr input = ConsoleNative.CreateFile("CONIN$", ConsoleNative.GENERIC_READ | ConsoleNative.GENERIC_WRITE,
            ConsoleNative.FILE_SHARE_READ | ConsoleNative.FILE_SHARE_WRITE, IntPtr.Zero, ConsoleNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (output == new IntPtr(-1))
        {
            return 4;
        }

        // Ctrl+C in this window should not kill the output; the user closes the window instead.
        _ctrlHandler = type => type is ConsoleNative.CTRL_C_EVENT or ConsoleNative.CTRL_BREAK_EVENT;
        ConsoleNative.SetConsoleCtrlHandler(_ctrlHandler, true);

        // Quick Edit lets a stray click "select" text, which freezes all output until Esc is pressed.
        uint originalInputMode = 0;
        bool haveInputMode = input != new IntPtr(-1) && ConsoleNative.GetConsoleMode(input, out originalInputMode);
        if (haveInputMode)
        {
            ConsoleNative.SetConsoleMode(input, (originalInputMode & ~ConsoleNative.ENABLE_QUICK_EDIT_MODE) | ConsoleNative.ENABLE_EXTENDED_FLAGS);
        }

        ConsoleNative.GetConsoleMode(output, out uint originalOutputMode);
        bool vt = ConsoleNative.SetConsoleMode(output, originalOutputMode | ConsoleNative.ENABLE_PROCESSED_OUTPUT
                                                       | ConsoleNative.ENABLE_VIRTUAL_TERMINAL_PROCESSING | ConsoleNative.DISABLE_NEWLINE_AUTO_RETURN);
        if (!vt)
        {
            // Older consoles reject DISABLE_NEWLINE_AUTO_RETURN; try VT alone.
            vt = ConsoleNative.SetConsoleMode(output, originalOutputMode | ConsoleNative.ENABLE_PROCESSED_OUTPUT | ConsoleNative.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }

        uint originalCodePage = ConsoleNative.GetConsoleOutputCP();
        ConsoleNative.SetConsoleOutputCP(65001);

        var session = new HostSession(output, vt);
        try
        {
            session.Enter();
            session.Run(pipeName);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or ObjectDisposedException)
        {
            // Pipe closed or parent gone: just restore and exit.
        }
        finally
        {
            session.Leave();
            ConsoleNative.SetConsoleMode(output, originalOutputMode);
            if (haveInputMode)
            {
                ConsoleNative.SetConsoleMode(input, originalInputMode);
            }

            ConsoleNative.SetConsoleOutputCP(originalCodePage);
            ConsoleNative.CloseHandle(output);
            if (input != new IntPtr(-1))
            {
                ConsoleNative.CloseHandle(input);
            }

            ConsoleNative.FreeConsole();
        }

        return 0;
    }

    private sealed class HostSession
    {
        private readonly IntPtr _output;
        private readonly bool _vt;
        private readonly VtFrameEncoder _encoder = new();
        private readonly CharBuffer _text = new();
        private readonly ConsoleFrame _frame = new();
        private readonly object _writeGate = new();
        private ConsoleNative.CHAR_INFO[] _legacyBuffer = [];
        private int _columns;
        private int _rows;
        private bool _hasFrame;
        private string? _status = "Waiting for frames…";

        public HostSession(IntPtr output, bool vt)
        {
            _output = output;
            _vt = vt;
        }

        public void Enter()
        {
            UpdateSize();
            if (_vt)
            {
                // Alternate screen buffer: no scrollback accumulates, and the original screen comes back on exit.
                Write("\u001b[?1049h\u001b[?25l\u001b[0m\u001b[2J");
            }
            else
            {
                var cursor = new ConsoleNative.CONSOLE_CURSOR_INFO { dwSize = 25, bVisible = false };
                ConsoleNative.SetConsoleCursorInfo(_output, ref cursor);
                MatchBufferToWindow();
            }

            DrawStatus();
        }

        public void Leave()
        {
            if (_vt)
            {
                Write("\u001b[0m\u001b[?25h\u001b[?1049l");
            }
            else
            {
                var cursor = new ConsoleNative.CONSOLE_CURSOR_INFO { dwSize = 25, bVisible = true };
                ConsoleNative.SetConsoleCursorInfo(_output, ref cursor);
            }
        }

        public void Run(string pipeName)
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);

            using var cts = new CancellationTokenSource();
            var sizeWatcher = Task.Run(() => WatchSizeAsync(pipe, cts.Token));
            try
            {
                SendTerminalInfo(pipe);
                var header = new byte[ConsoleProtocol.HeaderSize];
                byte[] payload = new byte[64 * 1024];
                while (true)
                {
                    if (!ReadExactly(pipe, header))
                    {
                        return;
                    }

                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    byte type = header[4];
                    if (length < 0 || length > ConsoleProtocol.MaxPayload)
                    {
                        throw new InvalidDataException("Invalid message length.");
                    }

                    if (payload.Length < length)
                    {
                        payload = new byte[Math.Max(length, payload.Length * 2)];
                    }

                    if (!ReadExactly(pipe, payload.AsSpan(0, length)))
                    {
                        return;
                    }

                    switch (type)
                    {
                        case ConsoleProtocol.Frame:
                            ConsoleProtocol.ReadFrame(payload.AsSpan(0, length), _frame);
                            lock (_writeGate)
                            {
                                if (_status is not null)
                                {
                                    _status = null;
                                    _encoder.Invalidate();
                                }

                                _hasFrame = true;
                                DrawFrame();
                            }

                            break;
                        case ConsoleProtocol.Status:
                            lock (_writeGate)
                            {
                                _status = System.Text.Encoding.UTF8.GetString(payload, 0, length);
                                _hasFrame = false;
                                DrawStatus();
                            }

                            break;
                        case ConsoleProtocol.Shutdown:
                            return;
                    }
                }
            }
            finally
            {
                cts.Cancel();
                try
                {
                    sizeWatcher.Wait(1000);
                }
                catch (AggregateException)
                {
                }
            }
        }

        private void SendTerminalInfo(NamedPipeClientStream pipe)
        {
            var message = ConsoleProtocol.WriteTerminalInfo(_columns, _rows, _vt);
            pipe.Write(message, 0, message.Length);
            pipe.Flush();
        }

        private async Task WatchSizeAsync(NamedPipeClientStream pipe, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    bool changed;
                    lock (_writeGate)
                    {
                        changed = UpdateSize();
                        if (changed)
                        {
                            if (!_vt)
                            {
                                MatchBufferToWindow();
                            }

                            _encoder.Invalidate();
                            if (_hasFrame)
                            {
                                DrawFrame();
                            }
                            else
                            {
                                DrawStatus();
                            }
                        }
                    }

                    if (changed)
                    {
                        var message = ConsoleProtocol.WriteTerminalInfo(_columns, _rows, _vt);
                        await pipe.WriteAsync(message, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private bool UpdateSize()
        {
            if (!ConsoleNative.GetConsoleScreenBufferInfo(_output, out var info))
            {
                return false;
            }

            int columns = info.srWindow.Right - info.srWindow.Left + 1;
            int rows = info.srWindow.Bottom - info.srWindow.Top + 1;
            if (columns == _columns && rows == _rows)
            {
                return false;
            }

            _columns = Math.Max(1, columns);
            _rows = Math.Max(1, rows);
            return true;
        }

        private void MatchBufferToWindow()
        {
            // Buffer the same size as the window: no scrollbar and no scrollback.
            ConsoleNative.SetConsoleScreenBufferSize(_output, new ConsoleNative.COORD((short)_columns, (short)_rows));
        }

        private void DrawFrame()
        {
            if (_vt)
            {
                _text.Clear();
                var depth = _frame.Depth == ConsoleColorDepth.Auto ? ConsoleColorDepth.TrueColor : _frame.Depth;
                _encoder.Encode(_frame, _columns, _rows, depth, _text);
                Write(_text.Span);
            }
            else
            {
                DrawLegacy();
            }
        }

        private void DrawStatus()
        {
            var message = "ASCII Vision — " + (_status ?? string.Empty);
            if (_vt)
            {
                _encoder.Invalidate();
                _text.Clear();
                int row = Math.Max(1, _rows / 2);
                int col = Math.Max(1, (_columns - message.Length) / 2 + 1);
                _text.Append("\u001b[0m\u001b[2J\u001b[").Append(row).Append(';').Append(col).Append("H\u001b[90m");
                _text.Append(message.Length > _columns ? message[.._columns] : message).Append("\u001b[0m");
                Write(_text.Span);
            }
            else
            {
                _frame.Resize(Math.Max(1, _columns), Math.Max(1, _rows - 1));
                _frame.HasCellBackground = false;
                _frame.DefaultBackground = 0xFF000000;
                Array.Fill(_frame.Chars, ' ', 0, _frame.Columns * _frame.Rows);
                Array.Fill(_frame.Foreground, 0xFF808080u, 0, _frame.Columns * _frame.Rows);
                int row = _frame.Rows / 2;
                int start = Math.Max(0, (_frame.Columns - message.Length) / 2);
                for (int i = 0; i < message.Length && start + i < _frame.Columns; i++)
                {
                    _frame.Chars[row * _frame.Columns + start + i] = message[i];
                }

                DrawLegacy();
            }
        }

        private unsafe void DrawLegacy()
        {
            int usableRows = Math.Max(1, _rows - 1);
            int cols = Math.Min(_frame.Columns, _columns);
            int rows = Math.Min(_frame.Rows, usableRows);
            int offsetX = Math.Max(0, (_columns - cols) / 2);
            int offsetY = Math.Max(0, (usableRows - rows) / 2);
            int srcX = (_frame.Columns - cols) / 2;
            int srcY = (_frame.Rows - rows) / 2;
            int total = _columns * usableRows;
            if (_legacyBuffer.Length < total)
            {
                _legacyBuffer = new ConsoleNative.CHAR_INFO[total];
            }

            ushort blankAttr = (ushort)(ConsolePalette.NearestAttribute(_frame.DefaultBackground) << 4);
            for (int i = 0; i < total; i++)
            {
                _legacyBuffer[i].UnicodeChar = ' ';
                _legacyBuffer[i].Attributes = blankAttr;
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int src = (srcY + r) * _frame.Columns + srcX + c;
                    int dst = (offsetY + r) * _columns + offsetX + c;
                    char ch = _frame.Chars[src];
                    _legacyBuffer[dst].UnicodeChar = ch < 0x20 || char.IsSurrogate(ch) ? ' ' : ch;
                    int fg = ConsolePalette.NearestAttribute(_frame.Foreground[src] | 0xFF000000);
                    int bg = ConsolePalette.NearestAttribute(_frame.BackgroundAt(src));
                    _legacyBuffer[dst].Attributes = (ushort)(fg | (bg << 4));
                }
            }

            var region = new ConsoleNative.SMALL_RECT { Left = 0, Top = 0, Right = (short)(_columns - 1), Bottom = (short)(usableRows - 1) };
            fixed (ConsoleNative.CHAR_INFO* buffer = _legacyBuffer)
            {
                ConsoleNative.WriteConsoleOutput(_output, buffer, new ConsoleNative.COORD((short)_columns, (short)usableRows), new ConsoleNative.COORD(0, 0), ref region);
            }
        }

        private unsafe void Write(ReadOnlySpan<char> text)
        {
            fixed (char* p = text)
            {
                int offset = 0;
                while (offset < text.Length)
                {
                    // Large single writes are slow on some console hosts; 16K chunks behave well everywhere.
                    int count = Math.Min(16 * 1024, text.Length - offset);
                    if (!ConsoleNative.WriteConsole(_output, p + offset, (uint)count, out uint written, IntPtr.Zero) || written == 0)
                    {
                        return;
                    }

                    offset += (int)written;
                }
            }
        }

        private void Write(string text) => Write(text.AsSpan());

        private static bool ReadExactly(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer[total..]);
                if (read == 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }
    }
}
