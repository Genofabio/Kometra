using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq; 
using System.Diagnostics; 

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _lastPos; 
    
    public SingleImageNodeView()
    {
        InitializeComponent();
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SingleImageNodeViewModel vm)
        {
            Debug.WriteLine("ERRORE: DataContext non è SingleImageNodeViewModel.");
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
            _lastPos = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastPos == null) return;
        
        if (DataContext is not SingleImageNodeViewModel vm)
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
        if (DataContext is not SingleImageNodeViewModel vm)
            return;

        // 1. Usa Math.Abs per evitare che la direzione si inverta se le soglie sono vicine
        double currentRange = Math.Abs(vm.FitsImage.WhitePoint - vm.FitsImage.BlackPoint);
    
        // Fallback se l'immagine è piatta
        if (currentRange < 1.0) currentRange = 1000.0; 
        
        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;
        
        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

        // Gap minimo di sicurezza (1 unità)
        double gap = 1.0;

        if (isShiftPressed)
        {
            // --- MODIFICA BLACK POINT ---
            double newBlack = vm.FitsImage.BlackPoint + deltaAmount;
        
            // BLOCCO: Il nero non può superare il (Bianco - gap)
            double limit = vm.FitsImage.WhitePoint - gap;
            if (newBlack > limit) newBlack = limit;

            vm.FitsImage.BlackPoint = newBlack;
        }
        else
        {
            // --- MODIFICA WHITE POINT ---
            double newWhite = vm.FitsImage.WhitePoint + deltaAmount;
        
            // BLOCCO: Il bianco non può scendere sotto il (Nero + gap)
            double limit = vm.FitsImage.BlackPoint + gap;
            if (newWhite < limit) newWhite = limit;

            vm.FitsImage.WhitePoint = newWhite;
        }
        
        e.Handled = true; 
    }
}