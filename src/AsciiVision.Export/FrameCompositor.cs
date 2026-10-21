using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Rendering;
using AsciiVision.Rendering.Glyphs;

namespace AsciiVision.Export;

/// <summary>Renders ASCII frames into fixed-size output images (for video files and screenshots).</summary>
public sealed class FrameCompositor
{
    private readonly GlyphAtlasCache _atlases;

    public FrameCompositor(GlyphAtlasCache atlases)
    {
        _atlases = atlases;
    }

    /// <summary>The character raster size at a given font size, rounded up to even dimensions for H.264.</summary>
    public (int Width, int Height) NativeSize(GridSize grid, AsciiSettings settings, double? fontSize = null, bool even = true)
    {
        var metrics = _atlases.GetMetrics(settings.FontFamily);
        var (cellW, cellH) = metrics.CellSize(fontSize ?? settings.FontSize);
        int width = grid.Columns * cellW;
        int height = grid.Rows * cellH;
        return even ? (width + (width & 1), height + (height & 1)) : (width, height);
    }

    /// <summary>
    /// Draws <paramref name="frame"/> centered in a <paramref name="width"/>×<paramref name="height"/> BGRA buffer,
    /// using the largest font size up to <paramref name="maxFontSize"/> at which the grid fits. Borders get the
    /// frame's background color.
    /// </summary>
    public void Compose(AsciiFrame frame, AsciiSettings settings, Span<byte> destination, int width, int height, double maxFontSize)
    {
        var metrics = _atlases.GetMetrics(settings.FontFamily);
        double size = metrics.FitFontSize(frame.Columns, frame.Rows, width, height, 2, Math.Max(2, maxFontSize));
        var atlas = _atlases.GetAtlas(settings.FontFamily, size, settings.EffectiveRamp);
        var (rasterW, rasterH) = AsciiRasterizer.Measure(frame.Grid, atlas);
        int offsetX = Math.Max(0, (width - rasterW) / 2);
        int offsetY = Math.Max(0, (height - rasterH) / 2);
        bool needsBorder = rasterW != width || rasterH != height;
        AsciiRasterizer.Render(frame, atlas, destination, width, height, width * 4, offsetX, offsetY, clearFirst: needsBorder);
    }
}
