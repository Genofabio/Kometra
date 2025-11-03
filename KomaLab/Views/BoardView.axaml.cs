using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using KomaLab.ViewModels;

namespace KomaLab.Views;

public partial class BoardView : UserControl
{
    private Point? _lastPointerPosForPanning;
    private bool _isPanning;

    public BoardView()
    {
        InitializeComponent();
    }
    
    // --- Gestione Dimensioni (Invariato) ---
    
    private void OnBoardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is BoardViewModel vm)
        {
            vm.ViewBounds = new Rect(e.NewSize);
        }
    }
    
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BoardViewModel vm)
        {
            vm.ViewBounds = this.Bounds;
        }
    }

    // --- Gestione Input (Pan e Deselezione) ---

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return; // Un nodo ha già gestito questo click

        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            // Logica di Panning (Inizio)
            _lastPointerPosForPanning = e.GetPosition(this);
            _isPanning = true; 
            e.Pointer.Capture(this); 
            e.Handled = true; 
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (props.IsLeftButtonPressed)
        {
            // Logica di Deselezione
            if (DataContext is BoardViewModel vm)
            {
                vm.DeselectAllNodes();
            }
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            // Logica di Panning (Fine)
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null); 
            e.Handled = true; 
            this.Cursor = Cursor.Default; 
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || DataContext is not BoardViewModel vm) 
            return;

        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            // Sicurezza: ferma il panning se il pulsante viene rilasciato
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _lastPointerPosForPanning!.Value;
        _lastPointerPosForPanning = pos;
        
        vm.Pan(delta);
        
        e.Handled = true;
    }

    // --- Gestione Input (Zoom) ---

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled)
            return;
            
        if (DataContext is not BoardViewModel vm)
            return;
        
        var mousePos = e.GetPosition(this);
        double delta = e.Delta.Y;
        
        vm.Zoom(delta, mousePos);
        
        e.Handled = true; 
    }
}