using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq;
using Avalonia.Media;

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    
    public SingleImageNodeView()
    {
        InitializeComponent();
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;
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
                // Salviamo il punto iniziale relativo al genitore (la Board)
                _startDragPoint = e.GetPosition(boardView);
                
                // Resettiamo la trasformazione visiva temporanea
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

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_startDragPoint == null || _tempTransform == null) return;
        
        var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        // Recuperiamo anche il ViewModel per sapere lo Scale attuale
        var boardVm = boardView?.DataContext as BoardViewModel;

        if (boardView == null || boardVm == null) return;
        
        var currentPos = e.GetPosition(boardView);
        var screenDelta = currentPos - _startDragPoint.Value;
        
        // --- FIX QUI ---
        // Dobbiamo convertire i pixel dello schermo in "unità coordinate del mondo".
        // Se siamo zoomati indietro (scale < 1), dobbiamo spostare il nodo di PIÙ unità 
        // affinché visivamente copra la stessa distanza del mouse.
        double scale = boardVm.Scale;
        if (scale <= 0.01) scale = 0.1; // Protezione divisione per zero

        _tempTransform.X = screenDelta.X / scale;
        _tempTransform.Y = screenDelta.Y / scale;
        
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_startDragPoint != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (DataContext is SingleImageNodeViewModel nodeVm && _tempTransform != null)
            {
                // Ora _tempTransform contiene già lo spostamento CORRETTO in coordinate mondo
                // (perché abbiamo diviso per la scala nel Move).
                
                // Opzione A: Aggiorniamo direttamente le proprietà (più efficiente/chiaro qui)
                nodeVm.X += _tempTransform.X;
                nodeVm.Y += _tempTransform.Y;

                /* * Opzione B: Se vuoi per forza usare MoveNode, devi "riconvertire" in screen pixels
                 * per rispettare la firma del tuo metodo, ma è un calcolo ridondante:
                 * * var boardVm = GetParentBoardViewModel(out _);
                 * double scale = boardVm?.Scale ?? 1.0;
                 * var screenDeltaReconstructed = new Vector(_tempTransform.X * scale, _tempTransform.Y * scale);
                 * nodeVm.MoveNode(screenDeltaReconstructed, scale);
                 */

                // Reset visivo
                _tempTransform.X = 0;
                _tempTransform.Y = 0;
            }

            _startDragPoint = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not SingleImageNodeViewModel vm) return;

        if (!vm.IsSelected) return;

        if (vm.FitsImage != null)
        {
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
        }

        e.Handled = true; 
    }
}