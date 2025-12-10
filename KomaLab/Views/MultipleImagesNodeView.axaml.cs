using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;

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
        var container = this.FindControl<Border>("ImageContainer"); // Assicurati che il nome nello XAML sia ImageContainer
        if (container != null)
        {
            container.SizeChanged += (_, e) => 
            {
                if (DataContext is MultipleImagesNodeViewModel vm)
                {
                    vm.Viewport.ViewportSize = e.NewSize;
                    // Opzionale: vm.Viewport.ResetView();
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
        this.RenderTransform = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;

        // --- PAN INTERNO (Tasto Centrale) ---
        if (properties.IsMiddleButtonPressed)
        {
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
        if (DataContext is not MultipleImagesNodeViewModel nodeVm) return;
        
        // --- GESTIONE PAN INTERNO ---
        if (_isPanningImage)
        {
            var currentPos = e.GetPosition(this);
            var delta = currentPos - _lastPanPosition;
            _lastPanPosition = currentPos;

            nodeVm.Viewport.ApplyPan(delta.X, delta.Y);
            
            e.Handled = true;
            return;
        }

        // --- GESTIONE DRAG DEL NODO ---
        if (_startDragPoint == null || _tempTransform == null || 
            _cachedBoardView == null || _cachedBoardVm == null) return;
        
        var currentPosBoard = e.GetPosition(_cachedBoardView);
        var screenDelta = currentPosBoard - _startDragPoint.Value;
        
        double scale = _cachedBoardVm.Scale;
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
        // --- RILASCIO PAN INTERNO ---
        if (_isPanningImage && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isPanningImage = false;
            this.Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        // --- RILASCIO DRAG DEL NODO ---
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

        // --- ZOOM (CTRL + WHEEL) ---
        if ((e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            var container = this.FindControl<Border>("ImageContainer");
            if (container != null)
            {
                var centerPoint = new Point(container.Bounds.Width / 2.0, container.Bounds.Height / 2.0);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                
                vm.Viewport.ApplyZoomAtPoint(factor, centerPoint);
            }
            e.Handled = true;
            return;
        }
        
        // --- SOGLIE (STANDARD) ---
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