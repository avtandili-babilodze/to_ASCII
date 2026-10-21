using System.Numerics;
using AsciiVision.Core.Ascii;

namespace AsciiVision.Processing;

/// <summary>
/// Picks, for every cell, the glyph whose ink distribution best matches the cell's brightness pattern.
///
/// Each cell is sampled on the same small grid (e.g. 3 × 6) as the glyph shape vectors. A bright stroke along
/// the cell's left edge therefore matches '|' or '[' rather than just "medium brightness", a diagonal boundary
/// matches '/' or '\', a bright bottom matches '_' and so on, which gives contours sub-cell precision.
///
/// Before matching, each cell vector gets a contrast boost relative to its own maximum
/// (v' = max · (v / max)^e): this exaggerates the light/dark split inside the cell so shapes win over
/// uniform "average" glyphs, which is what makes edges, eyes and lips read clearly. Nearest neighbour search is
/// brute force over the ~95 glyphs with <see cref="Vector{T}"/> SIMD, parallel over rows.
/// </summary>
internal static unsafe class ShapeMatcher
{
    /// <summary>
    /// Brightness gain on top of the density scale. Glyphs cover only part of their cell, so a linear
    /// mapping renders too dark; 1.6 matched the source's tonal balance on photographic test images,
    /// letting only the brightest highlights saturate on the densest glyph.
    /// </summary>
    private const float Gain = 1.6f;

    /// <summary>Contrast exponent range: sharpness 0..1 maps to exponent 1..3.</summary>
    private const float ExponentRange = 2f;

    public static void Match(
        byte* gray, long grayStep, int columns, int rows, GlyphShapeSet shapes,
        byte[] tone, bool brightIsDense, double sharpness, char[] output)
    {
        float exponent = (float)(1.0 + ExponentRange * Math.Clamp(sharpness, 0, 1));
        var job = new Job((IntPtr)gray, grayStep, columns, shapes, tone, brightIsDense, exponent, output);
        if ((long)columns * rows >= 1500)
        {
            Parallel.For(0, rows, job.MatchRow);
        }
        else
        {
            for (int r = 0; r < rows; r++)
            {
                job.MatchRow(r);
            }
        }
    }

    private sealed class Job
    {
        private readonly IntPtr _gray;
        private readonly long _step;
        private readonly int _columns;
        private readonly GlyphShapeSet _shapes;
        private readonly byte[] _tone;
        private readonly bool _brightIsDense;
        private readonly float _exponent;
        private readonly char[] _output;

        public Job(IntPtr gray, long step, int columns, GlyphShapeSet shapes, byte[] tone, bool brightIsDense, float exponent, char[] output)
        {
            _gray = gray;
            _step = step;
            _columns = columns;
            _shapes = shapes;
            _tone = tone;
            _brightIsDense = brightIsDense;
            _exponent = exponent;
            _output = output;
        }

        public void MatchRow(int row)
        {
            int sx = _shapes.SampleColumns;
            int sy = _shapes.SampleRows;
            int dims = _shapes.Dimensions;
            int stride = _shapes.Stride;
            int lane = Vector<float>.Count;
            float[] glyphs = _shapes.Vectors;
            float scale = _shapes.DensityScale * Gain / 255f;
            int glyphCount = _shapes.Count;
            Span<float> cell = stackalloc float[stride];
            byte* gray = (byte*)_gray;

            for (int c = 0; c < _columns; c++)
            {
                float max = 0;
                for (int j = 0; j < sy; j++)
                {
                    byte* line = gray + (row * sy + j) * _step + c * sx;
                    for (int i = 0; i < sx; i++)
                    {
                        int t = _tone[line[i]];
                        float v = Math.Min(1f, (_brightIsDense ? t : 255 - t) * scale);

                        cell[j * sx + i] = v;
                        max = Math.Max(max, v);
                    }
                }

                if (max > 0.01f && _exponent != 1f)
                {
                    float inv = 1f / max;
                    for (int d = 0; d < dims; d++)
                    {
                        cell[d] = max * MathF.Pow(cell[d] * inv, _exponent);
                    }
                }

                int best = 0;
                float bestDistance = float.MaxValue;
                for (int g = 0; g < glyphCount; g++)
                {
                    var acc = Vector<float>.Zero;
                    int offset = g * stride;
                    for (int d = 0; d < stride; d += lane)
                    {
                        var diff = new Vector<float>(cell.Slice(d, lane)) - new Vector<float>(glyphs, offset + d);
                        acc += diff * diff;
                    }

                    float distance = Vector.Sum(acc);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = g;
                    }
                }

                _output[row * _columns + c] = _shapes.Characters[best];
            }
        }
    }
}
