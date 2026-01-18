using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Shared;

// ---------------------------------------------------------------------------
// FILE: CoordinateEntry.cs
// RUOLO: Item ViewModel (Riga di Tabella)
// DESCRIZIONE:
// Rappresenta una singola voce nella lista delle coordinate di allineamento.
// Gestisce la formattazione dei dati per la visualizzazione (es. inversione asse Y).
// ---------------------------------------------------------------------------

public partial class CoordinateEntry : ObservableObject
{
    /// <summary>
    /// Indice dell'immagine nello stack (0-based).
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Altezza dell'immagine, necessaria per convertire le coordinate 
    /// dal sistema grafico (Top-Left 0,0) al sistema FITS (Bottom-Left 0,0).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedY))]
    private double _imageHeight; 

    [ObservableProperty] 
    private string? _displayName = "";

    /// <summary>
    /// Indica se questa riga corrisponde all'immagine attualmente visualizzata nel viewport.
    /// Utile per evidenziare la riga nella UI.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedX))] 
    [NotifyPropertyChangedFor(nameof(FormattedY))] 
    private Point? _coordinate;

    /// <summary>
    /// Indice visualizzato all'utente (1-based).
    /// </summary>
    public int DisplayIndex => Index + 1;

    public string FormattedX => _coordinate.HasValue 
        ? $"{_coordinate.Value.X:F2}" 
        : "-";

    public string FormattedY 
    {
        get
        {
            if (!_coordinate.HasValue) return "-";
            
            double yRaw = _coordinate.Value.Y;
            
            // Logica di inversione asse Y (FITS standard ha 0,0 in basso a sinistra)
            // Se ImageHeight è 0 (non ancora caricata), mostriamo il dato raw.
            double yDisplay = (ImageHeight > 0) ? (ImageHeight - yRaw) : yRaw;
            
            return $"{yDisplay:F2}";
        }
    }
}