using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace KomaLab.ViewModels.Visualization;

// ---------------------------------------------------------------------------
// FILE: ImageViewport.cs
// VERSIONE: Corretta (Rimosso padding indesiderato su ResetView)
// ---------------------------------------------------------------------------

public partial class ImageViewport : ObservableObject
{
    // --- Costanti di Configurazione ---
    private const double MinZoom = 0.01; // 1%
    private const double MaxZoom = 50.0; // 5000%
    private const double ZoomStep = 1.25; // Fattore di moltiplicazione zoom

    // --- Input (Dimensioni) ---
    
    [ObservableProperty] 
    private Size _viewportSize; // Dimensioni del controllo UI (Canvas/Image)

    [ObservableProperty] 
    private Size _imageSize;    // Dimensioni reali della Bitmap (Pixel)

    // --- Stato Trasformazione ---

    [ObservableProperty] 
    private double _offsetX;

    [ObservableProperty] 
    private double _offsetY;

    [ObservableProperty] 
    private double _scale = 1.0;
    
    public bool IsZoomed => Math.Abs(Scale - 1.0) > 0.001;

    // --- Metodi di Manipolazione Vista ---

    public virtual void ApplyZoomAtPoint(double zoomDelta, Point centerPoint)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * zoomDelta, MinZoom, MaxZoom);

        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        double ratio = newScale / oldScale;
        
        OffsetX = centerPoint.X - (centerPoint.X - OffsetX) * ratio;
        OffsetY = centerPoint.Y - (centerPoint.Y - OffsetY) * ratio;
        Scale = newScale;
    }

    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    public void ResetView()
    {
        if (ImageSize.Width <= 0 || ImageSize.Height <= 0 || 
            ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            Scale = 1.0; 
            OffsetX = 0; 
            OffsetY = 0; 
            return;
        }

        // FIX: Rimosso il margine del 5% (era 0.95). Ora l'immagine riempie esattamente il contenitore.
        double padding = 1.0; 
        
        double scaleX = (ViewportSize.Width * padding) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / ImageSize.Height;

        Scale = Math.Min(scaleX, scaleY);

        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2.0;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2.0;
    }

    public void ZoomIn()
    {
        var center = new Point(ViewportSize.Width / 2.0, ViewportSize.Height / 2.0);
        ApplyZoomAtPoint(ZoomStep, center);
    }

    public void ZoomOut()
    {
        var center = new Point(ViewportSize.Width / 2.0, ViewportSize.Height / 2.0);
        ApplyZoomAtPoint(1.0 / ZoomStep, center);
    }

    // --- Utility Coordinate ---

    public Point ToImageCoordinates(Point screenPoint)
    {
        if (Scale == 0) return new Point(0,0);
        double x = (screenPoint.X - OffsetX) / Scale;
        double y = (screenPoint.Y - OffsetY) / Scale;
        return new Point(x, y);
    }

    public Point ToScreenCoordinates(Point imagePoint)
    {
        double x = (imagePoint.X * Scale) + OffsetX;
        double y = (imagePoint.Y * Scale) + OffsetY;
        return new Point(x, y);
    }
    
    // --- Gestione Notifiche ---

    protected virtual void NotifyCalculatedProps()
    {
        OnPropertyChanged(nameof(IsZoomed));
    }

    partial void OnViewportSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnImageSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnOffsetXChanged(double value) => NotifyCalculatedProps();
    partial void OnOffsetYChanged(double value) => NotifyCalculatedProps();
    partial void OnScaleChanged(double value) => NotifyCalculatedProps();
}