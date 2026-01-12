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
        
        // Setup trasformazione per il movimento del nodo
        _tempTransform = new TranslateTransform();
        this.RenderTransform = _tempTransform;

        // --- NUOVO: Sincronizzazione Dimensioni Viewport ---
        // Troviamo il contenitore (definito nello XAML con x:Name="ImageContainer")
        // e aggiorniamo il VM quando la dimensione cambia.
        var container = this.FindControl<Border>("ImageContainer");
        if (container != null)
        {
            container.SizeChanged += (_, e) => 
            {
                if (DataContext is SingleImageNodeViewModel vm)
                {
                    vm.Viewport.ViewportSize = e.NewSize;
                    
                    // Opzionale: Se vuoi che faccia "Zoom to Fit" ogni volta 
                    // che ridimensioni il nodo, scommenta la riga sotto:
                    // vm.Viewport.ResetView(); 
                }
            };
        }
    }
    
    // --- OTTIMIZZAZIONE CACHE ---
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
    // ----------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;
        
        var properties = e.GetCurrentPoint(this).Properties;

        // --- NUOVO: PAN INTERNO (Tasto Destro) ---
        // Usiamo il tasto destro per spostare l'immagine DENTRO il nodo
        // senza spostare il nodo sulla board.
        if (properties.IsMiddleButtonPressed)
        {
            if (!nodeVm.IsSelected) return;
            _isPanningImage = true;
            _lastPanPosition = e.GetPosition(this);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(this);
            e.Handled = true;
            return; // Usciamo per non triggerare la selezione del nodo
        }

        // --- ESISTENTE: DRAG DEL NODO (Tasto Sinistro) ---
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
        if (DataContext is not SingleImageNodeViewModel nodeVm) return;

        // --- NUOVO: GESTIONE PAN INTERNO ---
        if (_isPanningImage)
        {
            var currentPos = e.GetPosition(this);
            var delta = currentPos - _lastPanPosition;
            _lastPanPosition = currentPos;

            // Deleghiamo la matematica al ViewportManager (codice riutilizzato!)
            nodeVm.Viewport.ApplyPan(delta.X, delta.Y);
            
            e.Handled = true;
            return;
        }

        // --- ESISTENTE: GESTIONE DRAG DEL NODO ---
        if (_startDragPoint == null || _tempTransform == null || 
            _cachedBoardView == null || _cachedBoardVm == null) return;
        
        var currentBoardPos = e.GetPosition(_cachedBoardView);
        var screenDelta = currentBoardPos - _startDragPoint.Value;
        
        double scale = _cachedBoardVm.Scale;
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
        // --- NUOVO: RILASCIO PAN INTERNO ---
        if (_isPanningImage && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isPanningImage = false;
            this.Cursor = Cursor.Default; // Ripristina il cursore normale
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        // --- ESISTENTE: RILASCIO DRAG DEL NODO ---
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

        // --- NUOVO: ZOOM (CTRL + WHEEL) ---
        if ((e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            // Troviamo il container per calcolare la posizione relativa del mouse
            var container = this.FindControl<Border>("ImageContainer");
            if (container != null)
            {
                var centerPoint = new Point(container.Bounds.Width / 2.0, container.Bounds.Height / 2.0);
                
                // Usiamo un fattore simile a quello usato nell'AlignmentTool
                // Delta > 0 = Zoom In, Delta < 0 = Zoom Out
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);

                // Deleghiamo al ViewportManager
                vm.Viewport.ApplyZoomAtPoint(factor, centerPoint);
            }

            e.Handled = true;
            return;
        }

        // --- ESISTENTE: MODIFICA SOGLIE (SHIFT/NONE + WHEEL) ---
        if (vm.FitsImage != null)
        {
            double currentRange = Math.Abs(vm.FitsImage.WhitePoint - vm.FitsImage.BlackPoint);
            if (currentRange < 0.001) currentRange = 1000.0; 
        
            double stepPercentage = 0.10; 
            double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;
        
            bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;
            double gap = 1.0; 

            if (isShiftPressed)
            {
                double newBlack = vm.FitsImage.BlackPoint + deltaAmount;
                if (newBlack > vm.FitsImage.WhitePoint - gap) newBlack = vm.FitsImage.WhitePoint - gap;
                vm.FitsImage.BlackPoint = newBlack;
            }
            else
            {
                double newWhite = vm.FitsImage.WhitePoint + deltaAmount;
                if (newWhite < vm.FitsImage.BlackPoint + gap) newWhite = vm.FitsImage.BlackPoint + gap;
                vm.FitsImage.WhitePoint = newWhite;
            }
        }
        e.Handled = true; 
    }
}