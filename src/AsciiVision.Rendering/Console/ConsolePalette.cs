namespace AsciiVision.Rendering.Console;

/// <summary>Color quantization for terminals without 24-bit color.</summary>
public static class ConsolePalette
{
    /// <summary>
    /// The Windows "Campbell" default palette, indexed by console attribute order
    /// (0 black, 1 blue, 2 green, 3 cyan, 4 red, 5 magenta, 6 yellow, 7 white, 8–15 bright variants).
    /// </summary>
    private static readonly (int R, int G, int B)[] Campbell =
    [
        (12, 12, 12), (0, 55, 218), (19, 161, 14), (58, 150, 221),
        (197, 15, 31), (136, 23, 152), (193, 156, 0), (204, 204, 204),
        (118, 118, 118), (59, 120, 255), (22, 198, 12), (97, 214, 214),
        (231, 72, 86), (180, 0, 158), (249, 241, 165), (242, 242, 242),
    ];

    /// <summary>ANSI SGR color number (0–7, +8 for bright) for each console attribute index.</summary>
    private static readonly int[] AttributeToAnsi = [0, 4, 2, 6, 1, 5, 3, 7, 8, 12, 10, 14, 9, 13, 11, 15];

    /// <summary>Nearest console attribute color (0–15) by squared RGB distance.</summary>
    public static int NearestAttribute(uint argb)
    {
        int r = (int)(argb >> 16) & 0xFF, g = (int)(argb >> 8) & 0xFF, b = (int)argb & 0xFF;
        int best = 0;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < Campbell.Length; i++)
        {
            var c = Campbell[i];
            int dr = r - c.R, dg = g - c.G, db = b - c.B;
            // Weighted toward green, roughly matching perceived brightness.
            int d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>ANSI color index (0–15) for an SGR 30–37/90–97 sequence.</summary>
    public static int NearestAnsi16(uint argb) => AttributeToAnsi[NearestAttribute(argb)];

    /// <summary>Nearest xterm-256 index from the 6×6×6 cube or the 24-step gray ramp.</summary>
    public static int NearestXterm256(uint argb)
    {
        int r = (int)(argb >> 16) & 0xFF, g = (int)(argb >> 8) & 0xFF, b = (int)argb & 0xFF;

        static int CubeIndex(int v) => v < 48 ? 0 : v < 115 ? 1 : (v - 35) / 40;
        static int CubeValue(int i) => i == 0 ? 0 : 55 + i * 40;

        int ri = CubeIndex(r), gi = CubeIndex(g), bi = CubeIndex(b);
        int cr = CubeValue(ri), cg = CubeValue(gi), cb = CubeValue(bi);
        int cubeDistance = (r - cr) * (r - cr) + (g - cg) * (g - cg) + (b - cb) * (b - cb);

        int average = (r + g + b) / 3;
        int grayIndex = average > 238 ? 23 : Math.Max(0, (average - 3) / 10);
        int gray = 8 + grayIndex * 10;
        int grayDistance = (r - gray) * (r - gray) + (g - gray) * (g - gray) + (b - gray) * (b - gray);

        return grayDistance < cubeDistance ? 232 + grayIndex : 16 + 36 * ri + 6 * gi + bi;
    }
}
