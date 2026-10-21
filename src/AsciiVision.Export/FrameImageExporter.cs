using System.Text;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Rendering;
using AsciiVision.Rendering.Glyphs;
using OpenCvSharp;

namespace AsciiVision.Export;

/// <summary>Screenshots of the current ASCII frame as PNG (same appearance as the preview) or text.</summary>
public sealed class FrameImageExporter
{
    private readonly GlyphAtlasCache _atlases;

    public FrameImageExporter(GlyphAtlasCache atlases)
    {
        _atlases = atlases;
    }

    /// <summary>Encodes the frame as PNG at the configured font size, optionally on a different background.</summary>
    public unsafe byte[] RenderPng(AsciiFrame frame, AsciiSettings settings, uint? backgroundOverride = null, double? fontSize = null)
    {
        var source = frame;
        AsciiFrame? recolored = null;
        if (backgroundOverride is { } background)
        {
            recolored = frame.Clone();
            recolored.DefaultBackground = background | 0xFF000000;
            source = recolored;
        }

        try
        {
            var atlas = _atlases.GetAtlas(settings.FontFamily, fontSize ?? settings.FontSize, settings.EffectiveRamp);
            var (width, height) = AsciiRasterizer.Measure(source.Grid, atlas);
            var pixels = new byte[width * height * 4];
            AsciiRasterizer.Render(source, atlas, pixels, width, height, width * 4);

            fixed (byte* p = pixels)
            {
                using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC4, (IntPtr)p, width * 4);
                // Encode in memory and write with .NET: cv::imwrite cannot handle non-ASCII paths on Windows.
                Cv2.ImEncode(".png", mat, out var png);
                return png;
            }
        }
        finally
        {
            recolored?.Dispose();
        }
    }

    public async Task SavePngAsync(AsciiFrame frame, AsciiSettings settings, string path, uint? backgroundOverride = null, CancellationToken ct = default)
    {
        var png = await Task.Run(() => RenderPng(frame, settings, backgroundOverride), ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(false);
    }

    public static Task SaveTextAsync(AsciiFrame frame, string path, CancellationToken ct = default) =>
        File.WriteAllTextAsync(path, frame.ToText(Environment.NewLine) + Environment.NewLine, new UTF8Encoding(false), ct);
}
