using System.Diagnostics;
using AsciiVision.Capture;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Imaging;
using AsciiVision.Core.Threading;
using AsciiVision.Processing;

namespace AsciiVision.Tests;

public class FrameLifecycleTests
{
    [Fact]
    public void ReleasedFramesReturnToPool()
    {
        var pool = new AsciiFramePool();
        var frame = pool.Rent(10, 5);
        frame.Retain();
        frame.Dispose();
        Assert.Equal(0, pool.AvailableCount); // still referenced once
        frame.Dispose();
        Assert.Equal(1, pool.AvailableCount);
        var again = pool.Rent(8, 4);
        Assert.Same(frame, again);
        Assert.Equal(1, pool.CreatedCount);
        again.Dispose();
    }

    [Fact]
    public void RetainAfterReleaseThrows()
    {
        var frame = AsciiFrame.CreateUnpooled(4, 4);
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.Retain());
    }

    [Fact]
    public void OverReleaseDoesNotCorruptThePool()
    {
        var pool = new AsciiFramePool();
        var frame = pool.Rent(4, 4);
        frame.Dispose();
        frame.Dispose();
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public void PooledFramesGrowWhenNeeded()
    {
        var pool = new AsciiFramePool();
        pool.Rent(4, 4).Dispose();
        using var big = pool.Rent(100, 50);
        Assert.Equal(5000, big.CellCount);
        Assert.Equal(5000, big.WritableChars.Length);
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var frame = AsciiFrame.CreateUnpooled(3, 1);
        "abc".AsSpan().CopyTo(frame.WritableChars);
        var clone = frame.Clone();
        frame.WritableChars[0] = 'z';
        Assert.Equal("abc", clone.ToText());
    }

    [Fact]
    public void ToTextHasOneLinePerRow()
    {
        var frame = AsciiFrame.CreateUnpooled(3, 2);
        "ab cd ".AsSpan().CopyTo(frame.WritableChars);
        Assert.Equal("ab \ncd ", frame.ToText("\n"));
    }

    [Fact]
    public void VideoFrameDisposeIsIdempotent()
    {
        var frame = VideoFrame.Rent(16, 16, TimeSpan.Zero);
        frame.Dispose();
        frame.Dispose();
        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => frame.Buffer);
    }

    [Fact]
    public void MailboxKeepsOnlyTheNewestItemAndDisposesTheRest()
    {
        int dropped = 0;
        using var mailbox = new LatestItemMailbox<VideoFrame>(() => dropped++);
        var a = VideoFrame.Rent(4, 4, TimeSpan.Zero, 1);
        var b = VideoFrame.Rent(4, 4, TimeSpan.Zero, 2);
        var c = VideoFrame.Rent(4, 4, TimeSpan.Zero, 3);
        mailbox.Post(a);
        mailbox.Post(b);
        mailbox.Post(c);
        Assert.True(a.IsDisposed && b.IsDisposed);
        Assert.Equal(2, dropped);
        Assert.Equal(2, mailbox.DroppedCount);
        using var taken = mailbox.TryTake();
        Assert.Same(c, taken);
        Assert.Null(mailbox.TryTake());
    }

    [Fact]
    public void DisposedMailboxDisposesLateItems()
    {
        var mailbox = new LatestItemMailbox<VideoFrame>();
        mailbox.Dispose();
        var frame = VideoFrame.Rent(4, 4, TimeSpan.Zero);
        mailbox.Post(frame);
        Assert.True(frame.IsDisposed);
    }

    [Fact]
    public void RateCounterMeasuresRealElapsedTime()
    {
        long now = 0;
        var counter = new RateCounter(TimeSpan.FromSeconds(1), clock: () => now);
        for (int i = 0; i < 30; i++)
        {
            counter.Tick();
            now += Stopwatch.Frequency / 30;
        }

        Assert.InRange(counter.Rate, 29, 31);
        now += Stopwatch.Frequency * 2;
        Assert.Equal(0, counter.Rate);
        Assert.Equal(30, counter.Total);
    }
}

public class AdaptiveQualityTests
{
    [Fact]
    public void SustainedOverloadReducesDensityGradually()
    {
        var now = TimeSpan.Zero;
        var controller = new AdaptiveQualityController(() => now);
        // Target 60 fps → 16.7 ms budget; report 30 ms frames for 10 seconds.
        for (int i = 0; i < 600; i++)
        {
            controller.Report(30, 60);
            now += TimeSpan.FromMilliseconds(1000.0 / 60);
        }

        Assert.True(controller.Scale < 1.0);
        Assert.True(controller.Scale >= AdaptiveQualityController.MinScale);
        Assert.True(controller.EffectiveColumns(160) < 160);
    }

    [Fact]
    public void NeverDropsBelowReadableLimits()
    {
        var now = TimeSpan.Zero;
        var controller = new AdaptiveQualityController(() => now);
        for (int i = 0; i < 10_000; i++)
        {
            controller.Report(500, 60);
            now += TimeSpan.FromMilliseconds(16);
        }

        Assert.Equal(AdaptiveQualityController.MinScale, controller.Scale, 3);
        Assert.Equal(AdaptiveQualityController.MinColumns, controller.EffectiveColumns(80));
        Assert.Equal(96, controller.EffectiveColumns(240)); // 40 % of the request
    }

    [Fact]
    public void HeadroomRestoresDensityWithoutOscillating()
    {
        var now = TimeSpan.Zero;
        var controller = new AdaptiveQualityController(() => now);
        for (int i = 0; i < 400; i++)
        {
            controller.Report(40, 60);
            now += TimeSpan.FromMilliseconds(16);
        }

        double reduced = controller.Scale;
        Assert.True(reduced < 1.0);

        int changes = 0;
        controller.ScaleChanged += (_, _) => changes++;
        for (int i = 0; i < 3000; i++)
        {
            controller.Report(4, 60);
            now += TimeSpan.FromMilliseconds(16);
        }

        Assert.Equal(1.0, controller.Scale, 3);
        // Only upward steps happened (no ping-pong between levels).
        Assert.True(changes <= 12);
    }

    [Fact]
    public void LoadBetweenThresholdsChangesNothing()
    {
        var now = TimeSpan.Zero;
        var controller = new AdaptiveQualityController(() => now);
        for (int i = 0; i < 2000; i++)
        {
            controller.Report(11, 60); // ~66 % of the budget: inside the hysteresis band
            now += TimeSpan.FromMilliseconds(16);
        }

        Assert.Equal(1.0, controller.Scale);
    }
}

public class PipelineTests
{
    private static (FramePipeline Pipeline, SettingsService Settings, PerformanceMetrics Metrics) CreatePipeline(AsciiSettings? settings = null)
    {
        var service = new SettingsService();
        if (settings is not null)
        {
            service.Update(settings);
        }

        var metrics = new PerformanceMetrics();
        var pipeline = new FramePipeline(new AsciiProcessorFactory(), service, new FixedGeometry(2.0), metrics, new AdaptiveQualityController());
        return (pipeline, service, metrics);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task SyntheticSourceFlowsThroughTheWholePipeline()
    {
        var (pipeline, _, metrics) = CreatePipeline(AsciiSettings.Default with { Preset = QualityPreset.Performance });
        var preview = new CollectingRenderer();
        pipeline.Preview = preview;
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(640, 360, 60));
            await WaitUntil(() => preview.Count >= 20);
            await pipeline.StopAsync();
        }

        var frames = preview.Snapshot();
        Assert.All(frames, f => Assert.Equal(new GridSize(80, 23), f.Grid)); // 80*360/640/2 = 22.5 → 23
        Assert.True(frames.Select(f => f.Sequence).SequenceEqual(frames.Select(f => f.Sequence).Order()), "Frames arrive in order.");
        Assert.True(metrics.ProcessingRate.Total >= 20);
        Assert.True(metrics.CaptureRate.Total >= metrics.ProcessingRate.Total);
    }

    [Fact]
    public async Task ConsoleWithDifferentGridGetsItsOwnConversionOfTheSameFrame()
    {
        var (pipeline, _, _) = CreatePipeline(AsciiSettings.Default with
        {
            DisplayMode = DisplayMode.Both,
            Preset = QualityPreset.Balanced,
            ConsoleSizeMode = ConsoleSizeMode.Custom,
            ConsoleColumns = 60,
        });
        var preview = new CollectingRenderer();
        var console = new CollectingRenderer();
        pipeline.Preview = preview;
        pipeline.Console = console;
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(640, 360, 30));
            await WaitUntil(() => preview.Count >= 5 && console.Count >= 5);
            await pipeline.StopAsync();
        }

        Assert.All(preview.Snapshot(), f => Assert.Equal(120, f.Columns));
        Assert.All(console.Snapshot(), f => Assert.Equal(60, f.Columns));
        // Both outputs came from the same source frames (same timestamps).
        var previewTimes = preview.Snapshot().Select(f => f.Timestamp).ToHashSet();
        Assert.Contains(console.Snapshot(), f => previewTimes.Contains(f.Timestamp));
    }

    [Fact]
    public async Task MatchPreviewSharesOneConversion()
    {
        var (pipeline, _, metrics) = CreatePipeline(AsciiSettings.Default with
        {
            DisplayMode = DisplayMode.Both,
            ConsoleSizeMode = ConsoleSizeMode.MatchPreview,
        });
        var preview = new CollectingRenderer();
        var console = new CollectingRenderer();
        pipeline.Preview = preview;
        pipeline.Console = console;
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(640, 360, 30));
            await WaitUntil(() => console.Count >= 5);
            await pipeline.StopAsync();
        }

        var previewSeq = preview.Snapshot().Select(f => f.Sequence).ToHashSet();
        Assert.All(console.Snapshot(), f => Assert.Contains(f.Sequence, previewSeq));
    }

    [Fact]
    public async Task DisplayModeRoutesFrames()
    {
        var (pipeline, settings, _) = CreatePipeline(AsciiSettings.Default with { DisplayMode = DisplayMode.Console, ConsoleSizeMode = ConsoleSizeMode.MatchPreview });
        var preview = new CollectingRenderer();
        var console = new CollectingRenderer();
        pipeline.Preview = preview;
        pipeline.Console = console;
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(320, 180, 60));
            await WaitUntil(() => console.Count >= 5);
            Assert.Equal(0, preview.Count);

            settings.Update(s => s with { DisplayMode = DisplayMode.App });
            int consoleBefore = console.Count;
            await WaitUntil(() => preview.Count >= 5);
            await Task.Delay(100);
            Assert.True(console.Count - consoleBefore <= 2, "Console stops receiving frames in App mode.");
        }
    }

    [Fact]
    public async Task StopIsPromptAndIdempotent()
    {
        var (pipeline, _, _) = CreatePipeline();
        pipeline.Preview = new CollectingRenderer();
        await pipeline.StartAsync(new SyntheticFrameSource(320, 180, 60));
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        await pipeline.StopAsync();
        await pipeline.StopAsync();
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Stop took {sw.ElapsedMilliseconds} ms");
        Assert.False(pipeline.IsRunning);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task SwitchingSourcesNeverMixesFrames()
    {
        var (pipeline, _, _) = CreatePipeline(AsciiSettings.Default with { Preset = QualityPreset.Performance });
        var preview = new CollectingRenderer();
        pipeline.Preview = preview;
        await using (pipeline)
        {
            for (int i = 0; i < 5; i++)
            {
                await pipeline.StartAsync(new SyntheticFrameSource(640, 360, 120));
                await Task.Delay(60);
            }

            int before = preview.Count;
            await pipeline.StartAsync(new SyntheticFrameSource(320, 320, 60)); // square → different grid
            await WaitUntil(() => preview.Count >= before + 10);
            await Task.Delay(100);
            await pipeline.StopAsync();
        }

        // After the last switch settles, only frames of the new source (40 rows for a square) arrive.
        var tail = preview.Snapshot().TakeLast(5).ToArray();
        Assert.All(tail, f => Assert.Equal(new GridSize(80, 40), f.Grid));
    }

    [Fact]
    public async Task CancelledStartLeavesPipelineStopped()
    {
        var (pipeline, _, _) = CreatePipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.StartAsync(new SyntheticFrameSource(), cts.Token));
        Assert.False(pipeline.IsRunning);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task FailingSourceStartCleansUp()
    {
        var (pipeline, _, _) = CreatePipeline();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StartAsync(new FailingSource()));
        Assert.False(pipeline.IsRunning);
        Assert.Null(pipeline.CurrentSource);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task SourceEndIsReported()
    {
        var (pipeline, _, _) = CreatePipeline();
        pipeline.Preview = new CollectingRenderer();
        var ended = new TaskCompletionSource<SourceEndedEventArgs>();
        pipeline.SourceEnded += (_, e) => ended.TrySetResult(e);
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(320, 180, 120, frameLimit: 10));
            var result = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SourceEndReason.Completed, result.Reason);
        }
    }

    [Fact]
    public async Task PauseHoldsFramesAndSettingsChangesStillRefresh()
    {
        var (pipeline, settings, _) = CreatePipeline();
        var preview = new CollectingRenderer();
        pipeline.Preview = preview;
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(320, 180, 60));
            await WaitUntil(() => preview.Count >= 3);
            pipeline.Pause();
            await Task.Delay(150);
            int paused = preview.Count;
            await Task.Delay(300);
            Assert.True(preview.Count - paused <= 1, "No new frames while paused.");

            settings.Update(s => s with { Style = RenderStyle.Outline });
            // The paused frame is re-converted with the new style (no new source frame needed).
            await WaitUntil(() => preview.Count > paused && preview.Snapshot()[^1].Chars.ToArray().Any(c => c is '|' or '/' or '\\' or '-' or '_'));
            pipeline.Resume();
            int resumed = preview.Count;
            await WaitUntil(() => preview.Count >= resumed + 5);
        }
    }

    [Fact]
    public async Task TargetFpsCapsProcessingRate()
    {
        var (pipeline, _, metrics) = CreatePipeline(AsciiSettings.Default with { TargetFps = 15, Preset = QualityPreset.Performance });
        pipeline.Preview = new CollectingRenderer();
        await using (pipeline)
        {
            await pipeline.StartAsync(new SyntheticFrameSource(320, 180, 120));
            await Task.Delay(1500);
            double processed = metrics.ProcessingRate.Rate;
            Assert.InRange(processed, 8, 19);
        }
    }

    [Fact]
    public async Task LatestFrameIsAvailableForScreenshotsAfterStop()
    {
        var (pipeline, _, _) = CreatePipeline();
        pipeline.Preview = new CollectingRenderer();
        await pipeline.StartAsync(new SyntheticFrameSource(320, 180, 60));
        await WaitUntil(() => pipeline.AcquireLatestFrame() is { } f && Dispose(f));
        await pipeline.StopAsync();
        using var latest = pipeline.AcquireLatestFrame();
        Assert.NotNull(latest);
        Assert.True(latest!.CellCount > 0);
        await pipeline.DisposeAsync();
    }

    private static bool Dispose(AsciiFrame frame)
    {
        frame.Dispose();
        return true;
    }

    private sealed class FailingSource : IFrameSource
    {
        public string DisplayName => "failing";
        public SourceKind Kind => SourceKind.TestPattern;
        public bool IsLive => true;
#pragma warning disable CS0067
        public event EventHandler<SourceEndedEventArgs>? Ended;
#pragma warning restore CS0067
        public Task StartAsync(IFrameSink sink, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
