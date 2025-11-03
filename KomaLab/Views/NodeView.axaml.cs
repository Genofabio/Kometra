using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Linq;
using System.Diagnostics;
using KomaLab.ViewModels; // Per Debug.WriteLine

namespace KomaLab.Views;

public partial class NodeView : UserControl
{
    private Point? _lastPos; 
    
    public NodeView()
    {
        InitializeComponent();
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not NodeViewModel vm) return;
        
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // 1. Notifica il VM della selezione
            vm.ParentBoard.SetSelectedNode(vm);

            // 2. Trova il pannello genitore per calcolare la posizione
            var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
            if (boardView == null)
            {
                Debug.WriteLine("ERRORE: boardView non trovato!");
                return;
            }
            
            // 3. Inizia il trascinamento (logica della View)
            _lastPos = e.GetPosition(boardView); 
            e.Pointer.Capture(this);
            e.Handled = true; 
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_lastPos != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            _lastPos = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastPos == null || DataContext is not NodeViewModel vm)
            return;
        
        var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        if (boardView == null) return;
        
        var pos = e.GetPosition(boardView);
        var delta = pos - _lastPos.Value; 
        
        _lastPos = pos;
        
        vm.MoveNode(delta);
        
        e.Handled = true;
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not NodeViewModel vm)
            return;
        
        // 1. Controlla se SHIFT è premuto
        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

        // 2. Inoltra l'input grezzo al ViewModel
        vm.AdjustThresholds(e.Delta.Y, isShiftPressed);
            
        // 3. Impedisce all'evento di "salire" e causare lo zoom del Board
        e.Handled = true; 
    }
}