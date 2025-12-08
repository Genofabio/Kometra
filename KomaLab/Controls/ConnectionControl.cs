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

    // --- COSTANTI DI CONFIGURAZIONE (Spessori) ---
    private const double ThicknessNormal = 12.0;
    private const double ThicknessHighlight = 14.0;
    
    // Chiave della risorsa definita in AppPalette.axaml
    private const string InactiveColorKey = "ConnectionInactiveColor";

    // --- CACHE & STATO ---
    
    // Cache per i colori delle categorie (Categoria, Selected) -> Colore
    private static readonly Dictionary<(NodeCategory, bool), Color> _resourceCache = new();
    
    // Cache specifica per il colore inattivo (Grigio)
    private static Color? _cachedInactiveColor;
    
    // Cache per la penna di default (ricreata solo se necessario)
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

    /// <summary>
    /// Recupera il colore neutro (grigio) dalla Palette.
    /// </summary>
    private Color GetInactiveColor()
    {
        // 1. Controllo cache veloce
        if (_cachedInactiveColor.HasValue) 
            return _cachedInactiveColor.Value;

        // 2. Lookup nello XAML usando la chiave costante
        if (Application.Current!.TryFindResource(InactiveColorKey, out var res) && res is Color color)
        {
            _cachedInactiveColor = color;
            return color;
        }

        // Fallback estremo se la risorsa manca
        return Colors.Gray;
    }

    /// <summary>
    /// Recupera il colore specifico della categoria (Main o Selection).
    /// </summary>
    private Color GetCategoryColor(NodeCategory category, bool isSelected)
    {
        var cacheKey = (category, isSelected);

        if (_resourceCache.TryGetValue(cacheKey, out var cachedColor))
            return cachedColor;

        string suffix = isSelected ? "SelectionColor" : "MainColor";
        string resourceKey = $"{category}{suffix}";

        if (Application.Current!.TryFindResource(resourceKey, out var res) && res is Color color)
        {
            _resourceCache[cacheKey] = color;
            return color;
        }

        return Colors.Gray; 
    }
    
    public static void InvalidateColorCache()
    {
        _resourceCache.Clear();
        _cachedInactiveColor = null;
        _cachedDefaultPen = null; // Forziamo la ricreazione della penna al prossimo frame
    }

    // --- RENDERING ---

    public override void Render(DrawingContext context)
    {
        if (_vm == null) return;

        // 1. Recupera il colore neutro (serve sia per DefaultPen che per Gradient)
        // Questo rimuove il magic number "#99666666" dal metodo Render
        Color neutralColor = GetInactiveColor();

        // 2. Gestione Penna Default (Lazy Loading)
        // Se non esiste ancora, la creiamo usando il colore recuperato dalla palette
        if (_cachedDefaultPen == null)
        {
            _cachedDefaultPen = new Pen(new SolidColorBrush(neutralColor), ThicknessNormal);
        }

        // 3. Calcolo Geometria (Invariato)
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

        // 4. Scelta Pennello
        IPen penToUse;

        if (!IsHighlighted)
        {
            // Usa la penna cacheata (colore preso dalla Palette)
            penToUse = _cachedDefaultPen;
        }
        else
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(p1, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(p2, RelativeUnit.Absolute)
            };

            var sourceColor = GetCategoryColor(_vm.Source.Category, _vm.Source.IsSelected);
            var targetColor = GetCategoryColor(_vm.Target.Category, _vm.Target.IsSelected);
            
            // Usiamo 'neutralColor' recuperato dinamicamente, non più Color.Parse(...)
            
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

            // Usiamo la costante ThicknessHighlight (14.0)
            penToUse = new Pen(gradient, ThicknessHighlight);
        }

        // 5. Disegno
        context.DrawGeometry(null, penToUse, geometry);
    }
}