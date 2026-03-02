using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kometra.Controls;

public class GridBackground : Control
{
    // --- PROPRIETÀ STYLED ---
    
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<GridBackground, IBrush?>(nameof(Background));
    
    // NUOVA PROPRIETÀ: Permette di bindare il colore delle linee da XAML/Palette
    public static readonly StyledProperty<IBrush?> GridLinesBrushProperty =
        AvaloniaProperty.Register<GridBackground, IBrush?>(nameof(GridLinesBrush));

    public static readonly StyledProperty<double> OffsetXProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(OffsetX));
    
    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(OffsetY));
    
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(Scale), 1.0);
    
    // Wrapper C#
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? GridLinesBrush
    {
        get => GetValue(GridLinesBrushProperty);
        set => SetValue(GridLinesBrushProperty, value);
    }

    public double OffsetX { get => GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
    public double OffsetY { get => GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    // --- CAMPI PRIVATI ---
    private double _tempPanX;
    private double _tempPanY;
    private readonly double _gridStep = 200; 
    
    private Pen _gridPen; // La penna verrà creata dinamicamente
    
    private readonly double[] _baseDashPattern = { 15, 15 };
    private double _basePatternLength; 

    private double _lastScale = -1;
    private double _scaledPatternLength; 

    static GridBackground()
    {
        // Diciamo ad Avalonia che se cambia il colore delle linee, deve ridisegnare
        AffectsRender<GridBackground>(
            BackgroundProperty, 
            GridLinesBrushProperty, // <--- Aggiunto
            OffsetXProperty, 
            OffsetYProperty, 
            ScaleProperty, 
            BoundsProperty
        );
    }

    public GridBackground()
    {
        ClipToBounds = false; 
        
        // RIMOSSO: Background = ... ("#121212"); 
        // Ora lasciamo che sia null di default, ci penserà lo XAML a settarlo.

        _basePatternLength = _baseDashPattern.Sum();
        
        // Inizializziamo una penna di default (trasparente o dummy)
        // Verrà sovrascritta non appena lo XAML applica il GridLinesBrush
        _gridPen = new Pen(Brushes.Transparent, 1.0);
    }
    
    // --- GESTIONE CAMBIO PROPRIETÀ ---
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Se cambia il pennello delle linee (es. cambio tema o init iniziale), ricreiamo la Penna
        if (change.Property == GridLinesBrushProperty)
        {
            var newBrush = change.NewValue as IBrush;
            // Creiamo una nuova penna immutabile per performance
            _gridPen = new Pen(newBrush ?? Brushes.Gray, 1.0);
            
            // Forziamo il ricalcolo dello stile tratteggiato
            _lastScale = -1; 
            UpdatePenStyleForScale();
        }
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
        // 1. Disegna Sfondo
        if (Background is not null)
            context.FillRectangle(Background, new Rect(Bounds.Size));

        double width = Bounds.Width;
        double height = Bounds.Height;
        
        if (width <= 0 || height <= 0 || Scale <= 0.001) return;
        
        // Se non abbiamo un pennello per le linee, non disegniamo la griglia
        if (GridLinesBrush == null) return;

        UpdatePenStyleForScale();

        double currentOffsetX = OffsetX + _tempPanX;
        double currentOffsetY = OffsetY + _tempPanY;
        double scaledStep = _gridStep * Scale;
        
        if (scaledStep < 10) return;

        // Logica allineamento
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
        // Ricalcoliamo solo se la scala cambia
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