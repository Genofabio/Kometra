namespace Kometra.Models.Primitives;

// ---------------------------------------------------------------------------
// FILE: Point2D.cs
// DESCRIZIONE:
// Primitiva geometrica agnostica rispetto al framework UI.
// Utilizza double precision (fondamentale per calcoli astrometrici),
// a differenza di System.Numerics.Vector2 che usa float.
// ---------------------------------------------------------------------------

public readonly struct Point2D
{
    public double X { get; }
    public double Y { get; }

    public Point2D(double x, double y)
    {
        X = x;
        Y = y;
    }

    // Opzionale: Deconstruct per usare la sintassi: var (x, y) = myPoint;
    public void Deconstruct(out double x, out double y)
    {
        x = X;
        y = Y;
    }

    public override string ToString() => $"({X:F3}, {Y:F3})";
}