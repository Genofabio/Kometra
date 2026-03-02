using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kometra.ViewModels.Nodes;

namespace Kometra.Controls;

public class ConnectionControl : Control
{
    private ConnectionViewModel? _vm;

    // --- COSTANTI GEOMETRICHE ---
    private const double ThicknessNormal = 12.0;    
    private const double ThicknessHighlight = 14.0; 

    // --- STYLED PROPERTIES ---
    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<ConnectionControl, bool>(nameof(IsHighlighted));

    public static readonly StyledProperty<IBrush> SelectedBrushProperty =
        AvaloniaProperty.Register<ConnectionControl, IBrush>(nameof(SelectedBrush));

    public static readonly StyledProperty<IBrush> DefaultBrushProperty =
        AvaloniaProperty.Register<ConnectionControl, IBrush>(nameof(DefaultBrush));

    public static readonly StyledProperty<IBrush> InactiveBrushProperty =
        AvaloniaProperty.Register<ConnectionControl, IBrush>(nameof(InactiveBrush));

    public bool IsHighlighted { get => GetValue(IsHighlightedProperty); set => SetValue(IsHighlightedProperty, value); }
    public IBrush SelectedBrush { get => GetValue(SelectedBrushProperty); set => SetValue(SelectedBrushProperty, value); }
    public IBrush DefaultBrush { get => GetValue(DefaultBrushProperty); set => SetValue(DefaultBrushProperty, value); }
    public IBrush InactiveBrush { get => GetValue(InactiveBrushProperty); set => SetValue(InactiveBrushProperty, value); }

    static ConnectionControl()
    {
        // Ottimizzazione Avalonia: ridisegna solo se cambiano queste proprietà
        AffectsRender<ConnectionControl>(IsHighlightedProperty, SelectedBrushProperty, DefaultBrushProperty, InactiveBrushProperty);
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
            
            // Sincronizzazione iniziale dello stato
            UpdateLocalState();
        }
        
        InvalidateVisual();
    }

    private void OnNodePositionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ridisegna se cambiano posizione, offset o stato di selezione
        if (e.PropertyName == "X" || e.PropertyName == "Y" || 
            e.PropertyName == "VisualOffsetX" || e.PropertyName == "VisualOffsetY" ||
            e.PropertyName == "EstimatedTotalSize" || e.PropertyName == "IsSelected")
        {
            UpdateLocalState();
            InvalidateVisual();
        }
    }

    private void UpdateLocalState()
    {
        if (_vm != null)
        {
            // Sincronizziamo la proprietà del controllo con quella del VM
            IsHighlighted = _vm.Source.IsSelected || _vm.Target.IsSelected;
        }
    }

    private Color GetColor(IBrush? brush)
    {
        if (brush is SolidColorBrush scb) return scb.Color;
        return Colors.Gray; // Questo è il grigio che vedi se lo XAML non passa il pennello
    }

    public override void Render(DrawingContext context)
    {
        if (_vm == null || _vm.Source == null || _vm.Target == null) return;

        // 1. CALCOLO GEOMETRIA (Dinamica durante il trascinamento)
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

        // 2. RENDERING
        if (!IsHighlighted)
        {
            // Stato normale: usa il pennello InactiveBrush caricato dallo XAML
            context.DrawGeometry(null, new Pen(InactiveBrush ?? Brushes.Gray, ThicknessNormal), geometry);
        }
        else
        {
            // STATO EVIDENZIATO: La sfumatura usa p1 e p2 come ancore assolute
            // In questo modo ruota e si allunga perfettamente con la linea
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(p1, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(p2, RelativeUnit.Absolute)
            };

            Color neutralColor = GetColor(InactiveBrush);
            Color selectionColor = GetColor(SelectedBrush);
            
            // Logica originale: la "luce" segue il nodo selezionato
            if (source.IsSelected)
            {
                gradient.GradientStops.Add(new GradientStop(selectionColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(selectionColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 1.0));
            }
            else 
            {
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(selectionColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(selectionColor, 1.0));
            }

            context.DrawGeometry(null, new Pen(gradient, ThicknessHighlight), geometry);
        }
    }
}