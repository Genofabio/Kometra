using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq;

namespace KomaLab.Views;

public partial class MultipleImagesNodeView : UserControl
{
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    
    // CACHE: Salviamo il riferimento per non cercarlo 60 volte al secondo
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public MultipleImagesNodeView()
    {
        InitializeComponent();
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Troviamo i riferimenti una volta sola quando il nodo appare a schermo
        _cachedBoardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        _cachedBoardVm = _cachedBoardView?.DataContext as BoardViewModel;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cachedBoardVm = null;
        _cachedBoardView = null;
        
        _startDragPoint = null;
        _tempTransform = null;
        this.RenderTransform = null;
        
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            // Fallback di sicurezza se la cache è vuota (es. cambio contesto)
            if (_cachedBoardVm == null || _cachedBoardView == null)
            {
                _cachedBoardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
                _cachedBoardVm = _cachedBoardView?.DataContext as BoardViewModel;
            }

            if (_cachedBoardVm != null)
            {
                _cachedBoardVm.SetSelectedNode(nodeVm);
            }

            nodeVm.BringToFront(); 

            if (_cachedBoardView != null)
            {
                _startDragPoint = e.GetPosition(_cachedBoardView); 
                
                if (_tempTransform == null)
                {
                    _tempTransform = new TranslateTransform();
                    this.RenderTransform = _tempTransform;
                }
                _tempTransform.X = 0;
                _tempTransform.Y = 0;
                
                nodeVm.VisualOffsetX = 0;
                nodeVm.VisualOffsetY = 0;

                e.Pointer.Capture(this); 
                e.Handled = true; 
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Controlli null check ultra-rapidi
        if (_startDragPoint == null || _tempTransform == null || 
            _cachedBoardView == null || _cachedBoardVm == null) return;
        
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        // --- QUI L'OTTIMIZZAZIONE ---
        // Usiamo _cachedBoardView invece di cercarlo nell'albero
        var currentPos = e.GetPosition(_cachedBoardView);
        var screenDelta = currentPos - _startDragPoint.Value;
        
        double scale = _cachedBoardVm.Scale;
        if (scale <= 0.001) scale = 0.1; 

        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        // Aggiornamento View (GPU)
        _tempTransform.X = worldDeltaX;
        _tempTransform.Y = worldDeltaY;
        
        // Aggiornamento VM (Logica Connessioni)
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
                nodeVm.X += _tempTransform.X;
                nodeVm.Y += _tempTransform.Y;

                _tempTransform.X = 0;
                _tempTransform.Y = 0;
                nodeVm.VisualOffsetX = 0;
                nodeVm.VisualOffsetY = 0;
            }

            _startDragPoint = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel vm) return;
        if (!vm.IsSelected) return;
        
        // Logica invariata, ma ho aggiunto una guardia per evitare calcoli su valori troppo piccoli
        double currentRange = Math.Abs(vm.WhitePoint - vm.BlackPoint);
        if (currentRange < 0.0001) currentRange = 1000.0;

        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;

        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
        double gap = 1.0;

        if (isShiftPressed)
        {
            double newBlack = vm.BlackPoint + deltaAmount;
            if (newBlack > vm.WhitePoint - gap) newBlack = vm.WhitePoint - gap;
            vm.BlackPoint = newBlack;
        }
        else
        {
            double newWhite = vm.WhitePoint + deltaAmount;
            if (newWhite < vm.BlackPoint + gap) newWhite = vm.BlackPoint + gap;
            vm.WhitePoint = newWhite;
        }

        e.Handled = true; 
    }
}