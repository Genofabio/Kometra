using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KomaLab.ViewModels;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.VisualTree;

namespace KomaLab.Views;

public partial class AlignmentToolView : Window
{
    private bool _hasLoaded; 
    private Point? _lastPointerPosForPanning;
    private bool _isPanning; 
    
    public AlignmentToolView()
    {
        InitializeComponent();
        
        this.AddHandler(
            PointerPressedEvent, 
            OnWindowPointerPressed_Global, 
            RoutingStrategies.Tunnel, 
            handledEventsToo: true);

        this.Loaded += OnWindowLoaded;
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null)
        {
            previewBorder.SizeChanged += OnPreviewSizeChanged;
        }
        
        var zoomInButton = this.FindControl<Button>("ZoomInButton");
        var zoomOutButton = this.FindControl<Button>("ZoomOutButton");
        if (zoomInButton != null) zoomInButton.Click += OnZoomInClicked;
        if (zoomOutButton != null) zoomOutButton.Click += OnZoomOutClicked;
        
        var resetViewButton = this.FindControl<Button>("ResetViewButton");
        var resetThresholdsButton = this.FindControl<Button>("ResetThresholdsButton");
        
        if (resetViewButton != null) 
            resetViewButton.Click += OnResetViewClicked;
            
        if (resetThresholdsButton != null) 
            resetThresholdsButton.Click += OnResetThresholdsClicked;
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _hasLoaded = true;
        await CenterImageAsync();
    }

    private async void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_hasLoaded) return;
        
        if (DataContext is AlignmentToolViewModel vm)
        {
            vm.ViewportSize = e.NewSize; 
        }

        await Task.Delay(1); 
        await CenterImageAsync();
    }

    private async Task CenterImageAsync()
    {
        if (DataContext is not AlignmentToolViewModel vm)
            return;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder == null)
            return;

        try
        {
            // 1. Informa il VM della dimensione corrente (per sicurezza)
            vm.ViewportSize = previewBorder.Bounds.Size;

            // 2. Aspetta che l'immagine sia caricata (questo è corretto)
            await vm.ImageLoadedTcs.Task;
        
            // 3. CHIAMA il metodo del VM. Non fare più la matematica qui.
            vm.ResetViewCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN TestView.CenterImageAsync --- {ex}");
        }
    }
    
    private bool IsInteractionBlocked()
    {
        if (DataContext is AlignmentToolViewModel vm)
        {
            return !vm.IsInteractionEnabled; // O vm.IsProcessingVisible
        }
        return true; // Se non c'è VM, blocca per sicurezza
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        if (DataContext is not AlignmentToolViewModel vm || this.PreviewBorder == null) return;
        
        var props = e.GetCurrentPoint(this.PreviewBorder).Properties;

        if (props.IsMiddleButtonPressed)
        {
            _lastPointerPosForPanning = e.GetPosition(this.PreviewBorder);
            _isPanning = true; 
            e.Pointer.Capture(this.PreviewBorder); 
            e.Handled = true; 
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (props.IsLeftButtonPressed)
        {
            if (vm.ActiveImage == null || vm.ActiveImage.ImageSize == default(Size))
                return;

            var viewportPos = e.GetPosition(this.PreviewBorder);
            double imageX = (viewportPos.X - vm.Viewport.OffsetX) / vm.Viewport.Scale;
            double imageY = (viewportPos.Y - vm.Viewport.OffsetY) / vm.Viewport.Scale;
            var imageSize = vm.ActiveImage.ImageSize;
        
            // Questa è la logica che fa lo "snap"
            double clampedX = Math.Clamp(imageX, 0, imageSize.Width);
            double clampedY = Math.Clamp(imageY, 0, imageSize.Height);
        
            vm.SetTargetCoordinate(new Point(clampedX, clampedY));
            e.Handled = true;
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
        if (IsInteractionBlocked()) return;
        
        if (!_isPanning || DataContext is not AlignmentToolViewModel vm || this.PreviewBorder == null) return;
        
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

        vm.ApplyPan(delta.X, delta.Y);
        e.Handled = true;
    }
    
    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        if (e.Handled || DataContext is not AlignmentToolViewModel vm || this.PreviewBorder == null) return;
        if (_isPanning) return; 
        if (vm.ActiveImage == null) return; 

        double currentRange = vm.WhitePoint - vm.BlackPoint;
        double step = currentRange * 0.05; 
    
        if (e.Delta.Y < 0) step = -step; 

        var modifiers = e.KeyModifiers;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            double newBlack = vm.BlackPoint + step;
            vm.BlackPoint = Math.Min(newBlack, vm.WhitePoint - 1); 
        }
        else
        {
            double newWhite = vm.WhitePoint + step;
            vm.WhitePoint = Math.Max(newWhite, vm.BlackPoint + 1);
        }

        e.Handled = true;
    }
    
    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlignmentToolViewModel vm || this.PreviewBorder == null) return;
        
        var centerPoint = new Point(this.PreviewBorder.Bounds.Width / 2, this.PreviewBorder.Bounds.Height / 2);
        vm.ApplyZoomAtPoint(1.2, centerPoint);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlignmentToolViewModel vm || this.PreviewBorder == null) return;
        
        var centerPoint = new Point(this.PreviewBorder.Bounds.Width / 2, this.PreviewBorder.Bounds.Height / 2);
        vm.ApplyZoomAtPoint(1 / 1.2, centerPoint);
    }
    
    /// <summary>
    /// Richiama CenterImageAsync per resettare zoom e pan.
    /// </summary>
    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AlignmentToolViewModel vm)
        {
            vm.ResetViewCommand.Execute(null);
        }
    }

    /// <summary>
    /// Chiama il metodo ResetThresholdsAsync sul ViewModel dell'immagine.
    /// </summary>
    private async void OnResetThresholdsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AlignmentToolViewModel vm)
        {
            // Chiama il COMANDO pubblico, che è asincrono
            await vm.ResetThresholdsCommand.ExecuteAsync(null);
        }
    }
    
    private void OnInteractionCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        if (DataContext is not AlignmentToolViewModel vm || 
            vm.ActiveImage == null || 
            sender is not Control canvas)
            return;
            
        var props = e.GetCurrentPoint(canvas).Properties;
        if (props.IsRightButtonPressed) 
        {
            vm.ClearTarget();
            e.Handled = true;
        }
    }
    
    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
    
    /// <summary>
    /// Seleziona tutto il testo quando l'utente clicca nella TextBox.
    /// </summary>
    private void OnSearchRadiusTextBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    /// <summary>
    /// Valida il valore quando l'utente lascia la TextBox.
    /// </summary>
    private void OnSearchRadiusTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || DataContext is not AlignmentToolViewModel vm)
        {
            return;
        }

        // 1. Prova a convertire il testo in un numero intero
        if (int.TryParse(textBox.Text, out int newValue))
        {
            // 2. Se è un numero, "forzalo" a rimanere nei limiti (Min/Max)
            int clampedValue = Math.Clamp(newValue, vm.MinSearchRadius, vm.MaxSearchRadius);

            // 3. Aggiorna il ViewModel
            vm.SearchRadius = clampedValue;
            
            // 4. Aggiorna la TextBox se il valore è stato modificato (es. "999" -> "100")
            textBox.Text = clampedValue.ToString();
        }
        else
        {
            // 4. Se il testo non è valido (es. "abc"), ripristina il valore precedente dal ViewModel.
            textBox.Text = vm.SearchRadius.ToString();
        }
    }
    
    private void OnWindowPointerPressed_Global(object? sender, PointerPressedEventArgs e)
    {
        // 1. Assicurati che la TextBox esista e abbia il focus
        if (this.SearchRadiusTextBox == null || !this.SearchRadiusTextBox.IsFocused)
        {
            return; 
        }

        // 2. Controlla se il click è avvenuto SULLA TextBox
        var source = e.Source as Control;
        bool isClickOnTextBox = false;
        
        if (source != null)
        {
            isClickOnTextBox = source == this.SearchRadiusTextBox || 
                               this.SearchRadiusTextBox.IsVisualAncestorOf(source);
        }

        // 3. Se il click è avvenuto FUORI dalla TextBox, pulisci il focus
        if (!isClickOnTextBox)
        {
            this.Focus();
        }
    }
    
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        // Se il focus è dentro una TextBox, lascia che la TextBox gestisca l'Enter (non cambiare immagine)
        if (e.Source is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (DataContext is AlignmentToolViewModel vm && vm.NextImageCommand.CanExecute(null))
            {
                vm.NextImageCommand.Execute(null);
                e.Handled = true;
            }
        }
    
        base.OnKeyDown(e);
    }
    
}