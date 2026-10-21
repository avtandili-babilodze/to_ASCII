using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsciiVision.Rendering.Glyphs;

/// <summary>
/// Pre-rasterized coverage masks (0..255) for every character a frame can contain, all at one
/// cell size. Immutable after construction, so any thread can read it while rendering.
/// </summary>
public sealed class GlyphAtlas
{
    private readonly byte[] _coverage;
    private readonly int[] _asciiIndex = new int[128];
    private readonly Dictionary<char, int> _extendedIndex = new();
    private readonly bool[] _blank;
    private readonly bool[] _solid;
    private readonly int _fallbackIndex;

    private GlyphAtlas(string familyName, double fontSizePx, int cellWidth, int cellHeight, string characters, byte[] coverage)
    {
        FamilyName = familyName;
        FontSizePx = fontSizePx;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        Characters = characters;
        _coverage = coverage;
        Array.Fill(_asciiIndex, -1);

        int glyphSize = cellWidth * cellHeight;
        _blank = new bool[characters.Length];
        _solid = new bool[characters.Length];
        for (int i = 0; i < characters.Length; i++)
        {
            char c = characters[i];
            if (c < 128)
            {
                _asciiIndex[c] = i;
            }
            else
            {
                _extendedIndex[c] = i;
            }

            var mask = coverage.AsSpan(i * glyphSize, glyphSize);
            _blank[i] = !mask.ContainsAnyExcept((byte)0);
            _solid[i] = !mask.ContainsAnyExcept((byte)255);
        }

        _fallbackIndex = _asciiIndex['?'] >= 0 ? _asciiIndex['?'] : 0;
    }

    public string FamilyName { get; }
    public double FontSizePx { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }
    public string Characters { get; }
    public int GlyphCount => Characters.Length;

    public int GetGlyphIndex(char c)
    {
        if (c < 128)
        {
            int index = _asciiIndex[c];
            return index >= 0 ? index : _fallbackIndex;
        }

        return _extendedIndex.TryGetValue(c, out var ext) ? ext : _fallbackIndex;
    }

    public bool Contains(char c) => c < 128 ? _asciiIndex[c] >= 0 : _extendedIndex.ContainsKey(c);

    public ReadOnlySpan<byte> GetCoverage(int glyphIndex)
    {
        int size = CellWidth * CellHeight;
        return _coverage.AsSpan(glyphIndex * size, size);
    }

    public bool IsBlank(int glyphIndex) => _blank[glyphIndex];

    public bool IsSolid(int glyphIndex) => _solid[glyphIndex];

    /// <summary>Printable ASCII plus the line-drawing characters used by edge modes.</summary>
    public static string BaseCharacters { get; } = new(Enumerable.Range(32, 95).Select(i => (char)i).ToArray());

    /// <summary>
    /// Rasterizes the glyphs with WPF (white on black, grayscale antialiasing) into one bitmap and
    /// slices it into per-cell coverage masks. Must run on an STA thread.
    /// </summary>
    internal static GlyphAtlas Build(string familyName, double fontSizePx, string characters)
    {
        var (metrics, typeface, glyphTypeface) = FontCellMetrics.Measure(familyName);
        var (cellW, cellH) = metrics.CellSize(fontSizePx);
        var chars = characters.Distinct().ToArray();
        const int perRow = 16;
        int atlasRows = (chars.Length + perRow - 1) / perRow;
        int atlasW = perRow * cellW;
        int atlasH = atlasRows * cellH;

        double advancePx = metrics.AdvanceEm * fontSizePx;
        // Whole-pixel origins plus hinted (Display) formatting keep small glyphs sharp instead of
        // smearing each stroke across two pixel rows/columns.
        double baselinePx = Math.Round(metrics.BaselineEm * fontSizePx + (cellH - metrics.LineSpacingEm * fontSizePx) / 2.0);
        double offsetX = Math.Round((cellW - advancePx) / 2.0);

        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Fixed);

        var synthetic = new HashSet<int>();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, atlasW, atlasH));
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (IsSyntheticBlock(c))
                {
                    synthetic.Add(i);
                    continue;
                }

                double x = (i % perRow) * cellW;
                double y = (i / perRow) * cellH;
                dc.PushClip(new RectangleGeometry(new Rect(x, y, cellW, cellH)));
                if (glyphTypeface.CharacterToGlyphMap.TryGetValue(c, out var glyph))
                {
                    var run = new GlyphRun(glyphTypeface, 0, false, fontSizePx, 1.0f, new[] { glyph },
                        new Point(x + offsetX, y + baselinePx), new[] { advancePx }, null, null, null, null, null, null);
                    dc.DrawGlyphRun(Brushes.White, run);
                }
                else if (!char.IsWhiteSpace(c))
                {
                    // Characters missing from the font use WPF font fallback, centered in the cell.
                    var text = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, fontSizePx, Brushes.White, 1.0);
                    dc.DrawText(text, new Point(x + (cellW - text.WidthIncludingTrailingWhitespace) / 2, y + (cellH - text.Height) / 2));
                }

                dc.Pop();
            }
        }

        var bitmap = new RenderTargetBitmap(atlasW, atlasH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[atlasW * atlasH * 4];
        bitmap.CopyPixels(pixels, atlasW * 4, 0);

        int glyphSize = cellW * cellH;
        var coverage = new byte[glyphSize * chars.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            var mask = coverage.AsSpan(i * glyphSize, glyphSize);
            if (synthetic.Contains(i))
            {
                FillSyntheticBlock(chars[i], mask, cellW, cellH);
                continue;
            }

            int ox = (i % perRow) * cellW;
            int oy = (i / perRow) * cellH;
            for (int y = 0; y < cellH; y++)
            {
                int src = ((oy + y) * atlasW + ox) * 4;
                for (int x = 0; x < cellW; x++)
                {
                    // White text on black: any color channel equals the coverage.
                    mask[y * cellW + x] = pixels[src + x * 4 + 1];
                }
            }
        }

        return new GlyphAtlas(metrics.FamilyName, fontSizePx, cellW, cellH, new string(chars), coverage);
    }

    /// <summary>Block elements are generated exactly so they tile seamlessly (font glyphs rarely fill the cell).</summary>
    private static bool IsSyntheticBlock(char c) => c is '█' or '▓' or '▒' or '░' or '▀' or '▄' or '▌' or '▐';

    private static void FillSyntheticBlock(char c, Span<byte> mask, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                mask[y * w + x] = c switch
                {
                    '█' => 255,
                    '▓' => 191,
                    '▒' => 128,
                    '░' => 64,
                    '▀' => (byte)(y < h / 2 ? 255 : 0),
                    '▄' => (byte)(y >= h / 2 ? 255 : 0),
                    '▌' => (byte)(x < w / 2 ? 255 : 0),
                    '▐' => (byte)(x >= w / 2 ? 255 : 0),
                    _ => 0,
                };
            }
        }
    }
}
