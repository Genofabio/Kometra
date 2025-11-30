using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq; 

namespace KomaLab.Views;

public partial class MultipleImagesNodeView : UserControl
{
    private Point? _lastPos; 
    
    public MultipleImagesNodeView()
    {
        InitializeComponent();
    }
    
    // Helper per trovare il contesto della Board
    private BoardViewModel? GetParentBoardViewModel(out Visual? visualParent)
    {
        visualParent = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        return visualParent?.DataContext as BoardViewModel;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            var boardVm = GetParentBoardViewModel(out var boardView);
            
            if (boardVm != null)
            {
                // Gestione selezione tramite Board
                boardVm.SetSelectedNode(nodeVm);
            }

            // Metodo corretto post-refactoring
            nodeVm.BringToFront(); 

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
        
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        var boardVm = GetParentBoardViewModel(out var boardVisual);
        
        if (boardVm == null || boardVisual == null) return;
        
        var currentPos = e.GetPosition(boardVisual);
        var delta = currentPos - _lastPos.Value;
        
        // Passiamo la scala corretta
        nodeVm.MoveNode(delta, boardVm.Scale); 
        
        _lastPos = currentPos;
        e.Handled = true;
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel vm) return;
    
        // In MultipleImagesNodeViewModel, BlackPoint e WhitePoint sono esposti
        // direttamente sul VM (per gestire il Sigma Locking su tutte le immagini)
        
        double currentRange = Math.Abs(vm.WhitePoint - vm.BlackPoint);
        if (currentRange < 1.0) currentRange = 1000.0;

        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;

        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
        double gap = 1.0;

        if (isShiftPressed)
        {
            double newBlack = vm.BlackPoint + deltaAmount;
            double limit = vm.WhitePoint - gap;
            if (newBlack > limit) newBlack = limit;
            vm.BlackPoint = newBlack;
        }
        else
        {
            double newWhite = vm.WhitePoint + deltaAmount;
            double limit = vm.BlackPoint + gap;
            if (newWhite < limit) newWhite = limit;
            vm.WhitePoint = newWhite;
        }

        e.Handled = true; 
    }
}