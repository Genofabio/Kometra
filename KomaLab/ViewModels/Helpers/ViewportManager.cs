using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace KomaLab.ViewModels.Helpers;

public partial class ViewportManager : ObservableObject
{
    // --- 1. PROPRIETÀ DI INPUT ---

    [ObservableProperty] private Size _viewportSize;
    [ObservableProperty] private Size _imageSize;
    [ObservableProperty] private Point? _targetCoordinate; // Coord. Immagine
    [ObservableProperty] private int _searchRadius;        // Pixel Immagine

    // --- 2. STATO INTERNO (Trasformazione) ---

    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 1.0;

    // --- 3. PROPRIETÀ CALCOLATE (Output per la View) ---

    // Mirino
    public double TargetMarkerScreenX => TargetCoordinate.HasValue 
        ? (TargetCoordinate.Value.X * Scale) + OffsetX : 0;

    public double TargetMarkerScreenY => TargetCoordinate.HasValue 
        ? (TargetCoordinate.Value.Y * Scale) + OffsetY : 0;

    // Riquadro di Ricerca (Tutto calcolato al volo, niente stato duplicato)
    public double SearchBoxLeft
    {
        get
        {
            if (!TargetCoordinate.HasValue) return 0;
            double centerXScreen = (TargetCoordinate.Value.X * Scale) + OffsetX;
            return centerXScreen - (SearchRadius * Scale);
        }
    }

    public double SearchBoxTop
    {
        get
        {
            if (!TargetCoordinate.HasValue) return 0;
            double centerYScreen = (TargetCoordinate.Value.Y * Scale) + OffsetY;
            return centerYScreen - (SearchRadius * Scale);
        }
    }

    public double SearchBoxWidth => (SearchRadius * 2) * Scale;
    public double SearchBoxHeight => SearchBoxWidth; // È un quadrato

    // --- 4. GESTIONE CAMBIAMENTI (Trigger Unico) ---

    // Ogni volta che una proprietà di input cambia, notifichiamo la UI 
    // che le proprietà calcolate devono essere rilette.
    
    partial void OnViewportSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnImageSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnTargetCoordinateChanged(Point? value) => NotifyCalculatedProps();
    partial void OnSearchRadiusChanged(int value) => NotifyCalculatedProps();
    partial void OnOffsetXChanged(double value) => NotifyCalculatedProps();
    partial void OnOffsetYChanged(double value) => NotifyCalculatedProps();
    partial void OnScaleChanged(double value) => NotifyCalculatedProps();

    private void NotifyCalculatedProps()
    {
        // Notifica in blocco per il Mirino
        OnPropertyChanged(nameof(TargetMarkerScreenX));
        OnPropertyChanged(nameof(TargetMarkerScreenY));
        
        // Notifica in blocco per il Box
        OnPropertyChanged(nameof(SearchBoxLeft));
        OnPropertyChanged(nameof(SearchBoxTop));
        OnPropertyChanged(nameof(SearchBoxWidth));
        OnPropertyChanged(nameof(SearchBoxHeight));
    }

    // --- 5. LOGICA DI MANIPOLAZIONE ---

    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * scaleFactor, 0.01, 20);
        
        // Formula standard per lo zoom centrato sul mouse
        OffsetX = viewportZoomPoint.X - (viewportZoomPoint.X - OffsetX) * (newScale / oldScale);
        OffsetY = viewportZoomPoint.Y - (viewportZoomPoint.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
    }

    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    public void ZoomIn()
    {
        var center = new Point(ViewportSize.Width / 2, ViewportSize.Height / 2);
        ApplyZoomAtPoint(1.25, center);
    }

    public void ZoomOut()
    {
        var center = new Point(ViewportSize.Width / 2, ViewportSize.Height / 2);
        ApplyZoomAtPoint(1.0 / 1.25, center);
    }

    public void ResetView()
    {
        if (ImageSize.Width == 0 || ImageSize.Height == 0 || ViewportSize.Width == 0)
        {
            Scale = 1.0; OffsetX = 0; OffsetY = 0; return;
        }
        
        // "Letterbox" fitting: adatta l'immagine interamente nella vista con un po' di margine
        double padding = 0.95;
        double scaleX = (ViewportSize.Width * padding) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / ImageSize.Height;
        
        Scale = Math.Min(scaleX, scaleY);
        
        // Centra l'immagine
        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2;
    }
}