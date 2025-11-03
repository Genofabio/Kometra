using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KomaLab.Controls; // O Controls

public partial class GridBackground : Control
{
    // Proprietà Styled
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<GridBackground, IBrush?>(nameof(Background));
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }
    public static readonly StyledProperty<double> OffsetXProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(OffsetX));
    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(OffsetY));
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<GridBackground, double>(nameof(Scale), 1.0);
    
    public double OffsetX { get => GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
    public double OffsetY { get => GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    // Campi Privati
    private readonly double _gridStep = 200;
    private Pen? _gridPenVertical;
    private Pen? _gridPenHorizontal;
    private readonly double[] _dashPattern = { 15, 15 };
    private readonly double _dashPatternLength;
    
    // Costruttori
    static GridBackground()
    {
        AffectsRender<GridBackground>(
            BackgroundProperty,
            OffsetXProperty,
            OffsetYProperty,
            ScaleProperty,
            BoundsProperty
        );
    }

    public GridBackground()
    {
        InitializeComponent();
        _dashPatternLength = _dashPattern.Sum();
    }
    
    public override void Render(DrawingContext context)
    {
        // Disegno Sfondo
        if (Background is not null)
            context.FillRectangle(Background, new Rect(Bounds.Size));

        // Inizializzazione Penne
        if (_gridPenVertical == null)
        {
            var brush = new SolidColorBrush(Color.Parse("#232323"));
            _gridPenVertical = new Pen(brush);
            _gridPenHorizontal = new Pen(brush);
        }

        // Controlli di Sicurezza
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0 || Scale <= 0 ||
            double.IsNaN(width) || double.IsNaN(height) ||
            double.IsNaN(Scale) || double.IsNaN(OffsetX) || double.IsNaN(OffsetY))
        {
            return;
        }

        double scaledStep = _gridStep * Scale;
        if (scaledStep < 2)
            return;

        // Calcolo Stile Tratteggio Dinamico
        double scaledDashOn = _dashPattern[0] * Scale;
        double scaledDashOff = _dashPattern[1] * Scale;
        double scaledPatternLength = _dashPatternLength * Scale; 

        if (scaledDashOn < 1 || scaledDashOff < 1)
        {
            _gridPenVertical!.DashStyle = null;
            _gridPenHorizontal!.DashStyle = null;
        }
        else
        {
            var scaledDashArray = new[] { scaledDashOn, scaledDashOff };
            double dashOffsetX = (-OffsetX % scaledPatternLength + scaledPatternLength) % scaledPatternLength;
            double dashOffsetY = (-OffsetY % scaledPatternLength + scaledPatternLength) % scaledPatternLength;

            _gridPenVertical!.DashStyle = new DashStyle(scaledDashArray, dashOffsetY);
            _gridPenHorizontal!.DashStyle = new DashStyle(scaledDashArray, dashOffsetX);
        }
        
        // Disegno Linee Verticali
        double startX = OffsetX % scaledStep; 
        for (double x = startX; x < width; x += scaledStep)
        {
            context.DrawLine(_gridPenVertical, new Point(x, 0), new Point(x, height));
        }

        // Disegno Linee Orizzontali
        double startY = OffsetY % scaledStep; 
        for (double y = startY; y < height; y += scaledStep)
        {
            context.DrawLine(_gridPenHorizontal, new Point(0, y), new Point(width, y));
        }
    }
}