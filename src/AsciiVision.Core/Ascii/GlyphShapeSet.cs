using System.Numerics;

namespace AsciiVision.Core.Ascii;

public enum ShapeNormalization
{
    /// <summary>Each region is scaled by its maximum over all glyphs (stretches rarely-inked regions).</summary>
    PerRegion,

    /// <summary>All regions share one scale: the largest coverage of any region of any glyph.</summary>
    Global,
}

/// <summary>
/// Shape vectors of a glyph set: for each character, the mean ink coverage (0..1) of every region of an
/// <see cref="SampleColumns"/> × <see cref="SampleRows"/> grid laid over its cell, row-major, normalized so the
/// 0..1 range of a cell's brightness maps onto the range of shapes the font can draw. Vectors are padded with
/// zeros to a SIMD-friendly stride.
/// </summary>
public sealed class GlyphShapeSet
{
    private readonly float[] _raw;

    public GlyphShapeSet(char[] characters, float[] rawVectors, int sampleColumns, int sampleRows,
        ShapeNormalization normalization = ShapeNormalization.Global)
    {
        int dimensions = sampleColumns * sampleRows;
        if (rawVectors.Length != characters.Length * dimensions)
        {
            throw new ArgumentException("Vector data does not match the character count and grid size.");
        }

        _raw = rawVectors;
        Characters = characters;
        SampleColumns = sampleColumns;
        SampleRows = sampleRows;
        Dimensions = dimensions;
        Normalization = normalization;
        int lane = Vector<float>.Count;
        Stride = (dimensions + lane - 1) / lane * lane;
        Vectors = new float[characters.Length * Stride];

        var max = new float[dimensions];
        for (int g = 0; g < characters.Length; g++)
        {
            for (int d = 0; d < dimensions; d++)
            {
                max[d] = Math.Max(max[d], rawVectors[g * dimensions + d]);
            }
        }

        if (normalization == ShapeNormalization.Global)
        {
            float globalMax = max.Length == 0 ? 0 : max.Max();
            Array.Fill(max, globalMax);
        }

        float densest = 0;
        for (int g = 0; g < characters.Length; g++)
        {
            float sum = 0;
            for (int d = 0; d < dimensions; d++)
            {
                float v = max[d] > 1e-6f ? rawVectors[g * dimensions + d] / max[d] : 0f;
                Vectors[g * Stride + d] = v;
                sum += v;
            }

            densest = Math.Max(densest, sum / dimensions);
        }

        DensityScale = densest;
    }

    /// <summary>
    /// Mean normalized coverage of the densest glyph. A cell's 0..1 brightness is scaled by this so that full
    /// brightness lands on the densest glyph and the whole range of glyph densities is used (otherwise every
    /// bright cell would saturate on the single densest character).
    /// </summary>
    public float DensityScale { get; }

    public char[] Characters { get; }

    /// <summary>Normalized vectors, <see cref="Stride"/> floats per glyph.</summary>
    public float[] Vectors { get; }

    public int SampleColumns { get; }
    public int SampleRows { get; }
    public int Dimensions { get; }
    public int Stride { get; }
    public ShapeNormalization Normalization { get; }
    public int Count => Characters.Length;

    /// <summary>A set restricted to <paramref name="characters"/> (those present in this set), re-normalized.</summary>
    public GlyphShapeSet Subset(string characters, ShapeNormalization? normalization = null)
    {
        var chars = new List<char>();
        var raw = new List<float>();
        foreach (char c in characters.Distinct())
        {
            int index = Array.IndexOf(Characters, c);
            if (index < 0)
            {
                continue;
            }

            chars.Add(c);
            raw.AddRange(_raw.AsSpan(index * Dimensions, Dimensions).ToArray());
        }

        if (chars.Count < 2)
        {
            throw new ArgumentException("A glyph set needs at least two characters.", nameof(characters));
        }

        return new GlyphShapeSet(chars.ToArray(), raw.ToArray(), SampleColumns, SampleRows, normalization ?? Normalization);
    }
}
