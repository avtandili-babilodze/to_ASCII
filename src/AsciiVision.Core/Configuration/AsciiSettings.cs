using System.Text.Json.Serialization;

namespace AsciiVision.Core.Configuration;

/// <summary>
/// Immutable snapshot of every user-adjustable option. The UI publishes a new instance on
/// each change and worker threads read the current reference once per frame, so no locking
/// is needed and a frame is always converted with one consistent set of values.
/// </summary>
public sealed record AsciiSettings
{
    public const string DefaultRamp = "@%#*+=-:. ";

    /// <summary>A long ramp (Paul Bourke's 70 levels) for smoother tones in Standard and Hybrid styles.</summary>
    public const string FineRamp = @"$@B%8&WM#*oahkbdpqwmZO0QLCJUYXzcvunxrjft/\|()1{}[]?-_+~<>i!lI;:,""^`'. ";

    public const string BlockRamp = "█▓▒░ ";

    public const string DefaultFontFamily = "Consolas";

    public static AsciiSettings Default { get; } = new();

    // Style
    public RenderStyle Style { get; init; } = RenderStyle.Detailed;
    public ColorMode ColorMode { get; init; } = ColorMode.White;

    [JsonConverter(typeof(HexColorJsonConverter))]
    public uint ForegroundColor { get; init; } = 0xFFFFB000; // custom monochrome (amber)

    [JsonConverter(typeof(HexColorJsonConverter))]
    public uint BackgroundColor { get; init; } = 0xFF000000;

    /// <summary>Original-colors mode only: tint each cell's background with a dimmed source color.</summary>
    public bool ColorBackgroundFill { get; init; }

    // Grid
    public QualityPreset Preset { get; init; } = QualityPreset.Balanced;
    public int Columns { get; init; } = 120;

    /// <summary>Cell height / width. Used when <see cref="AutoCharacterAspect"/> is off, and always for the console.</summary>
    public double CharacterAspect { get; init; } = 2.0;
    public bool AutoCharacterAspect { get; init; } = true;
    public double FontSize { get; init; } = 12;
    public string FontFamily { get; init; } = DefaultFontFamily;

    // Tone
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1.0;
    public double Gamma { get; init; } = 1.0;
    public bool InvertRamp { get; init; }

    /// <summary>Local contrast enhancement (CLAHE) strength, 0 = off. Brings out faces and texture in flat or dark areas.</summary>
    public double LocalContrast { get; init; } = 0.4;

    /// <summary>Detailed style: how strongly each cell's internal light/dark pattern is exaggerated before matching glyph shapes.</summary>
    public double DetailSharpness { get; init; } = 0.5;

    /// <summary>Detailed style: which characters may be used.</summary>
    public DetailCharacterSet DetailCharacters { get; init; } = DetailCharacterSet.Symbols;

    public string CharacterRamp { get; init; } = DefaultRamp;

    // Edges
    public double EdgeSensitivity { get; init; } = 0.5;
    public int EdgeThickness { get; init; } = 1;
    public double HybridBlend { get; init; } = 0.5;

    // Performance
    public int TargetFps { get; init; } = 60;
    public bool AdaptiveQuality { get; init; }

    // Output
    public DisplayMode DisplayMode { get; init; } = DisplayMode.App;
    public PreviewFit PreviewFit { get; init; } = PreviewFit.Fit;
    public ConsoleSizeMode ConsoleSizeMode { get; init; } = ConsoleSizeMode.FitTerminal;
    public int ConsoleColumns { get; init; } = 120;
    public ConsoleColorDepth ConsoleColorDepth { get; init; } = ConsoleColorDepth.Auto;

    // Capture
    public bool CaptureCursor { get; init; } = true;
    public bool ExcludeAppFromCapture { get; init; } = true;

    // Recording / export
    public EncodingQuality RecordingQuality { get; init; } = EncodingQuality.Balanced;
    public int RecordingFps { get; init; } = 30;

    /// <summary>Explicit ffmpeg.exe location; null means auto-detect.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Columns actually requested, after resolving the density preset.</summary>
    [JsonIgnore]
    public int EffectiveColumns => Ascii.GridCalculator.ColumnsForPreset(Preset, Columns);

    /// <summary>The ramp used for conversion (falls back to the default when invalid).</summary>
    [JsonIgnore]
    public string EffectiveRamp => SettingsValidator.IsValidRamp(CharacterRamp, out _) ? CharacterRamp : DefaultRamp;

    /// <summary>The single foreground color for the monochrome modes.</summary>
    [JsonIgnore]
    public uint MonochromeForeground => ColorMode switch
    {
        ColorMode.White => 0xFFFFFFFF,
        ColorMode.Green => 0xFF33FF66,
        _ => ForegroundColor | 0xFF000000,
    };
}
