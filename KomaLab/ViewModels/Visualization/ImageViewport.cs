using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace KomaLab.ViewModels.Visualization;

// ---------------------------------------------------------------------------
// FILE: ImageViewport.cs
// RUOLO: ViewModel Base (Logica 2D)
// DESCRIZIONE:
// Gestisce la matrice di trasformazione (Zoom, Pan, Scale) per visualizzare
// un'immagine all'interno di un contenitore.
// Fornisce le primitive matematiche per convertire coordinate e gestire l'input.
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
    
    // Proprietà calcolata utile per i binding (es. mostrare/nascondere scrollbars o pulsanti reset)
    public bool IsZoomed => Math.Abs(Scale - 1.0) > 0.001;

    // --- Metodi di Manipolazione Vista ---

    /// <summary>
    /// Esegue lo zoom mantenendo fisso un punto focale (es. la posizione del mouse).
    /// </summary>
    /// <param name="zoomDelta">Fattore moltiplicativo (es. 1.1 per zoom in, 0.9 per zoom out).</param>
    /// <param name="centerPoint">Il punto nello spazio Viewport che deve rimanere fermo.</param>
    public virtual void ApplyZoomAtPoint(double zoomDelta, Point centerPoint)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * zoomDelta, MinZoom, MaxZoom);

        // Se lo zoom non cambia (siamo ai limiti), usciamo per risparmiare ricalcoli
        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        // Formula standard per zoom centrato:
        // NewOffset = Mouse - (Mouse - OldOffset) * (NewScale / OldScale)
        double ratio = newScale / oldScale;
        
        OffsetX = centerPoint.X - (centerPoint.X - OffsetX) * ratio;
        OffsetY = centerPoint.Y - (centerPoint.Y - OffsetY) * ratio;
        Scale = newScale;
    }

    /// <summary>
    /// Sposta l'immagine (Pan) sommando un delta alle coordinate correnti.
    /// </summary>
    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    /// <summary>
    /// Resetta la vista adattando l'immagine al contenitore (Fit "Letterbox").
    /// </summary>
    public void ResetView()
    {
        // Protezione contro dimensioni non valide (es. controllo non ancora caricato)
        if (ImageSize.Width <= 0 || ImageSize.Height <= 0 || 
            ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            Scale = 1.0; 
            OffsetX = 0; 
            OffsetY = 0; 
            return;
        }

        // Calcola il fattore di scala per vedere tutta l'immagine con un margine del 5%
        double padding = 0.95; 
        double scaleX = (ViewportSize.Width * padding) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / ImageSize.Height;

        // Scegliamo il minore per garantire che tutto sia visibile
        Scale = Math.Min(scaleX, scaleY);

        // Centriamo l'immagine nel viewport
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

    // --- Utility Coordinate (Fondamentali per Mouse Input) ---

    /// <summary>
    /// Converte un punto dallo spazio Schermo (Viewport) allo spazio Immagine (Pixel).
    /// </summary>
    public Point ToImageCoordinates(Point screenPoint)
    {
        if (Scale == 0) return new Point(0,0);
        double x = (screenPoint.X - OffsetX) / Scale;
        double y = (screenPoint.Y - OffsetY) / Scale;
        return new Point(x, y);
    }

    /// <summary>
    /// Converte un punto dallo spazio Immagine (Pixel) allo spazio Schermo (Viewport).
    /// </summary>
    public Point ToScreenCoordinates(Point imagePoint)
    {
        double x = (imagePoint.X * Scale) + OffsetX;
        double y = (imagePoint.Y * Scale) + OffsetY;
        return new Point(x, y);
    }
    
    // --- Gestione Notifiche (Hook Pattern) ---

    /// <summary>
    /// Metodo virtuale chiamato ogni volta che la trasformazione cambia.
    /// Le classi derivate (es. AlignmentImageViewport) lo usano per aggiornare i mirini.
    /// </summary>
    protected virtual void NotifyCalculatedProps()
    {
        OnPropertyChanged(nameof(IsZoomed));
    }

    // Trigger generati automaticamente da CommunityToolkit.Mvvm
    partial void OnViewportSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnImageSizeChanged(Size value) => NotifyCalculatedProps();
    partial void OnOffsetXChanged(double value) => NotifyCalculatedProps();
    partial void OnOffsetYChanged(double value) => NotifyCalculatedProps();
    partial void OnScaleChanged(double value) => NotifyCalculatedProps();
}