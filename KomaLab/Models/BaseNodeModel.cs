namespace KomaLab.Models;

/// <summary>
/// Modello base astratto per tutti i nodi.
/// Contiene le proprietà comuni di "stato" (posizione, titolo).
/// </summary>
public abstract class BaseNodeModel
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Title { get; set; } = "";
}