using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImportExport;

namespace KomaLab.Views;

public partial class ExportView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;
    private ExportViewModel? _vm;

    public ExportView()
    {
        InitializeComponent();
        
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if(previewBorder != null)
        {
             previewBorder.SizeChanged += OnPreviewSizeChanged;
        }
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExportViewModel vm)
        {
            _vm = vm;
            _vm.RequestClose += Close;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            try 
            {
                await _vm.ImageLoadedTcs.Task;
                CenterImage();
            }
            catch { }
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RequestClose -= Close;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportViewModel.ActiveRenderer))
        {
            Dispatcher.UIThread.Post(() => CenterImage(), DispatcherPriority.Background);
        }
    }

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm != null && e.NewSize.Width > 0 && e.NewSize.Height > 0) 
        { 
            // 1. Aggiorna le dimensioni logiche del viewport
            _vm.Viewport.ViewportSize = e.NewSize; 
            
            // 2. CORREZIONE: Forziamo il ricalcolo della vista (Fit to Screen)
            // quando la finestra viene ridimensionata.
            if (_vm.ActiveRenderer != null)
            {
                CenterImage();
            }
        }
    }

    private void CenterImage()
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && border != null && border.Bounds.Width > 0 && border.Bounds.Height > 0) 
        { 
            _vm.Viewport.ViewportSize = border.Bounds.Size; 
            _vm.Viewport.ResetView(); 
        }
    }

    // --- INTERAZIONE MOUSE ---

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var border = sender as Control;
        if (border == null || _vm?.ActiveRenderer == null) return;

        var properties = e.GetCurrentPoint(border).Properties;
        if (properties.IsMiddleButtonPressed || properties.IsLeftButtonPressed)
        {
            _isPanning = true;
            _lastPointerPos = e.GetPosition(border);
            e.Pointer.Capture(border);
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPanning = false;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _vm == null || _lastPointerPos == null) return;
        
        var border = sender as Control;
        var pos = e.GetPosition(border);
        var delta = pos - _lastPointerPos.Value;
        _lastPointerPos = pos;
        
        _vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_vm?.ActiveRenderer == null) return;

        var modifiers = e.KeyModifiers;
        
        // --- 1. ZOOM (CTRL + WHEEL) ---
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            var border = sender as Control;
            double zoomFactor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
            _vm.Viewport.ApplyZoomAtPoint(zoomFactor, e.GetPosition(border));
            e.Handled = true;
            return;
        }

        // --- 2. SOGLIE DINAMICHE (5% RANGE) ---
        
        // Calcolo del range attuale e dello step dinamico
        double currentRange = Math.Abs(_vm.CurrentWhitePoint - _vm.CurrentBlackPoint);
        double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
        
        // Step minimo per non rimanere bloccati su valori infinitesimali
        double step = Math.Max(0.0001, baseStep); 

        // Direzione della rotella
        if (e.Delta.Y < 0) step = -step;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            // Modifica BLACK POINT (con protezione anti-crossing)
            double newBlack = _vm.CurrentBlackPoint + step;
            
            if (step > 0) // Alzando il nero
            {
                // Non superare il bianco, mantenendo un piccolo cuscinetto (10% dello step)
                double maxAllowed = _vm.CurrentWhitePoint - (step * 0.1);
                _vm.CurrentBlackPoint = Math.Clamp(Math.Min(newBlack, maxAllowed), 0, 65535);
            }
            else // Abbassando il nero
            {
                _vm.CurrentBlackPoint = Math.Max(0, newBlack);
            }
        }
        else
        {
            // Modifica WHITE POINT (con protezione anti-crossing)
            double newWhite = _vm.CurrentWhitePoint + step;
            
            if (step < 0) // Abbassando il bianco
            {
                // Non scendere sotto il nero
                double minAllowed = _vm.CurrentBlackPoint + (Math.Abs(step) * 0.1);
                _vm.CurrentWhitePoint = Math.Clamp(Math.Max(newWhite, minAllowed), 0, 65535);
            }
            else // Alzando il bianco
            {
                _vm.CurrentWhitePoint = Math.Min(65535, newWhite);
            }
        }

        e.Handled = true;
    }

    // --- PULSANTI OVERLAY ---

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (_vm?.ActiveRenderer != null && border != null) 
            _vm.Viewport.ApplyZoomAtPoint(1.2, border.Bounds.Center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (_vm?.ActiveRenderer != null && border != null) 
            _vm.Viewport.ApplyZoomAtPoint(1.0/1.2, border.Bounds.Center);
    }

    private void OnFitViewClicked(object? sender, RoutedEventArgs e)
    {
        CenterImage();
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    // --- BLOCCA ROTELLA SU COMBOBOX ---
    private void OnComboBoxPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}

