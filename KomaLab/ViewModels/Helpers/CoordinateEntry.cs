using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;

namespace KomaLab.ViewModels.Helpers;

public partial class CoordinateEntry : ObservableObject
{
    public int Index { get; set; }
    public string DisplayName { get; set; } = "";
    
    // Serve per calcolare la Y invertita per la visualizzazione
    public double ImageHeight { get; set; } 

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinateString))] 
    private Point? _coordinate;

    // --- MODIFICA LA LOGICA DELLA STRINGA ---
    public string CoordinateString
    {
        get
        {
            if (!Coordinate.HasValue) return "---";

            double x = Coordinate.Value.X;
            double yRaw = Coordinate.Value.Y; // Y "Alto-Sinistra" (Interne)

            // Se conosciamo l'altezza, convertiamo in "Basso-Sinistra" (Astronomiche)
            // Formula: Y_astro = Altezza - Y_raw
            double yDisplay = (ImageHeight > 0) ? (ImageHeight - yRaw) : yRaw;

            return $"({x:F2}; {yDisplay:F2})";
        }
    }
}