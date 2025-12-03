using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable; // Necessario se usi tipi specifici, ma qui ToImmutable è su IBrush

namespace KomaLab.Controls;

public partial class GridBackground : Control
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
    
    // Penne cacheate
    private Pen? _gridPenVertical;
    private Pen? _gridPenHorizontal;
    
    private readonly double[] _dashPattern = { 15, 15 };
    private readonly double _dashPatternLength;

    private double _lastScaleForDash = -1;
    private double _lastOffsetXForDash = -1;
    private double _lastOffsetYForDash = -1;

    static GridBackground()
    {
        AffectsRender<GridBackground>(
            BackgroundProperty, OffsetXProperty, OffsetYProperty, ScaleProperty, BoundsProperty
        );
    }

    public GridBackground()
    {
        _dashPatternLength = _dashPattern.Sum();
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
        if (Background is not null)
            context.FillRectangle(Background, new Rect(Bounds.Size));

        // --- INIZIALIZZAZIONE PENNE (Con ToImmutable) ---
        if (_gridPenVertical == null)
        {
            // Creiamo il brush e lo "congeliamo" rendendolo immutabile
            var brush = new SolidColorBrush(Color.Parse("#272727")).ToImmutable();
            
            _gridPenVertical = new Pen(brush);
            _gridPenHorizontal = new Pen(brush);
        }
        // ------------------------------------------------

        double width = Bounds.Width;
        double height = Bounds.Height;
        
        if (width <= 0 || height <= 0 || Scale <= 0.001 ||
            double.IsNaN(width) || double.IsNaN(height) ||
            double.IsNaN(Scale) || double.IsNaN(OffsetX) || double.IsNaN(OffsetY))
        {
            return;
        }

        double scaledStep = _gridStep * Scale;
        if (scaledStep < 3) return;

        double currentOffsetX = OffsetX + _tempPanX;
        double currentOffsetY = OffsetY + _tempPanY;

        UpdateDashStyles(currentOffsetX, currentOffsetY);

        double startX = currentOffsetX % scaledStep; 
        if (startX > 0) startX -= scaledStep;

        for (double x = startX; x < width; x += scaledStep)
        {
            if (x >= -2 && x <= width + 2)
            {
                context.DrawLine(_gridPenVertical!, new Point(x, 0), new Point(x, height));
            }
        }

        double startY = currentOffsetY % scaledStep; 
        if (startY > 0) startY -= scaledStep;

        for (double y = startY; y < height; y += scaledStep)
        {
            if (y >= -2 && y <= height + 2)
            {
                context.DrawLine(_gridPenHorizontal!, new Point(0, y), new Point(width, y));
            }
        }
    }

    private void UpdateDashStyles(double currentX, double currentY)
    {
        double scaledDashOn = _dashPattern[0] * Scale;
        double scaledDashOff = _dashPattern[1] * Scale;
        double scaledPatternLength = _dashPatternLength * Scale;

        bool needsUpdate = Math.Abs(Scale - _lastScaleForDash) > 0.001 ||
                           Math.Abs(currentX - _lastOffsetXForDash) > 0.5 ||
                           Math.Abs(currentY - _lastOffsetYForDash) > 0.5;

        if (!needsUpdate && _gridPenVertical!.DashStyle != null) 
            return;

        if (scaledDashOn < 1 || scaledDashOff < 1)
        {
            _gridPenVertical!.DashStyle = null;
            _gridPenHorizontal!.DashStyle = null;
        }
        else
        {
            var scaledDashArray = new[] { scaledDashOn, scaledDashOff };

            double dashOffsetX = (-currentX % scaledPatternLength + scaledPatternLength) % scaledPatternLength;
            double dashOffsetY = (-currentY % scaledPatternLength + scaledPatternLength) % scaledPatternLength;

            _gridPenVertical!.DashStyle = new DashStyle(scaledDashArray, dashOffsetY);
            _gridPenHorizontal!.DashStyle = new DashStyle(scaledDashArray, dashOffsetX);
        }

        _lastScaleForDash = Scale;
        _lastOffsetXForDash = currentX;
        _lastOffsetYForDash = currentY;
    }
}