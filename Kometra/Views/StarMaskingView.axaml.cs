using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.Globalization;
using Kometra.ViewModels.ImageProcessing;

namespace Kometra.Views;

public partial class StarMaskingView : Window
{
    private bool _hasLoaded;
    private bool _isPanning;
    private Point? _lastPointerPosForPanning;
    private StarMaskingViewModel? _vm;

    public StarMaskingView()
    {
        InitializeComponent();

        this.AddHandler(PointerPressedEvent, OnWindowPointerPressed_Global, RoutingStrategies.Tunnel, handledEventsToo: true);

        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;
        
        var zoomInBtn = this.FindControl<Button>("ZoomInButton");
        var zoomOutBtn = this.FindControl<Button>("ZoomOutButton");
        if (zoomInBtn != null) zoomInBtn.Click += OnZoomInClicked;
        if (zoomOutBtn != null) zoomOutBtn.Click += OnZoomOutClicked;
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _hasLoaded = true;
        
        if (DataContext is StarMaskingViewModel vm)
        {
            _vm = vm;
            
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.RequestFitToScreen += OnRequestFitToScreen;
            _vm.RequestClose += Close; 
            
            UpdateUiValues();
            CenterImage();
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.RequestFitToScreen -= OnRequestFitToScreen;
            _vm.RequestClose -= Close;
            _vm.Dispose(); 
            _vm = null;
        }
    }

    private void OnRequestFitToScreen()
    {
        Dispatcher.UIThread.InvokeAsync(CenterImage, DispatcherPriority.Background);
    }

    // =======================================================================
    // SINCRONIZZAZIONE DATI (ViewModel -> UI)
    // =======================================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() => 
        {
            if (_vm == null) return;
            switch (e.PropertyName)
            {
                case nameof(StarMaskingViewModel.CometThreshold): UpdateBox("CometThresholdBox", _vm.CometThreshold.ToString("N1")); break;
                case nameof(StarMaskingViewModel.CometDilation): UpdateBox("CometDilationBox", _vm.CometDilation.ToString()); break;
                case nameof(StarMaskingViewModel.StarThreshold): UpdateBox("StarThresholdBox", _vm.StarThreshold.ToString("N1")); break;
                case nameof(StarMaskingViewModel.StarDilation): UpdateBox("StarDilationBox", _vm.StarDilation.ToString()); break;
                case nameof(StarMaskingViewModel.MinStarDiameter): UpdateBox("MinStarDiameterBox", _vm.MinStarDiameter.ToString()); break;
            }
        });
    }

    private void UpdateBox(string name, string val)
    {
        var box = this.FindControl<TextBox>(name);
        if (box != null && !box.IsFocused) box.Text = val;
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        UpdateBox("CometThresholdBox", _vm.CometThreshold.ToString("N1"));
        UpdateBox("CometDilationBox", _vm.CometDilation.ToString());
        UpdateBox("StarThresholdBox", _vm.StarThreshold.ToString("N1"));
        UpdateBox("StarDilationBox", _vm.StarDilation.ToString());
        UpdateBox("MinStarDiameterBox", _vm.MinStarDiameter.ToString());
    }

    // =======================================================================
    // GESTIONE INPUT MANUALE (UI -> ViewModel)
    // =======================================================================

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox box) return;

        // Validazione stringa (accetta virgola e punto per i decimali in modo robusto)
        string input = (box.Text ?? "").Replace(',', '.');
        var culture = CultureInfo.InvariantCulture;

        if (box.Name == "CometThresholdBox" && double.TryParse(input, NumberStyles.Any, culture, out double ct))
            _vm.CometThreshold = ct;
        else if (box.Name == "CometDilationBox" && int.TryParse(input, out int cd))
            _vm.CometDilation = cd;
        else if (box.Name == "StarThresholdBox" && double.TryParse(input, NumberStyles.Any, culture, out double st))
            _vm.StarThreshold = st;
        else if (box.Name == "StarDilationBox" && int.TryParse(input, out int sd))
            _vm.StarDilation = sd;
        else if (box.Name == "MinStarDiameterBox" && int.TryParse(input, out int md))
            _vm.MinStarDiameter = md;

        // In tutti i casi (anche errori di digitazione o reset), forza il refresh UI
        UpdateUiValues();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus(); 
            e.Handled = true;
        }
    }

    // =======================================================================
    // GESTIONE VIEWPORT E MOUSE
    // =======================================================================

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm != null) _vm.Viewport.ViewportSize = e.NewSize;
    }

    private void CenterImage()
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && previewBorder != null && previewBorder.Bounds.Width > 0)
        {
            _vm.Viewport.ViewportSize = previewBorder.Bounds.Size;
            if (_vm.Viewport.ImageSize.Width > 0) _vm.Viewport.ResetView();
        }
    }

    private bool IsInteractionBlocked() => DataContext is StarMaskingViewModel vm && vm.IsBusy;

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder == null) return;

        var props = e.GetCurrentPoint(previewBorder).Properties;
        
        bool isMiddlePan = props.IsMiddleButtonPressed;
        bool isAltPan = props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (isMiddlePan || isAltPan)
        {
            _lastPointerPosForPanning = e.GetPosition(previewBorder);
            _isPanning = true;
            e.Pointer.Capture(previewBorder);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && (e.InitialPressMouseButton == MouseButton.Middle || e.InitialPressMouseButton == MouseButton.Left))
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (!_isPanning || DataContext is not StarMaskingViewModel vm || previewBorder == null || _lastPointerPosForPanning == null) return;

        var props = e.GetCurrentPoint(previewBorder).Properties;

        // Se l'utente rilascia i tasti, sganciamo il pan
        if (!props.IsMiddleButtonPressed && !props.IsLeftButtonPressed)
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
            return;
        }

        var currentPos = e.GetPosition(previewBorder);
        var delta = currentPos - _lastPointerPosForPanning.Value;
        _lastPointerPosForPanning = currentPos;
        vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (IsInteractionBlocked()) return;
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not StarMaskingViewModel vm || previewBorder == null || vm.ActiveRenderer == null) return;
        
        // FIX CROSS-PLATFORM: Gestione Delta.X per Mac (Shift + Scroll)
        double effectiveDelta = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(effectiveDelta) < 0.0001) return;

        // ZOOM (CTRL o CMD + WHEEL)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            var mousePos = e.GetPosition(previewBorder);
            double factor = effectiveDelta > 0 ? 1.1 : (1.0 / 1.1);
            vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
            e.Handled = true;
            return;
        }

        double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
        double step = Math.Max(0.0001, (currentRange > 0.00001) ? currentRange * 0.05 : 1.0);
        
        if (effectiveDelta < 0) step = -step;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
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
        e.Handled = true;
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is StarMaskingViewModel vm && previewBorder != null) 
            vm.Viewport.ApplyZoomAtPoint(1.2, previewBorder.Bounds.Center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is StarMaskingViewModel vm && previewBorder != null) 
            vm.Viewport.ApplyZoomAtPoint(1.0 / 1.2, previewBorder.Bounds.Center);
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    private void OnWindowPointerPressed_Global(object? sender, PointerPressedEventArgs e) => this.Focus();
}