using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KomaLab.ViewModels;

namespace KomaLab.Controls;

public class ConnectionControl : Control
{
    private ConnectionViewModel? _vm;
    private readonly Pen _pen;

    // Definiamo le proprietà per il Binding
    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<ConnectionControl, bool>(nameof(IsHighlighted));

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    public ConnectionControl()
    {
        // Penna riutilizzabile (ottimizzazione memoria)
        _pen = new Pen(new SolidColorBrush(Colors.Gray), 3);
        
        // Disabilitiamo l'HitTest per default (il mouse passa attraverso)
        // Se ti serve cliccarci sopra, mettilo a True
        IsHitTestVisible = false; 
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        // Scolleghiamo i vecchi eventi se il DataContext cambia
        if (_vm != null)
        {
            _vm.Source.PropertyChanged -= OnNodePositionChanged;
            _vm.Target.PropertyChanged -= OnNodePositionChanged;
        }

        _vm = DataContext as ConnectionViewModel;

        if (_vm != null)
        {
            // Ci colleghiamo direttamente ai nodi
            _vm.Source.PropertyChanged += OnNodePositionChanged;
            _vm.Target.PropertyChanged += OnNodePositionChanged;
            
            // Impostiamo il colore iniziale
            UpdatePenColor();
        }
        
        InvalidateVisual();
    }

    // Quando cambia la proprietà IsHighlighted (dal Binding)
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsHighlightedProperty)
        {
            UpdatePenColor();
            InvalidateVisual();
        }
    }

    private void OnNodePositionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Ridisegna SOLO se cambia posizione o dimensione
        if (e.PropertyName == nameof(BaseNodeViewModel.X) ||
            e.PropertyName == nameof(BaseNodeViewModel.Y) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetX) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetY))
        {
            // InvalidateVisual è "pigro": segna questo controllo come "sporco"
            // Il rendering avverrà al prossimo frame utile.
            InvalidateVisual();
        }
    }

    private void UpdatePenColor()
    {
        if (_vm == null) return;
        
        // Logica semplice per il colore: Viola se selezionato, Grigio se no
        if (IsHighlighted)
        {
            _pen.Brush = new SolidColorBrush(_vm.HighlightColor);
            _pen.Thickness = 5;
        }
        else
        {
            _pen.Brush = new SolidColorBrush(_vm.DefaultColor);
            _pen.Thickness = 3;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_vm == null) return;

        // 1. Calcolo coordinate (direttamente nella View)
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

        // 2. Calcolo Bezier (on-the-fly)
        // È velocissimo farlo qui, non serve caching complesso per una singola curva
        double deltaX = p2.X - p1.X;
        double offset = deltaX / 2;
        
        var cp1 = new Point(p1.X + offset, p1.Y);
        var cp2 = new Point(p2.X - offset, p2.Y);

        // 3. Disegno diretto
        // StreamGeometryContext è il modo più efficiente in assoluto
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(p1, false);
            c.CubicBezierTo(cp1, cp2, p2);
        }

        context.DrawGeometry(null, _pen, geometry);
    }
}