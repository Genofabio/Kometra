using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree; 
using KomaLab.ViewModels;
using System.Linq; 
using System.Diagnostics; 

namespace KomaLab.Views;

public partial class SingleImageNodeView : UserControl
{
    private Point? _lastPos; 
    
    public SingleImageNodeView()
    {
        InitializeComponent();
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 1. Aggiorna il tipo di ViewModel
        if (DataContext is not SingleImageNodeViewModel vm)
        {
            Debug.WriteLine("ERRORE: DataContext non è SingleImageNodeViewModel.");
            return;
        }
        
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Questa logica rimane invariata perché ParentBoard
            // è nella classe base
            vm.ParentBoard.SetSelectedNode(vm);

            var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
            if (boardView == null)
            {
                Debug.WriteLine("ERRORE: boardView non trovato!");
                return;
            }
            
            _lastPos = e.GetPosition(boardView); 
            e.Pointer.Capture(this);
            e.Handled = true; 
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_lastPos != null && e.InitialPressMouseButton == MouseButton.Left)
        {
            _lastPos = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastPos == null) return;
        
        // 2. Aggiorna il tipo di ViewModel
        if (DataContext is not SingleImageNodeViewModel vm)
            return;
        
        var boardView = this.GetVisualAncestors().OfType<BoardView>().FirstOrDefault();
        if (boardView == null) return;
        
        var pos = e.GetPosition(boardView);
        var delta = pos - _lastPos.Value;
        _lastPos = pos;
        
        // Questo metodo ora è nella classe base
        vm.MoveNode(delta); 
        
        e.Handled = true;
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // 3. Aggiorna il tipo di ViewModel
        if (DataContext is not SingleImageNodeViewModel vm)
            return;

        // 4. Inoltra la logica al "motore" FitsImage
        //    (Questa logica è stata spostata da BoardViewModel 
        //    a qui, ma ora la inoltriamo a FitsDisplayViewModel)
        
        // Calcola il range attuale
        double currentRange = vm.FitsImage.WhitePoint - vm.FitsImage.BlackPoint;
        if (currentRange <= 0) currentRange = 1000; // Fallback
            
        double stepPercentage = 0.10; 
        double deltaAmount = (currentRange * stepPercentage) * e.Delta.Y;
            
        bool isShiftPressed = (e.KeyModifiers & KeyModifiers.Shift) == KeyModifiers.Shift;

        if (isShiftPressed)
        {
            vm.FitsImage.BlackPoint += deltaAmount;
        }
        else
        {
            vm.FitsImage.WhitePoint += deltaAmount;
        }
            
        e.Handled = true; 
    }
}