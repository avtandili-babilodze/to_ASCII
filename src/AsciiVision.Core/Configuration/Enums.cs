namespace AsciiVision.Core.Configuration;

public enum RenderStyle
{
    /// <summary>Cell brightness → character ramp.</summary>
    Standard,

    /// <summary>Edge detection only.</summary>
    Outline,

    /// <summary>Ramp shading plus detected edges.</summary>
    Hybrid,

    /// <summary>Shape matching: the brightness pattern inside each cell picks the glyph whose shape fits best.</summary>
    Detailed,
}

/// <summary>Candidate characters for the Detailed (shape-matched) style.</summary>
public enum DetailCharacterSet
{
    /// <summary>Punctuation and symbols only: the cleanest, classic ASCII-art look.</summary>
    Symbols,

    /// <summary>Symbols plus round, evenly textured letters and digits: more tonal steps.</summary>
    SymbolsAndLetters,

    /// <summary>All printable ASCII: the most shapes to choose from, busier texture.</summary>
    AllAscii,
}

public enum ColorMode
{
    White,
    Green,
    Original,
    Custom,
}

public enum DisplayMode
{
    App,
    Console,
    Both,
}

public enum QualityPreset
{
    Performance,
    Balanced,
    Quality,
    Ultra,
    Custom,
}

public enum PreviewFit
{
    /// <summary>Scale the character raster to the preview area (crisp: glyphs are rasterized at the fitted size).</summary>
    Fit,

    /// <summary>Render at the configured font size, one bitmap pixel per device pixel (scrolls if larger).</summary>
    ActualSize,
}

public enum ConsoleSizeMode
{
    /// <summary>Convert at a grid that fits the terminal window.</summary>
    FitTerminal,

    /// <summary>Reuse the preview conversion (cropped by the terminal if it is too large).</summary>
    MatchPreview,

    /// <summary>Convert at an explicitly configured column count.</summary>
    Custom,
}

public enum ConsoleColorDepth
{
    Auto,
    TrueColor,
    Palette256,
    Basic16,
}

public enum EncodingQuality
{
    High,
    Balanced,
    Small,
}

public enum SourceKind
{
    Screen,
    Window,
    Video,
    TestPattern,
}
