using System.Globalization;
using AsciiVision.Core.Configuration;

namespace AsciiVision.App;

/// <summary>
/// Optional command-line automation, used for demos and unattended smoke tests, e.g.
/// <c>AsciiVision.exe --source test --display both --start --exit-after 10 --metrics-log m.jsonl</c>.
/// </summary>
public sealed record CommandLineOptions
{
    public string? Source { get; init; }
    public bool Start { get; init; }
    public DisplayMode? Display { get; init; }
    public RenderStyle? Style { get; init; }
    public ColorMode? Color { get; init; }
    public string? RecordPath { get; init; }
    public string? ScreenshotPath { get; init; }
    public double? ExitAfterSeconds { get; init; }
    public string? MetricsLogPath { get; init; }

    /// <summary>Turns off "hide from capture" so the window can be screen-recorded (e.g. for documentation).</summary>
    public bool ShowInCapture { get; init; }

    public bool HasAutomation => Source is not null || Start || ExitAfterSeconds is not null || ShowInCapture || Display is not null;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : string.Empty;
            switch (args[i].ToLowerInvariant())
            {
                case "--source":
                    options = options with { Source = Next() };
                    break;
                case "--start":
                    options = options with { Start = true };
                    break;
                case "--display":
                    options = options with { Display = Enum.TryParse<DisplayMode>(Next(), true, out var d) ? d : null };
                    break;
                case "--style":
                    options = options with { Style = Enum.TryParse<RenderStyle>(Next(), true, out var s) ? s : null };
                    break;
                case "--color":
                    options = options with { Color = Enum.TryParse<ColorMode>(Next(), true, out var c) ? c : null };
                    break;
                case "--record":
                    options = options with { RecordPath = Next() };
                    break;
                case "--screenshot":
                    options = options with { ScreenshotPath = Next() };
                    break;
                case "--exit-after":
                    options = options with
                    {
                        ExitAfterSeconds = double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : null,
                    };
                    break;
                case "--show-in-capture":
                    options = options with { ShowInCapture = true };
                    break;
                case "--metrics-log":
                    options = options with { MetricsLogPath = Next() };
                    break;
            }
        }

        return options;
    }
}
