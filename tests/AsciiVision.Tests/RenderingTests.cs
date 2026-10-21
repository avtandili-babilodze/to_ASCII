using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Rendering;
using AsciiVision.Rendering.Console;

namespace AsciiVision.Tests;

public class GlyphRasterTests
{
    [Fact]
    public void MetricsDescribeATallMonospaceCell()
    {
        var metrics = SharedGlyphs.Cache.GetMetrics("Consolas");
        Assert.InRange(metrics.Aspect, 1.6, 2.6);
        var (w, h) = metrics.CellSize(16);
        Assert.InRange(w, 7, 11);
        Assert.InRange(h, 15, 22);
    }

    [Fact]
    public void UnknownFontFallsBackToAMonospaceFont()
    {
        var metrics = SharedGlyphs.Cache.GetMetrics("Definitely Not A Real Font 123");
        Assert.InRange(metrics.Aspect, 1.5, 2.8);
    }

    [Fact]
    public void AtlasContainsAsciiAndRampCharacters()
    {
        var atlas = SharedGlyphs.Cache.GetAtlas("Consolas", 14, "█▓▒░ ");
        Assert.True(atlas.Contains('@'));
        Assert.True(atlas.Contains('|'));
        Assert.True(atlas.Contains('█'));
        Assert.True(atlas.IsBlank(atlas.GetGlyphIndex(' ')));
        Assert.True(atlas.IsSolid(atlas.GetGlyphIndex('█')));
        Assert.False(atlas.IsBlank(atlas.GetGlyphIndex('@')));
        // Characters not in the atlas fall back to '?' instead of failing.
        Assert.Equal(atlas.GetGlyphIndex('?'), atlas.GetGlyphIndex('Ж'));
    }

    [Fact]
    public void RasterizerDrawsRealGlyphsWithCellColors()
    {
        var atlas = SharedGlyphs.Cache.GetAtlas("Consolas", 16, string.Empty);
        var frame = AsciiFrame.CreateUnpooled(2, 1);
        frame.WritableChars[0] = '@';
        frame.WritableChars[1] = ' ';
        frame.WritableForeground.Fill(0xFFFF0000);
        frame.DefaultBackground = 0xFF0000FF;
        var (w, h) = AsciiRasterizer.Measure(frame.Grid, atlas);
        var pixels = new byte[w * h * 4];
        AsciiRasterizer.Render(frame, atlas, pixels, w, h, w * 4);
        var argb = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels);

        int cellW = atlas.CellWidth;
        int redish = 0, partial = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < cellW; x++)
            {
                uint p = argb[y * w + x];
                if (p == 0xFFFF0000) redish++;
                else if (p != 0xFF0000FF) partial++;
            }

            for (int x = cellW; x < w; x++)
            {
                Assert.Equal(0xFF0000FFu, argb[y * w + x]); // the space cell is pure background
            }
        }

        Assert.True(redish > 5, "Glyph pixels use the foreground color.");
        Assert.True(partial > 0, "Edges are antialiased.");
    }

    [Fact]
    public void RasterizerClipsToDestination()
    {
        var atlas = SharedGlyphs.Cache.GetAtlas("Consolas", 12, string.Empty);
        var frame = AsciiFrame.CreateUnpooled(50, 20);
        frame.WritableChars.Fill('#');
        frame.WritableForeground.Fill(0xFFFFFFFF);
        var pixels = new byte[40 * 30 * 4];
        AsciiRasterizer.Render(frame, atlas, pixels, 40, 30, 160, offsetX: -5, offsetY: -3, clearFirst: true);
    }

    [Fact]
    public void FitFontSizeFitsTheArea()
    {
        var metrics = SharedGlyphs.Cache.GetMetrics("Consolas");
        double size = metrics.FitFontSize(160, 45, 1600, 900, 3, 64);
        var (w, h) = metrics.CellSize(size);
        Assert.True(w * 160 <= 1600 && h * 45 <= 900);
        var (w2, h2) = metrics.CellSize(size + 1);
        Assert.True(w2 * 160 > 1600 || h2 * 45 > 900, "Should be the largest size that fits.");
    }
}

public class ConsoleEncodingTests
{
    private static ConsoleFrame Frame(string text, int cols, uint fg = 0xFFFFFFFF)
    {
        var ascii = AsciiFrame.CreateUnpooled(cols, text.Length / cols);
        text.AsSpan().CopyTo(ascii.WritableChars);
        ascii.WritableForeground.Fill(fg);
        ascii.IsMonochrome = true;
        return ConsoleFrame.FromAscii(ascii);
    }

    [Fact]
    public void FirstFrameIsAFullRedraw()
    {
        var encoder = new VtFrameEncoder();
        var output = new CharBuffer();
        encoder.Encode(Frame("abcdef", 3), 80, 25, ConsoleColorDepth.TrueColor, output);
        var text = output.ToString();
        Assert.Contains("\u001b[2J", text);
        Assert.Contains("abc", text);
        Assert.Contains("def", text);
        Assert.Contains("38;2;255;255;255m", text);
    }

    [Fact]
    public void IdenticalFrameProducesNoOutput()
    {
        var encoder = new VtFrameEncoder();
        var output = new CharBuffer();
        encoder.Encode(Frame("abcdef", 3), 80, 25, ConsoleColorDepth.TrueColor, output);
        output.Clear();
        encoder.Encode(Frame("abcdef", 3), 80, 25, ConsoleColorDepth.TrueColor, output);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void OnlyChangedSpanIsRewritten()
    {
        var encoder = new VtFrameEncoder();
        var output = new CharBuffer();
        encoder.Encode(Frame("abcdefghij", 10), 80, 25, ConsoleColorDepth.TrueColor, output);
        output.Clear();
        encoder.Encode(Frame("abcXeYghij", 10), 80, 25, ConsoleColorDepth.TrueColor, output);
        var text = output.ToString();
        Assert.DoesNotContain("2J", text);
        Assert.Contains("XeY", text);
        Assert.DoesNotContain("abc", text);
        Assert.DoesNotContain("ghij", text);
        // Frame is centered: 10 columns in 80 → column 35 + 3 → CUP column 39.
        Assert.Contains(";39H", text);
    }

    [Fact]
    public void ColorCodesAreOnlyEmittedOnChange()
    {
        var encoder = new VtFrameEncoder();
        var output = new CharBuffer();
        encoder.Encode(Frame("aaaaaaaaaa", 10, 0xFF00FF00), 80, 25, ConsoleColorDepth.TrueColor, output);
        var text = output.ToString();
        Assert.Equal(1, CountOccurrences(text, "38;2;0;255;0m"));
    }

    [Fact]
    public void PaletteModesUseIndexedColors()
    {
        var output = new CharBuffer();
        new VtFrameEncoder().Encode(Frame("ab", 2, 0xFFFF0000), 80, 25, ConsoleColorDepth.Palette256, output);
        Assert.Contains("38;5;196m", output.ToString());
        output.Clear();
        new VtFrameEncoder().Encode(Frame("ab", 2, 0xFFFF0000), 80, 25, ConsoleColorDepth.Basic16, output);
        var text = output.ToString();
        Assert.True(text.Contains("[91m") || text.Contains("[31m"), text);
    }

    [Fact]
    public void FramesLargerThanTheTerminalAreCroppedNotWrapped()
    {
        var encoder = new VtFrameEncoder();
        var output = new CharBuffer();
        var text = new string('x', 200 * 60);
        encoder.Encode(Frame(text, 200), 80, 25, ConsoleColorDepth.TrueColor, output);
        var s = output.ToString();
        Assert.DoesNotContain(new string('x', 81), s);
        Assert.DoesNotContain("[25;", s); // the bottom row stays untouched
    }

    [Fact]
    public void ControlCharactersAreSanitized()
    {
        var output = new CharBuffer();
        new VtFrameEncoder().Encode(Frame("a\u001bb", 3), 80, 25, ConsoleColorDepth.TrueColor, output);
        Assert.Contains("a b", output.ToString());
    }

    [Fact]
    public void ProtocolRoundTripsFrames()
    {
        var ascii = AsciiFrame.CreateUnpooled(4, 2);
        "ab@#|/\\-".AsSpan().CopyTo(ascii.WritableChars);
        for (int i = 0; i < 8; i++)
        {
            ascii.WritableForeground[i] = 0xFF000000u | (uint)(i * 1000);
            ascii.WritableBackground[i] = 0xFF101010u + (uint)i;
        }

        ascii.HasCellBackground = true;
        ascii.DefaultBackground = 0xFF223344;
        byte[] buffer = [];
        int length = ConsoleProtocol.WriteFrame(ref buffer, ascii, ConsoleColorDepth.Palette256);
        var decoded = new ConsoleFrame();
        ConsoleProtocol.ReadFrame(buffer.AsSpan(ConsoleProtocol.HeaderSize, length - ConsoleProtocol.HeaderSize), decoded);
        Assert.Equal(4, decoded.Columns);
        Assert.Equal(2, decoded.Rows);
        Assert.Equal("ab@#|/\\-", new string(decoded.Chars, 0, 8));
        Assert.Equal(ascii.Foreground.ToArray(), decoded.Foreground.Take(8).ToArray());
        Assert.Equal(ascii.Background.ToArray(), decoded.Background.Take(8).ToArray());
        Assert.True(decoded.HasCellBackground);
        Assert.Equal(0xFF223344u, decoded.DefaultBackground);
        Assert.Equal(ConsoleColorDepth.Palette256, decoded.Depth);
    }

    [Fact]
    public void TruncatedProtocolMessagesAreRejected()
    {
        var decoded = new ConsoleFrame();
        Assert.Throws<InvalidDataException>(() => ConsoleProtocol.ReadFrame(new byte[10], decoded));
    }

    [Theory]
    [InlineData(0xFFFF0000u, 196)]
    [InlineData(0xFF000000u, 16)]
    [InlineData(0xFFFFFFFFu, 231)]
    [InlineData(0xFF808080u, 244)]
    public void Xterm256Quantization(uint color, int expected) => Assert.Equal(expected, ConsolePalette.NearestXterm256(color));

    [Theory]
    [InlineData(0xFF000000u, 0)]
    [InlineData(0xFFF2F2F2u, 15)]
    [InlineData(0xFF13A10Eu, 2)]
    public void SixteenColorQuantization(uint color, int expectedAttribute) =>
        Assert.Equal(expectedAttribute, ConsolePalette.NearestAttribute(color));

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
