using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;
using AsciiVision.Processing;

namespace AsciiVision.Tests;

public class GlyphShapeSetTests
{
    [Fact]
    public void GlobalNormalizationScalesToTheLargestRegion()
    {
        var shapes = SharedGlyphs.Cache.GetShapes("Consolas", 3, 6, DetailCharacterSets.AllAscii);
        Assert.Equal(95, shapes.Count);
        Assert.Equal(18, shapes.Dimensions);
        Assert.Equal(0, shapes.Stride % System.Numerics.Vector<float>.Count);
        Assert.Equal(1f, shapes.Vectors.Max(), 3);
        Assert.InRange(shapes.DensityScale, 0.1f, 1f);
        int space = Array.IndexOf(shapes.Characters, ' ');
        Assert.All(shapes.Vectors.AsSpan(space * shapes.Stride, shapes.Dimensions).ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ShapesDescribeWhereTheInkIs()
    {
        var shapes = SharedGlyphs.Cache.GetShapes("Consolas", 3, 6, DetailCharacterSets.AllAscii);
        float[] Vector(char c)
        {
            int g = Array.IndexOf(shapes.Characters, c);
            return shapes.Vectors.AsSpan(g * shapes.Stride, shapes.Dimensions).ToArray();
        }

        // '_' has its ink in the bottom row, '^' near the top, '|' in the middle column.
        var underscore = Vector('_');
        Assert.True(underscore[15..18].Sum() > underscore[0..12].Sum());
        var caret = Vector('^');
        Assert.True(caret[0..9].Sum() > caret[9..18].Sum());
        var bar = Vector('|');
        float middle = 0, sides = 0;
        for (int r = 0; r < 6; r++)
        {
            middle += bar[r * 3 + 1];
            sides += bar[r * 3] + bar[r * 3 + 2];
        }

        Assert.True(middle > sides);
    }

    [Fact]
    public void SubsetsAreRestrictedAndRenormalized()
    {
        var all = SharedGlyphs.Cache.GetShapes("Consolas", 3, 6, DetailCharacterSets.AllAscii);
        var subset = all.Subset(" .:");
        Assert.Equal(" .:".ToCharArray(), subset.Characters);
        Assert.Equal(1f, subset.Vectors.Max(), 3);
        Assert.Throws<ArgumentException>(() => all.Subset("é"));
    }

    [Fact]
    public void ProviderCachesPerCharacterSet()
    {
        var a = SharedGlyphs.Cache.GetShapes("Consolas", 3, 6, DetailCharacterSets.Symbols);
        var b = SharedGlyphs.Cache.GetShapes("Consolas", 3, 6, DetailCharacterSets.Symbols);
        Assert.Same(a, b);
        Assert.Equal(DetailCharacterSets.Symbols.Length, a.Count);
    }
}

public class DetailedStyleTests
{
    private static readonly AsciiSettings Detailed = AsciiSettings.Default with
    {
        Style = RenderStyle.Detailed,
        BackgroundColor = 0xFF000000,
        LocalContrast = 0,
    };

    private static AsciiProcessor NewProcessor() => new(null, SharedGlyphs.Cache);

    private static GlyphShapeSet Shapes(AsciiSettings s, int rows = 6) =>
        SharedGlyphs.Cache.GetShapes(s.FontFamily, 3, rows, DetailCharacterSets.For(s.DetailCharacters));

    /// <summary>A frame where each cell (10 × 20 px) is painted by <paramref name="paint"/>(x, y within the cell).</summary>
    private static VideoFrame CellPattern(int columns, int rows, Func<int, int, bool> paint)
    {
        var frame = VideoFrame.Rent(columns * 10, rows * 20, TimeSpan.Zero);
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Data);
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                pixels[y * frame.Width + x] = paint(x % 10, y % 20) ? 0xFFFFFFFF : 0xFF000000;
            }
        }

        return frame;
    }

    [Fact]
    public void BlackBecomesSpacesAndWhiteBecomesDenseGlyphs()
    {
        using var processor = NewProcessor();
        using (var black = TestImages.Solid(300, 200, 0, 0, 0))
        using (var ascii = processor.Process(black, Detailed, new GridSize(30, 10)))
        {
            Assert.All(ascii.Chars.ToArray(), c => Assert.Equal(' ', c));
        }

        using var white = TestImages.Solid(300, 200, 255, 255, 255);
        using var bright = processor.Process(white, Detailed, new GridSize(30, 10));
        var chars = bright.Chars.ToArray();
        Assert.Single(chars.Distinct());
        var shapes = Shapes(Detailed);
        int index = Array.IndexOf(shapes.Characters, chars[0]);
        float density = shapes.Vectors.AsSpan(index * shapes.Stride, shapes.Dimensions).ToArray().Average();
        Assert.True(density >= shapes.DensityScale * 0.85f, $"'{chars[0]}' is not one of the densest glyphs.");
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    public void GlyphInkFollowsTheBrightPartOfTheCell(string side)
    {
        Func<int, int, bool> paint = side switch
        {
            "left" => (x, _) => x < 3,
            "right" => (x, _) => x >= 7,
            // Vertical bands reach into the glyph body: the top and bottom few pixels of a cell are the
            // font's line gap, where no glyph has ink.
            "top" => (_, y) => y < 8,
            _ => (_, y) => y >= 12,
        };
        using var frame = CellPattern(20, 6, paint);
        using var processor = NewProcessor();
        var settings = Detailed with { DetailCharacters = DetailCharacterSet.AllAscii };
        using var ascii = processor.Process(frame, settings, new GridSize(20, 6));
        var shapes = Shapes(settings);
        foreach (char c in ascii.Chars.ToArray().Distinct())
        {
            var v = shapes.Vectors.AsSpan(Array.IndexOf(shapes.Characters, c) * shapes.Stride, shapes.Dimensions).ToArray();
            float first = 0, second = 0;
            for (int r = 0; r < 6; r++)
            {
                for (int col = 0; col < 3; col++)
                {
                    float value = v[r * 3 + col];
                    bool inFirst = side is "left" or "right" ? col == 0 : r < 3;
                    if (inFirst)
                    {
                        first += value;
                    }
                    else if (side is "left" or "right" ? col == 2 : r >= 3)
                    {
                        second += value;
                    }
                }
            }

            bool expectFirst = side is "left" or "top";
            Assert.True(expectFirst ? first > second : second > first, $"'{c}' does not put its ink on the {side}.");
        }
    }

    [Theory]
    [InlineData(DetailCharacterSet.Symbols)]
    [InlineData(DetailCharacterSet.SymbolsAndLetters)]
    [InlineData(DetailCharacterSet.AllAscii)]
    public void OnlyCharactersOfTheChosenSetAreUsed(DetailCharacterSet set)
    {
        using var frame = Capture.SyntheticFrameSource.Render(960, 540, TimeSpan.FromSeconds(1.7));
        using var processor = NewProcessor();
        using var ascii = processor.Process(frame, Detailed with { DetailCharacters = set }, new GridSize(120, 30));
        var allowed = DetailCharacterSets.For(set);
        Assert.All(ascii.Chars.ToArray(), c => Assert.Contains(c, allowed));
        Assert.True(ascii.Chars.ToArray().Distinct().Count() > 8, "A real image should use a variety of shapes.");
    }

    [Fact]
    public void SharpnessMakesShapesCrisper()
    {
        using var frame = Capture.SyntheticFrameSource.Render(960, 540, TimeSpan.FromSeconds(1.7));
        using var processor = NewProcessor();
        int Blank(double sharpness)
        {
            using var a = processor.Process(frame, Detailed with { DetailSharpness = sharpness }, new GridSize(120, 30));
            return a.Chars.ToArray().Count(c => c == ' ');
        }

        // Exaggerating the in-cell contrast pushes weak cells to emptier glyphs.
        Assert.True(Blank(1.0) > Blank(0.0));
    }

    [Fact]
    public void ColorsStillComeFromTheSource()
    {
        using var frame = TestImages.Solid(300, 200, 200, 60, 20);
        using var processor = NewProcessor();
        using var ascii = processor.Process(frame, Detailed with { ColorMode = ColorMode.Original }, new GridSize(30, 10));
        uint fg = ascii.Foreground[0];
        Assert.True(ColorUtil.R(fg) > ColorUtil.G(fg) && ColorUtil.G(fg) > ColorUtil.B(fg), "Hue is preserved.");
        Assert.True(ColorUtil.R(fg) >= 200, "Glyph colors are brightened on dark backgrounds.");
    }

    [Fact]
    public void WithoutGlyphShapesDetailedFallsBackToStandard()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.HorizontalGradient(400, 100);
        using var ascii = processor.Process(frame, Detailed, new GridSize(40, 5));
        Assert.All(ascii.Chars.ToArray(), c => Assert.Contains(c, AsciiSettings.DefaultRamp));
    }

    [Fact]
    public void DetailedConversionMeetsAFrameBudget()
    {
        using var frame = Capture.SyntheticFrameSource.Render(1920, 1080, TimeSpan.FromSeconds(1));
        using var processor = NewProcessor();
        var grid = new GridSize(240, 63);
        for (int i = 0; i < 3; i++)
        {
            processor.Process(frame, Detailed, grid).Dispose();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            processor.Process(frame, Detailed, grid).Dispose();
        }

        // Generous bound (CI machines are slow); about 2.5 ms on the development machine.
        Assert.True(sw.Elapsed.TotalMilliseconds / 10 < 40, $"{sw.Elapsed.TotalMilliseconds / 10:0.0} ms per frame");
    }
}

public class LocalContrastTests
{
    [Fact]
    public void LocalContrastRevealsDetailInFlatAreas()
    {
        // A low-contrast gradient (gray 100..140) only spans a couple of ramp steps without enhancement.
        var frame = VideoFrame.Rent(640, 360, TimeSpan.Zero);
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Data);
        for (int y = 0; y < 360; y++)
        {
            for (int x = 0; x < 640; x++)
            {
                uint v = (uint)(100 + 40 * x / 640 + 6 * ((x / 40 + y / 40) % 2));
                pixels[y * 640 + x] = 0xFF000000u | (v << 16) | (v << 8) | v;
            }
        }

        using (frame)
        {
            using var processor = new AsciiProcessor();
            var settings = AsciiSettings.Default with { Style = RenderStyle.Standard, CharacterRamp = AsciiSettings.FineRamp };
            int Distinct(double strength)
            {
                using var a = processor.Process(frame, settings with { LocalContrast = strength }, new GridSize(128, 36));
                return a.Chars.ToArray().Distinct().Count();
            }

            Assert.True(Distinct(1.0) > Distinct(0.0) + 3);
        }
    }

    [Fact]
    public void FineRampIsAValidSeventyLevelRamp()
    {
        Assert.True(SettingsValidator.IsValidRamp(AsciiSettings.FineRamp, out _));
        Assert.Equal(70, AsciiSettings.FineRamp.Length);
        Assert.True(SettingsValidator.IsValidRamp(AsciiSettings.BlockRamp, out _));
    }
}
