namespace AsciiVision.Processing;

/// <summary>
/// Precomputed 256-entry lookup tables for brightness/contrast/gamma and luminance→ramp index.
/// Rebuilt only when the relevant settings change, so per-cell work is two array reads.
/// </summary>
public sealed class ToneMapping
{
    private ToneMapping(byte[] tone, byte[] rampIndex, string ramp, bool brightIsDense)
    {
        Tone = tone;
        RampIndexFromToned = rampIndex;
        Ramp = ramp;
        BrightIsDense = brightIsDense;
    }

    /// <summary>Adjusted value for an input channel/luma value.</summary>
    public byte[] Tone { get; }

    /// <summary>Ramp index for an already tone-adjusted luminance.</summary>
    public byte[] RampIndexFromToned { get; }

    public string Ramp { get; }

    /// <summary>True on dark backgrounds: bright pixels get the densest glyphs.</summary>
    public bool BrightIsDense { get; }

    public static byte AdjustValue(int value, double brightness, double contrast, double gamma)
    {
        double x = value / 255.0;
        x = (x - 0.5) * contrast + 0.5 + brightness;
        x = Math.Clamp(x, 0, 1);
        if (Math.Abs(gamma - 1.0) > 1e-6)
        {
            // gamma > 1 brightens mid-tones, matching the usual "gamma" slider convention.
            x = Math.Pow(x, 1.0 / gamma);
        }

        return (byte)Math.Clamp((int)Math.Round(x * 255), 0, 255);
    }

    /// <summary>
    /// Maps a tone-adjusted luminance to an index into a dense→sparse ramp using equal-width bins.
    /// </summary>
    public static int RampIndex(int tonedLuma, int rampLength, bool brightIsDense)
    {
        int density = brightIsDense ? tonedLuma : 255 - tonedLuma; // 255 = wants densest glyph
        int index = (255 - density) * rampLength / 256;
        return Math.Clamp(index, 0, rampLength - 1);
    }

    public static ToneMapping Create(string ramp, double brightness, double contrast, double gamma, bool brightIsDense)
    {
        if (string.IsNullOrEmpty(ramp))
        {
            throw new ArgumentException("Ramp must not be empty.", nameof(ramp));
        }

        var tone = new byte[256];
        var rampIndex = new byte[256];
        int length = Math.Min(ramp.Length, 256);
        for (int v = 0; v < 256; v++)
        {
            tone[v] = AdjustValue(v, brightness, contrast, gamma);
            rampIndex[v] = (byte)RampIndex(v, length, brightIsDense);
        }

        return new ToneMapping(tone, rampIndex, ramp, brightIsDense);
    }
}
