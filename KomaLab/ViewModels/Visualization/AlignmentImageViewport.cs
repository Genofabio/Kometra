using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Visualization;

/// <summary>
/// Estende il Viewport standard per gestire i marker di allineamento (mirino e box).
/// </summary>
public partial class AlignmentImageViewport : ImageViewport
{
    [ObservableProperty] 
    private Point? _targetCoordinate;

    [ObservableProperty] 
    private int _searchRadius = 100;

    // =======================================================================
    // PROPRIETÀ DI VISIBILITÀ (Risolvono gli errori nello XAML)
    // =======================================================================
    
    public bool IsTargetMarkerVisible => TargetCoordinate.HasValue;
    
    public bool IsSearchBoxVisible => TargetCoordinate.HasValue && SearchRadius > 0;

    // =======================================================================
    // COORDINATE DELLO SCHERMO (Proiezione Matematica)
    // =======================================================================
    
    // Posizione del mirino sullo schermo: (CoordImmagine * Scala) + Offset
    public double TargetMarkerScreenX => TargetCoordinate.HasValue ? (TargetCoordinate.Value.X * Scale) + OffsetX : 0;
    public double TargetMarkerScreenY => TargetCoordinate.HasValue ? (TargetCoordinate.Value.Y * Scale) + OffsetY : 0;

    // Coordinate del Box di ricerca (centrato sul mirino)
    public double SearchBoxLeft => TargetMarkerScreenX - (SearchRadius * Scale);
    public double SearchBoxTop => TargetMarkerScreenY - (SearchRadius * Scale);
    public double SearchBoxWidth => (SearchRadius * 2) * Scale;
    public double SearchBoxHeight => (SearchRadius * 2) * Scale;

    // =======================================================================
    // NOTIFICA DEI CAMBIAMENTI
    // =======================================================================

    /// <summary>
    /// Quando cambiano i parametri fondamentali (Zoom, Pan, Coordinata o Raggio),
    /// dobbiamo dire ad Avalonia di ricalcolare tutti i marker.
    /// </summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Se cambia uno di questi fattori, i marker sullo schermo si spostano o cambiano dimensione
        if (e.PropertyName == nameof(Scale) || 
            e.PropertyName == nameof(OffsetX) || 
            e.PropertyName == nameof(OffsetY) || 
            e.PropertyName == nameof(TargetCoordinate) || 
            e.PropertyName == nameof(SearchRadius))
        {
            OnPropertyChanged(nameof(TargetMarkerScreenX));
            OnPropertyChanged(nameof(TargetMarkerScreenY));
            OnPropertyChanged(nameof(IsTargetMarkerVisible));
            OnPropertyChanged(nameof(IsSearchBoxVisible));
            OnPropertyChanged(nameof(SearchBoxLeft));
            OnPropertyChanged(nameof(SearchBoxTop));
            OnPropertyChanged(nameof(SearchBoxWidth));
            OnPropertyChanged(nameof(SearchBoxHeight));
        }
    }
}