using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq; 

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _lastPos; 
    
    public SingleImageNodeView()
    {
        InitializeComponent();
    }
    
    // Helper per trovare la BoardView genitore e il suo ViewModel
    private BoardViewModel? GetParentBoardViewModel(out Visual? visualParent)
    {
        visualParent = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        return visualParent?.DataContext as BoardViewModel;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;
        
        // Controlliamo che sia il tasto sinistro
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            // 1. Recupera la Board tramite l'albero visuale
            var boardVm = GetParentBoardViewModel(out var boardView);
            
            if (boardVm != null)
            {
                // Imposta la selezione sulla Board (che gestisce l'esclusività)
                boardVm.SetSelectedNode(nodeVm);
            }

            // 2. Alza l'evento per portare in primo piano (Z-Index)
            nodeVm.BringToFront(); 

            // 3. Prepara il trascinamento
            if (boardView != null)
            {
                _lastPos = e.GetPosition(boardView); 
                e.Pointer.Capture(this); 
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
        
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;
        
        // Recupera la Board per avere la Scala e il riferimento posizionale
        var boardVm = GetParentBoardViewModel(out var boardVisual);
        
        if (boardVm == null || boardVisual == null) return;
        
        var currentPos = e.GetPosition(boardVisual);
        var delta = currentPos - _lastPos.Value;
        
        // IMPORTANTE: Passiamo la scala corrente per muovere il nodo 
        // della giusta distanza nel mondo virtuale, indipendentemente dallo zoom.
        nodeVm.MoveNode(delta, boardVm.Scale); 
        
        _lastPos = currentPos;
        e.Handled = true;
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not SingleImageNodeViewModel vm) return;

        if (!vm.IsSelected) return;
        
        double currentRange = Math.Abs(vm.FitsImage.WhitePoint - vm.FitsImage.BlackPoint);
        if (currentRange < 1.0) currentRange = 1000.0; 
        
        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;
        
        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
        double gap = 1.0; // Gap minimo tra bianco e nero

        if (isShiftPressed)
        {
            double newBlack = vm.FitsImage.BlackPoint + deltaAmount;
            double limit = vm.FitsImage.WhitePoint - gap;
            if (newBlack > limit) newBlack = limit;
            vm.FitsImage.BlackPoint = newBlack;
        }
        else
        {
            double newWhite = vm.FitsImage.WhitePoint + deltaAmount;
            double limit = vm.FitsImage.BlackPoint + gap;
            if (newWhite < limit) newWhite = limit;
            vm.FitsImage.WhitePoint = newWhite;
        }
        
        e.Handled = true; 
    }
}