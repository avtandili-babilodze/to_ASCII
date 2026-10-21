using System.Diagnostics;
using AsciiVision.Capture;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;
using AsciiVision.Core.Media;
using AsciiVision.Export;
using AsciiVision.Processing;

namespace AsciiVision.Tests;

public class MediaProbeTests
{
    [Fact]
    public void ParsesStreamsAndAppliesRotation()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
              "avg_frame_rate": "30000/1001", "duration": "12.5", "nb_frames": "375",
              "color_transfer": "smpte2084",
              "side_data_list": [ { "rotation": -90 } ] },
            { "codec_type": "audio", "codec_name": "aac" }
          ],
          "format": { "duration": "12.6" }
        }
        """;
        var info = MediaProbe.Parse("x.mp4", json);
        Assert.Equal(1080, info.Width);
        Assert.Equal(1920, info.Height);
        Assert.Equal(29.97, info.FrameRate, 2);
        Assert.Equal(TimeSpan.FromSeconds(12.5), info.Duration);
        Assert.True(info.HasAudio);
        Assert.True(info.IsHdr);
        Assert.Equal(375, info.EstimatedFrames(30));
    }

    [Fact]
    public void FileWithoutVideoIsRejected()
    {
        Assert.Throws<FfmpegException>(() => MediaProbe.Parse("a.mp4", """{ "streams": [ { "codec_type": "audio" } ] }"""));
    }

    [Theory]
    [InlineData("25/1", 25)]
    [InlineData("0/0", 0)]
    [InlineData("24", 24)]
    [InlineData(null, 0)]
    public void ParsesFrameRates(string? rate, double expected) => Assert.Equal(expected, MediaProbe.ParseRate(rate), 3);

    [Fact]
    public void DecodeSizeIsEvenAndBounded()
    {
        var (w, h) = FfmpegArguments.DecodeSize(3841, 2161, 1280);
        Assert.Equal(1280, w);
        Assert.Equal(0, h % 2);
        Assert.InRange(h, 718, 722);
    }

    [Fact]
    public void EncoderArgumentsDeclareRawFormatAndQuote()
    {
        var caps = new FfmpegCapabilities(true, false, true, false, false);
        var args = FfmpegArguments.EncodeRawBgraToMp4(new FfmpegArguments.EncoderSettings(640, 360, 30, EncodingQuality.High, @"C:\out dir\ü.mp4", @"C:\in.mp4"), caps);
        Assert.Equal("rawvideo", args[args.IndexOf("-f") + 1]);
        Assert.Equal("bgra", args[args.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("640x360", args[args.IndexOf("-video_size") + 1]);
        Assert.Equal("30", args[args.IndexOf("-framerate") + 1]);
        Assert.Contains("libx264", args);
        Assert.Contains("aac", args);
        Assert.Equal(@"C:\out dir\ü.mp4", args[^1]); // passed as a single argument (ArgumentList quotes it)
        Assert.Throws<ArgumentException>(() => FfmpegArguments.EncodeRawBgraToMp4(new FfmpegArguments.EncoderSettings(641, 360, 30, EncodingQuality.High, "x.mp4"), caps));
    }

    [Fact]
    public void MissingH264EncoderIsReported()
    {
        var caps = new FfmpegCapabilities(false, false, true, false, false);
        Assert.Throws<FfmpegException>(() => FfmpegArguments.EncodeRawBgraToMp4(new FfmpegArguments.EncoderSettings(64, 64, 30, EncodingQuality.High, "x.mp4"), caps));
    }

    [Fact]
    public void LocatorReturnsNullForMissingExplicitPathAndNoFallback()
    {
        var candidates = FfmpegLocator.CandidatePaths(@"Z:\nope\ffmpeg.exe").ToList();
        Assert.Equal(@"Z:\nope\ffmpeg.exe", candidates[0]);
    }
}

public class FfmpegIntegrationTests
{
    private static AsciiSettings Settings => AsciiSettings.Default with { Preset = QualityPreset.Performance, FontSize = 8 };

    [FfmpegFact]
    public async Task ProbesGeneratedSample()
    {
        var info = await MediaProbe.ProbeAsync(TestFfmpeg.Installation!, TestFfmpeg.SampleVideo!, CancellationToken.None);
        Assert.Equal(320, info.Width);
        Assert.Equal(180, info.Height);
        Assert.Equal(25, info.FrameRate, 1);
        Assert.True(info.HasAudio);
        Assert.InRange(info.Duration.TotalSeconds, 1.9, 2.2);
    }

    [FfmpegFact]
    public async Task VideoSourcePlaysInOrderWithSourceTimestamps()
    {
        await using var source = await FfmpegVideoSource.OpenAsync(TestFfmpeg.Installation!, TestFfmpeg.SampleVideo!);
        var sink = new FrameCollector();
        var ended = new TaskCompletionSource<SourceEndedEventArgs>();
        source.Ended += (_, e) => ended.TrySetResult(e);
        var sw = Stopwatch.StartNew();
        await source.StartAsync(sink, CancellationToken.None);
        var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
        sw.Stop();

        Assert.Equal(SourceEndReason.Completed, result.Reason);
        Assert.InRange(sink.Timestamps.Count, 40, 50); // 2 s at 25 fps (late frames may be skipped)
        Assert.True(sink.Timestamps.SequenceEqual(sink.Timestamps.Order()));
        Assert.Equal(TimeSpan.Zero, sink.Timestamps[0]);
        // Playback is paced in real time, not decoded as fast as possible.
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.6, 6);
    }

    [FfmpegFact]
    public async Task VideoSourceSeeksAndPauses()
    {
        await using var source = await FfmpegVideoSource.OpenAsync(TestFfmpeg.Installation!, TestFfmpeg.SampleVideo!);
        var sink = new FrameCollector();
        source.Pause();
        source.Seek(TimeSpan.FromSeconds(1.2));
        await source.StartAsync(sink, CancellationToken.None);
        await WaitFor(() => sink.Timestamps.Count >= 1);
        await Task.Delay(300);
        Assert.Single(sink.Timestamps); // paused: exactly the frame at the seek position
        Assert.InRange(sink.Timestamps[0].TotalSeconds, 1.15, 1.25);

        source.Resume();
        await WaitFor(() => sink.Timestamps.Count >= 5);
        Assert.True(sink.Timestamps[^1] > sink.Timestamps[0]);
        await source.StopAsync();
    }

    [FfmpegFact]
    public async Task HdrVideoIsToneMappedToSdr()
    {
        var ff = TestFfmpeg.Installation!;
        var hdr = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", "hdr-pq.mp4");
        if (!File.Exists(hdr))
        {
            using var make = FfmpegProcess.Start(ff.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=1",
                "-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-x265-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:log-level=error",
                "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", hdr,
            ], redirectInput: false, redirectOutput: false);
            await make.WaitForExitAsync(CancellationToken.None);
        }

        Assert.True(File.Exists(hdr), "ffmpeg could not create the HDR sample (libx265 missing?)");
        await using var source = await FfmpegVideoSource.OpenAsync(ff, hdr);
        Assert.True(source.Info.IsHdr);
        var sink = new FrameCollector();
        var ended = new TaskCompletionSource<SourceEndedEventArgs>();
        source.Ended += (_, e) => ended.TrySetResult(e);
        await source.StartAsync(sink, CancellationToken.None);
        var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(SourceEndReason.Completed, result.Reason);
        Assert.Equal(ff.Capabilities.HasZscale && ff.Capabilities.HasTonemap, source.IsToneMapping);
        Assert.InRange(sink.Timestamps.Count, 20, 25);
    }

    [FfmpegFact]
    public async Task ExportProducesAsciiMp4WithAudio()
    {
        var output = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", $"export ü {Guid.NewGuid():N}.mp4");
        var exporter = new VideoExporter(new FfmpegLocator(), new AsciiProcessorFactory(), SharedGlyphs.Cache);
        var progress = new List<ExportProgress>();
        var result = await exporter.ExportAsync(new ExportOptions
        {
            InputPath = TestFfmpeg.SampleVideo!,
            OutputPath = output,
            Resolution = (640, 360),
            FramesPerSecond = 20,
            IncludeAudio = true,
        }, Settings with { Style = RenderStyle.Hybrid, ColorMode = ColorMode.Original }, new SyncProgress<ExportProgress>(progress.Add), CancellationToken.None);

        Assert.True(File.Exists(output));
        Assert.Equal(40, result.Frames); // every frame of 2 s at 20 fps, none dropped
        Assert.True(result.HasAudio);
        var info = await MediaProbe.ProbeAsync(TestFfmpeg.Installation!, output, CancellationToken.None);
        Assert.Equal(640, info.Width);
        Assert.Equal(360, info.Height);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(20, info.FrameRate, 1);
        Assert.True(info.HasAudio);
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1].Fraction);
    }

    [FfmpegFact]
    public async Task NativeResolutionExportMatchesCharacterRaster()
    {
        var output = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", $"native {Guid.NewGuid():N}.mp4");
        var exporter = new VideoExporter(new FfmpegLocator(), new AsciiProcessorFactory(), SharedGlyphs.Cache);
        var result = await exporter.ExportAsync(new ExportOptions { InputPath = TestFfmpeg.SampleVideo!, OutputPath = output, IncludeAudio = false },
            Settings, null, CancellationToken.None);
        var metrics = SharedGlyphs.Cache.GetMetrics("Consolas");
        var (cw, _) = metrics.CellSize(8);
        Assert.InRange(result.Width, 80 * cw, 80 * cw + 1);
        Assert.Equal(0, result.Width % 2);
        Assert.False(result.HasAudio);
        Assert.Equal(50, result.Frames);
    }

    [FfmpegFact]
    public async Task CancelledExportLeavesNoFile()
    {
        var output = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", $"cancel {Guid.NewGuid():N}.mp4");
        var exporter = new VideoExporter(new FfmpegLocator(), new AsciiProcessorFactory(), SharedGlyphs.Cache);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ExportProgress>(p =>
        {
            if (p.FramesDone >= 5)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(
            new ExportOptions { InputPath = TestFfmpeg.SampleVideo!, OutputPath = output, Resolution = (1280, 720) },
            Settings with { Preset = QualityPreset.Ultra }, progress, cts.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.partial.mp4"));
    }

    [FfmpegFact]
    public async Task ExportOfMissingFileFailsCleanly()
    {
        var exporter = new VideoExporter(new FfmpegLocator(), new AsciiProcessorFactory(), SharedGlyphs.Cache);
        await Assert.ThrowsAsync<FileNotFoundException>(() => exporter.ExportAsync(
            new ExportOptions { InputPath = @"C:\does\not\exist.mp4", OutputPath = Path.GetTempFileName() + ".mp4" },
            Settings, null, CancellationToken.None));
    }

    [FfmpegFact]
    public async Task RecordingWritesConstantRateAsciiVideo()
    {
        var output = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", $"record {Guid.NewGuid():N}.mp4");
        var settings = new SettingsService();
        settings.Update(Settings);
        await using var recorder = new RecordingService(new FfmpegLocator(), SharedGlyphs.Cache, settings);
        using var processor = new AsciiProcessor();

        await recorder.StartAsync(new RecordingOptions(output, EncodingQuality.Small, 20));
        Assert.True(recorder.IsActive);
        var sw = Stopwatch.StartNew();
        int i = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            using var frame = SyntheticFrameSource.Render(320, 180, sw.Elapsed, i++);
            recorder.Submit(processor.Process(frame, settings.Current, new Core.Ascii.GridSize(80, 23)));
            await Task.Delay(30);
        }

        var result = await recorder.StopAsync();
        Assert.NotNull(result);
        Assert.False(recorder.IsRecording);
        Assert.True(File.Exists(output));
        var info = await MediaProbe.ProbeAsync(TestFfmpeg.Installation!, output, CancellationToken.None);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(20, info.FrameRate, 1);
        Assert.InRange(result!.FramesWritten, 24, 36); // ≈ 1.5 s × 20 fps of wall-clock time
    }

    [FfmpegFact]
    public async Task StoppingARecordingWithoutFramesProducesNothing()
    {
        var output = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", $"empty {Guid.NewGuid():N}.mp4");
        await using var recorder = new RecordingService(new FfmpegLocator(), SharedGlyphs.Cache, new SettingsService());
        await recorder.StartAsync(new RecordingOptions(output, EncodingQuality.Small, 30));
        Assert.Null(await recorder.StopAsync());
        Assert.False(File.Exists(output));
    }

    [FfmpegFact]
    public async Task PngAndTextScreenshotsMatchTheFrame()
    {
        using var processor = new AsciiProcessor();
        using var source = SyntheticFrameSource.Render(320, 180, TimeSpan.FromSeconds(1));
        var settings = Settings with { ColorMode = ColorMode.Original };
        using var frame = processor.Process(source, settings, new Core.Ascii.GridSize(40, 12));
        var exporter = new FrameImageExporter(SharedGlyphs.Cache);
        var dir = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", "shots ü");
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "frame.png");
        var txt = Path.Combine(dir, "frame.txt");
        await exporter.SavePngAsync(frame, settings, png, backgroundOverride: 0xFF203040);
        await FrameImageExporter.SaveTextAsync(frame, txt);

        var bytes = await File.ReadAllBytesAsync(png);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes.Take(4).ToArray());
        var lines = await File.ReadAllLinesAsync(txt);
        Assert.Equal(12, lines.Length);
        Assert.All(lines, l => Assert.Equal(40, l.Length));
        Assert.Equal(frame.ToText("\n").Split('\n'), lines);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }

    private sealed class FrameCollector : IFrameSink
    {
        private readonly object _gate = new();
        public List<TimeSpan> Timestamps { get; } = new();

        public void OnFrame(VideoFrame frame)
        {
            lock (_gate)
            {
                Timestamps.Add(frame.Timestamp);
            }

            frame.Dispose();
        }
    }

    /// <summary>Progress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
