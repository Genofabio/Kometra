using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq;
using Avalonia.Media;

namespace KomaLab.Views;

public partial class MultipleImagesNodeView : UserControl
{
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    
    public MultipleImagesNodeView()
    {
        InitializeComponent();
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;
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
                boardVm.SetSelectedNode(nodeVm);
            }

            nodeVm.BringToFront(); 

            if (boardView != null)
            {
                // Salviamo il punto di inizio relativo alla Board
                _startDragPoint = e.GetPosition(boardView); 
                
                // Resettiamo la trasformazione visiva
                if (_tempTransform == null)
                {
                    _tempTransform = new TranslateTransform();
                    this.RenderTransform = _tempTransform;
                }
                _tempTransform.X = 0;
                _tempTransform.Y = 0;

                e.Pointer.Capture(this); 
                e.Handled = true; 
            }
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_startDragPoint != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (DataContext is MultipleImagesNodeViewModel nodeVm && _tempTransform != null)
            {
                // Commit finale: 
                // Aggiorniamo le coordinate reali del ViewModel sommando lo spostamento accumulato
                // Nota: _tempTransform contiene già i valori corretti in "coordinate mondo" 
                // perché li abbiamo scalati nel PointerMoved.
                nodeVm.X += _tempTransform.X;
                nodeVm.Y += _tempTransform.Y;

                // Reset visivo (ora il controllo è posizionato correttamente dal Canvas)
                _tempTransform.X = 0;
                _tempTransform.Y = 0;
            }

            _startDragPoint = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Se non stiamo trascinando, esci subito
        if (_startDragPoint == null || _tempTransform == null) return;
        
        if (DataContext is not MultipleImagesNodeViewModel) return;
        
        // Recuperiamo la Board per calcolare lo spostamento rispetto allo Zoom
        var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();

        if (boardView == null || boardView.DataContext is not BoardViewModel boardVm) return;
        
        var currentPos = e.GetPosition(boardView);
        var screenDelta = currentPos - _startDragPoint.Value;
        
        // FIX SCALA & LAG:
        // 1. Dividiamo per Scale per convertire i pixel mouse in unità mondo (mantiene il sync col cursore)
        // 2. Modifichiamo SOLO _tempTransform per evitare il ricalcolo del layout (fluidità estrema)
        double scale = boardVm.Scale;
        if (scale <= 0.001) scale = 0.1; // Protezione

        _tempTransform.X = screenDelta.X / scale;
        _tempTransform.Y = screenDelta.Y / scale;
        
        e.Handled = true;
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel vm) return;
    
        if (!vm.IsSelected) return;
        
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