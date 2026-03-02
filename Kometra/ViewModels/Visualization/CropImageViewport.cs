using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kometra.ViewModels.Visualization;

public partial class CropImageViewport : ImageViewport
{
    // Dati logici (Coordinate Immagine Reali)
    [ObservableProperty] private Point? _cropCenter;
    [ObservableProperty] private double _cropWidth;
    [ObservableProperty] private double _cropHeight;

    // Visibilità
    [ObservableProperty] private bool _isCropVisible;
    
    // =======================================================================
    // COORDINATE SCHERMO (Calcolate per l'Overlay)
    // =======================================================================
    // Ogni volta che cambiano X, Y, W o H, dobbiamo notificare che anche 
    // i punti della croce (Crosshair) sono cambiati.

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXLess10))] 
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXPlus10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYPlus10))]
    private double _screenRectX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXPlus10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYPlus10))] 
    private double _screenRectY;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXPlus10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYPlus10))] 
    private double _screenRectW;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterXPlus10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYLess10))]
    [NotifyPropertyChangedFor(nameof(ScreenRectCenterYPlus10))]
    private double _screenRectH;

    // =======================================================================
    // CROSSHAIR GEOMETRY (Mirino Centrale)
    // =======================================================================
    
    // Orizzontale
    public Point ScreenRectCenterXLess10 => new Point(ScreenRectX + (ScreenRectW / 2) - 10, ScreenRectY + (ScreenRectH / 2));
    public Point ScreenRectCenterXPlus10 => new Point(ScreenRectX + (ScreenRectW / 2) + 10, ScreenRectY + (ScreenRectH / 2));
    
    // Verticale
    public Point ScreenRectCenterYLess10 => new Point(ScreenRectX + (ScreenRectW / 2), ScreenRectY + (ScreenRectH / 2) - 10);
    public Point ScreenRectCenterYPlus10 => new Point(ScreenRectX + (ScreenRectW / 2), ScreenRectY + (ScreenRectH / 2) + 10);

    // =======================================================================

    public void SetCropGeometry(Point center, double width, double height)
    {
        CropCenter = center;
        CropWidth = width;
        CropHeight = height;
        IsCropVisible = true;
        RecalculateScreenRect();
    }

    public void ClearCrop()
    {
        IsCropVisible = false;
        CropCenter = null;
    }

    private void RecalculateScreenRect()
    {
        if (!IsCropVisible || !CropCenter.HasValue) return;

        double imgLeft = CropCenter.Value.X - (CropWidth / 2.0);
        double imgTop = CropCenter.Value.Y - (CropHeight / 2.0);

        // Convertiamo da Pixel Immagine a Pixel Schermo usando Scale e Offset correnti
        ScreenRectX = (imgLeft * Scale) + OffsetX;
        ScreenRectY = (imgTop * Scale) + OffsetY;
        ScreenRectW = CropWidth * Scale;
        ScreenRectH = CropHeight * Scale;
    }

    // Intercettiamo i cambiamenti della classe base (Zoom/Pan) per ricalcolare il rettangolo
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(Scale) || 
            e.PropertyName == nameof(OffsetX) || 
            e.PropertyName == nameof(OffsetY))
        {
            RecalculateScreenRect();
        }
    }
}