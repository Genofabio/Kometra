using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    // --- Variabili per il Drag del Nodo (Spostamento sulla Board) ---
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    
    // --- Variabili per il Pan Interno (Spostamento Immagine nel Nodo) ---
    private bool _isPanningImage;
    private Point _lastPanPosition;
    
    // --- CACHE: Riferimenti per ottimizzazione ---
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public SingleImageNodeView()
    {
        InitializeComponent();
        
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;

        // --- SINCRONIZZAZIONE DIMENSIONI VIEWPORT ---
        var container = this.FindControl<Border>("ImageContainer");
        if (container != null)
        {
            container.SizeChanged += (_, e) => 
            {
                if (DataContext is ImageNodeViewModel vm)
                {
                    // Aggiorniamo la dimensione del viewport nel ViewModel
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
        if (DataContext is not ImageNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;

        // PAN INTERNO (Tasto Centrale/Rotella premuta)
        if (properties.IsMiddleButtonPressed)
        {
            if (!nodeVm.IsSelected) return;
            
            _isPanningImage = true;
            _lastPanPosition = e.GetPosition(this);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // DRAG DEL NODO (Tasto Sinistro)
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
                
                // Nota: VisualOffsetX/Y devono essere definiti in BaseNodeViewModel
                nodeVm.VisualOffsetX = 0;
                nodeVm.VisualOffsetY = 0;

                e.Pointer.Capture(this); 
                e.Handled = true; 
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ImageNodeViewModel nodeVm) return;

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
        
        var currentBoardPos = e.GetPosition(_cachedBoardView);
        var screenDelta = currentBoardPos - _startDragPoint.Value;
        
        // Usiamo il fattore di zoom della Board per muovere il nodo alla velocità corretta
        double scale = _cachedBoardVm.Viewport.Scale; 
        if (scale <= 0.01) scale = 0.1; 

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
            if (DataContext is ImageNodeViewModel nodeVm && _tempTransform != null)
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
        if (DataContext is not ImageNodeViewModel vm) return;
        if (!vm.IsSelected) return;

        // ZOOM (CTRL + WHEEL)
        if ((e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            var container = this.FindControl<Border>("ImageContainer");
            if (container != null)
            {
                // Zoom centrato sulla posizione del mouse relativa al container
                var mousePos = e.GetPosition(container);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
            }

            e.Handled = true;
            return;
        }

        // MODIFICA SOGLIE (SHIFT/NONE + WHEEL)
        // Puntiamo ad ActiveRenderer invece del vecchio FitsImage
        if (vm.ActiveRenderer != null)
        {
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            if (currentRange < 0.001) currentRange = 1.0; 
        
            double stepPercentage = 0.05; // 5% ad ogni scatto per maggior precisione
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