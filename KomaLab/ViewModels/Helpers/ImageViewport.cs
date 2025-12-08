using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace KomaLab.ViewModels.Helpers;

/// <summary>
/// Gestisce lo stato di visualizzazione (Zoom, Pan, Scale) di un'immagine.
/// Base comune per qualsiasi componente che debba mostrare un'immagine zoomabile.
/// </summary>
public partial class ImageViewport : ObservableObject
{
    // --- Input (Dati dall'esterno) ---
    [ObservableProperty] private Size _viewportSize; // Dimensione del controllo a schermo
    [ObservableProperty] private Size _imageSize;    // Dimensione reale dell'immagine (pixel)

    // --- Stato (Trasformazione interna) ---
    [ObservableProperty] private double _offsetX;
    [ObservableProperty] private double _offsetY;
    [ObservableProperty] private double _scale = 1.0;
    
    public bool IsZoomed => Math.Abs(Scale - 1.0) > 0.001;

    // --- Metodi di Manipolazione ---

    /// <summary>
    /// Esegue lo zoom mantenendo fisso un punto specifico (es. posizione mouse).
    /// </summary>
    public virtual void ApplyZoomAtPoint(double scaleFactor, Point centerPoint)
    {
        double oldScale = Scale;
        // Clamp: limita lo zoom tra 1% e 5000%
        double newScale = Math.Clamp(oldScale * scaleFactor, 0.01, 50.0);

        // Formula magica per lo zoom centrato
        OffsetX = centerPoint.X - (centerPoint.X - OffsetX) * (newScale / oldScale);
        OffsetY = centerPoint.Y - (centerPoint.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
    }

    /// <summary>
    /// Sposta l'immagine (Pan).
    /// </summary>
    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    /// <summary>
    /// Resetta la vista adattando l'immagine al contenitore (Fit).
    /// </summary>
    public void ResetView()
    {
        if (ImageSize.Width == 0 || ImageSize.Height == 0 || ViewportSize.Width == 0)
        {
            Scale = 1.0; OffsetX = 0; OffsetY = 0; return;
        }

        // Calcola scala per "Letterbox" (tutto visibile)
        double padding = 0.95; // 5% di margine
        double scaleX = (ViewportSize.Width * padding) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / ImageSize.Height;

        Scale = Math.Min(scaleX, scaleY);

        // Centra nell'area disponibile
        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2;
    }

    public void ZoomIn()
    {
        // Calcola il centro del controllo visibile
        var center = new Point(ViewportSize.Width / 2.0, ViewportSize.Height / 2.0);
        // Applica zoom (es. +25%)
        ApplyZoomAtPoint(1.25, center);
    }

    public void ZoomOut()
    {
        var center = new Point(ViewportSize.Width / 2.0, ViewportSize.Height / 2.0);
        // Applica zoom inverso
        ApplyZoomAtPoint(1.0 / 1.25, center);
    }
    
    // --- Hook per classi derivate ---
    // Questo metodo vuoto serve alla classe figlia per ricalcolare i mirini
    // ogni volta che cambia qualcosa nella geometria (senza duplicare codice).
    protected virtual void NotifyCalculatedProps()
    {
        OnPropertyChanged(nameof(IsZoomed));
    }

    // Trigger automatici
    partial void OnViewportSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnImageSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnOffsetXChanged(double value) => NotifyCalculatedProps();
    partial void OnOffsetYChanged(double value) => NotifyCalculatedProps();
    partial void OnScaleChanged(double value) => NotifyCalculatedProps();
}