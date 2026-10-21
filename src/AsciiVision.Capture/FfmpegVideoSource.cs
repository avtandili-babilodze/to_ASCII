using System.Diagnostics;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Imaging;
using AsciiVision.Core.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Capture;

/// <summary>
/// Plays a video file by piping raw BGRA frames out of an ffmpeg child process.
///
/// ffmpeg is asked for constant-frame-rate output (fps filter), so frame <c>i</c> after a seek to
/// <c>s</c> has timestamp <c>s + i / fps</c>. A dedicated thread paces frames against a wall clock
/// anchored to those timestamps (high-resolution timer); frames that are more than two intervals
/// late are skipped so playback stays synchronized instead of drifting. Pausing simply stops
/// reading: the pipe fills and ffmpeg blocks, costing no CPU. Seeking restarts ffmpeg with input
/// seeking (-ss before -i), which is frame-accurate when transcoding.
/// </summary>
public sealed class FfmpegVideoSource : IFrameSource, IPlaybackControl
{
    private readonly FfmpegInstallation _ffmpeg;
    private readonly MediaInfo _info;
    private readonly PerformanceMetrics? _metrics;
    private readonly ILogger _logger;
    private readonly int _width;
    private readonly int _height;
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly object _gate = new();

    private Thread? _thread;
    private IFrameSink? _sink;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private long _positionTicks;
    private long _seekTicks = -1;
    private FfmpegProcess? _process;
    private int _endedRaised;

    public FfmpegVideoSource(FfmpegInstallation ffmpeg, MediaInfo info, int maxDecodeWidth = 1280,
        PerformanceMetrics? metrics = null, ILogger? logger = null)
    {
        _ffmpeg = ffmpeg;
        _info = info;
        _metrics = metrics;
        _logger = logger ?? NullLogger.Instance;
        (_width, _height) = FfmpegArguments.DecodeSize(info.Width, info.Height, maxDecodeWidth);
        DisplayName = Path.GetFileName(info.Path);
    }

    public static async Task<FfmpegVideoSource> OpenAsync(FfmpegInstallation ffmpeg, string path, PerformanceMetrics? metrics = null,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.ProbeAsync(ffmpeg, path, cancellationToken).ConfigureAwait(false);
        return new FfmpegVideoSource(ffmpeg, info, metrics: metrics, logger: logger);
    }

    public MediaInfo Info => _info;

    public string DisplayName { get; }

    public SourceKind Kind => SourceKind.Video;

    public bool IsLive => false;

    public TimeSpan Duration => _info.Duration;

    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));

    public double FrameRate => _info.FrameRate;

    public bool IsPaused => _paused;

    public bool Loop { get; set; }

    /// <summary>True when an HDR transfer was detected and the tone-mapping filters were applied.</summary>
    public bool IsToneMapping { get; private set; }

    public event EventHandler<SourceEndedEventArgs>? Ended;

    public Task StartAsync(IFrameSink sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_thread is not null)
            {
                throw new InvalidOperationException("Playback already started.");
            }

            _sink = sink;
            _thread = new Thread(PlaybackThread) { IsBackground = true, Name = "ASCII Vision video decode" };
            _thread.Start();
        }

        return Task.CompletedTask;
    }

    public void Pause()
    {
        _paused = true;
        _resume.Reset();
    }

    public void Resume()
    {
        _paused = false;
        _resume.Set();
    }

    public void Seek(TimeSpan position)
    {
        if (_info.Duration > TimeSpan.Zero)
        {
            position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, _info.Duration.Ticks - TimeSpan.FromSeconds(1 / FrameRate).Ticks)));
        }
        else if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        Interlocked.Exchange(ref _seekTicks, position.Ticks);
        Interlocked.Exchange(ref _positionTicks, position.Ticks);
        lock (_gate)
        {
            _process?.Kill(); // unblocks a pending pipe read; the loop restarts at the new position
        }

        // A paused loop polls for seeks (see DecodeFrom), so the resume event stays untouched here.
    }

    private void PlaybackThread()
    {
        using var timer = new PreciseTimer();
        double fps = _info.FrameRate;
        var frameInterval = TimeSpan.FromSeconds(1.0 / fps);
        var start = TimeSpan.Zero;

        try
        {
            while (!_stopRequested)
            {
                long seek = Interlocked.Exchange(ref _seekTicks, -1);
                if (seek >= 0)
                {
                    start = TimeSpan.FromTicks(seek);
                }

                var outcome = DecodeFrom(start, fps, frameInterval, timer, out var error);
                switch (outcome)
                {
                    case DecodeOutcome.Seek:
                        continue;
                    case DecodeOutcome.Stopped:
                        return;
                    case DecodeOutcome.Failed:
                        RaiseEnded(new SourceEndedEventArgs(SourceEndReason.Failed, error ?? "Video decoding failed."));
                        return;
                    case DecodeOutcome.EndOfStream:
                        if (Loop && !_stopRequested)
                        {
                            start = TimeSpan.Zero;
                            continue;
                        }

                        RaiseEnded(new SourceEndedEventArgs(SourceEndReason.Completed, "Playback finished."));
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video playback thread failed");
            RaiseEnded(new SourceEndedEventArgs(SourceEndReason.Failed, "Video playback failed: " + ex.Message, ex));
        }
    }

    private enum DecodeOutcome
    {
        EndOfStream,
        Seek,
        Stopped,
        Failed,
    }

    private DecodeOutcome DecodeFrom(TimeSpan start, double fps, TimeSpan frameInterval, PreciseTimer timer, out string? error)
    {
        error = null;
        var args = FfmpegArguments.DecodeToRawBgra(_info, _ffmpeg.Capabilities, start, fps, _width, _height, out bool toneMapped);
        IsToneMapping = toneMapped;
        FfmpegProcess process;
        lock (_gate)
        {
            if (_stopRequested)
            {
                return DecodeOutcome.Stopped;
            }

            process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectInput: false, redirectOutput: true);
            _process = process;
        }

        try
        {
            var stream = process.StandardOutput!;
            long frameIndex = 0;
            bool showNextWhilePaused = _paused; // after a seek while paused, show the new position once
            long clockOrigin = Stopwatch.GetTimestamp();
            var clockBase = start;

            while (true)
            {
                if (_stopRequested)
                {
                    return DecodeOutcome.Stopped;
                }

                if (Interlocked.Read(ref _seekTicks) >= 0)
                {
                    return DecodeOutcome.Seek;
                }

                var timestamp = start + TimeSpan.FromTicks((long)(frameIndex * TimeSpan.TicksPerSecond / fps));
                var frame = VideoFrame.Rent(_width, _height, timestamp, frameIndex);
                bool complete;
                try
                {
                    complete = ReadFully(stream, frame.Data);
                }
                catch (IOException)
                {
                    complete = false;
                }

                if (!complete)
                {
                    frame.Dispose();
                    if (_stopRequested)
                    {
                        return DecodeOutcome.Stopped;
                    }

                    if (Interlocked.Read(ref _seekTicks) >= 0)
                    {
                        return DecodeOutcome.Seek;
                    }

                    int exit = process.WaitForExit(TimeSpan.FromSeconds(5)) ? process.ExitCode : -1;
                    if (exit != 0 && frameIndex == 0)
                    {
                        error = FfmpegException.FromProcess("Decoding the video", process, exit).Message;
                        return DecodeOutcome.Failed;
                    }

                    return DecodeOutcome.EndOfStream;
                }

                frameIndex++;
                if (frameIndex == 1)
                {
                    // Anchor the clock at the first decoded frame so start-up latency is not counted as lag.
                    clockOrigin = Stopwatch.GetTimestamp();
                    clockBase = timestamp;
                }

                if (showNextWhilePaused)
                {
                    showNextWhilePaused = false;
                    Publish(frame);
                    frame = null;
                }

                if (_paused)
                {
                    // Hold here; resuming re-anchors the clock to the held frame.
                    while (_paused && !_stopRequested && Interlocked.Read(ref _seekTicks) < 0)
                    {
                        _resume.Wait(30);
                    }

                    if (_stopRequested || Interlocked.Read(ref _seekTicks) >= 0)
                    {
                        frame?.Dispose();
                        continue;
                    }

                    clockOrigin = Stopwatch.GetTimestamp();
                    clockBase = timestamp;
                }

                if (frame is null)
                {
                    continue;
                }

                var playbackClock = clockBase + Stopwatch.GetElapsedTime(clockOrigin);
                var lateBy = playbackClock - timestamp;
                if (lateBy > frameInterval * 2)
                {
                    // Decoding or the machine fell behind: skip to stay in sync with the timeline.
                    frame.Dispose();
                    _metrics?.AddDropped();
                    Interlocked.Exchange(ref _positionTicks, timestamp.Ticks);
                    continue;
                }

                if (lateBy < TimeSpan.Zero)
                {
                    timer.Sleep(-lateBy);
                }

                Publish(frame);
            }
        }
        finally
        {
            lock (_gate)
            {
                _process = null;
            }

            process.Dispose();
        }
    }

    private void Publish(VideoFrame frame)
    {
        Interlocked.Exchange(ref _positionTicks, frame.Timestamp.Ticks);
        var sink = _sink;
        if (sink is null)
        {
            frame.Dispose();
            return;
        }

        sink.OnFrame(frame);
    }

    private static bool ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private void RaiseEnded(SourceEndedEventArgs e)
    {
        if (Interlocked.Exchange(ref _endedRaised, 1) == 0 && !_stopRequested)
        {
            Ended?.Invoke(this, e);
        }
    }

    public Task StopAsync()
    {
        Thread? thread;
        lock (_gate)
        {
            _stopRequested = true;
            _process?.Kill();
            thread = _thread;
            _thread = null;
        }

        _resume.Set();
        if (thread is not null && thread != Thread.CurrentThread)
        {
            return Task.Run(() =>
            {
                if (!thread.Join(TimeSpan.FromSeconds(5)))
                {
                    _logger.LogWarning("Video decode thread did not stop within 5 seconds");
                }

                _sink = null;
            });
        }

        _sink = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _resume.Dispose();
    }
}
