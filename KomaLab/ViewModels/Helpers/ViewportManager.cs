using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace KomaLab.ViewModels.Helpers;

/// <summary>
/// Gestisce tutto lo stato e la logica del viewport (pan, zoom,
/// e calcolo delle coordinate a schermo).
/// È un "sub-viewModel" posseduto dal ViewModel principale.
/// </summary>
public partial class ViewportManager : ObservableObject
{
    // --- 1. PROPRIETÀ DI INPUT (impostate dal ViewModel principale) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private Size _viewportSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private Size _imageSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private Point? _targetCoordinate; // Coordinata LOGICA (immagine)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private int _searchRadius; // Raggio LOGICO (pixel immagine)

    // --- 2. STATO INTERNO (per il pan/zoom) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private double _offsetX = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private double _offsetY = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenX))]
    [NotifyPropertyChangedFor(nameof(TargetMarkerScreenY))]
    [NotifyPropertyChangedFor(nameof(SearchBoxLeft))] // E tutti gli altri del riquadro
    [NotifyPropertyChangedFor(nameof(SearchBoxTop))]
    [NotifyPropertyChangedFor(nameof(SearchBoxWidth))]
    [NotifyPropertyChangedFor(nameof(SearchBoxHeight))]
    private double _scale = 1.0;

    // --- 3. PROPRIETÀ CALCOLATE (per il binding della View) ---

    public double TargetMarkerScreenX => TargetCoordinate.HasValue ? (TargetCoordinate.Value.X * Scale) + OffsetX : 0;
    public double TargetMarkerScreenY => TargetCoordinate.HasValue ? (TargetCoordinate.Value.Y * Scale) + OffsetY : 0;

    // Proprietà per il riquadro di ricerca (pixel schermo)
    [ObservableProperty] private double _searchBoxLeft;
    [ObservableProperty] private double _searchBoxTop;
    [ObservableProperty] private double _searchBoxWidth;
    [ObservableProperty] private double _searchBoxHeight;

    // --- 4. METODI PUBBLICI (chiamati dal ViewModel) ---

    public void ApplyZoomAtPoint(double scaleFactor, Point viewportZoomPoint)
    {
        double oldScale = Scale;
        double newScale = Math.Clamp(oldScale * scaleFactor, 0.01, 20);
        OffsetX = viewportZoomPoint.X - (viewportZoomPoint.X - OffsetX) * (newScale / oldScale);
        OffsetY = viewportZoomPoint.Y - (viewportZoomPoint.Y - OffsetY) * (newScale / oldScale);
        Scale = newScale;
        // Le notifiche vengono inviate automaticamente
    }

    public void ApplyPan(double deltaX, double deltaY)
    {
        OffsetX += deltaX;
        OffsetY += deltaY;
        // Le notifiche vengono inviate automaticamente
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
        double padding = 0.98;
        double scaleX = (ViewportSize.Width * padding) / ImageSize.Width;
        double scaleY = (ViewportSize.Height * padding) / ImageSize.Height;
        Scale = Math.Min(scaleX, scaleY);
        OffsetX = (ViewportSize.Width - (ImageSize.Width * Scale)) / 2;
        OffsetY = (ViewportSize.Height - (ImageSize.Height * Scale)) / 2;
        // Le notifiche vengono inviate automaticamente
    }

    // --- 5. LOGICA INTERNA (per ricalcolare) ---

    // Ogni volta che una proprietà di input o di stato cambia,
    // ricalcoliamo le coordinate a schermo.

    partial void OnViewportSizeChanged(Size value) => UpdateScreenCalculations();
    partial void OnImageSizeChanged(Size value) => UpdateScreenCalculations();
    partial void OnTargetCoordinateChanged(Point? value) => UpdateScreenCalculations();
    partial void OnSearchRadiusChanged(int value) => UpdateScreenCalculations();
    partial void OnOffsetXChanged(double value) => UpdateScreenCalculations();
    partial void OnOffsetYChanged(double value) => UpdateScreenCalculations();
    partial void OnScaleChanged(double value) => UpdateScreenCalculations();

    private void UpdateScreenCalculations()
    {
        // 1. Aggiorna il mirino
        OnPropertyChanged(nameof(TargetMarkerScreenX));
        OnPropertyChanged(nameof(TargetMarkerScreenY));

        // 2. Aggiorna il riquadro di ricerca
        if (!TargetCoordinate.HasValue || Scale == 0)
        {
            SearchBoxLeft = 0;
            SearchBoxTop = 0;
            SearchBoxWidth = 0;
            SearchBoxHeight = 0;
            return;
        }

        double halfSizeScreen = SearchRadius * Scale;
        double fullSizeScreen = halfSizeScreen * 2;
        double centerXScreen = (TargetCoordinate.Value.X * Scale) + OffsetX;
        double centerYScreen = (TargetCoordinate.Value.Y * Scale) + OffsetY;

        SearchBoxLeft = centerXScreen - halfSizeScreen;
        SearchBoxTop = centerYScreen - halfSizeScreen;
        SearchBoxWidth = fullSizeScreen;
        SearchBoxHeight = fullSizeScreen;
    }
}