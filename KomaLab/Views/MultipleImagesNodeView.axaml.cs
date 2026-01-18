using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using KomaLab.ViewModels.Nodes;
using KomaLab.ViewModels.Components;

namespace KomaLab.Views;

public partial class MultipleImagesNodeView : UserControl
{
    // --- Variabili per il Drag del Nodo ---
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    
    // --- Variabili per il Pan Interno ---
    private bool _isPanningImage;
    private Point _lastPanPosition;
    
    // --- Cache ---
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public MultipleImagesNodeView()
    {
        InitializeComponent();
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;
        
        // --- Sincronizzazione Dimensioni Viewport ---
        var container = this.FindControl<Border>("ImageContainer");
        if (container != null)
        {
            container.SizeChanged += (_, e) => 
            {
                if (DataContext is MultipleImagesNodeViewModel vm)
                {
                    vm.Viewport.ViewportSize = e.NewSize;
                }
            };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cachedBoardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        _cachedBoardVm = _cachedBoardView?.DataContext as BoardViewModel;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cachedBoardVm = null;
        _cachedBoardView = null;
        _startDragPoint = null;
        _tempTransform = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        var properties = e.GetCurrentPoint(this).Properties;

        // --- PAN INTERNO (Tasto Centrale) ---
        if (properties.IsMiddleButtonPressed)
        {
            // Verifichiamo il navigatore invece della vecchia proprietà IsAnimating
            if (!nodeVm.IsSelected || nodeVm.Navigator is SequenceNavigator { IsLooping: true }) return;
            
            _isPanningImage = true;
            _lastPanPosition = e.GetPosition(this);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return; 
        }

        // --- DRAG DEL NODO (Tasto Sinistro) ---
        if (properties.IsLeftButtonPressed)
        {
            _cachedBoardVm?.SetSelectedNode(nodeVm);
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
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        if (_isPanningImage)
        {
            var currentPos = e.GetPosition(this);
            var delta = currentPos - _lastPanPosition;
            _lastPanPosition = currentPos;

            nodeVm.Viewport.ApplyPan(delta.X, delta.Y);
            e.Handled = true;
            return;
        }

        if (_startDragPoint == null || _tempTransform == null || 
            _cachedBoardView == null || _cachedBoardVm == null) return;
        
        var currentPosBoard = e.GetPosition(_cachedBoardView);
        var screenDelta = currentPosBoard - _startDragPoint.Value;
        
        // Puntiamo al Viewport della Board per la scala globale
        double scale = _cachedBoardVm.Viewport.Scale;
        if (scale <= 0.001) scale = 0.1; 

        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        _tempTransform.X = worldDeltaX;
        _tempTransform.Y = worldDeltaY;
        
        nodeVm.VisualOffsetX = worldDeltaX;
        nodeVm.VisualOffsetY = worldDeltaY;
        
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanningImage && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isPanningImage = false;
            this.Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

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
        
        // Protezione: non modifichiamo soglie o zoom se la sequenza sta scorrendo velocemente
        if (vm.Navigator is SequenceNavigator { IsLooping: true }) return;
        if (!vm.IsSelected) return;

        // --- ZOOM (CTRL + WHEEL) ---
        if ((e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            var container = this.FindControl<Border>("ImageContainer");
            if (container != null)
            {
                var mousePos = e.GetPosition(container);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
            }
            e.Handled = true;
            return;
        }
        
        // --- SOGLIE RADIOMETRICHE (ActiveRenderer) ---
        if (vm.ActiveRenderer != null)
        {
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            if (currentRange < 0.0001) currentRange = 1.0;

            double stepPercentage = 0.05; 
            double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;

            bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

            if (isShiftPressed)
            {
                vm.ActiveRenderer.BlackPoint += deltaAmount;
            }
            else
            {
                vm.ActiveRenderer.WhitePoint += deltaAmount;
            }
        }

        e.Handled = true; 
    }
}