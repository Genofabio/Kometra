using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity; // Necessario per RoutedEventArgs
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using KomaLab.ViewModels.Nodes;
using SequenceNavigator = KomaLab.ViewModels.Shared.SequenceNavigator;

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
        var textBox = this.FindControl<TextBox>("TitleTextBox");
        var headerBorder = this.FindControl<Border>("TitleHeader");
        
        var sourceVisual = e.Source as Visual;

        // --- VERIFICA HIT TEST HEADER ---
        // Verifichiamo se il click è avvenuto sull'area del titolo (Header).
        // Se TextBox è ReadOnly, lo stile IsHitTestVisible=False fa passare il click al Border (TitleHeader).
        bool isHeaderClick = headerBorder != null && sourceVisual != null && 
                             (sourceVisual == headerBorder || headerBorder.IsVisualAncestorOf(sourceVisual));

        // Escludiamo il pulsante di chiusura (che è dentro l'header)
        if (isHeaderClick && (sourceVisual is Button || sourceVisual.GetVisualAncestors().OfType<Button>().Any())) 
            isHeaderClick = false;

        // --- 1. GESTIONE DOPPIO CLICK (EDIT) ---
        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            if (isHeaderClick && textBox != null)
            {
                // Attivazione manuale della modifica
                textBox.IsReadOnly = false;
                textBox.Focus();
                textBox.SelectAll();
                e.Handled = true; // Stop all'evento (niente drag)
                return;
            }
        }

        // --- 2. PAN INTERNO (Tasto Centrale) ---
        if (properties.IsMiddleButtonPressed)
        {
            if (!nodeVm.IsSelected || nodeVm.Navigator is SequenceNavigator { IsLooping: true }) return;
            
            _isPanningImage = true;
            _lastPanPosition = e.GetPosition(this);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return; 
        }

        // --- 3. DRAG DEL NODO (Tasto Sinistro) ---
        if (properties.IsLeftButtonPressed)
        {
            // Se siamo in modalità EDIT (!IsReadOnly), lasciamo gestire il click alla TextBox
            if (textBox != null && !textBox.IsReadOnly)
            {
                // Se clicchiamo fuori dalla textbox (ma non sull'header per riattivarla), chiudiamo l'edit
                if (!isHeaderClick && !textBox.IsVisualAncestorOf(sourceVisual))
                {
                    textBox.IsReadOnly = true;
                    this.Focus();
                }
                else
                {
                    return; // Lasciamo il cursore muoversi nel testo
                }
            }

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
    
    // --- GESTORI EDITING TITOLO ---

    private void TitleTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            textBox.IsReadOnly = true;
            this.Focus(); // Toglie il focus
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

    // --- ALTRI EVENTI (Moved, Released, Wheel) ---

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
        
        if (vm.Navigator is SequenceNavigator { IsLooping: true }) return;
        if (!vm.IsSelected) return;

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
        
        if (vm.ActiveRenderer != null)
        {
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            if (currentRange < 0.0001) currentRange = 1.0;

            double stepPercentage = 0.05; 
            double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;

            bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

            if (isShiftPressed)
                vm.ActiveRenderer.BlackPoint += deltaAmount;
            else
                vm.ActiveRenderer.WhitePoint += deltaAmount;
        }

        e.Handled = true; 
    }
}