using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KomaLab.ViewModels;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Media;

namespace KomaLab.Views;

public partial class AlignmentWindow : Window
{
    private bool _hasLoaded = false; 
    private Point? _lastPointerPosForPanning;
    private bool _isPanning = false; 
    
    public AlignmentWindow()
    {
        InitializeComponent();

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

        // --- MODIFICA: Aggancio i nuovi pulsanti ---
        var resetViewButton = this.FindControl<Button>("ResetViewButton");
        var resetThresholdsButton = this.FindControl<Button>("ResetThresholdsButton");
        
        if (resetViewButton != null) 
            resetViewButton.Click += OnResetViewClicked;
            
        if (resetThresholdsButton != null) 
            resetThresholdsButton.Click += OnResetThresholdsClicked;
        // --- FINE MODIFICA ---
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _hasLoaded = true;
        await CenterImageAsync();
    }

    private async void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_hasLoaded) return;
        await Task.Delay(1); 
        await CenterImageAsync();
    }

    private async Task CenterImageAsync()
    {
        // (Questo metodo rimane invariato, fa già quello che serve)
        
        if (DataContext is not AlignmentViewModel vm)
            return;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder == null)
            return;

        try
        {
            await vm.ImageLoadedTcs.Task;

            if (vm.ActiveImage == null || vm.ActiveImage.ImageSize == default(Size))
            {
                Debug.WriteLine("[TestView] CenterImage fallito: ActiveImage non valido.");
                return;
            }

            var viewportSize = previewBorder.Bounds.Size;
            var imageSize = vm.ActiveImage.ImageSize;

            if (imageSize.Width <= 0 || imageSize.Height <= 0 || viewportSize.Width <= 0 || viewportSize.Height <= 0)
                return;

            double scaleX = viewportSize.Width / imageSize.Width;
            double scaleY = viewportSize.Height / imageSize.Height;
            double newScale = Math.Min(scaleX, scaleY);

            double scaledWidth = imageSize.Width * newScale;
            double scaledHeight = imageSize.Height * newScale;
            
            double newOffsetX = (viewportSize.Width - scaledWidth) / 2;
            double newOffsetY = (viewportSize.Height - scaledHeight) / 2;

            vm.Scale = newScale;
            vm.OffsetX = newOffsetX;
            vm.OffsetY = newOffsetY;
            
            vm.UpdateTargetMarkerPosition();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"--- CRASH IN TestView.CenterImageAsync --- {ex}");
        }
    }
    
    // --- GESTORI EVENTI PER PAN, ZOOM E CLIC ---
    // (OnPreviewPointerPressed, Released, Moved rimangono invariati)

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AlignmentViewModel vm || this.PreviewBorder == null) return;
        
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
            double imageX = (viewportPos.X - vm.OffsetX) / vm.Scale;
            double imageY = (viewportPos.Y - vm.OffsetY) / vm.Scale;
            var imageSize = vm.ActiveImage.ImageSize;
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
        if (!_isPanning || DataContext is not AlignmentViewModel vm || this.PreviewBorder == null) return;
        
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
    
    // (OnPreviewPointerWheelChanged rimane invariato)
    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || DataContext is not AlignmentViewModel vm || this.PreviewBorder == null) return;
        
        if (_isPanning) return; 
        
        if (vm.ActiveImage == null) return;

        double currentRange = vm.ActiveImage.WhitePoint - vm.ActiveImage.BlackPoint;
        double step = currentRange * 0.05; 
        
        if (e.Delta.Y < 0) step = -step; 

        var modifiers = e.KeyModifiers;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            double newBlack = vm.ActiveImage.BlackPoint + step;
            vm.ActiveImage.BlackPoint = Math.Min(newBlack, vm.ActiveImage.WhitePoint - 1); 
        }
        else
        {
            double newWhite = vm.ActiveImage.WhitePoint + step;
            vm.ActiveImage.WhitePoint = Math.Max(newWhite, vm.ActiveImage.BlackPoint + 1);
        }

        e.Handled = true;
    }

    // (Gestori Zoom In/Out rimangono invariati)
    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlignmentViewModel vm || this.PreviewBorder == null) return;
        
        var centerPoint = new Point(this.PreviewBorder.Bounds.Width / 2, this.PreviewBorder.Bounds.Height / 2);
        vm.ApplyZoomAtPoint(1.2, centerPoint);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AlignmentViewModel vm || this.PreviewBorder == null) return;
        
        var centerPoint = new Point(this.PreviewBorder.Bounds.Width / 2, this.PreviewBorder.Bounds.Height / 2);
        vm.ApplyZoomAtPoint(1 / 1.2, centerPoint);
    }
    
    // --- MODIFICA: Nuovi gestori per i pulsanti di Reset ---
    
    /// <summary>
    /// Richiama CenterImageAsync per resettare zoom e pan.
    /// </summary>
    private async void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        // Questo metodo fa già tutto (calcola lo zoom-to-fit e centra)
        await CenterImageAsync();
    }

    /// <summary>
    /// Chiama il metodo ResetThresholdsAsync sul ViewModel dell'immagine.
    /// </summary>
    private async void OnResetThresholdsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AlignmentViewModel vm && vm.ActiveImage != null)
        {
            // Il FitsDisplayViewModel sa come resettare le sue soglie
            await vm.ActiveImage.ResetThresholdsAsync();
        }
    }
    // --- FINE MODIFICA ---
    
    // (OnInteractionCanvasPointerPressed rimane invariato)
    private void OnInteractionCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not AlignmentViewModel vm || 
            vm.ActiveImage == null || 
            sender is not Control canvas)
            return;
            
        var props = e.GetCurrentPoint(canvas).Properties;
        if (props.IsLeftButtonPressed)
        {
            var imageCoordinate = e.GetPosition(canvas);
            vm.SetTargetCoordinate(imageCoordinate);
            e.Handled = true; 
        }
        else if (props.IsRightButtonPressed) 
        {
            vm.ClearTarget();
            e.Handled = true;
        }
    }
}