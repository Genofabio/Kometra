using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KomaLab.ViewModels.Nodes; // Namespace corretto per ConnectionViewModel

namespace KomaLab.Controls;

public class ConnectionControl : Control
{
    private ConnectionViewModel? _vm;

    // --- COSTANTI (VALORI ORIGINALI RIPRISTINATI) ---
    private const double ThicknessNormal = 12.0;    // Ripristinato a 12
    private const double ThicknessHighlight = 14.0; // Ripristinato a 14
    
    // --- CHIAVI RISORSE (Mappate su AppPalette.axaml) ---
    private const string InactiveColorKey = "Color.Grey.50";      // Era ConnectionInactiveColor
    private const string MainColorKey = "Color.Purple.Deep";      // Era ImageMainColor
    private const string SelectionColorKey = "Color.Purple.Base"; // Era ImageSelectionColor

    // --- CACHE ---
    private static readonly Dictionary<bool, Color> _resourceCache = new();
    private static Color? _cachedInactiveColor;
    private static IPen? _cachedDefaultPen;

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
        IsHitTestVisible = false; 
    }

    // --- GESTIONE EVENTI ---
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        
        // Disiscrizione vecchi eventi
        if (_vm != null)
        {
            if (_vm.Source != null) _vm.Source.PropertyChanged -= OnNodePositionChanged;
            if (_vm.Target != null) _vm.Target.PropertyChanged -= OnNodePositionChanged;
        }

        _vm = DataContext as ConnectionViewModel;

        // Iscrizione nuovi eventi
        if (_vm != null)
        {
            if (_vm.Source != null) _vm.Source.PropertyChanged += OnNodePositionChanged;
            if (_vm.Target != null) _vm.Target.PropertyChanged += OnNodePositionChanged;
        }
        
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsHighlightedProperty) InvalidateVisual();
    }

    private void OnNodePositionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Ridisegna se cambiano posizione o dimensione
        if (e.PropertyName == "X" || e.PropertyName == "Y" || 
            e.PropertyName == "VisualOffsetX" || e.PropertyName == "VisualOffsetY" ||
            e.PropertyName == "EstimatedTotalSize")
        {
            InvalidateVisual();
        }
    }

    // --- HELPER COLORI ---

    private Color GetInactiveColor()
    {
        if (_cachedInactiveColor.HasValue) return _cachedInactiveColor.Value;

        if (Application.Current!.TryFindResource(InactiveColorKey, out var res) && res is Color color)
        {
            _cachedInactiveColor = color;
            return color;
        }
        return Colors.Gray;
    }

    /// <summary>
    /// Recupera il colore usando le nuove chiavi della Palette, mantenendo la logica originale.
    /// </summary>
    private Color GetImageCategoryColor(bool isSelected)
    {
        if (_resourceCache.TryGetValue(isSelected, out var cachedColor))
            return cachedColor;

        // Mappa la logica originale sulle nuove chiavi
        string resourceKey = isSelected ? SelectionColorKey : MainColorKey;

        if (Application.Current!.TryFindResource(resourceKey, out var res) && res is Color color)
        {
            _resourceCache[isSelected] = color;
            return color;
        }

        return Colors.Gray; 
    }
    
    public static void InvalidateColorCache()
    {
        _resourceCache.Clear();
        _cachedInactiveColor = null;
        _cachedDefaultPen = null; 
    }

    // --- RENDERING (LOGICA ORIGINALE) ---

    public override void Render(DrawingContext context)
    {
        if (_vm == null || _vm.Source == null || _vm.Target == null) return;

        Color neutralColor = GetInactiveColor();

        if (_cachedDefaultPen == null)
        {
            _cachedDefaultPen = new Pen(new SolidColorBrush(neutralColor), ThicknessNormal);
        }

        // Calcolo geometria
        var source = _vm.Source;
        var target = _vm.Target;
        
        // Calcolo punti centrali
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

        IPen penToUse;

        if (!IsHighlighted)
        {
            penToUse = _cachedDefaultPen;
        }
        else
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(p1, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(p2, RelativeUnit.Absolute)
            };

            // Recupera colori in base allo stato di selezione
            var sourceColor = GetImageCategoryColor(_vm.Source.IsSelected);
            var targetColor = GetImageCategoryColor(_vm.Target.IsSelected);
            
            // LOGICA GRADIENTE ORIGINALE (0.0 -> 0.45 -> 0.55 -> 1.0)
            if (_vm.Source.IsSelected)
            {
                gradient.GradientStops.Add(new GradientStop(sourceColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(sourceColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 1.0));
            }
            else 
            {
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.0));
                gradient.GradientStops.Add(new GradientStop(neutralColor, 0.45));
                gradient.GradientStops.Add(new GradientStop(targetColor, 0.55));
                gradient.GradientStops.Add(new GradientStop(targetColor, 1.0));
            }

            penToUse = new Pen(gradient, ThicknessHighlight);
        }

        context.DrawGeometry(null, penToUse, geometry);
    }
}