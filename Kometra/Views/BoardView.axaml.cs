using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Kometra.ViewModels;

namespace Kometra.Views;

public partial class BoardView : UserControl
{
    private Point? _lastPointerPosForPanning;
    private bool _isPanning;
    private TranslateTransform? _viewportTransform;

    public BoardView()
    {
        InitializeComponent();
        this.Focusable = true;
        
        // Cache della trasformazione per lo spostamento fluido dei nodi
        if (ViewportContainer.RenderTransform is TranslateTransform tt)
        {
            _viewportTransform = tt;
        }
        else
        {
            _viewportTransform = new TranslateTransform();
            ViewportContainer.RenderTransform = _viewportTransform;
        }
    }
    
    // --- Gestione Dimensioni ---
    
    private void OnBoardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is BoardViewModel vm)
        {
            // Sincronizziamo la dimensione della finestra con il Viewport
            vm.Viewport.ViewportSize = e.NewSize;
        }
    }
    
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BoardViewModel vm)
        {
            vm.Viewport.ViewportSize = this.Bounds.Size;
        }
    }

    // --- Gestione Input (Pan e Click) ---

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Se un nodo ha già gestito il click (e.Handled = true), la Board non deve fare nulla.
        if (e.Handled) return;
        
        this.Focus();

        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            // Inizio Panning
            _lastPointerPosForPanning = e.GetPosition(this);
            _isPanning = true; 
            
            if (_viewportTransform != null)
            {
                _viewportTransform.X = 0;
                _viewportTransform.Y = 0;
            }
            
            // La griglia deve partire sincronizzata
            BackgroundGrid.SetVisualPan(0, 0);
            
            // Disabilitiamo temporaneamente l'hit test sui nodi per migliorare le performance del drag
            ViewportContainer.IsHitTestVisible = false;

            e.Pointer.Capture(this); 
            e.Handled = true; 
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (props.IsLeftButtonPressed)
        {
            // Se arriviamo qui, significa che l'utente ha cliccato sul "vuoto" della Board,
            // poiché il click non è stato intercettato da nessun nodo.
            if (DataContext is BoardViewModel vm)
            {
                vm.DeselectAllNodes();
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _lastPointerPosForPanning == null) 
            return;

        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            EndPanning(e);
            return;
        }

        var currentPos = e.GetPosition(this);
        var delta = currentPos - _lastPointerPosForPanning.Value;
        
        if (_viewportTransform != null)
        {
            // 1. Spostamento VISIVO immediato (GPU)
            _viewportTransform.X += delta.X;
            _viewportTransform.Y += delta.Y;
            
            // 2. Spostamento della GRIGLIA (Custom Drawing)
            BackgroundGrid.SetVisualPan(_viewportTransform.X, _viewportTransform.Y);
        }
        
        _lastPointerPosForPanning = currentPos;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            EndPanning(e);
        }
    }
    
    private void EndPanning(PointerEventArgs e)
    {
        if (DataContext is BoardViewModel vm && _viewportTransform != null)
        {
            // COMMIT LOGICO:
            // Applichiamo lo spostamento accumulato al Viewport del ViewModel.
            // Poiché siamo sulla Board, il delta è in pixel schermo, ApplyPan lo gestirà correttamente.
            vm.Viewport.ApplyPan(_viewportTransform.X, _viewportTransform.Y);

            // RESET VISIVO:
            // Riportiamo a zero le trasformazioni temporanee perché ora i nodi si sono 
            // spostati "fisicamente" tramite il binding a OffsetX/Y nel ViewModel.
            _viewportTransform.X = 0;
            _viewportTransform.Y = 0;
            
            BackgroundGrid.SetVisualPan(0, 0);
        }
        
        ViewportContainer.IsHitTestVisible = true;
        _isPanning = false;
        _lastPointerPosForPanning = null;
        e.Pointer.Capture(null); 
        e.Handled = true; 
        this.Cursor = Cursor.Default;
    }

    // --- Gestione Zoom ---

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not BoardViewModel vm) return;
        
        var mousePos = e.GetPosition(this);
        
        // Calcolo fattore di zoom (esponenziale per fluidità)
        double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
        
        // Deleghiamo al ViewportManager della Board
        vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
        
        e.Handled = true; 
    }
    
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isPanning = false;
        _lastPointerPosForPanning = null;
        this.Cursor = Cursor.Default;
        base.OnDetachedFromVisualTree(e);
    }
}