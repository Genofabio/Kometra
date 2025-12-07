using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KomaLab.Models;
using KomaLab.ViewModels;

namespace KomaLab.Controls;

public class ConnectionControl : Control
{
    private ConnectionViewModel? _vm;
    
    // --- PENNA STATICA DI DEFAULT ---
    // Usata per il 99% dei casi (linee non selezionate). Spessore 12.
    private static readonly IPen DefaultPen = new Pen(new SolidColorBrush(Color.Parse("#99666666")), 12);

    // --- CACHE COLORI ---
    // Usiamo una Tupla come chiave: (Categoria, ÈSelezionato?) -> Colore
    // Questo permette di cacheare separatamente il colore "Main" e il colore "Selection".
    private static readonly Dictionary<(NodeCategory, bool), Color> ResourceCache = new();

    // --- PROPRIETÀ ---
    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<ConnectionControl, bool>(nameof(IsHighlighted));

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    public ConnectionControl()
    {
        // Disabilitiamo l'HitTest per permettere al mouse di passare attraverso i cavi
        IsHitTestVisible = false; 
    }

    // --- GESTIONE EVENTI ---

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
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetY) ||
            e.PropertyName == nameof(BaseNodeViewModel.EstimatedTotalSize))
        {
            InvalidateVisual();
        }
    }

    // --- HELPER RECUPERO COLORI XAML ---

    private Color GetCategoryColor(NodeCategory category, bool isSelected)
    {
        // 1. Creiamo la chiave di cache composta
        var cacheKey = (category, isSelected);

        // 2. Controllo veloce in Cache (RAM)
        if (ResourceCache.TryGetValue(cacheKey, out var cachedColor))
            return cachedColor;

        // 3. Costruzione chiave risorsa XAML
        // Se isSelected = true  -> "ImageSelectionColor" (Viola Acceso)
        // Se isSelected = false -> "ImageMainColor"      (Viola Scuro)
        string suffix = isSelected ? "SelectionColor" : "MainColor";
        string resourceKey = $"{category}{suffix}";

        // 4. Lookup nello XAML
        if (Application.Current!.TryFindResource(resourceKey, out var res) && res is Color color)
        {
            ResourceCache[cacheKey] = color;
            return color;
        }

        // Fallback
        return Colors.Gray; 
    }
    
    public static void InvalidateColorCache()
    {
        ResourceCache.Clear();
    }

    // --- RENDERING ---

    public override void Render(DrawingContext context)
    {
        if (_vm == null) return;

        // 1. Calcolo Coordinate
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

        // 2. Calcolo Curva
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

        // 3. Scelta Pennello
        IPen penToUse;

        if (!IsHighlighted)
        {
            penToUse = DefaultPen;
        }
        else
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(p1, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(p2, RelativeUnit.Absolute)
            };

            // Richiediamo i colori specificando se i nodi sono selezionati
            var sourceColor = GetCategoryColor(_vm.Source.Category, _vm.Source.IsSelected);
            var targetColor = GetCategoryColor(_vm.Target.Category, _vm.Target.IsSelected);
            
            var neutralColor = Color.Parse("#99666666"); 

            if (_vm.Source.IsSelected)
            {
                // Sorgente Attiva: Colore Selezione -> Grigio
                gradient.GradientStops.Add(new GradientStop(sourceColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(sourceColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 1.0));
            }
            else 
            {
                // Target Attivo: Grigio -> Colore Selezione
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(targetColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(targetColor, 1.0));
            }

            penToUse = new Pen(gradient, 14.0);
        }

        // 4. Disegno
        context.DrawGeometry(null, penToUse, geometry);
    }
}