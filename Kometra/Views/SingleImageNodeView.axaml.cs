using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Kometra.ViewModels;
using Kometra.ViewModels.Nodes;

namespace Kometra.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _startDragPoint;
    private bool _isPanningImage;
    private Point _lastPanPosition;
    
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public SingleImageNodeView()
    {
        InitializeComponent();

        var container = this.FindControl<Border>("ImageContainer");
        if (container != null)
        {
            container.SizeChanged += (_, e) => 
            {
                if (DataContext is ImageNodeViewModel vm)
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
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ImageNodeViewModel nodeVm) return;

        var properties = e.GetCurrentPoint(this).Properties;
        var textBox = this.FindControl<TextBox>("TitleTextBox");
        var headerBorder = this.FindControl<Border>("TitleHeader");
        
        var sourceVisual = e.Source as Visual;

        bool isHeaderClick = headerBorder != null && sourceVisual != null && 
                             (sourceVisual == headerBorder || headerBorder.IsVisualAncestorOf(sourceVisual));

        if (isHeaderClick && sourceVisual is Button) isHeaderClick = false; 
        if (isHeaderClick && sourceVisual.GetVisualAncestors().OfType<Button>().Any()) isHeaderClick = false;

        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            if (isHeaderClick && textBox != null)
            {
                textBox.IsReadOnly = false; 
                textBox.Focus();            
                textBox.SelectAll();        
                e.Handled = true; 
                return;
            }
        }

        bool isMiddleClickPan = properties.IsMiddleButtonPressed;
        bool isAltPan = properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (isMiddleClickPan || isAltPan)
        {
            if (!nodeVm.IsSelected) return;
            _isPanningImage = true;
            _lastPanPosition = e.GetPosition(this);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            if (textBox != null && !textBox.IsReadOnly)
            {
                if (!isHeaderClick && !textBox.IsVisualAncestorOf(sourceVisual))
                {
                    textBox.IsReadOnly = true;
                    this.Focus();
                }
                else return; 
            }

            bool isModifier = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || 
                              e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            if (!nodeVm.IsSelected || isModifier)
            {
                _cachedBoardVm?.SetSelectedNode(nodeVm, isModifier);
            }
            else if (_cachedBoardVm != null && _cachedBoardVm.SelectedNodesCount == 2)
            {
                // Il nodo è già selezionato, niente modificatori e ci sono esattamente 2 nodi.
                // Lo spostiamo in cima alla collezione per farlo diventare "A".
                if (_cachedBoardVm.SelectedNodes.IndexOf(nodeVm) != 0)
                {
                    _cachedBoardVm.SelectedNodes.Remove(nodeVm);
                    _cachedBoardVm.SelectedNodes.Insert(0, nodeVm);
                }
            }
            
            nodeVm.BringToFront();

            if (_cachedBoardView != null)
            {
                _startDragPoint = e.GetPosition(_cachedBoardView);
                
                if (_cachedBoardVm != null)
                {
                    foreach (var n in _cachedBoardVm.SelectedNodes)
                    {
                        n.VisualOffsetX = 0;
                        n.VisualOffsetY = 0;
                    }
                }

                e.Pointer.Capture(this);
                e.Handled = true;
            }
        }
    }
    
    private void TitleTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            textBox.IsReadOnly = true;
            this.Focus(); 
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox tb)
        {
            tb.IsReadOnly = true;
            this.Focus();
            e.Handled = true;
        }
    }
    
    private void TitleTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.IsReadOnly = true;
            textBox.SelectionStart = 0;
            textBox.SelectionEnd = 0;
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

        if (_startDragPoint == null || _cachedBoardView == null || _cachedBoardVm == null) return;
        
        var currentBoardPos = e.GetPosition(_cachedBoardView);
        var screenDelta = currentBoardPos - _startDragPoint.Value;

        double scale = _cachedBoardVm.Viewport.Scale; 
        if (scale <= 0.01) scale = 0.1; 

        double worldDeltaX = screenDelta.X / scale;
        double worldDeltaY = screenDelta.Y / scale;

        foreach (var n in _cachedBoardVm.SelectedNodes)
        {
            n.VisualOffsetX = worldDeltaX;
            n.VisualOffsetY = worldDeltaY;
        }
        
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanningImage && (e.InitialPressMouseButton == MouseButton.Middle || e.InitialPressMouseButton == MouseButton.Left))
        {
            _isPanningImage = false;
            this.Cursor = Cursor.Default;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (_startDragPoint != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            if (_cachedBoardVm != null)
            {
                foreach (var n in _cachedBoardVm.SelectedNodes)
                {
                    n.X += n.VisualOffsetX;
                    n.Y += n.VisualOffsetY;
                    
                    n.VisualOffsetX = 0;
                    n.VisualOffsetY = 0;
                }
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

        double effectiveDelta = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(effectiveDelta) < 0.0001) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            var container = this.FindControl<Border>("ImageContainer");
            if (container != null)
            {
                var mousePos = e.GetPosition(container);
                double factor = effectiveDelta > 0 ? 1.1 : (1.0 / 1.1);
                vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
            }
            e.Handled = true;
            return;
        }

        if (vm.ActiveRenderer != null)
        {
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
            double step = Math.Max(0.0001, baseStep);

            if (effectiveDelta < 0) step = -step;

            bool isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (isShiftPressed)
            {
                double newBlack = vm.ActiveRenderer.BlackPoint + step;
                if (step > 0)
                {
                    double maxAllowed = vm.ActiveRenderer.WhitePoint - (step * 0.1);
                    vm.ActiveRenderer.BlackPoint = Math.Min(newBlack, maxAllowed);
                }
                else vm.ActiveRenderer.BlackPoint = newBlack;
            }
            else
            {
                double newWhite = vm.ActiveRenderer.WhitePoint + step;
                if (step < 0)
                {
                    double minAllowed = vm.ActiveRenderer.BlackPoint + (Math.Abs(step) * 0.1);
                    vm.ActiveRenderer.WhitePoint = Math.Max(newWhite, minAllowed);
                }
                else vm.ActiveRenderer.WhitePoint = newWhite;
            }
        }
        
        e.Handled = true; 
    }
}