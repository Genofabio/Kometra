using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;

namespace KomaLab.ViewModels.Helpers;

/// <summary>
/// Rappresenta una riga nella lista delle immagini dello strumento di allineamento.
/// Gestisce la visualizzazione delle coordinate convertendo lo spazio schermo (Top-Left)
/// nello spazio astronomico (Bottom-Left).
/// </summary>
public partial class CoordinateEntry : ObservableObject
{
    public int Index { get; set; }
    
    // Altezza dell'immagine necessaria per il flip della coordinata Y
    public double ImageHeight { get; set; } 

    // Rendiamolo Observable per completezza (es. se carichi il nome file asincrono)
    [ObservableProperty] 
    private string? _displayName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinateString))] 
    private Point? _coordinate;

    /// <summary>
    /// Stringa formattata per la UI.
    /// Converte la Y da "Computer Graphics" (0 in alto) a "Scientifico/FITS" (0 in basso).
    /// </summary>
    public string CoordinateString
    {
        get
        {
            if (!Coordinate.HasValue) return "---";

            double x = Coordinate.Value.X;
            double yRaw = Coordinate.Value.Y; // Y interna (Avalonia, Top-Left)

            // Conversione per l'astronomo: Y_astro = Altezza - Y_raw
            // Se ImageHeight non è impostato (0), mostriamo la raw.
            double yDisplay = (ImageHeight > 0) ? (ImageHeight - yRaw) : yRaw;

            return $"X: {x:F1}  Y: {yDisplay:F1}";
        }
    }
}