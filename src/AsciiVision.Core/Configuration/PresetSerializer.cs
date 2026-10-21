using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsciiVision.Core.Configuration;

/// <summary>JSON (de)serialization of settings presets with validation.</summary>
public static class PresetSerializer
{
    public const int CurrentVersion = 1;
    public const long MaxPresetBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private sealed class PresetDocument
    {
        public string Format { get; set; } = "ascii-vision-preset";
        public int Version { get; set; } = CurrentVersion;
        public string? Name { get; set; }
        public AsciiSettings? Settings { get; set; }
    }

    public static string Serialize(AsciiSettings settings, string? name = null)
    {
        // Machine-specific paths do not belong in a shareable preset.
        var doc = new PresetDocument { Name = name, Settings = settings with { FfmpegPath = null } };
        return JsonSerializer.Serialize(doc, Options);
    }

    /// <summary>Serializes the full settings, including machine-specific values (for app state).</summary>
    public static string SerializeAppState(AsciiSettings settings) =>
        JsonSerializer.Serialize(new PresetDocument { Name = "app-state", Settings = settings }, Options);

    /// <summary>
    /// Parses and validates a preset. Throws <see cref="PresetFormatException"/> with a readable
    /// message if the document is not a usable preset. Clamped values are reported in <paramref name="warnings"/>.
    /// </summary>
    public static AsciiSettings Deserialize(string json, ICollection<string>? warnings = null, AsciiSettings? keepMachineValuesFrom = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PresetFormatException("The preset file is empty.");
        }

        PresetDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<PresetDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new PresetFormatException($"The preset file is not valid JSON or has a wrongly typed value ({ex.Message}).", ex);
        }

        if (doc is null || doc.Settings is null)
        {
            throw new PresetFormatException("The file does not contain ASCII Vision settings.");
        }

        if (!string.Equals(doc.Format, "ascii-vision-preset", StringComparison.OrdinalIgnoreCase))
        {
            throw new PresetFormatException($"Unknown preset format '{doc.Format}'.");
        }

        if (doc.Version > CurrentVersion)
        {
            warnings?.Add($"The preset was written by a newer version (v{doc.Version}); unknown options were ignored.");
        }

        var settings = SettingsValidator.Sanitize(doc.Settings, warnings);
        if (keepMachineValuesFrom is not null)
        {
            settings = settings with { FfmpegPath = keepMachineValuesFrom.FfmpegPath };
        }

        return settings;
    }

    public static async Task SaveAsync(string path, AsciiSettings settings, string? name = null, CancellationToken ct = default)
    {
        var json = Serialize(settings, name ?? Path.GetFileNameWithoutExtension(path));
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public static async Task<AsciiSettings> LoadAsync(string path, ICollection<string>? warnings = null, AsciiSettings? current = null, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new PresetFormatException("The preset file does not exist.");
        }

        if (info.Length > MaxPresetBytes)
        {
            throw new PresetFormatException("The file is too large to be an ASCII Vision preset.");
        }

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return Deserialize(json, warnings, current);
    }
}

public sealed class PresetFormatException : Exception
{
    public PresetFormatException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
