using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Helpers;

/// <summary>
/// Estende ImageViewport aggiungendo funzionalità specifiche 
/// per l'allineamento (Target, Search Box, Reticoli).
/// </summary>
public partial class AlignmentImageViewport : ImageViewport
{
    // --- Input Specifici Allineamento ---
    [ObservableProperty] private Point? _targetCoordinate; // Coordinate immagine (X,Y)
    [ObservableProperty] private int _searchRadius;        // Raggio in pixel immagine

    // --- Output Calcolati (Per il Binding della View) ---
    // Queste proprietà trasformano le coordinate Immagine -> coordinate Schermo
    
    public double TargetMarkerScreenX => TargetCoordinate.HasValue 
        ? (TargetCoordinate.Value.X * Scale) + OffsetX : 0;

    public double TargetMarkerScreenY => TargetCoordinate.HasValue 
        ? (TargetCoordinate.Value.Y * Scale) + OffsetY : 0;

    public double SearchBoxLeft
    {
        get
        {
            if (!TargetCoordinate.HasValue) return 0;
            // Centro X a schermo - (Raggio scalato)
            double screenCenterX = (TargetCoordinate.Value.X * Scale) + OffsetX;
            return screenCenterX - (SearchRadius * Scale);
        }
    }

    public double SearchBoxTop
    {
        get
        {
            if (!TargetCoordinate.HasValue) return 0;
            double screenCenterY = (TargetCoordinate.Value.Y * Scale) + OffsetY;
            return screenCenterY - (SearchRadius * Scale);
        }
    }

    public double SearchBoxWidth => (SearchRadius * 2) * Scale;
    public double SearchBoxHeight => (SearchRadius * 2) * Scale;

    // --- Gestione Aggiornamenti ---

    // Se cambiano Target o Raggio, ricalcola i mirini
    partial void OnTargetCoordinateChanged(Point? value) => NotifyCalculatedProps();
    partial void OnSearchRadiusChanged(int value) => NotifyCalculatedProps();

    // Override del metodo base: viene chiamato anche quando fai Zoom/Pan (da ImageViewport)
    protected override void NotifyCalculatedProps()
    {
        // Notifica le proprietà specifiche di questa classe
        OnPropertyChanged(nameof(TargetMarkerScreenX));
        OnPropertyChanged(nameof(TargetMarkerScreenY));
        OnPropertyChanged(nameof(SearchBoxLeft));
        OnPropertyChanged(nameof(SearchBoxTop));
        OnPropertyChanged(nameof(SearchBoxWidth));
        OnPropertyChanged(nameof(SearchBoxHeight));
    }
}