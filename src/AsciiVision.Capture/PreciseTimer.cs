using AsciiVision.Capture.Interop;

namespace AsciiVision.Capture;

/// <summary>
/// Sub-millisecond sleeping for frame pacing. The default Windows timer tick is ~15.6 ms,
/// which would make Thread.Sleep-based 60 FPS pacing stutter. A high-resolution waitable
/// timer (Windows 10 1803+) gives ~0.5 ms accuracy without changing the global timer rate;
/// older systems fall back to Thread.Sleep plus a short spin.
/// </summary>
public sealed class PreciseTimer : IDisposable
{
    private IntPtr _timer;

    public PreciseTimer()
    {
        _timer = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
    }

    public bool IsHighResolution => _timer != IntPtr.Zero;

    /// <summary>Sleeps for the given duration, or until <paramref name="ct"/> is cancelled (checked every 50 ms).</summary>
    public void Sleep(TimeSpan duration, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + duration;
        while (duration > TimeSpan.Zero && !ct.IsCancellationRequested)
        {
            var slice = duration > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : duration;
            SleepOnce(slice);
            duration = deadline - DateTime.UtcNow;
        }
    }

    private void SleepOnce(TimeSpan duration)
    {
        if (_timer != IntPtr.Zero)
        {
            long due = -duration.Ticks; // negative = relative, in 100 ns units
            if (NativeMethods.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                NativeMethods.WaitForSingleObject(_timer, 1000);
                return;
            }
        }

        var end = DateTime.UtcNow + duration;
        if (duration > TimeSpan.FromMilliseconds(2))
        {
            Thread.Sleep(duration - TimeSpan.FromMilliseconds(1.5));
        }

        while (DateTime.UtcNow < end)
        {
            Thread.SpinWait(50);
        }
    }

    public void Dispose()
    {
        var timer = Interlocked.Exchange(ref _timer, IntPtr.Zero);
        if (timer != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(timer);
        }
    }
}
