using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels.Visualization;

public partial class BaseViewport : ObservableObject
{
    protected virtual double MinZoomLimit => 0.01; 
    protected virtual double MaxZoomLimit => 50.0; 
    protected virtual double ZoomStep => 1.25;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsZoomed))]
    private double _offsetX;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsZoomed))]
    private double _offsetY;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsZoomed))]
    private double _scale = 1.0;
    
    [ObservableProperty] 
    private Size _viewportSize;

    public bool IsZoomed => Math.Abs(Scale - 1.0) > 0.001;

    // --- Hooks per le sottoclassi ---
    partial void OnOffsetXChanged(double value) => NotifyStateChanged();
    partial void OnOffsetYChanged(double value) => NotifyStateChanged();
    partial void OnScaleChanged(double value) => NotifyStateChanged();
    partial void OnViewportSizeChanged(Size value) => NotifyStateChanged();

    protected virtual void NotifyStateChanged() { }

    // --- Matematica Pura ---

    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
    }

    public virtual void ApplyZoomAtPoint(double zoomDelta, Point centerPoint)
    {
        double oldScale = Scale;
        // Ora usiamo le proprietà virtuali, non più le costanti
        double newScale = Math.Clamp(oldScale * zoomDelta, MinZoomLimit, MaxZoomLimit);

        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        double ratio = newScale / oldScale;
        
        OffsetX = centerPoint.X - (centerPoint.X - OffsetX) * ratio;
        OffsetY = centerPoint.Y - (centerPoint.Y - OffsetY) * ratio;
        Scale = newScale;
    }

    public void ZoomIn() => ApplyZoomAtPoint(ZoomStep, new Point(ViewportSize.Width / 2.0, ViewportSize.Height / 2.0));
    public void ZoomOut() => ApplyZoomAtPoint(1.0 / ZoomStep, new Point(ViewportSize.Width / 2.0, ViewportSize.Height / 2.0));

    public Point ToWorldCoordinates(Point screenPoint)
    {
        if (Scale == 0) return new Point(0, 0);
        return new Point(
            (screenPoint.X - OffsetX) / Scale,
            (screenPoint.Y - OffsetY) / Scale
        );
    }
}