using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Processing;
using OpenCvSharp;

namespace AsciiVision.Tests;

public class ToneMappingTests
{
    [Theory]
    [InlineData(255, '@')]
    [InlineData(0, ' ')]
    public void BrightPixelsGetDenseGlyphsOnDarkBackground(int luma, char expected)
    {
        var tone = ToneMapping.Create(AsciiSettings.DefaultRamp, 0, 1, 1, brightIsDense: true);
        Assert.Equal(expected, tone.Ramp[tone.RampIndexFromToned[tone.Tone[luma]]]);
    }

    [Theory]
    [InlineData(255, ' ')]
    [InlineData(0, '@')]
    public void LightBackgroundReversesTheRamp(int luma, char expected)
    {
        var tone = ToneMapping.Create(AsciiSettings.DefaultRamp, 0, 1, 1, brightIsDense: false);
        Assert.Equal(expected, tone.Ramp[tone.RampIndexFromToned[tone.Tone[luma]]]);
    }

    [Fact]
    public void RampIndexIsMonotonicAndUsesEveryCharacter()
    {
        var used = new HashSet<int>();
        int previous = int.MaxValue;
        for (int v = 0; v < 256; v++)
        {
            int index = ToneMapping.RampIndex(v, 10, brightIsDense: true);
            Assert.InRange(index, 0, 9);
            Assert.True(index <= previous, "Brighter values must never map to a sparser glyph.");
            previous = index;
            used.Add(index);
        }

        Assert.Equal(10, used.Count);
    }

    [Fact]
    public void BrightnessContrastAndGammaAdjustValues()
    {
        Assert.Equal(128, ToneMapping.AdjustValue(128, 0, 1, 1));
        Assert.True(ToneMapping.AdjustValue(128, 0.2, 1, 1) > 170, "Positive brightness raises values.");
        Assert.True(ToneMapping.AdjustValue(200, 0, 2, 1) > ToneMapping.AdjustValue(200, 0, 1, 1), "Contrast spreads values away from mid-gray.");
        Assert.True(ToneMapping.AdjustValue(50, 0, 2, 1) < ToneMapping.AdjustValue(50, 0, 1, 1));
        Assert.True(ToneMapping.AdjustValue(64, 0, 1, 2) > 64, "Gamma > 1 brightens mid-tones.");
        Assert.Equal(255, ToneMapping.AdjustValue(255, 0, 1, 2.5));
        Assert.Equal(0, ToneMapping.AdjustValue(0, 0, 1, 2.5));
    }
}

public class GridCalculatorTests
{
    [Fact]
    public void RowsFollowSourceAspectAndCellAspect()
    {
        var grid = GridCalculator.Compute(1920, 1080, 120, 2.0);
        Assert.Equal(new GridSize(120, 34), grid); // 120 * 1080/1920 / 2 = 33.75
    }

    [Fact]
    public void TallerCellsMeanFewerRows()
    {
        var square = GridCalculator.Compute(1000, 1000, 100, 1.0);
        var tall = GridCalculator.Compute(1000, 1000, 100, 2.0);
        Assert.Equal(100, square.Rows);
        Assert.Equal(50, tall.Rows);
    }

    [Theory]
    [InlineData(QualityPreset.Performance, 80)]
    [InlineData(QualityPreset.Balanced, 120)]
    [InlineData(QualityPreset.Quality, 160)]
    [InlineData(QualityPreset.Ultra, 240)]
    public void PresetsMapToColumnCounts(QualityPreset preset, int columns)
    {
        Assert.Equal(columns, GridCalculator.ColumnsForPreset(preset, 999));
        Assert.Equal(columns, (AsciiSettings.Default with { Preset = preset }).EffectiveColumns);
    }

    [Fact]
    public void CustomPresetUsesClampedColumns()
    {
        Assert.Equal(200, GridCalculator.ColumnsForPreset(QualityPreset.Custom, 200));
        Assert.Equal(GridCalculator.MaxColumns, GridCalculator.ColumnsForPreset(QualityPreset.Custom, 100_000));
    }

    [Fact]
    public void FitWithinRespectsBothLimits()
    {
        var wide = GridCalculator.FitWithin(1920, 1080, 100, 20, 2.0);
        Assert.True(wide.Columns <= 100 && wide.Rows <= 20);
        Assert.Equal(20, wide.Rows); // height is the limiting side
        var tallTerminal = GridCalculator.FitWithin(1920, 1080, 80, 200, 2.0);
        Assert.Equal(80, tallTerminal.Columns);
        Assert.Equal(23, tallTerminal.Rows); // 80*1080/1920/2 = 22.5 → 23
    }

    [Fact]
    public void InvalidSourceSizeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GridCalculator.Compute(0, 100, 80, 2));
    }

    [Fact]
    public void NonFiniteAspectFallsBackToDefault()
    {
        var grid = GridCalculator.Compute(1920, 1080, 120, double.NaN);
        Assert.Equal(34, grid.Rows);
    }
}

public class EdgeCharacterMapperTests
{
    // Gradients in image coordinates (y down). The edge is perpendicular to the gradient.
    [Theory]
    [InlineData(1.0, 0.0, 0.0, '|')]   // horizontal gradient → vertical edge
    [InlineData(0.0, 1.0, 0.0, '-')]   // vertical gradient → horizontal edge
    [InlineData(0.5, 0.5, 0.5, '/')]   // gradient along (1,1) in y-down → edge rising to the right
    [InlineData(0.5, 0.5, -0.5, '\\')] // gradient along (1,-1) → edge falling to the right
    public void OrientationMapsToDirectionalCharacter(double sxx, double syy, double sxy, char expected)
    {
        Assert.Equal(expected, EdgeCharacterMapper.Classify(sxx, syy, sxy, 0.5));
    }

    [Fact]
    public void HorizontalEdgeNearCellBottomBecomesUnderscore()
    {
        Assert.Equal('_', EdgeCharacterMapper.Classify(0, 1, 0, centroidY: 0.85));
        Assert.Equal('-', EdgeCharacterMapper.Classify(0, 1, 0, centroidY: 0.5));
    }

    [Fact]
    public void StrongIsotropicEdgesBecomeJunctions()
    {
        Assert.Equal('+', EdgeCharacterMapper.Classify(1, 1, 0, 0.5, strength: 2.0));
        // Weak isotropic cells keep a directional character instead of cluttering with '+'.
        Assert.NotEqual('+', EdgeCharacterMapper.Classify(1, 1, 0, 0.5, strength: 1.0));
    }

    [Theory]
    [InlineData(0, '-')]
    [InlineData(30, '/')]
    [InlineData(45, '/')]
    [InlineData(90, '|')]
    [InlineData(135, '\\')]
    [InlineData(170, '-')]
    public void AngleBins(double angle, char expected)
    {
        Assert.Equal(expected, EdgeCharacterMapper.FromAngle(angle));
    }
}

public class AsciiProcessorTests
{
    private static readonly AsciiSettings Base = AsciiSettings.Default with { BackgroundColor = 0xFF000000, Style = RenderStyle.Standard, LocalContrast = 0 };

    [Fact]
    public void WhiteFrameBecomesDensestGlyphs()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(320, 180, 255, 255, 255);
        using var ascii = processor.Process(frame, Base, new GridSize(40, 12));
        Assert.Equal(40 * 12, ascii.CellCount);
        Assert.All(ascii.Chars.ToArray(), c => Assert.Equal('@', c));
    }

    [Fact]
    public void BlackFrameBecomesSpaces()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(320, 180, 0, 0, 0);
        using var ascii = processor.Process(frame, Base, new GridSize(40, 12));
        Assert.All(ascii.Chars.ToArray(), c => Assert.Equal(' ', c));
    }

    [Fact]
    public void GradientProducesTheFullRampInOrder()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.HorizontalGradient(800, 100);
        using var ascii = processor.Process(frame, Base, new GridSize(100, 5));
        var row = new string(ascii.GetRow(2));
        Assert.Equal(' ', row[0]);
        Assert.Equal('@', row[^1]);
        foreach (var c in AsciiSettings.DefaultRamp)
        {
            Assert.Contains(c, row);
        }

        // Density never decreases from left (dark) to right (bright).
        var ramp = AsciiSettings.DefaultRamp;
        for (int i = 1; i < row.Length; i++)
        {
            Assert.True(ramp.IndexOf(row[i]) <= ramp.IndexOf(row[i - 1]));
        }
    }

    [Fact]
    public void CustomRampIsUsed()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.HorizontalGradient(400, 100);
        var settings = Base with { CharacterRamp = "#. " };
        using var ascii = processor.Process(frame, settings, new GridSize(30, 4));
        Assert.All(ascii.Chars.ToArray(), c => Assert.Contains(c, "#. "));
        Assert.Contains('#', ascii.Chars.ToArray());
        Assert.Contains('.', ascii.Chars.ToArray());
    }

    [Fact]
    public void InvalidRampFallsBackToDefault()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(100, 100, 255, 255, 255);
        using var ascii = processor.Process(frame, Base with { CharacterRamp = "x" }, new GridSize(10, 5));
        Assert.Equal('@', ascii.Chars[0]);
    }

    [Fact]
    public void InvertRampSwapsDirection()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(100, 100, 255, 255, 255);
        using var ascii = processor.Process(frame, Base with { InvertRamp = true }, new GridSize(10, 5));
        Assert.Equal(' ', ascii.Chars[0]);
    }

    [Fact]
    public void LightBackgroundUsesDenseGlyphsForDarkPixels()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(100, 100, 0, 0, 0);
        using var ascii = processor.Process(frame, Base with { BackgroundColor = 0xFFFFFFFF }, new GridSize(10, 5));
        Assert.Equal('@', ascii.Chars[0]);
    }

    [Fact]
    public void OutlineModeDrawsVerticalLineWithPipes()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Line(640, 360, new Point(320, 0), new Point(320, 359), 3);
        var settings = Base with { Style = RenderStyle.Outline };
        using var ascii = processor.Process(frame, settings, new GridSize(64, 18));
        var chars = ascii.Chars.ToArray();
        int pipes = chars.Count(c => c == '|');
        Assert.True(pipes >= 12, $"Expected a column of '|', got {pipes}.\n{ascii.ToText()}");
        Assert.True(chars.Count(c => c != ' ' && c != '|') <= 4, "Outline mode should not add unrelated characters.\n" + ascii.ToText());
    }

    [Fact]
    public void OutlineModeDrawsHorizontalLineWithDashes()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Line(640, 360, new Point(0, 180), new Point(639, 180), 3);
        using var ascii = processor.Process(frame, Base with { Style = RenderStyle.Outline }, new GridSize(64, 18));
        int dashes = ascii.Chars.ToArray().Count(c => c is '-' or '_');
        Assert.True(dashes >= 50, $"Expected a row of '-', got {dashes}.\n{ascii.ToText()}");
    }

    [Theory]
    [InlineData(0, 359, 359, 0, '/')]   // bottom-left → top-right
    [InlineData(0, 0, 359, 359, '\\')]  // top-left → bottom-right
    public void OutlineModeDrawsDiagonals(int x1, int y1, int x2, int y2, char expected)
    {
        using var processor = new AsciiProcessor();
        // Square source and square cells: a 45° line in the image is 45° in the grid.
        using var frame = TestImages.Line(360, 360, new Point(x1, y1), new Point(x2, y2), 3);
        using var ascii = processor.Process(frame, Base with { Style = RenderStyle.Outline }, new GridSize(36, 36));
        var chars = ascii.Chars.ToArray();
        int matching = chars.Count(c => c == expected);
        int edges = chars.Count(c => c != ' ');
        Assert.True(matching >= edges * 0.6, $"Expected mostly '{expected}' ({matching}/{edges}).\n{ascii.ToText()}");
    }

    [Fact]
    public void BlankImageHasNoEdges()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(320, 180, 128, 128, 128);
        using var ascii = processor.Process(frame, Base with { Style = RenderStyle.Outline }, new GridSize(32, 9));
        Assert.All(ascii.Chars.ToArray(), c => Assert.Equal(' ', c));
    }

    [Fact]
    public void HybridCombinesEdgesAndShading()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Line(640, 360, new Point(320, 0), new Point(320, 359), 3);
        // Fill the background with mid-gray so there is texture to shade.
        var data = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Data);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == 0xFF000000)
            {
                data[i] = 0xFF808080;
            }
        }

        using var hybrid = processor.Process(frame, Base with { Style = RenderStyle.Hybrid, HybridBlend = 0.5 }, new GridSize(64, 18));
        var chars = hybrid.Chars.ToArray();
        Assert.Contains('|', chars);
        Assert.True(chars.Count(c => AsciiSettings.DefaultRamp.Contains(c) && c != ' ') > 500, "Shading should remain.\n" + hybrid.ToText());
    }

    [Fact]
    public void HybridBlendControlsEdgeEmphasis()
    {
        using var processor = new AsciiProcessor();
        using var frame = SyntheticPattern();
        int CountEdges(double blend)
        {
            using var a = processor.Process(frame, Base with { Style = RenderStyle.Hybrid, HybridBlend = blend }, new GridSize(96, 27));
            // '-' and '+' also occur in the shading ramp, so only count glyphs that are unambiguously edges.
            return a.Chars.ToArray().Count(c => c is '|' or '/' or '\\' or '_');
        }

        Assert.True(CountEdges(1.0) > CountEdges(0.0), "A stronger blend must mark more cells as edges.");
    }

    [Fact]
    public void EdgeSensitivityControlsDetection()
    {
        using var processor = new AsciiProcessor();
        using var frame = SyntheticPattern();
        int Count(double s)
        {
            using var a = processor.Process(frame, Base with { Style = RenderStyle.Outline, EdgeSensitivity = s }, new GridSize(96, 27));
            return a.Chars.ToArray().Count(c => c != ' ');
        }

        Assert.True(Count(0.9) > Count(0.1));
    }

    [Fact]
    public void EdgeThicknessIncreasesCoverage()
    {
        using var processor = new AsciiProcessor();
        using var frame = SyntheticPattern();
        int Count(int t)
        {
            using var a = processor.Process(frame, Base with { Style = RenderStyle.Outline, EdgeThickness = t }, new GridSize(96, 27));
            return a.Chars.ToArray().Count(c => c != ' ');
        }

        Assert.True(Count(3) >= Count(1));
    }

    [Fact]
    public void OriginalColorsUseCellColors()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(200, 100, 200, 40, 20);
        using var ascii = processor.Process(frame, Base with { ColorMode = ColorMode.Original }, new GridSize(10, 5));
        Assert.False(ascii.IsMonochrome);
        var fg = ascii.Foreground[0];
        // Glyph colors are brightened on dark backgrounds (hue kept): 200,40,20 scaled by 245/200.
        Assert.InRange(ColorUtil.R(fg), 240, 245);
        Assert.InRange(ColorUtil.G(fg), 47, 50);
        Assert.InRange(ColorUtil.B(fg), 23, 25);
    }

    [Fact]
    public void BackgroundFillProducesPerCellBackgrounds()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(200, 100, 200, 100, 50);
        using var ascii = processor.Process(frame, Base with { ColorMode = ColorMode.Original, ColorBackgroundFill = true }, new GridSize(10, 5));
        Assert.True(ascii.HasCellBackground);
        Assert.Equal(ColorUtil.FromRgb(70, 35, 17), ascii.Background[0]);
    }

    [Theory]
    [InlineData(ColorMode.White, 0xFFFFFFFFu)]
    [InlineData(ColorMode.Green, 0xFF33FF66u)]
    public void MonochromeModesUseFixedColor(ColorMode mode, uint expected)
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.HorizontalGradient(200, 100);
        using var ascii = processor.Process(frame, Base with { ColorMode = mode }, new GridSize(20, 5));
        Assert.True(ascii.IsMonochrome);
        Assert.All(ascii.Foreground.ToArray(), c => Assert.Equal(expected, c));
    }

    [Fact]
    public void CustomModeUsesCustomColors()
    {
        using var processor = new AsciiProcessor();
        using var frame = TestImages.Solid(100, 100, 255, 255, 255);
        var settings = Base with { ColorMode = ColorMode.Custom, ForegroundColor = 0xFF123456, BackgroundColor = 0xFF010203 };
        using var ascii = processor.Process(frame, settings, new GridSize(10, 5));
        Assert.Equal(0xFF123456u, ascii.Foreground[0]);
        Assert.Equal(0xFF010203u, ascii.DefaultBackground);
    }

    [Fact]
    public void DarkSourceColorsAreLiftedForVisibility()
    {
        uint lifted = AsciiProcessor.EnsureVisible(10, 5, 0, darkBackground: true, emphasize: false);
        Assert.True(Math.Max(ColorUtil.R(lifted), Math.Max(ColorUtil.G(lifted), ColorUtil.B(lifted))) >= 72);
        Assert.True(ColorUtil.R(lifted) > ColorUtil.G(lifted), "Hue is preserved.");
        uint darkened = AsciiProcessor.EnsureVisible(250, 250, 250, darkBackground: false, emphasize: false);
        Assert.True(ColorUtil.Luma(darkened) <= 151);
    }

    [Fact]
    public void FrameMetadataIsPreserved()
    {
        using var processor = new AsciiProcessor();
        using var frame = Core.Imaging.VideoFrame.Rent(64, 32, TimeSpan.FromSeconds(1.5), 7);
        using var ascii = processor.Process(frame, Base, new GridSize(16, 4));
        Assert.Equal(TimeSpan.FromSeconds(1.5), ascii.Timestamp);
        Assert.Equal(64, ascii.SourceWidth);
        Assert.Equal(32, ascii.SourceHeight);
    }

    [Fact]
    public void ProcessorReusesPooledFrames()
    {
        var pool = new AsciiFramePool();
        using var processor = new AsciiProcessor(pool);
        using var frame = TestImages.Solid(64, 64, 10, 10, 10);
        for (int i = 0; i < 50; i++)
        {
            processor.Process(frame, Base, new GridSize(16, 8)).Dispose();
        }

        Assert.Equal(1, pool.CreatedCount);
    }

    [Fact]
    public void DisposedProcessorThrows()
    {
        var processor = new AsciiProcessor();
        processor.Dispose();
        using var frame = TestImages.Solid(16, 16, 0, 0, 0);
        Assert.Throws<ObjectDisposedException>(() => processor.Process(frame, Base, new GridSize(8, 4)));
    }

    private static Core.Imaging.VideoFrame SyntheticPattern() =>
        Capture.SyntheticFrameSource.Render(960, 540, TimeSpan.FromSeconds(2.3));
}
