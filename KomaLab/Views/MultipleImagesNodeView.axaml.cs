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
                _startDragPoint = e.GetPosition(boardView); 
                
                if (_tempTransform == null)
                {
                    _tempTransform = new TranslateTransform();
                    this.RenderTransform = _tempTransform;
                }
                _tempTransform.X = 0;
                _tempTransform.Y = 0;
                
                // Assicuriamoci che l'offset logico sia pulito all'inizio
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
        
        // Castiamo il DataContext per accedere alle proprietà VisualOffset
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        // Recuperiamo anche il VM per lo scale
        var boardVm = boardView?.DataContext as BoardViewModel; 

        if (boardView == null || boardVm == null) return;
        
        var currentPos = e.GetPosition(boardView);
        var screenDelta = currentPos - _startDragPoint.Value;
        
        double scale = boardVm.Scale;
        if (scale <= 0.001) scale = 0.1; 

        // Calcoliamo lo spostamento in unità mondo
        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        // 1. Spostamento Visivo del NODO (GPU)
        _tempTransform.X = worldDeltaX;
        _tempTransform.Y = worldDeltaY;
        
        // 2. Spostamento Visivo delle CONNESSIONI (ViewModel)
        // Aggiornando queste proprietà, la linea di connessione verrà ricalcolata in tempo reale
        nodeVm.VisualOffsetX = worldDeltaX;
        nodeVm.VisualOffsetY = worldDeltaY;
        
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_startDragPoint != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (DataContext is MultipleImagesNodeViewModel nodeVm && _tempTransform != null)
            {
                // Commit finale: aggiorniamo la posizione reale X,Y
                nodeVm.X += _tempTransform.X;
                nodeVm.Y += _tempTransform.Y;

                // Reset Visivo (GPU)
                _tempTransform.X = 0;
                _tempTransform.Y = 0;

                // Reset Logico (ViewModel)
                // Fondamentale: azzeriamo l'offset temporaneo ora che X e Y sono aggiornate
                nodeVm.VisualOffsetX = 0;
                nodeVm.VisualOffsetY = 0;
            }

            _startDragPoint = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
    
    // Pulizia per sicurezza
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _startDragPoint = null;
        _tempTransform = null;
        this.RenderTransform = null;
        base.OnDetachedFromVisualTree(e);
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