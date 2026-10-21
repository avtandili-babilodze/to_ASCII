using AsciiVision.Core.Configuration;

namespace AsciiVision.Core.Ascii;

/// <summary>Candidate characters for shape matching (all printable ASCII so every terminal can show them).</summary>
public static class DetailCharacterSets
{
    /// <summary>Punctuation and symbols: strokes in every direction and position, without letter "texture".</summary>
    public const string Symbols = " .,:;'`\"^~-_=+*!|/\\()[]{}<>?#%&@$";

    /// <summary>Symbols plus letters and digits with even, rounded ink, which add intermediate densities.</summary>
    public const string SymbolsAndLetters = Symbols + "oO08xXsScCeaunvzwmMWNHBQDRUVAKG";

    public static readonly string AllAscii = new(Enumerable.Range(32, 95).Select(i => (char)i).ToArray());

    public static string For(DetailCharacterSet set) => set switch
    {
        DetailCharacterSet.SymbolsAndLetters => SymbolsAndLetters,
        DetailCharacterSet.AllAscii => AllAscii,
        _ => Symbols,
    };
}
