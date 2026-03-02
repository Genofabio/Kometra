using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.VisualTree;
using AlignmentToolViewModel = Kometra.ViewModels.ImageProcessing.AlignmentToolViewModel;
using ImageProcessing_AlignmentToolViewModel = Kometra.ViewModels.ImageProcessing.AlignmentToolViewModel;

namespace Kometra.Views;

public partial class AlignmentToolView : Window
{
    private bool _hasLoaded; 
    private Point? _lastPointerPosForPanning;
    private bool _isPanning; 
    
    public AlignmentToolView()
    {
        InitializeComponent();
        
        // Intercettazione globale dei pointer per la gestione del focus
        this.AddHandler(
            PointerPressedEvent, 
            OnWindowPointerPressed_Global, 
            RoutingStrategies.Tunnel, 
            handledEventsToo: true);

        this.Loaded += OnWindowLoaded;
        
        // Risoluzione riferimenti controlli definiti nello XAML
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null)
        {
            previewBorder.SizeChanged += OnPreviewSizeChanged;
        }
        
        // Hook manuale degli eventi se non gestiti tramite Command nello XAML
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
    
        if (DataContext is ImageProcessing_AlignmentToolViewModel vm)
        {
            // Aggiorna la dimensione della viewport nel VM
            vm.Viewport.ViewportSize = e.NewSize; 
        
            // Aspettiamo un istante per il rendering di Avalonia
            await Task.Delay(1); 
            vm.Viewport.ResetView();
        }
    }

    private async Task CenterImageAsync()
    {
        if (DataContext is not ImageProcessing_AlignmentToolViewModel vm)
            return;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder == null)
            return;

        try
        {
            // 1. Aspettiamo che l'immagine sia stata caricata nel ViewModel
            // Sevm.ImageLoadedTcs non è ancora completato, il codice si ferma qui
            await vm.ImageLoadedTcs.Task;

            // 2. Passiamo le dimensioni REALI correnti del border al ViewModel
            // previewBorder.Bounds.Size è garantito essere corretto qui
            vm.Viewport.ViewportSize = previewBorder.Bounds.Size;

            // 3. Eseguiamo il ResetView del Viewport (che ora ha ImageSize e ViewportSize)
            if (vm.ActiveRenderer != null)
            {
                vm.Viewport.ResetView();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--- ERROR IN CenterImageAsync --- {ex}");
        }
    }
    
    private bool IsInteractionBlocked()
    {
        if (DataContext is ImageProcessing_AlignmentToolViewModel vm)
        {
            // L'interazione è bloccata se il tool sta calcolando o elaborando (IsBusy)
            return vm.IsBusy; 
        }
        return true; 
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractionBlocked()) return;
    
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not ImageProcessing_AlignmentToolViewModel vm || previewBorder == null) return;
    
        var props = e.GetCurrentPoint(previewBorder).Properties;

        if (props.IsMiddleButtonPressed)
        {
            _lastPointerPosForPanning = e.GetPosition(previewBorder);
            _isPanning = true; 
            e.Pointer.Capture(previewBorder); 
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (props.IsLeftButtonPressed)
        {
            if (vm.ActiveRenderer?.Image == null) return;
            if (!vm.IsTargetPlacementAllowed) return;

            var viewportPos = e.GetPosition(previewBorder);
        
            // Calcolo coordinata immagine reale
            double imageX = (viewportPos.X - vm.Viewport.OffsetX) / vm.Viewport.Scale;
            double imageY = (viewportPos.Y - vm.Viewport.OffsetY) / vm.Viewport.Scale;
        
            vm.SetTargetCoordinate(new Point(imageX, imageY));
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
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (!_isPanning || DataContext is not ImageProcessing_AlignmentToolViewModel vm || previewBorder == null) return;
        
        if (!e.GetCurrentPoint(previewBorder).Properties.IsMiddleButtonPressed)
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
            return;
        }

        var pos = e.GetPosition(previewBorder);
        var delta = pos - _lastPointerPosForPanning!.Value;
        _lastPointerPosForPanning = pos;

        // Delega al componente Viewport
        vm.Viewport.ApplyPan(delta.X, delta.Y);
        e.Handled = true;
    }
    
    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (e.Handled || DataContext is not ImageProcessing_AlignmentToolViewModel vm || previewBorder == null) return;
        if (_isPanning) return; 
        if (vm.ActiveRenderer == null) return; 

        // ZOOM (CTRL + WHEEL)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var mousePos = e.GetPosition(previewBorder);
            double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);

            vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
            e.Handled = true;
            return;
        }

        // SOGLIE RADIOMETRICHE (SHIFT o DEFAULT + WHEEL)
        // 1. Calcolo step dinamico (5% del range attuale)
        double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
        double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
        
        // Step minimo per evitare blocco su float molto piccoli
        double step = Math.Max(0.0001, baseStep); 

        // Direzione
        if (e.Delta.Y < 0) step = -step;

        // 2. Applicazione con guardie anti-crossing
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Modifica BLACK POINT
            double newBlack = vm.ActiveRenderer.BlackPoint + step;
            
            // Se stiamo alzando il nero (step > 0), non deve superare il bianco.
            // Lasciamo un piccolo cuscinetto (step * 0.1).
            if (step > 0)
            {
                double maxAllowed = vm.ActiveRenderer.WhitePoint - (step * 0.1);
                vm.ActiveRenderer.BlackPoint = Math.Min(newBlack, maxAllowed);
            }
            else
            {
                vm.ActiveRenderer.BlackPoint = newBlack;
            }
        }
        else
        {
            // Modifica WHITE POINT
            double newWhite = vm.ActiveRenderer.WhitePoint + step;
            
            // Se stiamo abbassando il bianco (step < 0), non deve scendere sotto il nero.
            if (step < 0)
            {
                double minAllowed = vm.ActiveRenderer.BlackPoint + (Math.Abs(step) * 0.1);
                vm.ActiveRenderer.WhitePoint = Math.Max(newWhite, minAllowed);
            }
            else
            {
                vm.ActiveRenderer.WhitePoint = newWhite;
            }
        }

        e.Handled = true;
    }
    
    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not ImageProcessing_AlignmentToolViewModel vm || previewBorder == null) return;
        
        var centerPoint = new Point(previewBorder.Bounds.Width / 2, previewBorder.Bounds.Height / 2);
        vm.Viewport.ApplyZoomAtPoint(1.2, centerPoint);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not ImageProcessing_AlignmentToolViewModel vm || previewBorder == null) return;
        
        var centerPoint = new Point(previewBorder.Bounds.Width / 2, previewBorder.Bounds.Height / 2);
        vm.Viewport.ApplyZoomAtPoint(1 / 1.2, centerPoint);
    }
    
    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageProcessing_AlignmentToolViewModel vm)
        {
            vm.Viewport.ResetView();
        }
    }

    private async void OnResetThresholdsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageProcessing_AlignmentToolViewModel vm)
        {
            await vm.ResetThresholdsCommand.ExecuteAsync(null);
        }
    }
    
    private void OnInteractionCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        if (DataContext is not ImageProcessing_AlignmentToolViewModel vm || 
            vm.ActiveRenderer == null || 
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
    
    private void OnSearchRadiusTextBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void OnSearchRadiusTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || DataContext is not ImageProcessing_AlignmentToolViewModel vm)
            return;

        if (int.TryParse(textBox.Text, out int newValue))
        {
            // Usiamo dei limiti di sicurezza se non definiti nel VM
            int clampedValue = Math.Clamp(newValue, 5, 500);
            vm.SearchRadius = clampedValue;
            textBox.Text = clampedValue.ToString();
        }
        else
        {
            textBox.Text = vm.SearchRadius.ToString();
        }
    }
    
    private void OnWindowPointerPressed_Global(object? sender, PointerPressedEventArgs e)
    {
        var searchTextBox = this.FindControl<TextBox>("SearchRadiusTextBox");
        if (searchTextBox == null || !searchTextBox.IsFocused) return;

        var source = e.Source as Control;
        bool isClickOnTextBox = source != null && (source == searchTextBox || searchTextBox.IsVisualAncestorOf(source));

        if (!isClickOnTextBox)
        {
            this.Focus();
        }
    }
    
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        
        if (e.Source is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        // Tasto INVIO per scorrere la sequenza velocemente (proxy verso il Navigator)
        if (e.Key == Key.Enter)
        {
            if (DataContext is ImageProcessing_AlignmentToolViewModel vm && vm.Navigator.CanMoveNext)
            {
                vm.Navigator.NextCommand.Execute(null);
                e.Handled = true;
            }
        }
    
        base.OnKeyDown(e);
    }
}