using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;

namespace AsciiVision.Rendering.Console;

/// <summary>
/// Length-prefixed binary messages between the app and its console host process over a named pipe.
/// The app sends structured cells (not escape sequences) so the host can encode them for whatever
/// its terminal supports (VT true color, 256 colors, 16 colors, or the legacy console API).
/// </summary>
public static class ConsoleProtocol
{
    public const byte Frame = 1;
    public const byte Status = 2;
    public const byte Shutdown = 3;
    public const byte TerminalInfo = 10;

    public const int HeaderSize = 5; // int32 payload length + byte type
    public const int MaxPayload = 64 * 1024 * 1024;

    private const int FrameHeaderSize = 4 + 4 + 4 + 1 + 1;

    /// <summary>Serializes a frame into <paramref name="buffer"/> (grown if needed); returns the message length.</summary>
    public static int WriteFrame(ref byte[] buffer, AsciiFrame frame, ConsoleColorDepth depth)
    {
        int cells = frame.CellCount;
        bool hasBg = frame.HasCellBackground;
        int payload = FrameHeaderSize + cells * 2 + cells * 4 + (hasBg ? cells * 4 : 0);
        int total = HeaderSize + payload;
        if (buffer.Length < total)
        {
            buffer = new byte[total];
        }

        var span = buffer.AsSpan(0, total);
        BinaryPrimitives.WriteInt32LittleEndian(span, payload);
        span[4] = Frame;
        var p = span[HeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, frame.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], frame.Rows);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], frame.DefaultBackground);
        p[12] = (byte)((hasBg ? 1 : 0) | (frame.IsMonochrome ? 2 : 0));
        p[13] = (byte)depth;
        int offset = FrameHeaderSize;
        MemoryMarshal.AsBytes(frame.Chars).CopyTo(p[offset..]);
        offset += cells * 2;
        MemoryMarshal.AsBytes(frame.Foreground).CopyTo(p[offset..]);
        offset += cells * 4;
        if (hasBg)
        {
            MemoryMarshal.AsBytes(frame.Background).CopyTo(p[offset..]);
        }

        return total;
    }

    public static byte[] WriteText(byte type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var message = new byte[HeaderSize + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(message, bytes.Length);
        message[4] = type;
        bytes.CopyTo(message, HeaderSize);
        return message;
    }

    public static byte[] WriteTerminalInfo(int columns, int rows, bool virtualTerminal)
    {
        var message = new byte[HeaderSize + 9];
        BinaryPrimitives.WriteInt32LittleEndian(message, 9);
        message[4] = TerminalInfo;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(5), columns);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(9), rows);
        message[13] = (byte)(virtualTerminal ? 1 : 0);
        return message;
    }

    public static (int Columns, int Rows, bool VirtualTerminal) ReadTerminalInfo(ReadOnlySpan<byte> payload) =>
        (BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload[4..]), payload[8] != 0);

    /// <summary>Decodes a frame payload into a reusable <see cref="ConsoleFrame"/>.</summary>
    public static void ReadFrame(ReadOnlySpan<byte> payload, ConsoleFrame target)
    {
        if (payload.Length < FrameHeaderSize)
        {
            throw new InvalidDataException("Truncated frame message.");
        }

        int cols = BinaryPrimitives.ReadInt32LittleEndian(payload);
        int rows = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        if (cols <= 0 || rows <= 0 || (long)cols * rows > 4_000_000)
        {
            throw new InvalidDataException($"Invalid frame size {cols}x{rows}.");
        }

        uint defaultBg = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        byte flags = payload[12];
        bool hasBg = (flags & 1) != 0;
        int cells = cols * rows;
        int expected = FrameHeaderSize + cells * 6 + (hasBg ? cells * 4 : 0);
        if (payload.Length < expected)
        {
            throw new InvalidDataException("Frame message is shorter than its declared size.");
        }

        target.Resize(cols, rows);
        target.DefaultBackground = defaultBg | 0xFF000000;
        target.HasCellBackground = hasBg;
        target.IsMonochrome = (flags & 2) != 0;
        target.Depth = (ConsoleColorDepth)payload[13];
        int offset = FrameHeaderSize;
        payload.Slice(offset, cells * 2).CopyTo(MemoryMarshal.AsBytes(target.Chars.AsSpan(0, cells)));
        offset += cells * 2;
        payload.Slice(offset, cells * 4).CopyTo(MemoryMarshal.AsBytes(target.Foreground.AsSpan(0, cells)));
        offset += cells * 4;
        if (hasBg)
        {
            payload.Slice(offset, cells * 4).CopyTo(MemoryMarshal.AsBytes(target.Background.AsSpan(0, cells)));
        }
    }
}

/// <summary>A decoded frame on the console host side (buffers reused between frames).</summary>
public sealed class ConsoleFrame
{
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public char[] Chars { get; private set; } = [];
    public uint[] Foreground { get; private set; } = [];
    public uint[] Background { get; private set; } = [];
    public uint DefaultBackground { get; set; } = 0xFF000000;
    public bool HasCellBackground { get; set; }
    public bool IsMonochrome { get; set; }
    public ConsoleColorDepth Depth { get; set; }

    public void Resize(int columns, int rows)
    {
        int cells = columns * rows;
        if (Chars.Length < cells)
        {
            Chars = new char[cells];
            Foreground = new uint[cells];
            Background = new uint[cells];
        }

        Columns = columns;
        Rows = rows;
    }

    public uint BackgroundAt(int index) => HasCellBackground ? Background[index] | 0xFF000000 : DefaultBackground;

    public static ConsoleFrame FromAscii(AsciiFrame frame, ConsoleColorDepth depth = ConsoleColorDepth.TrueColor)
    {
        var result = new ConsoleFrame();
        result.Resize(frame.Columns, frame.Rows);
        frame.Chars.CopyTo(result.Chars);
        frame.Foreground.CopyTo(result.Foreground);
        frame.Background.CopyTo(result.Background);
        result.DefaultBackground = frame.DefaultBackground | 0xFF000000;
        result.HasCellBackground = frame.HasCellBackground;
        result.IsMonochrome = frame.IsMonochrome;
        result.Depth = depth;
        return result;
    }
}
