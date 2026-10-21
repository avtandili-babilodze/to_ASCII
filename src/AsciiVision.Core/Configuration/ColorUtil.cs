using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsciiVision.Core.Configuration;

/// <summary>Helpers for 0xAARRGGBB colors.</summary>
public static class ColorUtil
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint FromRgb(int r, int g, int b) =>
        0xFF000000u | ((uint)Math.Clamp(r, 0, 255) << 16) | ((uint)Math.Clamp(g, 0, 255) << 8) | (uint)Math.Clamp(b, 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte R(uint argb) => (byte)(argb >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte G(uint argb) => (byte)(argb >> 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte B(uint argb) => (byte)argb;

    /// <summary>Rec. 709 luma in 0..255 using integer weights that sum to 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Luma(int r, int g, int b) => (r * 54 + g * 183 + b * 19) >> 8;

    public static int Luma(uint argb) => Luma(R(argb), G(argb), B(argb));

    public static bool IsDark(uint argb) => Luma(argb) < 128;

    public static string ToHex(uint argb) => $"#{R(argb):X2}{G(argb):X2}{B(argb):X2}";

    public static bool TryParseHex(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }

        if (s.Length == 8)
        {
            s = s[2..]; // ignore alpha in #AARRGGBB
        }

        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        argb = 0xFF000000u | rgb;
        return true;
    }
}

/// <summary>Serializes colors as "#RRGGBB" so preset files stay human-editable.</summary>
public sealed class HexColorJsonConverter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetUInt32(out var number))
        {
            return number | 0xFF000000;
        }

        var text = reader.GetString();
        if (ColorUtil.TryParseHex(text, out var argb))
        {
            return argb;
        }

        throw new JsonException($"'{text}' is not a valid color. Use the form #RRGGBB.");
    }

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ColorUtil.ToHex(value));
}
