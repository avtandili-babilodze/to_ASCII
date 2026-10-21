using System.Windows;
using System.Windows.Media;

namespace AsciiVision.Rendering.Glyphs;

/// <summary>Size-independent cell metrics of a (monospaced) font, in em units.</summary>
public sealed record FontCellMetrics(string FamilyName, double AdvanceEm, double LineSpacingEm, double BaselineEm)
{
    public const string FallbackFamily = "Consolas";

    /// <summary>Unrounded cell height / width.</summary>
    public double Aspect => LineSpacingEm / AdvanceEm;

    public (int Width, int Height) CellSize(double sizePx) =>
        (Math.Max(1, (int)Math.Round(AdvanceEm * sizePx)), Math.Max(1, (int)Math.Round(LineSpacingEm * sizePx)));

    /// <summary>
    /// Largest font size (in 0.5 px steps, between <paramref name="minSize"/> and <paramref name="maxSize"/>)
    /// whose cell grid fits in the given pixel area.
    /// </summary>
    public double FitFontSize(int columns, int rows, int availableWidth, int availableHeight, double minSize, double maxSize)
    {
        if (columns <= 0 || rows <= 0 || availableWidth <= 0 || availableHeight <= 0)
        {
            return minSize;
        }

        double size = Math.Min(availableWidth / (columns * AdvanceEm), availableHeight / (rows * LineSpacingEm));
        size = Math.Floor(size * 2) / 2;
        size = Math.Clamp(size, minSize, maxSize);
        // Rounding of the cell size can push the grid over the limit; step down until it fits.
        while (size > minSize)
        {
            var (w, h) = CellSize(size);
            if (w * columns <= availableWidth && h * rows <= availableHeight)
            {
                break;
            }

            size -= 0.5;
        }

        return size;
    }

    /// <summary>Measures a font family. Must run on an STA thread (see <see cref="WpfGlyphThread"/>).</summary>
    internal static (FontCellMetrics Metrics, Typeface Typeface, GlyphTypeface GlyphTypeface) Measure(string familyName)
    {
        foreach (var name in new[] { familyName, FallbackFamily, "Courier New" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var family = new FontFamily(name);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
            {
                continue;
            }

            // A family that silently fell back to another font would report a different name.
            bool resolved = glyphTypeface.FamilyNames.Values.Concat(glyphTypeface.Win32FamilyNames.Values)
                .Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase) || n.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (!resolved)
            {
                continue;
            }

            if (!glyphTypeface.CharacterToGlyphMap.TryGetValue('M', out var glyph))
            {
                continue;
            }

            double advance = glyphTypeface.AdvanceWidths[glyph];
            double lineSpacing = family.LineSpacing > 0 ? family.LineSpacing : glyphTypeface.Height;
            double baseline = family.Baseline > 0 ? family.Baseline : glyphTypeface.Baseline;
            if (advance <= 0 || lineSpacing <= 0)
            {
                continue;
            }

            return (new FontCellMetrics(name, advance, lineSpacing, baseline), typeface, glyphTypeface);
        }

        throw new InvalidOperationException("No usable monospaced font was found (tried the configured font, Consolas and Courier New).");
    }

    /// <summary>Installed font families whose glyphs share one advance width. Call on an STA thread.</summary>
    public static IReadOnlyList<string> GetMonospacedFamilies()
    {
        var result = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!typeface.TryGetGlyphTypeface(out var gt))
                {
                    continue;
                }

                var map = gt.CharacterToGlyphMap;
                if (!map.TryGetValue('i', out var gi) || !map.TryGetValue('M', out var gm) || !map.TryGetValue('@', out var ga))
                {
                    continue;
                }

                double i = gt.AdvanceWidths[gi], m = gt.AdvanceWidths[gm], a = gt.AdvanceWidths[ga];
                if (Math.Abs(i - m) < 0.001 && Math.Abs(a - m) < 0.001 && !gt.Symbol)
                {
                    result.Add(family.Source);
                }
            }
            catch (Exception)
            {
                // Broken font files should not break the list.
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
