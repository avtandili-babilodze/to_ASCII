using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;

namespace AsciiVision.Rendering.Glyphs;

/// <summary>
/// Builds and caches glyph atlases and font metrics. Atlases are keyed by font, pixel size and
/// character set; a small LRU keeps the few sizes in use (preview fit size, export size, PNG size).
/// </summary>
public sealed class GlyphAtlasCache : ICellGeometryProvider, IGlyphShapeProvider, IDisposable
{
    private const int MaxAtlases = 8;

    private readonly WpfGlyphThread _glyphThread;
    private readonly bool _ownsThread;
    private readonly object _gate = new();
    private readonly LinkedList<(string Key, GlyphAtlas Atlas)> _atlases = new();
    private readonly Dictionary<string, FontCellMetrics> _metrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GlyphShapeSet> _shapes = new(StringComparer.OrdinalIgnoreCase);
    private const double ShapeReferenceSizePx = 48;

    public GlyphAtlasCache(WpfGlyphThread? glyphThread = null)
    {
        _ownsThread = glyphThread is null;
        _glyphThread = glyphThread ?? new WpfGlyphThread();
    }

    public FontCellMetrics GetMetrics(string familyName)
    {
        lock (_gate)
        {
            if (_metrics.TryGetValue(familyName, out var cached))
            {
                return cached;
            }
        }

        var metrics = _glyphThread.Invoke(() => FontCellMetrics.Measure(familyName).Metrics);
        lock (_gate)
        {
            _metrics[familyName] = metrics;
        }

        return metrics;
    }

    public double GetCellAspect(string fontFamily) => GetMetrics(fontFamily).Aspect;

    /// <summary>Returns an atlas containing printable ASCII, edge characters and every character of <paramref name="extraCharacters"/>.</summary>
    public GlyphAtlas GetAtlas(string familyName, double fontSizePx, string extraCharacters)
    {
        fontSizePx = Math.Round(Math.Clamp(fontSizePx, 3, 160) * 4) / 4;
        var extras = new string(extraCharacters.Where(c => c >= 128 && !char.IsControl(c) && !char.IsSurrogate(c)).Distinct().OrderBy(c => c).ToArray());
        string key = $"{familyName}|{fontSizePx:0.##}|{extras}";

        lock (_gate)
        {
            for (var node = _atlases.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    _atlases.Remove(node);
                    _atlases.AddFirst(node);
                    return node.Value.Atlas;
                }
            }
        }

        var atlas = _glyphThread.Invoke(() => GlyphAtlas.Build(familyName, fontSizePx, GlyphAtlas.BaseCharacters + extras));
        lock (_gate)
        {
            _atlases.AddFirst((key, atlas));
            while (_atlases.Count > MaxAtlases)
            {
                _atlases.RemoveLast();
            }
        }

        return atlas;
    }

    /// <summary>
    /// Shape vectors of the printable ASCII glyphs, measured from a large reference rasterization of the
    /// real font so they are independent of the on-screen size.
    /// </summary>
    public GlyphShapeSet GetShapes(string fontFamily, int sampleColumns, int sampleRows, string characters)
    {
        string subsetKey = $"{fontFamily}|{sampleColumns}x{sampleRows}|{characters}";
        lock (_gate)
        {
            if (_shapes.TryGetValue(subsetKey, out var cachedSubset))
            {
                return cachedSubset;
            }
        }

        var subset = GetAllShapes(fontFamily, sampleColumns, sampleRows).Subset(characters);
        lock (_gate)
        {
            _shapes[subsetKey] = subset;
        }

        return subset;
    }

    private GlyphShapeSet GetAllShapes(string fontFamily, int sampleColumns, int sampleRows)
    {
        string key = $"{fontFamily}|{sampleColumns}x{sampleRows}";
        lock (_gate)
        {
            if (_shapes.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var atlas = GetAtlas(fontFamily, ShapeReferenceSizePx, string.Empty);
        var characters = GlyphAtlas.BaseCharacters.ToCharArray();
        int dims = sampleColumns * sampleRows;
        var raw = new float[characters.Length * dims];
        int w = atlas.CellWidth, h = atlas.CellHeight;
        for (int g = 0; g < characters.Length; g++)
        {
            var coverage = atlas.GetCoverage(atlas.GetGlyphIndex(characters[g]));
            for (int ry = 0; ry < sampleRows; ry++)
            {
                int y0 = ry * h / sampleRows, y1 = (ry + 1) * h / sampleRows;
                for (int rx = 0; rx < sampleColumns; rx++)
                {
                    int x0 = rx * w / sampleColumns, x1 = (rx + 1) * w / sampleColumns;
                    long sum = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            sum += coverage[y * w + x];
                        }
                    }

                    int count = Math.Max(1, (y1 - y0) * (x1 - x0));
                    raw[g * dims + ry * sampleColumns + rx] = sum / (255f * count);
                }
            }
        }

        var set = new GlyphShapeSet(characters, raw, sampleColumns, sampleRows);
        lock (_gate)
        {
            _shapes[key] = set;
        }

        return set;
    }

    public IReadOnlyList<string> GetMonospacedFamilies() => _glyphThread.Invoke(FontCellMetrics.GetMonospacedFamilies);

    public void Dispose()
    {
        if (_ownsThread)
        {
            _glyphThread.Dispose();
        }
    }
}
