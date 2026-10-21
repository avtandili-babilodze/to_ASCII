namespace AsciiVision.App.ViewModels;

public enum SessionState
{
    Idle,
    Starting,
    Running,
    Paused,
    Stopping,
}

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A labelled value for combo boxes.</summary>
public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
