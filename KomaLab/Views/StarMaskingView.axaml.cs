using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImageProcessing;

namespace KomaLab.Views;

public partial class StarMaskingView : Window
{
    private bool _hasLoaded;
    private bool _isPanning;
    private Point? _lastPointerPosForPanning;
    private StarMaskingViewModel? _vm;

    public StarMaskingView()
    {
        InitializeComponent();

        // Cattura click globale per togliere focus dalle TextBox (commit valori)
        this.AddHandler(PointerPressedEvent, OnWindowPointerPressed_Global, RoutingStrategies.Tunnel, handledEventsToo: true);

        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;
        
        // Collega i pulsanti di zoom manuali (definiti nello XAML tramite Name)
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
            
            // Sottoscrizione eventi ViewModel
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.RequestFitToScreen += OnRequestFitToScreen;
            
            // Inizializza valori UI (TextBox) dai dati attuali
            UpdateUiValues();
            
            // Tenta il centraggio iniziale
            CenterImage();
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.RequestFitToScreen -= OnRequestFitToScreen;
            _vm = null;
        }
    }

    // Risponde alla richiesta del ViewModel di centrare la vista (es. nuova immagine caricata)
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
    }

    // =======================================================================
    // GESTIONE INPUT MANUALE (UI -> ViewModel)
    // =======================================================================

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox box) return;

        bool isD = double.TryParse(box.Text, out double d);
        bool isI = int.TryParse(box.Text, out int i);

        if (isD || isI)
        {
            switch (box.Name)
            {
                case "CometThresholdBox": _vm.CometThreshold = d; break;
                case "CometDilationBox": _vm.CometDilation = i; break;
                
                case "StarThresholdBox": _vm.StarThreshold = d; break;
                case "StarDilationBox": _vm.StarDilation = i; break;
            }
        }
        else
        {
            UpdateUiValues(); // Revert se input non valido
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus(); // Toglie il focus per forzare il LostFocus/Commit
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
        if (props.IsMiddleButtonPressed || props.IsLeftButtonPressed)
        {
            _lastPointerPosForPanning = e.GetPosition(previewBorder);
            _isPanning = true;
            e.Pointer.Capture(previewBorder);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
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
        
        // CTRL + Wheel = Zoom
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var mousePos = e.GetPosition(previewBorder);
            double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
            vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
            e.Handled = true;
            return;
        }

        // Wheel = Stretch (Livelli)
        double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
        double step = Math.Max(0.0001, (currentRange > 0.00001) ? currentRange * 0.05 : 1.0);
        if (e.Delta.Y < 0) step = -step;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift = Black Point
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
            // Normal = White Point
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