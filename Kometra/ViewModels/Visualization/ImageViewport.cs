using System;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kometra.ViewModels.Visualization;

public partial class ImageViewport : BaseViewport
{
    [ObservableProperty] 
    private Size _imageSize; 

    // --- Override ResetView (Logica specifica per Immagini) ---
    public virtual void ResetView()
    {
        if (ImageSize.Width <= 0 || ImageSize.Height <= 0 || 
            ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            Scale = 1.0; OffsetX = 0; OffsetY = 0;
            return;
        }

        double padding = 1.0;
        double scaleX = (ViewportSize.Width * padding) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / ImageSize.Height;

        Scale = Math.Min(scaleX, scaleY);
        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2.0;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2.0;
    }

    // --- Hooks ---
    partial void OnImageSizeChanged(Size value) => NotifyStateChanged();

    // --- Utility Coordinate Immagine ---
    public Point ToImageCoordinates(Point screenPoint) => ToWorldCoordinates(screenPoint);

    public Point ToScreenCoordinates(Point imagePoint)
    {
        return new Point(
            (imagePoint.X * Scale) + OffsetX,
            (imagePoint.Y * Scale) + OffsetY
        );
    }
}