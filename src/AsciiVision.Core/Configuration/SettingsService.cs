using AsciiVision.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Core.Configuration;

/// <summary>
/// Holds the current settings snapshot and optionally persists it to a JSON file.
/// Updates are atomic reference swaps, so readers on worker threads never see a half-applied change.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string? _storagePath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private AsciiSettings _current;

    public SettingsService(string? storagePath = null, ILogger<SettingsService>? logger = null)
    {
        _storagePath = storagePath;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _current = LoadInitial();
    }

    public AsciiSettings Current => Volatile.Read(ref _current);

    /// <summary>Problems found while loading the persisted settings (shown once at start-up).</summary>
    public IReadOnlyList<string> LoadWarnings { get; private set; } = [];

    public event EventHandler<AsciiSettings>? Changed;

    public void Update(AsciiSettings settings) => Update(_ => settings);

    public void Update(Func<AsciiSettings, AsciiSettings> change)
    {
        AsciiSettings updated;
        lock (_gate)
        {
            var current = _current;
            updated = SettingsValidator.Sanitize(change(current));
            if (updated == current)
            {
                return;
            }

            Volatile.Write(ref _current, updated);
        }

        Changed?.Invoke(this, updated);
    }

    public void ResetToDefaults()
    {
        // Keep machine-specific values that are not "look" settings.
        var ffmpeg = Current.FfmpegPath;
        Update(AsciiSettings.Default with { FfmpegPath = ffmpeg });
    }

    public async Task SaveAsync()
    {
        if (_storagePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            var json = PresetSerializer.SerializeAppState(Current);
            var temp = _storagePath + ".tmp";
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Move(temp, _storagePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save settings to {Path}", _storagePath);
        }
    }

    public Task SavePresetAsync(string path) => PresetSerializer.SaveAsync(path, Current);

    public async Task<IReadOnlyList<string>> LoadPresetAsync(string path)
    {
        var warnings = new List<string>();
        var loaded = await PresetSerializer.LoadAsync(path, warnings, Current).ConfigureAwait(false);
        Update(loaded);
        return warnings;
    }

    private AsciiSettings LoadInitial()
    {
        if (_storagePath is null || !File.Exists(_storagePath))
        {
            return AsciiSettings.Default;
        }

        try
        {
            var warnings = new List<string>();
            var json = File.ReadAllText(_storagePath);
            var settings = PresetSerializer.Deserialize(json, warnings);
            LoadWarnings = warnings;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PresetFormatException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable settings file {Path}", _storagePath);
            LoadWarnings = ["Saved settings could not be read and were reset to defaults."];
            return AsciiSettings.Default;
        }
    }
}
