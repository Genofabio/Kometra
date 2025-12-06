using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KomaLab.ViewModels;

public partial class ConnectionViewModel : ObservableObject, IDisposable
{
    private static readonly Color DefaultColor = Color.Parse("#666666");
    private static readonly Color HighlightColor = Color.Parse("#8058E8");
    private static readonly IBrush DefaultBrush = new SolidColorBrush(DefaultColor, 0.6);

    public BaseNodeViewModel Source { get; }
    public BaseNodeViewModel Target { get; }

    private Point _startPoint;
    private Point _endPoint;
    private bool _isSourceSelected;
    private bool _isTargetSelected;

    [ObservableProperty]
    private Geometry _curveGeometry;
    
    [ObservableProperty]
    private IBrush _strokeBrush = DefaultBrush;

    public bool IsHighlighted => _isSourceSelected || _isTargetSelected;

    public ConnectionViewModel(BaseNodeViewModel source, BaseNodeViewModel target)
    {
        Source = source;
        Target = target;
        _curveGeometry = new StreamGeometry();

        Source.PropertyChanged += OnNodePropertyChanged;
        Target.PropertyChanged += OnNodePropertyChanged;

        UpdateGeometry();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BaseNodeViewModel.IsSelected))
        {
            _isSourceSelected = Source.IsSelected;
            _isTargetSelected = Target.IsSelected;
            OnPropertyChanged(nameof(IsHighlighted));
            UpdateBrush();
            return; 
        }
        
        // Nella versione originale, qui ascoltiamo SOLO le modifiche geometriche.
        // La selezione viene gestita esternamente dalla Board.
        if (e.PropertyName == nameof(BaseNodeViewModel.X) ||
            e.PropertyName == nameof(BaseNodeViewModel.Y) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetX) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetY) ||
            e.PropertyName == nameof(BaseNodeViewModel.EstimatedTotalSize))
        {
            UpdateGeometry();
        }
    }

    public void SetSelectionState(bool sourceSelected, bool targetSelected)
    {
        if (_isSourceSelected == sourceSelected && _isTargetSelected == targetSelected) return;

        _isSourceSelected = sourceSelected;
        _isTargetSelected = targetSelected;

        OnPropertyChanged(nameof(IsHighlighted));
        UpdateBrush();
    }

    private void UpdateGeometry()
    {
        if (Source.EstimatedTotalSize.Width == 0 || Target.EstimatedTotalSize.Width == 0) return;

        // 1. Punti di Ancoraggio (Invariati)
        _startPoint = new Point(
            (Source.X + Source.VisualOffsetX) + (Source.EstimatedTotalSize.Width / 2),
            (Source.Y + Source.VisualOffsetY) + (Source.EstimatedTotalSize.Height / 2)
        );

        _endPoint = new Point(
            (Target.X + Target.VisualOffsetX) + (Target.EstimatedTotalSize.Width / 2),
            (Target.Y + Target.VisualOffsetY) + (Target.EstimatedTotalSize.Height / 2)
        );

        // 2. Calcolo Simmetrico
        // Togliamo Math.Abs. 
        // Se deltaX è positivo (Target a destra), offset è positivo -> curva a destra.
        // Se deltaX è negativo (Target a sinistra), offset è negativo -> curva a sinistra.
        double deltaX = _endPoint.X - _startPoint.X;
        
        // Usiamo esattamente metà distanza. 
        // Questo fa sì che i punti di controllo abbiano la stessa coordinata X (il punto medio).
        // È la formula matematica per la curva più morbida possibile tra due punti con tangenti orizzontali.
        double controlOffset = deltaX / 2;

        // Aggiungiamo un piccolo "smoothing" se i nodi sono perfettamente allineati in verticale (deltaX ~ 0)
        // Se vuoi che resti una linea dritta verticale, rimuovi questo if.
        // Se vuoi che faccia una piccola curva a S anche in verticale, lascia una soglia minima.
        // Ma per un grafo non direzionale, la pura linea verticale è spesso preferibile.
        
        var controlPoint1 = new Point(_startPoint.X + controlOffset, _startPoint.Y);
        
        // Nota il MENO qui: stiamo sottraendo l'offset dal punto finale.
        // Se offset è positivo (destra), end - offset torna indietro verso il centro.
        // Se offset è negativo (sinistra), end - (-offset) va avanti verso il centro.
        var controlPoint2 = new Point(_endPoint.X - controlOffset, _endPoint.Y);

        // 3. Disegno
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(_startPoint, false);
            ctx.CubicBezierTo(controlPoint1, controlPoint2, _endPoint);
        }

        CurveGeometry = geometry;

        if (IsHighlighted) UpdateBrush();
    }

    private void UpdateBrush()
    {
        // 1. Reset base
        if (!_isSourceSelected && !_isTargetSelected)
        {
            StrokeBrush = DefaultBrush;
            return;
        }

        // 2. Creiamo il gradiente lineare Assoluto
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(_startPoint, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(_endPoint, RelativeUnit.Absolute)
        };

        if (_isSourceSelected)
        {
            // === SORGENTE SELEZIONATA ===
            gradient.GradientStops.Add(new GradientStop(HighlightColor, 0.0));
            gradient.GradientStops.Add(new GradientStop(HighlightColor, 0.45));
            gradient.GradientStops.Add(new GradientStop(DefaultColor, 0.55));
            gradient.GradientStops.Add(new GradientStop(DefaultColor, 1.0));
        }
        else if (_isTargetSelected)
        {
            // === TARGET SELEZIONATO ===
            gradient.GradientStops.Add(new GradientStop(DefaultColor, 0.0));
            gradient.GradientStops.Add(new GradientStop(DefaultColor, 0.45));
            gradient.GradientStops.Add(new GradientStop(HighlightColor, 0.55));
            gradient.GradientStops.Add(new GradientStop(HighlightColor, 1.0));
        }

        StrokeBrush = gradient;
    }

    public void Dispose()
    {
        Source.PropertyChanged -= OnNodePropertyChanged;
        Target.PropertyChanged -= OnNodePropertyChanged;
        GC.SuppressFinalize(this);
    }
}