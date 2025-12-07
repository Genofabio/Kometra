using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq;

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    
    // CACHE: Salviamo i riferimenti per evitare la ricerca continua
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public SingleImageNodeView()
    {
        InitializeComponent();
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;
    }
    
    // --- OTTIMIZZAZIONE CACHE ---
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Troviamo il genitore UNA volta sola all'inizio
        _cachedBoardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        _cachedBoardVm = _cachedBoardView?.DataContext as BoardViewModel;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Pulizia riferimenti per evitare memory leaks
        _cachedBoardVm = null;
        _cachedBoardView = null;
        
        _startDragPoint = null;
        _tempTransform = null;
        this.RenderTransform = null;
        
        base.OnDetachedFromVisualTree(e);
    }
    // ----------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            // Fallback di sicurezza se la cache è vuota (raro, ma possibile)
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
        // Null checks rapidi
        if (_startDragPoint == null || _tempTransform == null || 
            _cachedBoardView == null || _cachedBoardVm == null) return;
        
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;

        // --- QUI L'OTTIMIZZAZIONE ---
        // Usiamo _cachedBoardView e _cachedBoardVm direttamente (O(1))
        // Invece di cercarli nell'albero (O(N))
        
        var currentPos = e.GetPosition(_cachedBoardView);
        var screenDelta = currentPos - _startDragPoint.Value;
        
        double scale = _cachedBoardVm.Scale;
        if (scale <= 0.01) scale = 0.1; 

        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        // 1. Spostamento GPU
        _tempTransform.X = worldDeltaX;
        _tempTransform.Y = worldDeltaY;

        // 2. Spostamento Logico (Connessioni)
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
        if (DataContext is not SingleImageNodeViewModel vm) return;
        if (!vm.IsSelected) return;

        if (vm.FitsImage != null)
        {
            double currentRange = Math.Abs(vm.FitsImage.WhitePoint - vm.FitsImage.BlackPoint);
            // Evitiamo divisioni per zero o range troppo piccoli
            if (currentRange < 0.001) currentRange = 1000.0; 
        
            double stepPercentage = 0.10; 
            double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;
        
            bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
            double gap = 1.0; 

            if (isShiftPressed)
            {
                double newBlack = vm.FitsImage.BlackPoint + deltaAmount;
                // Clamp intelligente
                if (newBlack > vm.FitsImage.WhitePoint - gap) newBlack = vm.FitsImage.WhitePoint - gap;
                vm.FitsImage.BlackPoint = newBlack;
            }
            else
            {
                double newWhite = vm.FitsImage.WhitePoint + deltaAmount;
                // Clamp intelligente
                if (newWhite < vm.FitsImage.BlackPoint + gap) newWhite = vm.FitsImage.BlackPoint + gap;
                vm.FitsImage.WhitePoint = newWhite;
            }
        }
        e.Handled = true; 
    }
}