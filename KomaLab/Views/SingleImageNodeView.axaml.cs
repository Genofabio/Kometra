using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using KomaLab.ViewModels.Nodes;

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _startDragPoint;
    private TranslateTransform? _tempTransform;
    private bool _isPanningImage;
    private Point _lastPanPosition;
    
    private BoardViewModel? _cachedBoardVm;
    private Visual? _cachedBoardView;

    public SingleImageNodeView()
    {
        InitializeComponent();
        
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;

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
        _tempTransform = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ImageNodeViewModel nodeVm) return;

        var properties = e.GetCurrentPoint(this).Properties;
        var textBox = this.FindControl<TextBox>("TitleTextBox");
        var headerBorder = this.FindControl<Border>("TitleHeader");
        
        var sourceVisual = e.Source as Visual;

        // VERIFICA: Abbiamo cliccato sulla zona del titolo (Header)?
        // Nota: Grazie allo stile, se TextBox è ReadOnly, il click "passa attraverso" e colpisce TitleHeader.
        // Se TextBox è in edit, il click colpisce TextBox.
        bool isHeaderClick = headerBorder != null && sourceVisual != null && 
                             (sourceVisual == headerBorder || headerBorder.IsVisualAncestorOf(sourceVisual));

        // Verifichiamo che NON sia il pulsante di chiusura (che è dentro l'header)
        if (isHeaderClick && sourceVisual is Button) isHeaderClick = false; 
        if (isHeaderClick && sourceVisual.GetVisualAncestors().OfType<Button>().Any()) isHeaderClick = false;

        // --- 1. GESTIONE ATTIVAZIONE EDIT (DOPPIO CLICK) ---
        if (e.ClickCount == 2 && properties.IsLeftButtonPressed)
        {
            if (isHeaderClick && textBox != null)
            {
                // Attiviamo manualmente la TextBox
                // Lo stile XAML riattiverà automaticamente IsHitTestVisible quando IsReadOnly diventa false
                textBox.IsReadOnly = false; 
                textBox.Focus();            
                textBox.SelectAll();        
                e.Handled = true; 
                return;
            }
        }

        // --- 2. PAN INTERNO ---
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

        // --- 3. DRAG DEL NODO & SELEZIONE ---
        if (properties.IsLeftButtonPressed)
        {
            // Se la TextBox è in modalità EDIT (!IsReadOnly), lasciamo che gestisca lei il click
            // (per posizionare il cursore). sourceVisual sarà la TextBox o un suo figlio.
            if (textBox != null && !textBox.IsReadOnly)
            {
                // Se clicchiamo fuori dalla textbox mentre è aperta, la chiudiamo
                if (!isHeaderClick && !textBox.IsVisualAncestorOf(sourceVisual))
                {
                    textBox.IsReadOnly = true;
                    this.Focus();
                }
                else
                {
                    return; // Lasciamo il cursore muoversi
                }
            }

            // Selezione e Drag standard
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
    
    private void TitleTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            // Conferma e chiudi
            textBox.IsReadOnly = true;
            this.Focus(); 
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox tb)
        {
            // Annulla (opzionale: qui servirebbe ricaricare il vecchio titolo)
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

    // ... (Il resto dei metodi OnPointerMoved, OnPointerReleased, OnPointerWheelChanged rimane invariato) ...
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
            if (currentRange < 0.001) currentRange = 1.0; 
        
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