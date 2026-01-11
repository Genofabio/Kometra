using System;

namespace KomaLab.Models.Primitives;

// ---------------------------------------------------------------------------
// FILE: Rect2D.cs
// DESCRIZIONE:
// Primitiva geometrica per definire un rettangolo (X, Y, Width, Height).
// Sostituisce 'Avalonia.Rect' nei layer di Logica/Service per eliminare
// la dipendenza dalla libreria UI.
// Implementata come 'readonly struct' per efficienza e immutabilità.
// ---------------------------------------------------------------------------

public readonly struct Rect2D
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    /// <summary>
    /// Crea un nuovo rettangolo.
    /// </summary>
    public Rect2D(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Restituisce true se il rettangolo ha area positiva.
    /// </summary>
    public bool IsValid => Width > 0 && Height > 0;

    public override string ToString() => $"[X={X:F1}, Y={Y:F1}, W={Width:F1}, H={Height:F1}]";
}