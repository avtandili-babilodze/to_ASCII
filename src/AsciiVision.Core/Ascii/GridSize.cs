namespace AsciiVision.Core.Ascii;

public readonly record struct GridSize(int Columns, int Rows)
{
    public int CellCount => Columns * Rows;

    public bool IsEmpty => Columns <= 0 || Rows <= 0;

    public override string ToString() => $"{Columns}×{Rows}";
}
