using AsciiVision.Core.Configuration;

namespace AsciiVision.Rendering.Console;

/// <summary>
/// Encodes frames as VT escape sequences, emitting only what changed since the previous frame.
///
/// Per row, the first and last differing cells are found and just that span is rewritten after an
/// absolute cursor move (CUP), so a static scene costs almost nothing and nothing is ever cleared
/// between frames (no flicker). Color codes are emitted only when the pen color changes. The
/// frame is centered and cropped to the terminal, and the bottom terminal row is never written,
/// which avoids accidental scrolling when a line is written to the last column.
/// </summary>
public sealed class VtFrameEncoder
{
    public const string Esc = "\u001b[";

    private char[] _prevChars = [];
    private uint[] _prevFg = [];
    private uint[] _prevBg = [];
    private int _prevCols;
    private int _prevRows;
    private int _prevOffsetX = -1;
    private int _prevOffsetY = -1;
    private uint _prevDefaultBg;
    private ConsoleColorDepth _prevDepth;
    private bool _valid;
    private long _penFg;
    private long _penBg;

    /// <summary>Forces a full redraw on the next frame (after a resize, status screen, etc.).</summary>
    public void Invalidate() => _valid = false;

    /// <summary>Appends the sequences to update the terminal from the previous frame to <paramref name="frame"/>.</summary>
    public void Encode(ConsoleFrame frame, int terminalColumns, int terminalRows, ConsoleColorDepth depth, CharBuffer output)
    {
        if (depth == ConsoleColorDepth.Auto)
        {
            depth = ConsoleColorDepth.TrueColor;
        }

        int usableRows = Math.Max(1, terminalRows - 1);
        int visibleCols = Math.Min(frame.Columns, Math.Max(1, terminalColumns));
        int visibleRows = Math.Min(frame.Rows, usableRows);
        int offsetX = Math.Max(0, (terminalColumns - visibleCols) / 2);
        int offsetY = Math.Max(0, (usableRows - visibleRows) / 2);

        // When the frame is larger than the terminal, show its center.
        int sourceX = (frame.Columns - visibleCols) / 2;
        int sourceY = (frame.Rows - visibleRows) / 2;

        bool full = !_valid || visibleCols != _prevCols || visibleRows != _prevRows || offsetX != _prevOffsetX
                    || offsetY != _prevOffsetY || frame.DefaultBackground != _prevDefaultBg || depth != _prevDepth;

        if (full)
        {
            _penFg = -1;
            _penBg = -1;
            output.Append(Esc).Append("0m");
            AppendBackground(output, frame.DefaultBackground, depth);
            output.Append(Esc).Append("2J");
            EnsureCapacity(visibleCols * visibleRows);
        }

        for (int r = 0; r < visibleRows; r++)
        {
            int frameRow = (sourceY + r) * frame.Columns + sourceX;
            int prevRow = r * visibleCols;
            int first = 0;
            int last = visibleCols - 1;
            if (!full)
            {
                first = -1;
                for (int c = 0; c < visibleCols; c++)
                {
                    if (CellChanged(frame, frameRow + c, prevRow + c))
                    {
                        first = c;
                        break;
                    }
                }

                if (first < 0)
                {
                    continue;
                }

                last = first;
                for (int c = visibleCols - 1; c > first; c--)
                {
                    if (CellChanged(frame, frameRow + c, prevRow + c))
                    {
                        last = c;
                        break;
                    }
                }
            }

            output.Append(Esc).Append(offsetY + r + 1).Append(';').Append(offsetX + first + 1).Append('H');
            for (int c = first; c <= last; c++)
            {
                int i = frameRow + c;
                uint fg = frame.Foreground[i] | 0xFF000000;
                uint bg = frame.BackgroundAt(i);
                long fgKey = Quantize(fg, depth);
                long bgKey = Quantize(bg, depth);
                if (bgKey != _penBg)
                {
                    AppendBackground(output, bg, depth);
                    _penBg = bgKey;
                }

                if (fgKey != _penFg)
                {
                    AppendForeground(output, fg, depth);
                    _penFg = fgKey;
                }

                output.Append(Sanitize(frame.Chars[i]));

                _prevChars[prevRow + c] = frame.Chars[i];
                _prevFg[prevRow + c] = fg;
                _prevBg[prevRow + c] = bg;
            }
        }

        _prevCols = visibleCols;
        _prevRows = visibleRows;
        _prevOffsetX = offsetX;
        _prevOffsetY = offsetY;
        _prevDefaultBg = frame.DefaultBackground;
        _prevDepth = depth;
        _valid = true;
    }

    private bool CellChanged(ConsoleFrame frame, int frameIndex, int prevIndex) =>
        frame.Chars[frameIndex] != _prevChars[prevIndex]
        || (frame.Foreground[frameIndex] | 0xFF000000) != _prevFg[prevIndex]
        || frame.BackgroundAt(frameIndex) != _prevBg[prevIndex];

    private void EnsureCapacity(int cells)
    {
        if (_prevChars.Length < cells)
        {
            _prevChars = new char[cells];
            _prevFg = new uint[cells];
            _prevBg = new uint[cells];
        }
    }

    private static char Sanitize(char c) =>
        c < 0x20 || (c >= 0x7F && c < 0xA0) || char.IsSurrogate(c) ? ' ' : c;

    private static long Quantize(uint color, ConsoleColorDepth depth) => depth switch
    {
        ConsoleColorDepth.Palette256 => ConsolePalette.NearestXterm256(color),
        ConsoleColorDepth.Basic16 => ConsolePalette.NearestAnsi16(color),
        _ => color & 0xFFFFFF,
    };

    private static void AppendForeground(CharBuffer o, uint color, ConsoleColorDepth depth) => AppendColor(o, color, depth, foreground: true);

    private static void AppendBackground(CharBuffer o, uint color, ConsoleColorDepth depth) => AppendColor(o, color, depth, foreground: false);

    private static void AppendColor(CharBuffer o, uint color, ConsoleColorDepth depth, bool foreground)
    {
        switch (depth)
        {
            case ConsoleColorDepth.Palette256:
                o.Append(Esc).Append(foreground ? "38;5;" : "48;5;").Append(ConsolePalette.NearestXterm256(color)).Append('m');
                break;
            case ConsoleColorDepth.Basic16:
                int ansi = ConsolePalette.NearestAnsi16(color);
                int code = ansi < 8 ? (foreground ? 30 : 40) + ansi : (foreground ? 90 : 100) + ansi - 8;
                o.Append(Esc).Append(code).Append('m');
                break;
            default:
                o.Append(Esc).Append(foreground ? "38;2;" : "48;2;")
                    .Append((int)(color >> 16) & 0xFF).Append(';')
                    .Append((int)(color >> 8) & 0xFF).Append(';')
                    .Append((int)color & 0xFF).Append('m');
                break;
        }
    }
}

/// <summary>A minimal growable char buffer with allocation-free integer formatting.</summary>
public sealed class CharBuffer
{
    private char[] _buffer;

    public CharBuffer(int capacity = 64 * 1024)
    {
        _buffer = new char[capacity];
    }

    public int Length { get; private set; }

    public ReadOnlySpan<char> Span => _buffer.AsSpan(0, Length);

    public char[] Array => _buffer;

    public void Clear() => Length = 0;

    public CharBuffer Append(char c)
    {
        if (Length == _buffer.Length)
        {
            Grow(1);
        }

        _buffer[Length++] = c;
        return this;
    }

    public CharBuffer Append(string s)
    {
        if (Length + s.Length > _buffer.Length)
        {
            Grow(s.Length);
        }

        s.CopyTo(0, _buffer, Length, s.Length);
        Length += s.Length;
        return this;
    }

    public CharBuffer Append(int value)
    {
        if (Length + 11 > _buffer.Length)
        {
            Grow(11);
        }

        value.TryFormat(_buffer.AsSpan(Length), out int written, provider: System.Globalization.CultureInfo.InvariantCulture);
        Length += written;
        return this;
    }

    public override string ToString() => new(_buffer, 0, Length);

    private void Grow(int extra) => System.Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + extra));
}
