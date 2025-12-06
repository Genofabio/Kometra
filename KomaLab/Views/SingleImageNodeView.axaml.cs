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
                _startDragPoint = e.GetPosition(boardView);
                
                if (_tempTransform == null) 
                {
                    _tempTransform = new TranslateTransform();
                    this.RenderTransform = _tempTransform;
                }
                _tempTransform.X = 0;
                _tempTransform.Y = 0;
                
                // Assicuriamoci che anche l'offset logico sia zero all'inizio
                nodeVm.VisualOffsetX = 0;
                nodeVm.VisualOffsetY = 0;

                e.Pointer.Capture(this); 
                e.Handled = true; 
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_startDragPoint == null || _tempTransform == null) return;
        
        // Nota: Qui usiamo BaseNodeViewModel o SingleImageNodeViewModel, va bene uguale
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;

        var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        var boardVm = boardView?.DataContext as BoardViewModel;

        if (boardView == null || boardVm == null) return;
        
        var currentPos = e.GetPosition(boardView);
        var screenDelta = currentPos - _startDragPoint.Value;
        
        double scale = boardVm.Scale;
        if (scale <= 0.01) scale = 0.1; 

        // Calcoliamo lo spostamento in coordinate mondo
        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        // 1. Spostiamo il nodo VISIVAMENTE (RenderTransform -> GPU veloce)
        _tempTransform.X = worldDeltaX;
        _tempTransform.Y = worldDeltaY;

        // 2. --- MODIFICA FONDAMENTALE ---
        // Comunichiamo questo spostamento temporaneo al ViewModel.
        // Questo farà ridisegnare la linea di connessione in tempo reale!
        nodeVm.VisualOffsetX = worldDeltaX;
        nodeVm.VisualOffsetY = worldDeltaY;
        
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_startDragPoint != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (DataContext is SingleImageNodeViewModel nodeVm && _tempTransform != null)
            {
                // Commit finale: Applichiamo lo spostamento accumulato alla posizione reale
                nodeVm.X += _tempTransform.X;
                nodeVm.Y += _tempTransform.Y;

                // Reset Visivo (GPU)
                _tempTransform.X = 0;
                _tempTransform.Y = 0;
                
                // --- MODIFICA FONDAMENTALE ---
                // Reset Logico (ViewModel). 
                // Poiché X e Y sono state aggiornate, l'offset temporaneo deve tornare a 0
                // altrimenti la linea verrebbe sommata due volte.
                nodeVm.VisualOffsetX = 0;
                nodeVm.VisualOffsetY = 0;
            }

            _startDragPoint = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
    
    // Ti consiglio di aggiungere questo per sicurezza contro i memory leak
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _startDragPoint = null;
        _tempTransform = null;
        this.RenderTransform = null;
        base.OnDetachedFromVisualTree(e);
    }
    
    // ... OnPointerWheelChanged rimane invariato ...
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
            double gap = 1.0; 

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