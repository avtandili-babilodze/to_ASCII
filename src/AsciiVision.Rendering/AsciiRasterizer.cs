using System.Runtime.CompilerServices;
using AsciiVision.Core.Ascii;
using AsciiVision.Rendering.Glyphs;

namespace AsciiVision.Rendering;

/// <summary>
/// Draws an <see cref="AsciiFrame"/> into a BGRA32 buffer by blending pre-rasterized glyph
/// coverage masks between each cell's foreground and background color. This is the single
/// rendering path for the WPF preview, PNG screenshots, live recording and MP4 export, so all
/// outputs look identical. Rows of cells are rendered in parallel (they write disjoint memory).
/// </summary>
public static unsafe class AsciiRasterizer
{
    public static (int Width, int Height) Measure(GridSize grid, GlyphAtlas atlas) =>
        (grid.Columns * atlas.CellWidth, grid.Rows * atlas.CellHeight);

    /// <summary>Fills the destination with one color.</summary>
    public static void Clear(Span<byte> destination, int width, int height, int stride, uint color)
    {
        for (int y = 0; y < height; y++)
        {
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination.Slice(y * stride, width * 4));
            row.Fill(color);
        }
    }

    /// <summary>
    /// Renders <paramref name="frame"/> with its top-left cell at (<paramref name="offsetX"/>, <paramref name="offsetY"/>).
    /// Cells outside the destination are clipped. Pixels not covered by the grid are left untouched
    /// unless <paramref name="clearFirst"/> is set, in which case they get the frame's default background.
    /// </summary>
    public static void Render(AsciiFrame frame, GlyphAtlas atlas, Span<byte> destination, int width, int height, int stride,
        int offsetX = 0, int offsetY = 0, bool clearFirst = false, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(atlas);
        if (destination.Length < (long)stride * height)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        if (clearFirst)
        {
            Clear(destination, width, height, stride, frame.DefaultBackground | 0xFF000000);
        }

        fixed (byte* dst = destination)
        {
            var job = new RenderJob(frame, atlas, (IntPtr)dst, width, height, stride, offsetX, offsetY);
            if (parallel && frame.Rows > 4)
            {
                Parallel.For(0, frame.Rows, job.RenderRow);
            }
            else
            {
                for (int r = 0; r < frame.Rows; r++)
                {
                    job.RenderRow(r);
                }
            }
        }
    }

    private sealed class RenderJob
    {
        private readonly AsciiFrame _frame;
        private readonly GlyphAtlas _atlas;
        private readonly IntPtr _destination;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;
        private readonly int _offsetX;
        private readonly int _offsetY;

        public RenderJob(AsciiFrame frame, GlyphAtlas atlas, IntPtr destination, int width, int height, int stride, int offsetX, int offsetY)
        {
            _frame = frame;
            _atlas = atlas;
            _destination = destination;
            _width = width;
            _height = height;
            _stride = stride;
            _offsetX = offsetX;
            _offsetY = offsetY;
        }

        public void RenderRow(int row)
        {
            int cellW = _atlas.CellWidth;
            int cellH = _atlas.CellHeight;
            int top = _offsetY + row * cellH;
            int yStart = Math.Max(0, -top);
            int yEnd = Math.Min(cellH, _height - top);
            if (yEnd <= yStart)
            {
                return;
            }

            int cols = _frame.Columns;
            var chars = _frame.Chars;
            var fg = _frame.Foreground;
            var bg = _frame.Background;
            bool cellBackground = _frame.HasCellBackground;
            uint defaultBg = _frame.DefaultBackground | 0xFF000000;
            byte* dst = (byte*)_destination;

            for (int c = 0; c < cols; c++)
            {
                int left = _offsetX + c * cellW;
                int xStart = Math.Max(0, -left);
                int xEnd = Math.Min(cellW, _width - left);
                if (xEnd <= xStart)
                {
                    if (left >= _width)
                    {
                        break;
                    }

                    continue;
                }

                int i = row * cols + c;
                uint fore = fg[i] | 0xFF000000;
                uint back = cellBackground ? bg[i] | 0xFF000000 : defaultBg;
                int glyph = _atlas.GetGlyphIndex(chars[i]);

                if (_atlas.IsBlank(glyph) || fore == back)
                {
                    FillRect(dst, left, top, xStart, xEnd, yStart, yEnd, back);
                    continue;
                }

                if (_atlas.IsSolid(glyph))
                {
                    FillRect(dst, left, top, xStart, xEnd, yStart, yEnd, fore);
                    continue;
                }

                var coverage = _atlas.GetCoverage(glyph);
                int fr = (int)(fore >> 16) & 0xFF, fgc = (int)(fore >> 8) & 0xFF, fb = (int)fore & 0xFF;
                int br = (int)(back >> 16) & 0xFF, bgc = (int)(back >> 8) & 0xFF, bb = (int)back & 0xFF;

                for (int y = yStart; y < yEnd; y++)
                {
                    uint* pixel = (uint*)(dst + (long)(top + y) * _stride) + left;
                    int maskRow = y * cellW;
                    for (int x = xStart; x < xEnd; x++)
                    {
                        int a = coverage[maskRow + x];
                        if (a == 0)
                        {
                            pixel[x] = back;
                        }
                        else if (a == 255)
                        {
                            pixel[x] = fore;
                        }
                        else
                        {
                            int ia = 255 - a;
                            uint r = (uint)Div255(fr * a + br * ia);
                            uint g = (uint)Div255(fgc * a + bgc * ia);
                            uint b = (uint)Div255(fb * a + bb * ia);
                            pixel[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
                        }
                    }
                }
            }
        }

        private void FillRect(byte* dst, int left, int top, int xStart, int xEnd, int yStart, int yEnd, uint color)
        {
            for (int y = yStart; y < yEnd; y++)
            {
                uint* pixel = (uint*)(dst + (long)(top + y) * _stride) + left + xStart;
                new Span<uint>(pixel, xEnd - xStart).Fill(color);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Div255(int value) => (value + 128 + ((value + 128) >> 8)) >> 8;
    }
}
