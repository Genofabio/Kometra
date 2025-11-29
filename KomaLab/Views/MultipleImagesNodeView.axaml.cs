using System;
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
    private Point? _lastPos; 
    
    public MultipleImagesNodeView()
    {
        InitializeComponent();
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel vm) return;
    
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // 1. Seleziona
            vm.ParentBoard.SetSelectedNode(vm);
        
            // 2. Porta in primo piano (Ora cambia solo lo ZIndex, non la lista!)
            vm.RequestBringToFront(); 

            // 3. Prepara il trascinamento
            var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
            if (boardView != null)
            {
                _lastPos = e.GetPosition(boardView); 
                e.Pointer.Capture(this); // La cattura ora NON viene persa
                e.Handled = true; 
            }
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
        if (DataContext is not MultipleImagesNodeViewModel vm)
            return;
    
        // 1. Usa Math.Abs per garantire una direzione di scorrimento costante
        double currentRange = Math.Abs(vm.WhitePoint - vm.BlackPoint);
    
        // Fallback se il range è troppo piccolo
        if (currentRange < 1.0) currentRange = 1000.0;

        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;

        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
    
        // Gap minimo di sicurezza (1 unità)
        double gap = 1.0;

        if (isShiftPressed)
        {
            // --- MODIFICA BLACK POINT ---
            double newBlack = vm.BlackPoint + deltaAmount;
        
            // BLOCCO: Il nero non può superare il (Bianco - gap)
            double limit = vm.WhitePoint - gap;
            if (newBlack > limit) newBlack = limit;

            vm.BlackPoint = newBlack;
        }
        else
        {
            // --- MODIFICA WHITE POINT ---
            double newWhite = vm.WhitePoint + deltaAmount;
        
            // BLOCCO: Il bianco non può scendere sotto il (Nero + gap)
            double limit = vm.BlackPoint + gap;
            if (newWhite < limit) newWhite = limit;

            vm.WhitePoint = newWhite;
        }

        e.Handled = true; 
    }
}