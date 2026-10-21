using System.Diagnostics;

namespace AsciiVision.Processing;

/// <summary>
/// Gradually lowers grid density when frames take longer than the target frame budget and
/// restores it when there is clear headroom.
///
/// Oscillation is avoided with hysteresis on three axes: separate thresholds (overloaded above
/// 85 % of the budget, headroom below 50 %), separate persistence requirements (2 vs 4 consecutive
/// one-second windows) and cooldowns after every change. Cost grows roughly with cell count
/// (scale²), so a +10 % step from 50 % load lands near 60 %, well under the overload threshold.
/// </summary>
public sealed class AdaptiveQualityController
{
    public const double MinScale = 0.4;
    public const int MinColumns = 40;
    public const double OverloadFraction = 0.85;
    public const double HeadroomFraction = 0.5;

    private readonly object _gate = new();
    private readonly TimeSpan _window = TimeSpan.FromSeconds(1);
    private readonly Func<TimeSpan> _clock;

    private double _scale = 1.0;
    private double _sum;
    private int _samples;
    private TimeSpan _windowStart;
    private TimeSpan _lastChange = TimeSpan.MinValue;
    private int _overloadStreak;
    private int _headroomStreak;

    public AdaptiveQualityController(Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(0));
        _windowStart = _clock();
    }

    public double Scale
    {
        get { lock (_gate) { return _scale; } }
    }

    public event EventHandler<double>? ScaleChanged;

    /// <summary>Columns to use for a request, never below <see cref="MinColumns"/> (or the request if smaller).</summary>
    public int EffectiveColumns(int requestedColumns)
    {
        double scale = Scale;
        int floor = Math.Min(requestedColumns, Math.Max(MinColumns, (int)Math.Ceiling(requestedColumns * MinScale)));
        return Math.Max(floor, (int)Math.Round(requestedColumns * scale));
    }

    /// <summary>Records the cost of one frame (the slowest concurrent stage) in milliseconds.</summary>
    public void Report(double frameCostMilliseconds, int targetFps)
    {
        double? changed = null;
        lock (_gate)
        {
            var now = _clock();
            _sum += frameCostMilliseconds;
            _samples++;
            if (now - _windowStart < _window)
            {
                return;
            }

            double average = _sum / _samples;
            double budget = 1000.0 / Math.Max(1, targetFps);
            _sum = 0;
            _samples = 0;
            _windowStart = now;

            _overloadStreak = average > budget * OverloadFraction ? _overloadStreak + 1 : 0;
            _headroomStreak = average < budget * HeadroomFraction ? _headroomStreak + 1 : 0;
            var sinceChange = _lastChange == TimeSpan.MinValue ? TimeSpan.MaxValue : now - _lastChange;

            if (_overloadStreak >= 2 && sinceChange >= TimeSpan.FromSeconds(2) && _scale > MinScale)
            {
                _scale = Math.Max(MinScale, _scale * 0.85);
                changed = _scale;
            }
            else if (_headroomStreak >= 4 && sinceChange >= TimeSpan.FromSeconds(4) && _scale < 1.0)
            {
                _scale = Math.Min(1.0, _scale * 1.1);
                changed = _scale;
            }

            if (changed is not null)
            {
                _lastChange = now;
                _overloadStreak = 0;
                _headroomStreak = 0;
            }
        }

        if (changed is { } value)
        {
            ScaleChanged?.Invoke(this, value);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _scale = 1.0;
            _sum = 0;
            _samples = 0;
            _overloadStreak = 0;
            _headroomStreak = 0;
            _windowStart = _clock();
            _lastChange = TimeSpan.MinValue;
        }
    }
}
