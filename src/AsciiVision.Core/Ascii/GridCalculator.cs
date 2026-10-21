using AsciiVision.Core.Configuration;

namespace AsciiVision.Core.Ascii;

/// <summary>
/// Converts a requested column count into a character grid that preserves the
/// source aspect ratio once rendered with cells that are <c>cellAspect</c> times taller than wide.
/// </summary>
public static class GridCalculator
{
    public const int MinColumns = 8;
    public const int MaxColumns = 640;
    public const int MinRows = 2;
    public const int MaxRows = 400;

    public static GridSize Compute(int sourceWidth, int sourceHeight, int columns, double cellAspect)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be positive.");
        }

        cellAspect = SanitizeAspect(cellAspect);
        columns = Math.Clamp(columns, MinColumns, MaxColumns);

        double rows = columns * (double)sourceHeight / sourceWidth / cellAspect;
        int rowCount = (int)Math.Round(rows, MidpointRounding.AwayFromZero);

        if (rowCount > MaxRows)
        {
            // Very tall sources: cap rows and shrink columns to keep proportions.
            rowCount = MaxRows;
            columns = Math.Max(MinColumns, (int)Math.Round(rowCount * cellAspect * sourceWidth / sourceHeight));
        }

        return new GridSize(columns, Math.Max(MinRows, rowCount));
    }

    /// <summary>
    /// Largest grid with the source proportions that fits inside a <paramref name="maxColumns"/> x <paramref name="maxRows"/> area
    /// (e.g. a terminal window).
    /// </summary>
    public static GridSize FitWithin(int sourceWidth, int sourceHeight, int maxColumns, int maxRows, double cellAspect)
    {
        if (maxColumns < MinColumns || maxRows < MinRows)
        {
            return new GridSize(Math.Max(1, maxColumns), Math.Max(1, maxRows));
        }

        cellAspect = SanitizeAspect(cellAspect);
        var byWidth = Compute(sourceWidth, sourceHeight, maxColumns, cellAspect);
        if (byWidth.Rows <= maxRows && byWidth.Columns <= maxColumns)
        {
            return byWidth;
        }

        int columns = (int)Math.Floor(maxRows * cellAspect * sourceWidth / sourceHeight);
        columns = Math.Clamp(columns, MinColumns, maxColumns);
        var byHeight = Compute(sourceWidth, sourceHeight, columns, cellAspect);
        return byHeight with { Rows = Math.Min(byHeight.Rows, maxRows) };
    }

    public static int ColumnsForPreset(QualityPreset preset, int customColumns) => preset switch
    {
        QualityPreset.Performance => 80,
        QualityPreset.Balanced => 120,
        QualityPreset.Quality => 160,
        QualityPreset.Ultra => 240,
        _ => Math.Clamp(customColumns, MinColumns, MaxColumns),
    };

    private static double SanitizeAspect(double cellAspect) =>
        double.IsFinite(cellAspect) && cellAspect > 0.1 ? Math.Clamp(cellAspect, 0.5, 4.0) : 2.0;
}
