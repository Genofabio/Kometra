using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using KomaLab.ViewModels;
using System.Linq;
using System.Diagnostics;

namespace KomaLab.Views;

public partial class MultipleImagesNodeView : UserControl
{
    private Point? _lastPos; // Campo per il trascinamento
    
    public MultipleImagesNodeView()
    {
        InitializeComponent();
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel vm)
        {
            Debug.WriteLine("ERRORE: DataContext non è MultipleImagesNodeViewModel.");
            return;
        }
        
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            vm.ParentBoard.SetSelectedNode(vm);

            var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
            if (boardView == null)
            {
                Debug.WriteLine("ERRORE: boardView non trovato!");
                return;
            }
            
            _lastPos = e.GetPosition(boardView); 
            e.Pointer.Capture(this);
            e.Handled = true; 
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_lastPos != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            // 1. Rimosso l'errore di battitura 'S'
            _lastPos = null;
            
            // 2. Aggiunte le parentesi graffe per includere
            //    tutta la logica di rilascio
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastPos == null) return;
        
        if (DataContext is not MultipleImagesNodeViewModel vm)
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
        // Lega lo scroll alle proprietà Black/White del NODO PADRE
        if (DataContext is not MultipleImagesNodeViewModel vm)
            return;

        // Calcola il range attuale
        double currentRange = vm.WhitePoint - vm.BlackPoint;
        if (currentRange <= 0) currentRange = 1000; // Fallback

        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;

        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

        if (isShiftPressed)
        {
            vm.BlackPoint += deltaAmount;
        }
        else
        {
            vm.WhitePoint += deltaAmount;
        }

        e.Handled = true; 
    }
}