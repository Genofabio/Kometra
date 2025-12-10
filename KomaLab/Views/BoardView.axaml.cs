using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KomaLab.ViewModels;

namespace KomaLab.Views;

public partial class BoardView : UserControl
{
    private Point? _lastPointerPosForPanning;
    private bool _isPanning;
    private TranslateTransform? _viewportTransform;

    public BoardView()
    {
        InitializeComponent();
        this.Focusable = true;
        
        // Cache della trasformazione per accesso rapido
        if (ViewportContainer.RenderTransform is TranslateTransform tt)
        {
            _viewportTransform = tt;
        }
        else
        {
            _viewportTransform = new TranslateTransform();
            ViewportContainer.RenderTransform = _viewportTransform;
        }
    }
    
    // --- Gestione Dimensioni ---
    
    private void OnBoardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is BoardViewModel vm)
        {
            vm.ViewBounds = new Rect(e.NewSize);
        }
    }
    
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BoardViewModel vm)
        {
            vm.ViewBounds = this.Bounds;
        }
    }

    // --- Gestione Input (Pan e Click) ---

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        this.Focus();

        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            // Inizio Panning
            _lastPointerPosForPanning = e.GetPosition(this);
            _isPanning = true; 
            
            // Assicuriamoci che i valori visivi temporanei siano a zero
            if (_viewportTransform != null)
            {
                _viewportTransform.X = 0;
                _viewportTransform.Y = 0;
            }
            // Anche la griglia deve partire "pulita" (anche se dovrebbe già esserlo)
            BackgroundGrid.SetVisualPan(0, 0);
            
            ViewportContainer.IsHitTestVisible = false;

            e.Pointer.Capture(this); 
            e.Handled = true; 
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (props.IsLeftButtonPressed)
        {
            // Deselezione cliccando sul vuoto
            if (DataContext is BoardViewModel vm)
            {
                vm.DeselectAllNodes();
            }
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Se non stiamo pannando o abbiamo perso il riferimento, esci
        if (!_isPanning || _lastPointerPosForPanning == null) 
            return;

        // Sicurezza: se rilasciano il tasto fuori finestra
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            EndPanning(e);
            return;
        }

        var currentPos = e.GetPosition(this);
        var delta = currentPos - _lastPointerPosForPanning.Value;
        
        if (_viewportTransform != null)
        {
            // 1. Spostiamo i NODI fisicamente (RenderTransform -> GPU)
            // Accumuliamo il delta nel RenderTransform
            _viewportTransform.X += delta.X;
            _viewportTransform.Y += delta.Y;
            
            // 2. Spostiamo la GRIGLIA virtualmente (InvalidateVisual -> CPU)
            // La griglia NON si sposta, ma ridisegna le linee con questo offset.
            // Passiamo lo stesso identico valore accumulato.
            BackgroundGrid.SetVisualPan(_viewportTransform.X, _viewportTransform.Y);
        }
        
        _lastPointerPosForPanning = currentPos;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            EndPanning(e);
        }
    }
    
    private void EndPanning(PointerEventArgs e)
    {
        if (DataContext is BoardViewModel vm && _viewportTransform != null)
        {
            // COMMIT:
            // Applichiamo lo spostamento totale accumulato al ViewModel.
            // Questo causerà l'aggiornamento di OffsetX/Y (che sposterà logicamente i nodi e la griglia).
            vm.Pan(new Vector(_viewportTransform.X, _viewportTransform.Y));

            // RESET VISIVO:
            // 1. I nodi ora sono nella posizione giusta grazie al VM. 
            //    Dobbiamo annullare il TranslateTransform locale per non sommare lo spostamento due volte.
            _viewportTransform.X = 0;
            _viewportTransform.Y = 0;
            
            // 2. La griglia ora riceve il nuovo OffsetX/Y dal VM tramite Binding.
            //    Dobbiamo annullare l'offset temporaneo visivo.
            BackgroundGrid.SetVisualPan(0, 0);
        }
        
        ViewportContainer.IsHitTestVisible = true;

        _isPanning = false;
        _lastPointerPosForPanning = null;
        e.Pointer.Capture(null); 
        e.Handled = true; 
        this.Cursor = Cursor.Default;
    }

    // --- Gestione Zoom ---

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not BoardViewModel vm) return;
        
        var mousePos = e.GetPosition(this);
        double delta = e.Delta.Y;
        
        // Lo zoom passa direttamente al VM (accettabile perché è un evento discreto)
        vm.Zoom(delta, mousePos);
        
        e.Handled = true; 
    }
    
    // --- Pulizia ---
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isPanning = false;
        _lastPointerPosForPanning = null;
        this.Cursor = Cursor.Default;
        base.OnDetachedFromVisualTree(e);
    }
}