using AsciiVision.Core.Configuration;

namespace AsciiVision.Tests;

public class SettingsValidationTests
{
    [Fact]
    public void OutOfRangeValuesAreClampedAndReported()
    {
        var problems = new List<string>();
        var sanitized = SettingsValidator.Sanitize(AsciiSettings.Default with
        {
            Columns = 5,
            Contrast = 50,
            Gamma = -1,
            TargetFps = 0,
            EdgeThickness = 99,
            HybridBlend = 7,
            Brightness = double.NaN,
        }, problems);

        Assert.Equal(SettingsValidator.ColumnsRange.Min, sanitized.Columns);
        Assert.Equal(SettingsValidator.ContrastRange.Max, sanitized.Contrast);
        Assert.Equal(SettingsValidator.GammaRange.Min, sanitized.Gamma);
        Assert.Equal(SettingsValidator.FpsRange.Min, sanitized.TargetFps);
        Assert.Equal(SettingsValidator.ThicknessRange.Max, sanitized.EdgeThickness);
        Assert.Equal(1.0, sanitized.HybridBlend);
        Assert.Equal(0.0, sanitized.Brightness);
        Assert.True(problems.Count >= 7);
    }

    [Fact]
    public void ValidSettingsAreUnchanged()
    {
        var problems = new List<string>();
        var sanitized = SettingsValidator.Sanitize(AsciiSettings.Default, problems);
        Assert.Equal(AsciiSettings.Default, sanitized);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("ab\tc")]
    [InlineData("ab\nc")]
    public void InvalidRampsAreRejected(string ramp)
    {
        Assert.False(SettingsValidator.IsValidRamp(ramp, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        var sanitized = SettingsValidator.Sanitize(AsciiSettings.Default with { CharacterRamp = ramp });
        Assert.Equal(AsciiSettings.DefaultRamp, sanitized.CharacterRamp);
    }

    [Fact]
    public void UnicodeRampsAreAllowed()
    {
        Assert.True(SettingsValidator.IsValidRamp("█▓▒░ ", out _));
    }

    [Fact]
    public void UnknownEnumValuesFallBack()
    {
        var sanitized = SettingsValidator.Sanitize(AsciiSettings.Default with { Style = (RenderStyle)42, ColorMode = (ColorMode)(-3) });
        Assert.Equal(AsciiSettings.Default.Style, sanitized.Style);
        Assert.Equal(ColorMode.White, sanitized.ColorMode);
    }

    [Theory]
    [InlineData("#FF8800", 0xFFFF8800u)]
    [InlineData("ff8800", 0xFFFF8800u)]
    [InlineData("#f80", 0xFFFF8800u)]
    [InlineData("#80FF8800", 0xFFFF8800u)]
    public void HexColorsParse(string text, uint expected)
    {
        Assert.True(ColorUtil.TryParseHex(text, out var color));
        Assert.Equal(expected, color);
        Assert.Equal("#FF8800", ColorUtil.ToHex(color));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("red")]
    public void InvalidHexColorsFail(string text) => Assert.False(ColorUtil.TryParseHex(text, out _));

    [Fact]
    public void SettingsServiceRaisesChangedOnlyOnRealChanges()
    {
        var service = new SettingsService();
        int changes = 0;
        service.Changed += (_, _) => changes++;
        service.Update(s => s with { Columns = 150 });
        service.Update(s => s with { Columns = 150 });
        Assert.Equal(1, changes);
        Assert.Equal(150, service.Current.Columns);
    }

    [Fact]
    public void ResetKeepsFfmpegPath()
    {
        var service = new SettingsService();
        service.Update(s => s with { FfmpegPath = @"C:\tools\ffmpeg.exe", Columns = 300 });
        service.ResetToDefaults();
        Assert.Equal(@"C:\tools\ffmpeg.exe", service.Current.FfmpegPath);
        Assert.Equal(AsciiSettings.Default.Columns, service.Current.Columns);
    }

    [Fact]
    public async Task SettingsPersistAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", Guid.NewGuid() + ".json");
        var first = new SettingsService(path);
        first.Update(s => s with { Style = RenderStyle.Hybrid, ForegroundColor = 0xFF00AAFF, FfmpegPath = @"D:\x\ffmpeg.exe" });
        await first.SaveAsync();

        var second = new SettingsService(path);
        Assert.Equal(RenderStyle.Hybrid, second.Current.Style);
        Assert.Equal(0xFF00AAFFu, second.Current.ForegroundColor);
        Assert.Equal(@"D:\x\ffmpeg.exe", second.Current.FfmpegPath);
    }

    [Fact]
    public void CorruptSettingsFileFallsBackToDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var service = new SettingsService(path);
        Assert.Equal(AsciiSettings.Default, service.Current);
        Assert.NotEmpty(service.LoadWarnings);
    }
}

public class PresetSerializationTests
{
    [Fact]
    public void RoundTripPreservesSettings()
    {
        var settings = AsciiSettings.Default with
        {
            Style = RenderStyle.Outline,
            ColorMode = ColorMode.Custom,
            ForegroundColor = 0xFF11AA33,
            BackgroundColor = 0xFF202020,
            CharacterRamp = "█▓▒░ ",
            Columns = 222,
            Preset = QualityPreset.Custom,
            Gamma = 1.4,
            DisplayMode = DisplayMode.Both,
        };

        var json = PresetSerializer.Serialize(settings, "test");
        var loaded = PresetSerializer.Deserialize(json);
        Assert.Equal(settings, loaded);
        Assert.Contains("\"#11AA33\"", json);
        Assert.Contains("\"outline\"", json);
    }

    [Fact]
    public void MachineSpecificPathIsNotWrittenToPresets()
    {
        var json = PresetSerializer.Serialize(AsciiSettings.Default with { FfmpegPath = @"C:\secret\ffmpeg.exe" });
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void LoadingKeepsCurrentFfmpegPath()
    {
        var json = PresetSerializer.Serialize(AsciiSettings.Default);
        var loaded = PresetSerializer.Deserialize(json, null, AsciiSettings.Default with { FfmpegPath = "keep.exe" });
        Assert.Equal("keep.exe", loaded.FfmpegPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"something-else\",\"settings\":{}}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"settings\":{\"style\":\"sparkly\"}}")]
    [InlineData("{\"settings\":{\"columns\":\"many\"}}")]
    [InlineData("{\"settings\":{\"foregroundColor\":\"blue\"}}")]
    public void InvalidPresetsAreRejectedWithReadableErrors(string json)
    {
        var ex = Assert.Throws<PresetFormatException>(() => PresetSerializer.Deserialize(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void OutOfRangePresetValuesAreClampedWithWarnings()
    {
        var warnings = new List<string>();
        var loaded = PresetSerializer.Deserialize("{\"settings\":{\"columns\":5000,\"gamma\":0.01,\"characterRamp\":\"\"}}", warnings);
        Assert.Equal(SettingsValidator.ColumnsRange.Max, loaded.Columns);
        Assert.Equal(SettingsValidator.GammaRange.Min, loaded.Gamma);
        Assert.Equal(AsciiSettings.DefaultRamp, loaded.CharacterRamp);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void PartialPresetsKeepDefaultsForMissingValues()
    {
        var loaded = PresetSerializer.Deserialize("{ \"settings\": { \"style\": \"hybrid\" } }");
        Assert.Equal(RenderStyle.Hybrid, loaded.Style);
        Assert.Equal(AsciiSettings.Default.Columns, loaded.Columns);
    }

    [Fact]
    public async Task PresetFilesRoundTripThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", "presets with spaces ü");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "my preset.json");
        var service = new SettingsService();
        service.Update(s => s with { Style = RenderStyle.Hybrid, HybridBlend = 0.8 });
        await service.SavePresetAsync(path);

        var other = new SettingsService();
        var warnings = await other.LoadPresetAsync(path);
        Assert.Empty(warnings);
        Assert.Equal(RenderStyle.Hybrid, other.Current.Style);
        Assert.Equal(0.8, other.Current.HybridBlend);
    }

    [Fact]
    public async Task OversizedPresetFilesAreRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "AsciiVisionTests", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, new string(' ', (int)PresetSerializer.MaxPresetBytes + 10));
        await Assert.ThrowsAsync<PresetFormatException>(() => PresetSerializer.LoadAsync(path));
    }
}
