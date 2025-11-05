using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using KomaLab.ViewModels;

namespace KomaLab.Views;

public partial class AlignmentWindow : Window
{
    private Point? _lastPointerPosForPanning;
    private bool _isPanning = false;
    
    // NOTA: Con la logica del ViewModel attuale, il centraggio
    // all'avvio non è gestito. Lo risolveremo
    // se la logica di zoom/pan del VM si rivelerà un problema.
    
    public AlignmentWindow()
    {
        InitializeComponent();
        
        // Non è più necessario collegare i comandi
        // perché il XAML usa Command="{Binding ...}"
    }
    
    // --- Gestione Eventi Mouse (Pan e Soglie) ---

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this.PreviewBorder).Properties;
        if (props.IsMiddleButtonPressed)
        {
            _lastPointerPosForPanning = e.GetPosition(this.PreviewBorder);
            _isPanning = true; 
            e.Pointer.Capture(this.PreviewBorder); 
            e.Handled = true; 
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && e.InitialPressMouseButton == MouseButton.Middle)
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null); 
            e.Handled = true; 
            this.Cursor = Cursor.Default; 
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || DataContext is not AlignmentViewModel vm) return;
        
        if (!e.GetCurrentPoint(this.PreviewBorder).Properties.IsMiddleButtonPressed)
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
            return;
        }

        var pos = e.GetPosition(this.PreviewBorder);
        var delta = pos - _lastPointerPosForPanning!.Value;
        _lastPointerPosForPanning = pos;

        // Aggiorna il ViewModel (che gestisce il Pan)
        vm.PreviewOffsetX += delta.X;
        vm.PreviewOffsetY += delta.Y;
        e.Handled = true;
    }
    
    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || DataContext is not AlignmentViewModel vm) return;

        // Gestione Soglie (Rotellina)
        var props = e.GetCurrentPoint(this.PreviewBorder).Properties;
        if (!props.IsMiddleButtonPressed) // Non cambiare soglie se sto pannando
        {
            double currentRange = vm.WhitePoint - vm.BlackPoint;
            if (currentRange <= 0) currentRange = 1000;
            double deltaAmount = (currentRange * 0.10) * e.Delta.Y;
            bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

            if (isShiftPressed) vm.BlackPoint += deltaAmount;
            else vm.WhitePoint += deltaAmount;
            
            e.Handled = true;
        }
    }
    
    /// <summary>
    /// Gestisce il clic sinistro sul Canvas di interazione.
    /// </summary>
    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AlignmentViewModel vm || sender is not Control canvas)
            return;
            
        var props = e.GetCurrentPoint(canvas).Properties;
        if (props.IsLeftButtonPressed)
        {
            // Ottiene le coordinate *relative al canvas scalato*
            // (es. 150.5, 300.2). Questa è l'coordinata dell'immagine.
            var imageCoordinate = e.GetPosition(canvas);
            vm.SetTargetCoordinate(imageCoordinate);
            
            e.Handled = true;
        }
    }
}