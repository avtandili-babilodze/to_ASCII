using System.Diagnostics;

namespace AsciiVision.Core.Diagnostics;

/// <summary>
/// Measures events per second over a sliding window using real elapsed time
/// (Stopwatch ticks), so the value reflects what actually happened rather than a target.
/// </summary>
public sealed class RateCounter
{
    private readonly long[] _ticks;
    private readonly long _windowTicks;
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private int _head;
    private int _count;
    private long _total;

    public RateCounter(TimeSpan? window = null, int capacity = 512, Func<long>? clock = null)
    {
        _ticks = new long[capacity];
        _windowTicks = (long)((window ?? TimeSpan.FromSeconds(1)).TotalSeconds * Stopwatch.Frequency);
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    public long Total => Interlocked.Read(ref _total);

    public void Tick()
    {
        long now = _clock();
        lock (_gate)
        {
            _ticks[_head] = now;
            _head = (_head + 1) % _ticks.Length;
            if (_count < _ticks.Length)
            {
                _count++;
            }
        }

        Interlocked.Increment(ref _total);
    }

    /// <summary>Events per second within the window ending now.</summary>
    public double Rate
    {
        get
        {
            long now = _clock();
            long cutoff = now - _windowTicks;
            lock (_gate)
            {
                int inWindow = 0;
                for (int i = 0; i < _count; i++)
                {
                    int index = (_head - 1 - i + _ticks.Length) % _ticks.Length;
                    if (_ticks[index] < cutoff)
                    {
                        break;
                    }

                    inWindow++;
                }

                return inWindow / (_windowTicks / (double)Stopwatch.Frequency);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _count = 0;
            _head = 0;
        }
    }
}

/// <summary>Exponential moving average for timings.</summary>
public sealed class MovingAverage
{
    private readonly double _alpha;
    private double _value;
    private bool _hasValue;
    private readonly object _gate = new();

    public MovingAverage(double alpha = 0.1)
    {
        _alpha = alpha;
    }

    public double Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    public void Add(double sample)
    {
        lock (_gate)
        {
            _value = _hasValue ? _value + _alpha * (sample - _value) : sample;
            _hasValue = true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _value = 0;
            _hasValue = false;
        }
    }
}
