using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AsciiVision.App.ViewModels;

/// <summary>
/// Two-way mirror between the bindable properties and the immutable <see cref="AsciiSettings"/>.
/// Every change publishes a new snapshot immediately, so adjustments apply to the next frame
/// (or re-render the paused frame) without restarting the source.
/// </summary>
public sealed partial class MainViewModel
{
    private bool _loading;
    private bool _syncingPreset;

    public IReadOnlyList<Option<QualityPreset>> QualityPresets { get; } =
    [
        new("Performance — 80 columns", QualityPreset.Performance),
        new("Balanced — 120 columns", QualityPreset.Balanced),
        new("Quality — 160 columns", QualityPreset.Quality),
        new("Ultra — 240 columns", QualityPreset.Ultra),
        new("Custom", QualityPreset.Custom),
    ];

    public IReadOnlyList<Option<ConsoleSizeMode>> ConsoleSizeModes { get; } =
    [
        new("Fit terminal window", ConsoleSizeMode.FitTerminal),
        new("Same grid as preview", ConsoleSizeMode.MatchPreview),
        new("Custom columns", ConsoleSizeMode.Custom),
    ];

    public IReadOnlyList<Option<ConsoleColorDepth>> ConsoleColorDepths { get; } =
    [
        new("Auto (24-bit when supported)", ConsoleColorDepth.Auto),
        new("24-bit true color", ConsoleColorDepth.TrueColor),
        new("256 colors", ConsoleColorDepth.Palette256),
        new("16 colors", ConsoleColorDepth.Basic16),
    ];

    public IReadOnlyList<Option<EncodingQuality>> EncodingQualities { get; } =
    [
        new("High (CRF 18)", EncodingQuality.High),
        new("Balanced (CRF 23)", EncodingQuality.Balanced),
        new("Small file (CRF 28)", EncodingQuality.Small),
    ];

    public IReadOnlyList<int> RecordingFpsOptions { get; } = [24, 30, 60];

    public IReadOnlyList<Option<DetailCharacterSet>> DetailCharacterOptions { get; } =
    [
        new("Symbols (clean)", DetailCharacterSet.Symbols),
        new("Symbols + letters", DetailCharacterSet.SymbolsAndLetters),
        new("All ASCII", DetailCharacterSet.AllAscii),
    ];

    public IReadOnlyList<Option<string>> RampPresets { get; } =
    [
        new("Classic (10 levels)", AsciiSettings.DefaultRamp),
        new("Fine (70 levels)", AsciiSettings.FineRamp),
        new("Blocks", AsciiSettings.BlockRamp),
    ];

    public IReadOnlyList<string> ColorSwatches { get; } =
        ["#FFFFFF", "#33FF66", "#FFB000", "#4C8DFF", "#FF5C8A", "#B48CFF", "#00E5FF", "#000000", "#101418", "#1B2A1E", "#F4F1E8"];

    [ObservableProperty] private RenderStyle _style;
    [ObservableProperty] private ColorMode _colorMode;
    [ObservableProperty] private DisplayMode _displayMode;
    [ObservableProperty] private QualityPreset _preset;
    [ObservableProperty] private double _columns;
    [ObservableProperty] private double _characterAspect;
    [ObservableProperty] private bool _autoCharacterAspect;
    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private string _fontFamily = AsciiSettings.DefaultFontFamily;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _gamma;
    [ObservableProperty] private bool _invertRamp;
    [ObservableProperty] private double _localContrast;
    [ObservableProperty] private double _detailSharpness;
    [ObservableProperty] private DetailCharacterSet _detailCharacters;
    [ObservableProperty] private string _characterRamp = AsciiSettings.DefaultRamp;
    [ObservableProperty] private string? _rampError;
    [ObservableProperty] private double _edgeSensitivity;
    [ObservableProperty] private double _edgeThickness;
    [ObservableProperty] private double _hybridBlend;
    [ObservableProperty] private double _targetFps;
    [ObservableProperty] private bool _adaptiveQuality;
    [ObservableProperty] private string _foregroundHex = "#FFB000";
    [ObservableProperty] private string _backgroundHex = "#000000";
    [ObservableProperty] private bool _colorBackgroundFill;
    [ObservableProperty] private PreviewFit _previewFit;
    [ObservableProperty] private ConsoleSizeMode _consoleSizeMode;
    [ObservableProperty] private double _consoleColumns;
    [ObservableProperty] private ConsoleColorDepth _consoleColorDepth;
    [ObservableProperty] private EncodingQuality _recordingQuality;
    [ObservableProperty] private int _recordingFps;
    [ObservableProperty] private bool _excludeAppFromCapture;
    [ObservableProperty] private bool _captureCursor;

    /// <summary>Optional PNG screenshot background (empty = same as the preview).</summary>
    [ObservableProperty] private string _screenshotBackgroundHex = string.Empty;

    public bool CanExcludeFromCapture => Capture.CaptureSupport.CanExcludeFromCapture;

    public bool CanToggleCursor => Capture.CaptureSupport.CanToggleCursor;

    /// <summary>Raised when the "hide from capture" option changes (the window applies it to its handle).</summary>
    public event EventHandler<bool>? ExcludeFromCaptureChanged;

    private void LoadFromSettings(AsciiSettings s)
    {
        _loading = true;
        try
        {
            Style = s.Style;
            ColorMode = s.ColorMode;
            DisplayMode = Core.Configuration.DisplayMode.App; // the console window opens on demand, never at start-up
            Preset = s.Preset;
            Columns = s.EffectiveColumns;
            CharacterAspect = s.CharacterAspect;
            AutoCharacterAspect = s.AutoCharacterAspect;
            FontSize = s.FontSize;
            FontFamily = s.FontFamily;
            Brightness = s.Brightness;
            Contrast = s.Contrast;
            Gamma = s.Gamma;
            InvertRamp = s.InvertRamp;
            LocalContrast = s.LocalContrast;
            DetailSharpness = s.DetailSharpness;
            DetailCharacters = s.DetailCharacters;
            CharacterRamp = s.CharacterRamp;
            RampError = null;
            EdgeSensitivity = s.EdgeSensitivity;
            EdgeThickness = s.EdgeThickness;
            HybridBlend = s.HybridBlend;
            TargetFps = s.TargetFps;
            AdaptiveQuality = s.AdaptiveQuality;
            ForegroundHex = ColorUtil.ToHex(s.ForegroundColor);
            BackgroundHex = ColorUtil.ToHex(s.BackgroundColor);
            ColorBackgroundFill = s.ColorBackgroundFill;
            PreviewFit = s.PreviewFit;
            ConsoleSizeMode = s.ConsoleSizeMode;
            ConsoleColumns = s.ConsoleColumns;
            ConsoleColorDepth = s.ConsoleColorDepth;
            RecordingQuality = s.RecordingQuality;
            RecordingFps = s.RecordingFps;
            ExcludeAppFromCapture = s.ExcludeAppFromCapture;
            CaptureCursor = s.CaptureCursor;
        }
        finally
        {
            _loading = false;
        }

        // Persisted display mode is forced to App at start-up; make the settings agree.
        _settings.Update(x => x with { DisplayMode = DisplayMode });
        ExcludeFromCaptureChanged?.Invoke(this, ExcludeAppFromCapture);
    }

    private void Push(Func<AsciiSettings, AsciiSettings> change)
    {
        if (!_loading)
        {
            _settings.Update(change);
        }
    }

    partial void OnStyleChanged(RenderStyle value) => Push(s => s with { Style = value });

    partial void OnColorModeChanged(ColorMode value) => Push(s => s with { ColorMode = value });

    partial void OnDisplayModeChanged(DisplayMode value)
    {
        Push(s => s with { DisplayMode = value });
        if (!_loading)
        {
            _ = SyncConsoleWithDisplayModeAsync();
        }
    }

    partial void OnPresetChanged(QualityPreset value)
    {
        if (value != QualityPreset.Custom)
        {
            _syncingPreset = true;
            Columns = GridCalculator.ColumnsForPreset(value, 0);
            _syncingPreset = false;
        }

        Push(s => s with { Preset = value, Columns = (int)Math.Round(Columns) });
    }

    partial void OnColumnsChanged(double value)
    {
        int columns = (int)Math.Round(value);
        if (!_syncingPreset && !_loading && Preset != QualityPreset.Custom && GridCalculator.ColumnsForPreset(Preset, 0) != columns)
        {
            _syncingPreset = true;
            Preset = QualityPreset.Custom;
            _syncingPreset = false;
        }

        Push(s => s with { Columns = columns, Preset = Preset });
    }

    partial void OnCharacterAspectChanged(double value) => Push(s => s with { CharacterAspect = Math.Round(value, 2) });

    partial void OnAutoCharacterAspectChanged(bool value) => Push(s => s with { AutoCharacterAspect = value });

    partial void OnFontSizeChanged(double value) => Push(s => s with { FontSize = Math.Round(value * 2) / 2 });

    partial void OnFontFamilyChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Push(s => s with { FontFamily = value });
        }
    }

    partial void OnBrightnessChanged(double value) => Push(s => s with { Brightness = Math.Round(value, 2) });

    partial void OnContrastChanged(double value) => Push(s => s with { Contrast = Math.Round(value, 2) });

    partial void OnGammaChanged(double value) => Push(s => s with { Gamma = Math.Round(value, 2) });

    partial void OnInvertRampChanged(bool value) => Push(s => s with { InvertRamp = value });

    partial void OnLocalContrastChanged(double value) => Push(s => s with { LocalContrast = Math.Round(value, 2) });

    partial void OnDetailSharpnessChanged(double value) => Push(s => s with { DetailSharpness = Math.Round(value, 2) });

    partial void OnDetailCharactersChanged(DetailCharacterSet value) => Push(s => s with { DetailCharacters = value });

    partial void OnCharacterRampChanged(string value)
    {
        // Invalid ramps (e.g. while the user is still typing) are reported but not applied.
        if (SettingsValidator.IsValidRamp(value, out var error))
        {
            RampError = null;
            Push(s => s with { CharacterRamp = value });
        }
        else
        {
            RampError = error;
        }
    }

    partial void OnEdgeSensitivityChanged(double value) => Push(s => s with { EdgeSensitivity = Math.Round(value, 2) });

    partial void OnEdgeThicknessChanged(double value) => Push(s => s with { EdgeThickness = (int)Math.Round(value) });

    partial void OnHybridBlendChanged(double value) => Push(s => s with { HybridBlend = Math.Round(value, 2) });

    partial void OnTargetFpsChanged(double value) => Push(s => s with { TargetFps = (int)Math.Round(value) });

    partial void OnAdaptiveQualityChanged(bool value) => Push(s => s with { AdaptiveQuality = value });

    partial void OnForegroundHexChanged(string value)
    {
        if (ColorUtil.TryParseHex(value, out var color))
        {
            Push(s => s with { ForegroundColor = color });
        }
    }

    partial void OnBackgroundHexChanged(string value)
    {
        if (ColorUtil.TryParseHex(value, out var color))
        {
            Push(s => s with { BackgroundColor = color });
        }
    }

    partial void OnColorBackgroundFillChanged(bool value) => Push(s => s with { ColorBackgroundFill = value });

    partial void OnPreviewFitChanged(PreviewFit value) => Push(s => s with { PreviewFit = value });

    partial void OnConsoleSizeModeChanged(ConsoleSizeMode value) => Push(s => s with { ConsoleSizeMode = value });

    partial void OnConsoleColumnsChanged(double value) => Push(s => s with { ConsoleColumns = (int)Math.Round(value) });

    partial void OnConsoleColorDepthChanged(ConsoleColorDepth value) => Push(s => s with { ConsoleColorDepth = value });

    partial void OnRecordingQualityChanged(EncodingQuality value) => Push(s => s with { RecordingQuality = value });

    partial void OnRecordingFpsChanged(int value) => Push(s => s with { RecordingFps = value });

    partial void OnExcludeAppFromCaptureChanged(bool value)
    {
        Push(s => s with { ExcludeAppFromCapture = value });
        ExcludeFromCaptureChanged?.Invoke(this, value);
    }

    private void OverrideCaptureExclusion(bool excluded) => ExcludeFromCaptureChanged?.Invoke(this, excluded);

    partial void OnCaptureCursorChanged(bool value) => Push(s => s with { CaptureCursor = value });

    [RelayCommand]
    private void SetForeground(string hex) => ForegroundHex = hex;

    [RelayCommand]
    private void SetBackground(string hex) => BackgroundHex = hex;

    [RelayCommand]
    private void ResetRamp() => CharacterRamp = AsciiSettings.DefaultRamp;

    [RelayCommand]
    private void SetRamp(string ramp) => CharacterRamp = ramp;

    [RelayCommand]
    private void ResetDefaults()
    {
        _settings.ResetToDefaults();
        var mode = DisplayMode;
        LoadFromSettings(_settings.Current with { DisplayMode = mode });
        DisplayMode = mode;
        Notify("Settings were reset to defaults.", NotificationSeverity.Info);
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        var path = _dialogs.SaveFile("Save preset", "ASCII Vision preset (*.json)|*.json", "ascii-preset.json", PresetDirectory());
        if (path is null)
        {
            return;
        }

        try
        {
            await _settings.SavePresetAsync(path);
            Notify($"Preset saved: {Path.GetFileName(path)}", NotificationSeverity.Success, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("The preset could not be saved: " + ex.Message, NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task LoadPresetAsync()
    {
        var path = _dialogs.OpenFile("Load preset", "ASCII Vision preset (*.json)|*.json|All files (*.*)|*.*", PresetDirectory());
        if (path is null)
        {
            return;
        }

        try
        {
            var mode = DisplayMode;
            var warnings = await _settings.LoadPresetAsync(path);
            LoadFromSettings(_settings.Current with { DisplayMode = mode });
            DisplayMode = mode;
            if (warnings.Count == 0)
            {
                Notify($"Preset loaded: {Path.GetFileName(path)}", NotificationSeverity.Success);
            }
            else
            {
                Notify($"Preset loaded with adjustments: {string.Join(" ", warnings.Take(3))}", NotificationSeverity.Warning);
            }
        }
        catch (PresetFormatException ex)
        {
            Notify($"\"{Path.GetFileName(path)}\" is not a valid preset: {ex.Message}", NotificationSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("The preset could not be read: " + ex.Message, NotificationSeverity.Error);
        }
    }

    private static string PresetDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ASCII Vision", "Presets");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return dir;
    }
}
