namespace AsciiVision.Core.Configuration;

/// <summary>
/// Range-checks settings coming from presets or the UI. Out-of-range numbers are clamped
/// (and reported) rather than rejected, so a slightly edited preset still loads.
/// </summary>
public static class SettingsValidator
{
    public const int MaxRampLength = 128;

    public static readonly (double Min, double Max) BrightnessRange = (-1.0, 1.0);
    public static readonly (double Min, double Max) ContrastRange = (0.1, 4.0);
    public static readonly (double Min, double Max) GammaRange = (0.2, 4.0);
    public static readonly (double Min, double Max) AspectRange = (1.0, 3.5);
    public static readonly (double Min, double Max) FontSizeRange = (5.0, 48.0);
    public static readonly (int Min, int Max) FpsRange = (1, 240);
    public static readonly (int Min, int Max) ColumnsRange = (16, 640);
    public static readonly (int Min, int Max) ThicknessRange = (1, 4);
    public static readonly (int Min, int Max) RecordingFpsRange = (10, 120);

    public static bool IsValidRamp(string? ramp, out string? error)
    {
        if (string.IsNullOrEmpty(ramp))
        {
            error = "The character ramp is empty.";
            return false;
        }

        if (ramp.Length < 2)
        {
            error = "The character ramp needs at least two characters (dense to sparse).";
            return false;
        }

        if (ramp.Length > MaxRampLength)
        {
            error = $"The character ramp is limited to {MaxRampLength} characters.";
            return false;
        }

        foreach (var c in ramp)
        {
            if (char.IsControl(c) || char.IsSurrogate(c))
            {
                error = "The character ramp may not contain control characters, tabs, line breaks or emoji.";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static AsciiSettings Sanitize(AsciiSettings settings, ICollection<string>? problems = null)
    {
        double Clamp(double value, (double Min, double Max) range, string name, double fallback)
        {
            if (!double.IsFinite(value))
            {
                problems?.Add($"{name} was not a number; using {fallback}.");
                return fallback;
            }

            if (value < range.Min || value > range.Max)
            {
                problems?.Add($"{name} {value} is outside {range.Min}–{range.Max}; it was clamped.");
                return Math.Clamp(value, range.Min, range.Max);
            }

            return value;
        }

        int ClampInt(int value, (int Min, int Max) range, string name)
        {
            if (value < range.Min || value > range.Max)
            {
                problems?.Add($"{name} {value} is outside {range.Min}–{range.Max}; it was clamped.");
                return Math.Clamp(value, range.Min, range.Max);
            }

            return value;
        }

        T EnumOr<T>(T value, T fallback, string name) where T : struct, Enum
        {
            if (Enum.IsDefined(value))
            {
                return value;
            }

            problems?.Add($"{name} has an unknown value; using {fallback}.");
            return fallback;
        }

        var d = AsciiSettings.Default;
        var ramp = settings.CharacterRamp;
        if (!IsValidRamp(ramp, out var rampError))
        {
            problems?.Add(rampError + " The default ramp was restored.");
            ramp = AsciiSettings.DefaultRamp;
        }

        var font = string.IsNullOrWhiteSpace(settings.FontFamily) ? AsciiSettings.DefaultFontFamily : settings.FontFamily.Trim();
        if (font.Length > 100)
        {
            problems?.Add("Font family name is too long; using the default.");
            font = AsciiSettings.DefaultFontFamily;
        }

        string? ffmpeg = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? null : settings.FfmpegPath.Trim();

        return settings with
        {
            Style = EnumOr(settings.Style, d.Style, "Style"),
            ColorMode = EnumOr(settings.ColorMode, d.ColorMode, "Color mode"),
            ForegroundColor = settings.ForegroundColor | 0xFF000000,
            BackgroundColor = settings.BackgroundColor | 0xFF000000,
            Preset = EnumOr(settings.Preset, d.Preset, "Density preset"),
            Columns = ClampInt(settings.Columns, ColumnsRange, "Columns"),
            CharacterAspect = Clamp(settings.CharacterAspect, AspectRange, "Character aspect", d.CharacterAspect),
            FontSize = Clamp(settings.FontSize, FontSizeRange, "Font size", d.FontSize),
            FontFamily = font,
            Brightness = Clamp(settings.Brightness, BrightnessRange, "Brightness", d.Brightness),
            Contrast = Clamp(settings.Contrast, ContrastRange, "Contrast", d.Contrast),
            Gamma = Clamp(settings.Gamma, GammaRange, "Gamma", d.Gamma),
            CharacterRamp = ramp,
            LocalContrast = Clamp(settings.LocalContrast, (0, 1), "Local contrast", d.LocalContrast),
            DetailCharacters = EnumOr(settings.DetailCharacters, d.DetailCharacters, "Detail characters"),
            DetailSharpness = Clamp(settings.DetailSharpness, (0, 1), "Detail sharpness", d.DetailSharpness),
            EdgeSensitivity = Clamp(settings.EdgeSensitivity, (0, 1), "Edge sensitivity", d.EdgeSensitivity),
            EdgeThickness = ClampInt(settings.EdgeThickness, ThicknessRange, "Edge thickness"),
            HybridBlend = Clamp(settings.HybridBlend, (0, 1), "Hybrid blend", d.HybridBlend),
            TargetFps = ClampInt(settings.TargetFps, FpsRange, "Target FPS"),
            DisplayMode = EnumOr(settings.DisplayMode, d.DisplayMode, "Display mode"),
            PreviewFit = EnumOr(settings.PreviewFit, d.PreviewFit, "Preview fit"),
            ConsoleSizeMode = EnumOr(settings.ConsoleSizeMode, d.ConsoleSizeMode, "Console size mode"),
            ConsoleColumns = ClampInt(settings.ConsoleColumns, ColumnsRange, "Console columns"),
            ConsoleColorDepth = EnumOr(settings.ConsoleColorDepth, d.ConsoleColorDepth, "Console color depth"),
            RecordingQuality = EnumOr(settings.RecordingQuality, d.RecordingQuality, "Recording quality"),
            RecordingFps = ClampInt(settings.RecordingFps, RecordingFpsRange, "Recording FPS"),
            FfmpegPath = ffmpeg,
        };
    }
}
