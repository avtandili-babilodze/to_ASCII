using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;
using OpenCvSharp;

namespace AsciiVision.Processing;

/// <summary>
/// Converts BGRA frames to <see cref="AsciiFrame"/>s.
///
/// Sampling: an INTER_AREA resize to (columns × rows) yields the exact mean color of every cell
/// in one optimized OpenCV call. Edge styles additionally resize to a working image with
/// k × k2 pixels per cell (square source pixels), run Canny + Sobel there, and aggregate the
/// per-pixel structure tensor back to cell resolution with the same box-averaging resize,
/// so the whole edge analysis is a handful of vectorized OpenCV passes plus one loop over cells.
///
/// The Detailed style samples a small grid (e.g. 3 x 6) inside every cell and matches that brightness
/// pattern against the measured shapes of the font's glyphs (<see cref="ShapeMatcher"/>), so characters
/// are chosen by where the light is inside the cell, not only by how much there is.
///
/// Local contrast (CLAHE) can be applied to the luminance used for character selection in every style.
///
/// All Mats are members and reused between frames; the class is single-threaded by design.
/// </summary>
public sealed unsafe class AsciiProcessor : IAsciiProcessor
{
    private const double MaxEdgeWorkPixels = 450_000;

    /// <summary>Maximum brightening of glyph colors in Original-colors mode on dark backgrounds.</summary>
    private const float ColorBoostLimit = 1.8f;

    /// <summary>
    /// Shape-sampling regions across a cell in the Detailed style (rows follow the cell aspect, so 3 × 6 for
    /// typical fonts). Four columns over-favour thin vertical glyphs; three matched faces and contours best.
    /// </summary>
    private const int DetailSampleColumns = 3;

    private readonly AsciiFramePool _pool;
    private readonly IGlyphShapeProvider? _shapes;

    private readonly Mat _cells = new();
    private readonly List<Mat> _pyramid = new();
    private readonly Mat _work = new();
    private readonly Mat _gray = new();
    private readonly Mat _blur = new();
    private readonly Mat _edges = new();
    private readonly Mat _edgesThick = new();
    private readonly Mat _gx = new();
    private readonly Mat _gy = new();
    private readonly Mat _mask = new();
    private readonly Mat _product = new();
    private readonly Mat _cellGxx = new();
    private readonly Mat _cellGyy = new();
    private readonly Mat _cellGxy = new();
    private readonly Mat _cellDensity = new();
    private readonly Mat _cellCentroid = new();
    private readonly Mat _cellLuma = new();
    private readonly Mat _detail = new();
    private readonly Mat _detailGray = new();
    private readonly Mat _contrastTemp = new();
    private CLAHE? _clahe;
    private char[] _detailChars = [];

    private Mat? _yLocal;
    private Size _yLocalSize;
    private int _yLocalK2;
    private Mat? _dilateKernel;
    private int _kernelThickness;

    private ToneMapping? _tone;
    private (string Ramp, double B, double C, double G, bool BrightIsDense) _toneKey;
    private long _sequence;
    private bool _disposed;

    /// <param name="shapes">Glyph shapes for the Detailed style; without it Detailed falls back to Standard.</param>
    public AsciiProcessor(AsciiFramePool? pool = null, IGlyphShapeProvider? shapes = null)
    {
        _pool = pool ?? new AsciiFramePool();
        _shapes = shapes;
    }

    /// <summary>Pixels per cell (horizontal, vertical) used for the last edge analysis.</summary>
    public (int K, int K2) LastEdgeSampling { get; private set; }

    public AsciiFrame Process(VideoFrame frame, AsciiSettings settings, GridSize grid)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (grid.IsEmpty)
        {
            throw new ArgumentException("Grid must not be empty.", nameof(grid));
        }

        int cols = grid.Columns;
        int rows = grid.Rows;
        var tone = GetToneMapping(settings);
        bool detailed = settings.Style == RenderStyle.Detailed && _shapes is not null;
        bool needEdges = settings.Style is RenderStyle.Outline or RenderStyle.Hybrid;
        int k = 0, k2 = 0;

        fixed (byte* pixels = frame.Buffer)
        {
            using var src = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, (IntPtr)pixels, frame.Stride);
            if (detailed)
            {
                MatchShapes(src, cols, rows, settings, tone);
            }
            else if (needEdges)
            {
                (k, k2) = ComputeEdgeMaps(src, cols, rows, settings);
            }
            else
            {
                AreaDownsample(src, new Size(cols, rows), _cells);
            }
        }

        // Luminance per cell for ramp shading, with optional local contrast enhancement.
        Cv2.CvtColor(_cells, _cellLuma, ColorConversionCodes.BGRA2GRAY);
        if (!detailed)
        {
            ApplyLocalContrast(_cellLuma, settings.LocalContrast);
        }

        var output = _pool.Rent(cols, rows);
        try
        {
            output.Timestamp = frame.Timestamp;
            output.Sequence = ++_sequence;
            output.SourceWidth = frame.Width;
            output.SourceHeight = frame.Height;
            output.DefaultBackground = settings.BackgroundColor | 0xFF000000;
            FillCells(output, settings, tone, needEdges, k, k2, detailed ? _detailChars : null);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private ToneMapping GetToneMapping(AsciiSettings s)
    {
        bool darkBackground = ColorUtil.IsDark(s.BackgroundColor);
        bool brightIsDense = darkBackground ^ s.InvertRamp;
        var key = (s.EffectiveRamp, s.Brightness, s.Contrast, s.Gamma, brightIsDense);
        if (_tone is null || key != _toneKey)
        {
            _tone = ToneMapping.Create(key.EffectiveRamp, key.Brightness, key.Contrast, key.Gamma, brightIsDense);
            _toneKey = key;
        }

        return _tone;
    }

    private (int K, int K2) ComputeEdgeMaps(Mat src, int cols, int rows, AsciiSettings s)
    {
        // Pixels per cell in the working image. Never upsample beyond the source and keep
        // working pixels square so measured angles are true image angles.
        // Large grids lower the pixels per cell so the working image stays around 450k pixels;
        // 3–4 pixels across a cell are still enough to resolve its dominant edge orientation.
        double cellAspect = (src.Height / (double)rows) / (src.Width / (double)cols);
        int k = Math.Clamp(src.Width / cols, 2, 6);
        while (k > 2 && (double)cols * k * rows * Math.Round(k * cellAspect) > MaxEdgeWorkPixels)
        {
            k--;
        }

        int k2 = Math.Clamp((int)Math.Round(k * cellAspect), 2, 16);
        var workSize = new Size(cols * k, rows * k2);
        var cellSize = new Size(cols, rows);

        AreaDownsample(src, workSize, _work);
        // The working image is an exact k × k2 multiple of the grid, so this is an exact per-cell
        // box average on OpenCV's fast integer-scale path (no second full-resolution pass).
        Cv2.Resize(_work, _cells, cellSize, 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(_work, _gray, ColorConversionCodes.BGRA2GRAY);
        ApplyLocalContrast(_gray, s.LocalContrast); // reveals faint edges (facial features, texture)
        Cv2.GaussianBlur(_gray, _blur, new Size(3, 3), 0.8);

        double sensitivity = Math.Clamp(s.EdgeSensitivity, 0, 1);
        double high = 30 + (1 - sensitivity) * 200;
        Cv2.Canny(_blur, _edges, high * 0.4, high, 3, true);

        Mat edges = _edges;
        if (s.EdgeThickness > 1)
        {
            if (_dilateKernel is null || _kernelThickness != s.EdgeThickness)
            {
                _dilateKernel?.Dispose();
                int size = 2 * s.EdgeThickness - 1;
                _dilateKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size, size));
                _kernelThickness = s.EdgeThickness;
            }

            Cv2.Dilate(_edges, _edgesThick, _dilateKernel);
            edges = _edgesThick;
        }

        Cv2.Sobel(_blur, _gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(_blur, _gy, MatType.CV_32F, 0, 1, 3);
        edges.ConvertTo(_mask, MatType.CV_32F, 1.0 / 255.0);

        // Structure tensor restricted to edge pixels, box-averaged per cell.
        Cv2.Multiply(_gx, _gx, _product);
        Cv2.Multiply(_product, _mask, _product);
        Cv2.Resize(_product, _cellGxx, cellSize, 0, 0, InterpolationFlags.Area);

        Cv2.Multiply(_gy, _gy, _product);
        Cv2.Multiply(_product, _mask, _product);
        Cv2.Resize(_product, _cellGyy, cellSize, 0, 0, InterpolationFlags.Area);

        Cv2.Multiply(_gx, _gy, _product);
        Cv2.Multiply(_product, _mask, _product);
        Cv2.Resize(_product, _cellGxy, cellSize, 0, 0, InterpolationFlags.Area);

        Cv2.Resize(_mask, _cellDensity, cellSize, 0, 0, InterpolationFlags.Area);

        EnsureLocalY(workSize, k2);
        Cv2.Multiply(_mask, _yLocal!, _product);
        Cv2.Resize(_product, _cellCentroid, cellSize, 0, 0, InterpolationFlags.Area);

        LastEdgeSampling = (k, k2);
        return (k, k2);
    }

    /// <summary>Detailed style: sample each cell on the glyph-shape grid and pick the best-matching glyph.</summary>
    private void MatchShapes(Mat src, int cols, int rows, AsciiSettings s, ToneMapping tone)
    {
        // Sample regions roughly square in source pixels: 3 across and (3 x cell aspect) down.
        int sampleColumns = DetailSampleColumns;
        double cellAspect = (src.Height / (double)rows) / (src.Width / (double)cols);
        int sampleRows = Math.Clamp((int)Math.Round(sampleColumns * cellAspect), 3, 9);
        var shapes = _shapes!.GetShapes(s.FontFamily, sampleColumns, sampleRows, DetailCharacterSets.For(s.DetailCharacters));

        AreaDownsample(src, new Size(cols * sampleColumns, rows * sampleRows), _detail);
        // Exact integer factor: per-cell mean colors on OpenCV's fast area path.
        Cv2.Resize(_detail, _cells, new Size(cols, rows), 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(_detail, _detailGray, ColorConversionCodes.BGRA2GRAY);
        ApplyLocalContrast(_detailGray, s.LocalContrast);

        if (_detailChars.Length < cols * rows)
        {
            _detailChars = new char[cols * rows];
        }

        ShapeMatcher.Match((byte*)_detailGray.DataPointer, _detailGray.Step(), cols, rows, shapes,
            tone.Tone, tone.BrightIsDense, s.DetailSharpness, _detailChars);
    }

    /// <summary>
    /// Blends a CLAHE (contrast-limited adaptive histogram equalization) result into an 8-bit luminance
    /// image. Local equalization lifts detail in flat or dark regions (faces in shadow, background texture)
    /// without blowing out areas that already have contrast; the clip limit prevents noise amplification.
    /// </summary>
    private void ApplyLocalContrast(Mat gray, double strength)
    {
        if (strength <= 0.005 || gray.Width < 8 || gray.Height < 8)
        {
            return;
        }

        _clahe ??= Cv2.CreateCLAHE(2.5, new Size(8, 8));
        // Tiles of roughly 32 px keep the equalization local at every grid size.
        _clahe.TilesGridSize = new Size(Math.Clamp(gray.Width / 32, 2, 16), Math.Clamp(gray.Height / 32, 2, 16));
        _clahe.Apply(gray, _contrastTemp);
        Cv2.AddWeighted(gray, 1.0 - strength, _contrastTemp, strength, 0, gray);
    }

    /// <summary>
    /// Area (box-filter) downsampling, tuned for speed. OpenCV's INTER_AREA is fast only for integer
    /// scale factors; a large non-integer reduction (e.g. 1920 → 120) runs single-threaded (~5 ms) and
    /// even a 960 → 720 step costs ~3 ms. So each axis is halved independently while it stays at least
    /// as large as the target (exact 2× box filters on the fast path), leaving a residual factor below 2.
    /// That residual is resampled bilinearly for large images (a 2×2 footprint covers a &lt;2× step well)
    /// and with true area averaging for small targets such as the cell grid, where it is cheap.
    /// </summary>
    private void AreaDownsample(Mat source, Size target, Mat destination)
    {
        Mat current = source;
        int level = 0;
        while (true)
        {
            int width = current.Width / 2 >= target.Width ? current.Width / 2 : current.Width;
            int height = current.Height / 2 >= target.Height ? current.Height / 2 : current.Height;
            if (width == current.Width && height == current.Height)
            {
                break;
            }

            if (level == _pyramid.Count)
            {
                _pyramid.Add(new Mat());
            }

            var next = _pyramid[level++];
            Cv2.Resize(current, next, new Size(width, height), 0, 0, InterpolationFlags.Area);
            current = next;
        }

        if (current.Width == target.Width && current.Height == target.Height)
        {
            current.CopyTo(destination);
            return;
        }

        var interpolation = target.Width * target.Height > 20_000 ? InterpolationFlags.Linear : InterpolationFlags.Area;
        Cv2.Resize(current, destination, target, 0, 0, interpolation);
    }

    /// <summary>A map holding each pixel's vertical position inside its cell (0..1), for underscore detection.</summary>
    private void EnsureLocalY(Size workSize, int k2)
    {
        if (_yLocal is not null && _yLocalSize == workSize && _yLocalK2 == k2)
        {
            return;
        }

        _yLocal?.Dispose();
        _yLocal = new Mat(workSize, MatType.CV_32FC1);
        for (int y = 0; y < workSize.Height; y++)
        {
            float value = ((y % k2) + 0.5f) / k2;
            var row = new Span<float>((void*)_yLocal.Ptr(y), workSize.Width);
            row.Fill(value);
        }

        _yLocalSize = workSize;
        _yLocalK2 = k2;
    }

    private void FillCells(AsciiFrame output, AsciiSettings s, ToneMapping tone, bool hasEdges, int k, int k2, char[]? matchedChars)
    {
        int cols = output.Columns;
        int rows = output.Rows;
        var chars = output.WritableChars;
        var fg = output.WritableForeground;
        var bgCells = output.WritableBackground;
        var ramp = tone.Ramp;
        var toneLut = tone.Tone;
        var rampLut = tone.RampIndexFromToned;
        bool brightIsDense = tone.BrightIsDense;

        bool original = s.ColorMode == ColorMode.Original;
        bool fillBackground = original && s.ColorBackgroundFill;
        uint mono = s.MonochromeForeground;
        uint background = s.BackgroundColor | 0xFF000000;
        bool darkBackground = ColorUtil.IsDark(background);

        bool outline = s.Style == RenderStyle.Outline;
        bool hybrid = s.Style == RenderStyle.Hybrid;
        double blend = Math.Clamp(s.HybridBlend, 0, 1);
        double sensitivity = Math.Clamp(s.EdgeSensitivity, 0, 1);

        // An edge cell needs roughly a line crossing it: (1.25 - s) × the short cell side in pixels.
        double threshold = Math.Max(0.5, (1.25 - sensitivity) * Math.Min(k, k2));
        if (hybrid)
        {
            threshold *= 1.5 - blend; // stronger blend → more cells become edges
        }

        double cellPixels = k * k2;
        double shadingScale = hybrid ? 1.0 - 0.5 * blend : 1.0; // de-emphasize texture so outlines read

        output.IsMonochrome = !original;
        output.HasCellBackground = fillBackground;

        for (int r = 0; r < rows; r++)
        {
            byte* colorRow = (byte*)_cells.Ptr(r);
            byte* lumaRow = (byte*)_cellLuma.Ptr(r);
            float* gxx = hasEdges ? (float*)_cellGxx.Ptr(r) : null;
            float* gyy = hasEdges ? (float*)_cellGyy.Ptr(r) : null;
            float* gxy = hasEdges ? (float*)_cellGxy.Ptr(r) : null;
            float* density = hasEdges ? (float*)_cellDensity.Ptr(r) : null;
            float* centroid = hasEdges ? (float*)_cellCentroid.Ptr(r) : null;

            int rowOffset = r * cols;
            for (int c = 0; c < cols; c++)
            {
                int i = rowOffset + c;
                int b = colorRow[c * 4];
                int g = colorRow[c * 4 + 1];
                int red = colorRow[c * 4 + 2];
                int toned = toneLut[lumaRow[c]];

                char ch;
                bool isEdge = false;
                if (matchedChars is not null)
                {
                    ch = matchedChars[i];
                }
                else if (hasEdges)
                {
                    double count = density[c] * cellPixels;
                    double energy = gxx[c] + gyy[c];
                    if (count >= threshold && energy > 1.0)
                    {
                        isEdge = true;
                        double cy = density[c] > 1e-6 ? centroid[c] / density[c] : 0.5;
                        ch = EdgeCharacterMapper.Classify(gxx[c], gyy[c], gxy[c], cy, count / threshold);
                    }
                    else if (outline)
                    {
                        ch = ' ';
                    }
                    else
                    {
                        ch = ramp[rampLut[Shade(toned, shadingScale, brightIsDense)]];
                    }
                }
                else
                {
                    ch = ramp[rampLut[toned]];
                }

                chars[i] = ch;

                if (original)
                {
                    int tr = toneLut[red], tg = toneLut[g], tb = toneLut[b];
                    if (fillBackground)
                    {
                        bgCells[i] = ColorUtil.FromRgb(tr * 35 / 100, tg * 35 / 100, tb * 35 / 100);
                        fg[i] = ColorUtil.FromRgb(tr + (255 - tr) / 4, tg + (255 - tg) / 4, tb + (255 - tb) / 4);
                    }
                    else
                    {
                        if (darkBackground)
                        {
                            // A glyph covers only part of its cell, so the true color would look dim:
                            // brighten it (hue kept) and let the glyph's density carry the tone.
                            int max = Math.Max(tr, Math.Max(tg, tb));
                            if (max > 0)
                            {
                                float boost = Math.Min(ColorBoostLimit, 245f / max);
                                tr = (int)(tr * boost);
                                tg = (int)(tg * boost);
                                tb = (int)(tb * boost);
                            }
                        }

                        fg[i] = EnsureVisible(tr, tg, tb, darkBackground, isEdge);
                    }
                }
                else
                {
                    fg[i] = mono;
                }
            }
        }
    }

    private static int Shade(int toned, double scale, bool brightIsDense)
    {
        if (scale >= 0.999)
        {
            return toned;
        }

        // Move the value toward the sparse end of the ramp.
        return brightIsDense ? (int)(toned * scale) : 255 - (int)((255 - toned) * scale);
    }

    /// <summary>
    /// Keeps source colors legible against the background: very dark colors on a dark background
    /// are lifted (hue preserved), very light colors on a light background are darkened.
    /// </summary>
    internal static uint EnsureVisible(int r, int g, int b, bool darkBackground, bool emphasize)
    {
        if (darkBackground)
        {
            int floor = emphasize ? 110 : 72;
            int max = Math.Max(r, Math.Max(g, b));
            if (max < floor)
            {
                if (max == 0)
                {
                    return ColorUtil.FromRgb(floor, floor, floor);
                }

                double scale = floor / (double)max;
                return ColorUtil.FromRgb((int)(r * scale), (int)(g * scale), (int)(b * scale));
            }
        }
        else
        {
            int luma = ColorUtil.Luma(r, g, b);
            const int ceiling = 150;
            if (luma > ceiling)
            {
                double scale = ceiling / (double)luma;
                return ColorUtil.FromRgb((int)(r * scale), (int)(g * scale), (int)(b * scale));
            }
        }

        return ColorUtil.FromRgb(r, g, b);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var mat in new[] { _cells, _work, _gray, _blur, _edges, _edgesThick, _gx, _gy, _mask, _product, _cellGxx, _cellGyy, _cellGxy, _cellDensity, _cellCentroid, _cellLuma, _detail, _detailGray, _contrastTemp })
        {
            mat.Dispose();
        }

        _clahe?.Dispose();

        foreach (var level in _pyramid)
        {
            level.Dispose();
        }

        _yLocal?.Dispose();
        _dilateKernel?.Dispose();
    }
}

public sealed class AsciiProcessorFactory : IAsciiProcessorFactory
{
    private readonly AsciiFramePool _pool = new(32);
    private readonly IGlyphShapeProvider? _shapes;

    public AsciiProcessorFactory(IGlyphShapeProvider? shapes = null)
    {
        _shapes = shapes;
    }

    public IAsciiProcessor Create() => new AsciiProcessor(_pool, _shapes);
}
