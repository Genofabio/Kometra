using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KomaLab.ViewModels;
using System.Threading.Tasks;

namespace KomaLab.Views;

public partial class PosterizationToolView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;

    public PosterizationToolView()
    {
        InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await Task.Delay(100); // Hack per layout
        if (DataContext is PosterizationToolViewModel vm)
        {
            var border = this.FindControl<Border>("PreviewBorder");
            if (border != null) vm.Viewport.ViewportSize = border.Bounds.Size;
            vm.ResetView();
        }
    }

    // --- LOGICA IDENTICA AD ALIGNMENT TOOL ---

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border) return;
        var props = e.GetCurrentPoint(border).Properties;
        
        // Pan con tasto centrale
        if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPointerPos = e.GetPosition(border);
            e.Pointer.Capture(border);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _lastPointerPos == null || sender is not Border border) return;
        if (DataContext is not PosterizationToolViewModel vm) return;

        var currentPos = e.GetPosition(border);
        var delta = currentPos - _lastPointerPos.Value;
        
        vm.ApplyPan(delta.X, delta.Y);
        _lastPointerPos = currentPos;
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PosterizationToolViewModel vm) return;
        if (sender is not Border border) return;

        // --- 1. ZOOM (CTRL + WHEEL) ---
        // Priorità allo Zoom se si preme Control
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var mousePos = e.GetPosition(border);
            // Rotella Su = Zoom In, Rotella Giù = Zoom Out
            double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
            vm.ApplyZoomAtPoint(factor, mousePos);
            e.Handled = true;
            return;
        }

        // --- 2. GESTIONE SOGLIE (STRETCHING) ---
        
        // Calcoliamo uno step proporzionale al range attuale (es. 5%)
        // Questo rende la regolazione precisa sia su range ampi che stretti
        double currentRange = vm.WhitePoint - vm.BlackPoint;
        if (currentRange < 1) currentRange = 100; // Fallback di sicurezza
        
        double step = currentRange * 0.05; 

        // Se ruotiamo verso il basso (Delta < 0), sottraiamo il valore
        if (e.Delta.Y < 0) step = -step;

        // LOGICA RICHIESTA:
        // Shift -> Modifica NERO (Black Point)
        // Default -> Modifica BIANCO (White Point)

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
             // Modifica Punto di Nero
             double newBlack = vm.BlackPoint + step;
             
             // Vincolo: Il Nero non può superare il Bianco (meno un margine di sicurezza)
             if (newBlack < vm.WhitePoint - 1) 
             {
                 vm.BlackPoint = newBlack;
             }
        }
        else
        {
             // Modifica Punto di Bianco (Comportamento Standard)
             double newWhite = vm.WhitePoint + step;
             
             // Vincolo: Il Bianco non può scendere sotto il Nero
             if (newWhite > vm.BlackPoint + 1)
             {
                 vm.WhitePoint = newWhite;
             }
        }

        e.Handled = true;
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e) { if (DataContext is PosterizationToolViewModel vm) vm.ZoomIn(); }
    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) { if (DataContext is PosterizationToolViewModel vm) vm.ZoomOut(); }
    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PosterizationToolViewModel vm)
        {
            var border = this.FindControl<Border>("PreviewBorder");
            if (border != null) vm.Viewport.ViewportSize = border.Bounds.Size;
            vm.ResetView();
        }
    }
}