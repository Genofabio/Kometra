using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace KomaLab.Controls;

// RIMOSSO "partial" -> Ora è una classe normale
public class GridBackground : Control
{
    // --- PROPRIETÀ STYLED ---
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<GridBackground, IBrush?>(nameof(Background));
    
    public static readonly StyledProperty<double> OffsetXProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(OffsetX));
    
    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(OffsetY));
    
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(Scale), 1.0);
    
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public double OffsetX { get => GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
    public double OffsetY { get => GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    // --- CAMPI PRIVATI ---
    private double _tempPanX;
    private double _tempPanY;
    private readonly double _gridStep = 200; 
    
    private Pen _gridPen;
    
    private readonly double[] _baseDashPattern = { 15, 15 };
    private double _basePatternLength; 

    private double _lastScale = -1;
    private double _scaledPatternLength; 

    static GridBackground()
    {
        AffectsRender<GridBackground>(
            BackgroundProperty, OffsetXProperty, OffsetYProperty, ScaleProperty, BoundsProperty
        );
    }

    public GridBackground()
    {
        // --- QUI SOSTITUIAMO LO XAML ---
        // Impostiamo i default che prima avevi nello XAML
        ClipToBounds = false; 
        Background = new SolidColorBrush(Color.Parse("#121212")); 
        // -------------------------------

        _basePatternLength = _baseDashPattern.Sum();
        
        var brush = new SolidColorBrush(Color.Parse("#272727")).ToImmutable();
        _gridPen = new Pen(brush, 1.0);
    }
    
    public void SetVisualPan(double x, double y)
    {
        if (Math.Abs(_tempPanX - x) < 0.1 && Math.Abs(_tempPanY - y) < 0.1) 
            return;
        _tempPanX = x;
        _tempPanY = y;
        InvalidateVisual(); 
    }

    public override void Render(DrawingContext context)
    {
        // Disegna lo sfondo (gestito manualmente perché Control base non lo disegna da solo)
        if (Background is not null)
            context.FillRectangle(Background, new Rect(Bounds.Size));

        double width = Bounds.Width;
        double height = Bounds.Height;
        
        if (width <= 0 || height <= 0 || Scale <= 0.001) return;

        UpdatePenStyleForScale();

        double currentOffsetX = OffsetX + _tempPanX;
        double currentOffsetY = OffsetY + _tempPanY;
        double scaledStep = _gridStep * Scale;
        
        if (scaledStep < 10) return;

        // Logica allineamento tratteggio
        double patternOffsetY = currentOffsetY % _scaledPatternLength;
        if (patternOffsetY > 0) patternOffsetY -= _scaledPatternLength;
        
        double patternOffsetX = currentOffsetX % _scaledPatternLength;
        if (patternOffsetX > 0) patternOffsetX -= _scaledPatternLength;

        // Disegno Verticale
        double startX = currentOffsetX % scaledStep; 
        if (startX > 0) startX -= scaledStep;

        for (double x = startX; x < width; x += scaledStep)
        {
            context.DrawLine(_gridPen, new Point(x, patternOffsetY), new Point(x, height));
        }

        // Disegno Orizzontale
        double startY = currentOffsetY % scaledStep; 
        if (startY > 0) startY -= scaledStep;

        for (double y = startY; y < height; y += scaledStep)
        {
            context.DrawLine(_gridPen, new Point(patternOffsetX, y), new Point(width, y));
        }
    }

    private void UpdatePenStyleForScale()
    {
        if (Math.Abs(Scale - _lastScale) < 0.001) return;

        _lastScale = Scale;
        _scaledPatternLength = _basePatternLength * Scale;

        double dashOn = _baseDashPattern[0] * Scale;
        double dashOff = _baseDashPattern[1] * Scale;

        if (dashOn < 1 || dashOff < 1)
        {
             _gridPen.DashStyle = null;
        }
        else
        {
            _gridPen.DashStyle = new DashStyle(new[] { dashOn, dashOff }, 0);
        }
    }
}