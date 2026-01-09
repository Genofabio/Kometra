using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KomaLab.ViewModels;

namespace KomaLab.Controls;

public class ConnectionControl : Control
{
    private ConnectionViewModel? _vm;

    // --- COSTANTI ---
    private const double ThicknessNormal = 12.0;
    private const double ThicknessHighlight = 14.0;
    private const string InactiveColorKey = "ConnectionInactiveColor";
    
    // Costante per la categoria unica attuale
    private const string DefaultCategory = "Image"; 

    // --- CACHE ---
    // La chiave è solo 'bool' (isSelected) perché la categoria è sempre "Image"
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
        if (change.Property == IsHighlightedProperty) InvalidateVisual();
    }

    private void OnNodePositionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Controllo generico sulle proprietà che influenzano il disegno
        if (e.PropertyName == nameof(BaseNodeViewModel.X) ||
            e.PropertyName == nameof(BaseNodeViewModel.Y) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetX) ||
            e.PropertyName == nameof(BaseNodeViewModel.VisualOffsetY) ||
            e.PropertyName == nameof(BaseNodeViewModel.EstimatedTotalSize))
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
    /// Recupera il colore per la categoria fissa "Image".
    /// </summary>
    private Color GetImageCategoryColor(bool isSelected)
    {
        // Cache molto più semplice
        if (_resourceCache.TryGetValue(isSelected, out var cachedColor))
            return cachedColor;

        // Cerca sempre "ImageSelectionColor" o "ImageMainColor"
        string suffix = isSelected ? "SelectionColor" : "MainColor";
        string resourceKey = $"{DefaultCategory}{suffix}";

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

    // --- RENDERING ---

    public override void Render(DrawingContext context)
    {
        if (_vm == null) return;

        Color neutralColor = GetInactiveColor();

        if (_cachedDefaultPen == null)
        {
            _cachedDefaultPen = new Pen(new SolidColorBrush(neutralColor), ThicknessNormal);
        }

        // Calcolo geometria
        var source = _vm.Source;
        var target = _vm.Target;
        
        // (Nota: qui assumiamo che EstimatedTotalSize sia calcolato correttamente nel VM)
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

            // SEMPLIFICAZIONE: Usiamo sempre la logica "Image"
            var sourceColor = GetImageCategoryColor(_vm.Source.IsSelected);
            var targetColor = GetImageCategoryColor(_vm.Target.IsSelected);
            
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