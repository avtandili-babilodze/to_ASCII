using System.Diagnostics;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;
using OpenCvSharp;

namespace AsciiVision.Capture;

/// <summary>
/// An animated test pattern (gradients, a bouncing shaded ball, a rotating bar, outlines and text)
/// so the whole pipeline can be exercised and benchmarked without a video file or capture permission.
/// The rotating bar sweeps every edge orientation, which makes it a good check for Outline mode.
/// </summary>
public sealed class SyntheticFrameSource : IFrameSource
{
    private readonly int _width;
    private readonly int _height;
    private readonly double _fps;
    private readonly long? _frameLimit;
    private Thread? _thread;
    private volatile bool _stop;
    private IFrameSink? _sink;

    public SyntheticFrameSource(int width = 960, int height = 540, double fps = 60, long? frameLimit = null)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _frameLimit = frameLimit;
    }

    public string DisplayName => "Test pattern";

    public SourceKind Kind => SourceKind.TestPattern;

    public bool IsLive => true;

    public event EventHandler<SourceEndedEventArgs>? Ended;

    public Task StartAsync(IFrameSink sink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sink = sink;
        _thread = new Thread(Run) { IsBackground = true, Name = "ASCII Vision test pattern" };
        _thread.Start();
        return Task.CompletedTask;
    }

    private void Run()
    {
        using var timer = new PreciseTimer();
        long origin = Stopwatch.GetTimestamp();
        long index = 0;
        while (!_stop)
        {
            if (_frameLimit is { } limit && index >= limit)
            {
                Ended?.Invoke(this, new SourceEndedEventArgs(SourceEndReason.Completed, "Test pattern finished."));
                return;
            }

            var timestamp = TimeSpan.FromSeconds(index / _fps);
            var frame = Render(_width, _height, timestamp, index);
            index++;

            var wait = timestamp - Stopwatch.GetElapsedTime(origin);
            if (wait > TimeSpan.Zero)
            {
                timer.Sleep(wait);
            }

            var sink = _sink;
            if (sink is null || _stop)
            {
                frame.Dispose();
                return;
            }

            sink.OnFrame(frame);
        }
    }

    private static readonly Lazy<uint[]> GradientPalette = new(() =>
    {
        var table = new uint[1024];
        for (int i = 0; i < table.Length; i++)
        {
            double phase = i / 512.0;
            int r = (int)(30 + 150 * (0.5 + 0.5 * Math.Sin(phase * Math.PI)));
            int g = (int)(30 + 120 * (0.5 + 0.5 * Math.Sin((phase + 0.66) * Math.PI)));
            int b = (int)(50 + 170 * (0.5 + 0.5 * Math.Sin((phase + 1.33) * Math.PI)));
            table[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        return table;
    });

    /// <summary>Renders a single pattern frame (deterministic for a given timestamp).</summary>
    public static unsafe VideoFrame Render(int width, int height, TimeSpan timestamp, long index = 0)
    {
        var frame = VideoFrame.Rent(width, height, timestamp, index);
        double t = timestamp.TotalSeconds;

        fixed (byte* p = frame.Buffer)
        {
            // Diagonal color gradient that slowly drifts. The sine waves come from a lookup table
            // (one period = 1024 entries) so the pattern costs ~1 ms per frame instead of ~30 ms.
            var palette = GradientPalette.Value;
            double scale1024 = 1024 / 2.0; // sin(phase·π) has a period of 2 in phase units
            for (int y = 0; y < height; y++)
            {
                uint* row = (uint*)(p + y * frame.Stride);
                double rowPhase = y / (double)height * 1.3 + t * 0.25;
                double step = 2.2 / width * scale1024;
                double start = rowPhase * scale1024;
                for (int x = 0; x < width; x++)
                {
                    row[x] = palette[(int)(start + x * step) & 1023];
                }
            }

            using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC4, (IntPtr)p, frame.Stride);
            double scale = height / 540.0;

            // Rotating bar through the center: sweeps all orientations.
            var center = new Point(width / 2, height / 2);
            double angle = t * 0.6;
            int length = (int)(220 * scale);
            var a = new Point(center.X + (int)(Math.Cos(angle) * length), center.Y + (int)(Math.Sin(angle) * length));
            var bEnd = new Point(center.X - (int)(Math.Cos(angle) * length), center.Y - (int)(Math.Sin(angle) * length));
            Cv2.Line(mat, a, bEnd, new Scalar(240, 240, 240, 255), Math.Max(2, (int)(10 * scale)), LineTypes.AntiAlias);

            // Static outlined shapes.
            Cv2.Rectangle(mat, new Rect((int)(40 * scale), (int)(40 * scale), (int)(200 * scale), (int)(130 * scale)),
                new Scalar(80, 220, 255, 255), Math.Max(2, (int)(6 * scale)), LineTypes.AntiAlias);
            Cv2.Circle(mat, new Point(width - (int)(150 * scale), (int)(120 * scale)), (int)(80 * scale),
                new Scalar(255, 140, 60, 255), Math.Max(2, (int)(6 * scale)), LineTypes.AntiAlias);

            // Bouncing shaded ball (concentric fills approximate a radial gradient).
            double bx = 0.5 + 0.38 * Math.Sin(t * 1.3);
            double by = 0.5 + 0.32 * Math.Abs(Math.Sin(t * 2.1)) * -1 + 0.25;
            var ballCenter = new Point((int)(bx * width), (int)(by * height));
            int radius = (int)(70 * scale);
            for (int i = radius; i > 0; i -= Math.Max(1, radius / 14))
            {
                double k = 1 - i / (double)radius;
                Cv2.Circle(mat, ballCenter, i, new Scalar(40 + 200 * k, 60 + 160 * k, 200 + 55 * k, 255), -1, LineTypes.AntiAlias);
            }

            Cv2.PutText(mat, "ASCII VISION", new Point((int)(60 * scale), height - (int)(60 * scale)),
                HersheyFonts.HersheyDuplex, 2.2 * scale, new Scalar(255, 255, 255, 255), Math.Max(2, (int)(4 * scale)), LineTypes.AntiAlias);
            Cv2.PutText(mat, $"frame {index}", new Point(width - (int)(260 * scale), height - (int)(30 * scale)),
                HersheyFonts.HersheySimplex, 0.9 * scale, new Scalar(200, 200, 200, 255), Math.Max(1, (int)(2 * scale)), LineTypes.AntiAlias);
        }

        return frame;
    }

    public Task StopAsync()
    {
        _stop = true;
        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        _sink = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
