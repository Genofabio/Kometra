using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using KomaLab.ViewModels;

namespace KomaLab.Controls;

public class ConnectionControl : Control
{
    private ConnectionViewModel? _vm;
    
    // Penna statica per le linee normali (grigie) -> Performance massima
    private static readonly IPen DefaultPen = new Pen(new SolidColorBrush(Color.Parse("#99666666")), 12);

    // Proprietà di dipendenza
    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<ConnectionControl, bool>(nameof(IsHighlighted));

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    public ConnectionControl()
    {
        IsHitTestVisible = false; 
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        if (_vm != null)
        {
            _vm.Source.PropertyChanged -= OnNodePositionChanged;
            _vm.Target.PropertyChanged -= OnNodePositionChanged;
        }

        _vm = DataContext as ConnectionViewModel;

        if (_vm != null)
        {
            _vm.Source.PropertyChanged += OnNodePositionChanged;
            _vm.Target.PropertyChanged += OnNodePositionChanged;
        }
        
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsHighlightedProperty)
        {
            InvalidateVisual();
        }
    }

    private void OnNodePositionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BaseNodeViewModel.X) ||
            e.PropertyName == nameof(BaseNodeViewModel.Y) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetX) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetY))
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_vm == null) return;

        // 1. Calcolo coordinate
        var source = _vm.Source;
        var target = _vm.Target;

        var p1 = new Point(
            source.X + source.VisualOffsetX + (source.EstimatedTotalSize.Width / 2),
            source.Y + source.VisualOffsetY + (source.EstimatedTotalSize.Height / 2)
        );

        var p2 = new Point(
            target.X + target.VisualOffsetX + (target.EstimatedTotalSize.Width / 2),
            target.Y + target.VisualOffsetY + (target.EstimatedTotalSize.Height / 2)
        );

        // 2. Calcolo Bezier
        double deltaX = p2.X - p1.X;
        double offset = deltaX / 2;
        
        var cp1 = new Point(p1.X + offset, p1.Y);
        var cp2 = new Point(p2.X - offset, p2.Y);

        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(p1, false);
            c.CubicBezierTo(cp1, cp2, p2);
        }

        // 3. SCELTA PENNELLO (Qui abbiamo ripristinato il gradiente)
        IPen penToUse;

        if (!IsHighlighted)
        {
            // Caso veloce: Nessuna selezione -> Penna statica grigia
            penToUse = DefaultPen;
        }
        else
        {
            // Caso High-Quality: Calcoliamo il gradiente orientato
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(p1, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(p2, RelativeUnit.Absolute)
            };

            // Colori presi dal VM o definiti qui
            var highlightColor = _vm.HighlightColor; 
            var defaultColor = _vm.DefaultColor; 

            // Logica orientamento:
            // Se Source è selezionato: Viola -> Grigio
            // Se Target è selezionato: Grigio -> Viola
            if (_vm.Source.IsSelected)
            {
                gradient.GradientStops.Add(new GradientStop(highlightColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(highlightColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(defaultColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(defaultColor, 1.0));
            }
            else // Target Selected o entrambi
            {
                gradient.GradientStops.Add(new GradientStop(defaultColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(defaultColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(highlightColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(highlightColor, 1.0));
            }

            // Spessore maggiorato per l'highlight (es. 5.0)
            penToUse = new Pen(gradient, 14.0);
        }

        context.DrawGeometry(null, penToUse, geometry);
    }
}