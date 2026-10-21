using System.Diagnostics;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Processing;
using AsciiVision.Rendering;
using Xunit.Abstractions;

namespace AsciiVision.Tests;

/// <summary>Runs only when ASCIIVISION_BENCHMARK=1 (timings depend on the machine).</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ASCIIVISION_BENCHMARK") != "1")
        {
            Skip = "Set ASCIIVISION_BENCHMARK=1 to run benchmarks.";
        }
    }
}

public class BenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public BenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [BenchmarkFact]
    public void ConversionAndRasterTimings()
    {
        foreach (var (width, height) in new[] { (1280, 720), (1920, 1080) })
        {
            using var frame = Capture.SyntheticFrameSource.Render(width, height, TimeSpan.FromSeconds(2));
            foreach (var style in Enum.GetValues<RenderStyle>())
            {
                foreach (var preset in new[] { QualityPreset.Performance, QualityPreset.Balanced, QualityPreset.Quality, QualityPreset.Ultra })
                {
                    using var processor = new AsciiProcessor(null, SharedGlyphs.Cache);
                    var settings = AsciiSettings.Default with { Style = style, ColorMode = ColorMode.Original, Preset = preset };
                    var grid = GridCalculator.Compute(width, height, settings.EffectiveColumns, 2.13);
                    for (int i = 0; i < 10; i++)
                    {
                        processor.Process(frame, settings, grid).Dispose();
                    }

                    const int runs = 60;
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < runs; i++)
                    {
                        processor.Process(frame, settings, grid).Dispose();
                    }

                    _output.WriteLine($"{width}x{height} {style,-8} {grid,-8} {sw.Elapsed.TotalMilliseconds / runs,6:0.00} ms/frame");
                }
            }
        }

        var atlas = SharedGlyphs.Cache.GetAtlas("Consolas", 16, string.Empty);
        using var source = Capture.SyntheticFrameSource.Render(1920, 1080, TimeSpan.FromSeconds(2));
        using var p = new AsciiProcessor();
        using var ascii = p.Process(source, AsciiSettings.Default with { ColorMode = ColorMode.Original }, new GridSize(240, 64));
        var (w, h) = AsciiRasterizer.Measure(ascii.Grid, atlas);
        var buffer = new byte[w * h * 4];
        var raster = Stopwatch.StartNew();
        for (int i = 0; i < 60; i++)
        {
            AsciiRasterizer.Render(ascii, atlas, buffer, w, h, w * 4);
        }

        _output.WriteLine($"Glyph raster 240x64 cells at 16 px ({w}x{h}): {raster.Elapsed.TotalMilliseconds / 60:0.00} ms/frame");
    }
}
