using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;

namespace KomaLab.ViewModels.Helpers;

public partial class CoordinateEntry : ObservableObject
{
    public int Index { get; set; }
    public double ImageHeight { get; set; } 

    [ObservableProperty] 
    private string? _displayName = "";

    // NUOVA PROPRIETÀ: Indica se questa riga corrisponde all'immagine visualizzata
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedX))] 
    [NotifyPropertyChangedFor(nameof(FormattedY))] 
    private Point? _coordinate;

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
            double yDisplay = (ImageHeight > 0) ? (ImageHeight - yRaw) : yRaw;
            return $"{yDisplay:F2}";
        }
    }
}