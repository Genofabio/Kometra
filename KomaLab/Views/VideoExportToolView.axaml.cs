using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImportExport;

namespace KomaLab.Views;

public partial class VideoExportToolView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;
    private VideoExportToolViewModel? _vm;

    public VideoExportToolView()
    {
        InitializeComponent();
        this.Loaded += OnWindowLoaded;
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null) previewBorder.SizeChanged += OnPreviewSizeChanged;
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoExportToolViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            // Inizializza il testo nella box del riscalamento
            UpdateUiValues();

            try 
            {
                // Attendiamo che il primo frame sia caricato e renderizzato
                await _vm.ImageLoadedTcs.Task;
                
                // Ora che l'immagine esiste, centriamo la vista
                CenterImage();
                
                // Forza l'aggiornamento delle soglie nel riepilogo
                _vm.NotifyThresholdsChanged();
            }
            catch { }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(VideoExportToolViewModel.ScaleFactor))
        {
            UpdateBox("ScaleFactorBox", _vm.ScaleFactor.ToString("N2"));
        }
    }

    private void UpdateBox(string name, string val)
    {
        var box = this.FindControl<TextBox>(name);
        if (box != null && !box.IsFocused) box.Text = val;
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        UpdateBox("ScaleFactorBox", _vm.ScaleFactor.ToString("N2"));
    }

    // =======================================================================
    // GESTIONE INPUT MANUALE (Validazione Custom)
    // =======================================================================

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox box) return;

        // Sostituiamo la virgola con il punto per compatibilità universale
        string cleanText = box.Text.Replace(",", ".");
    
        if (double.TryParse(cleanText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
        {
            _vm.ScaleFactor = Math.Clamp(val, 0.1, 2.0);
        }
    
        // Forza la UI a mostrare il valore formattato correttamente
        UpdateUiValues();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus(); // Triggera LostFocus -> Commit
            e.Handled = true;
        }
    }

    // =======================================================================
    // VIEWPORT E MOUSE
    // =======================================================================

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm != null && e.NewSize.Width > 0) 
        { 
            _vm.Viewport.ViewportSize = e.NewSize; 
            _vm.Viewport.ResetView(); 
        }
    }

    private void CenterImage()
    {
        var b = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && b != null && b.Bounds.Width > 0) 
        { 
            _vm.Viewport.ViewportSize = b.Bounds.Size; 
            _vm.Viewport.ResetView(); 
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e) 
    {
        var border = sender as Control;
        if (border == null) return;
        if (e.GetCurrentPoint(border).Properties.IsMiddleButtonPressed) 
        {
            _isPanning = true; 
            _lastPointerPos = e.GetPosition(border);
            e.Pointer.Capture(border);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e) 
    {
        _isPanning = false; e.Pointer.Capture(null); this.Cursor = Cursor.Default;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e) 
    {
        if (!_isPanning || _vm == null || _lastPointerPos == null) return;
        var pos = e.GetPosition(sender as Control);
        var delta = pos - _lastPointerPos.Value; 
        _lastPointerPos = pos;
        _vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e) 
    {
        if (_vm == null || _vm.ActiveRenderer == null) return;
        var border = sender as Control; if (border == null) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) 
        {
            _vm.Viewport.ApplyZoomAtPoint(e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1, e.GetPosition(border));
        } 
        else 
        {
            double range = Math.Abs(_vm.ActiveRenderer.WhitePoint - _vm.ActiveRenderer.BlackPoint);
            double step = (range > 1e-6 ? range * 0.05 : 0.001) * (e.Delta.Y < 0 ? -1 : 1);

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) 
                _vm.ActiveRenderer.BlackPoint += step;
            else 
                _vm.ActiveRenderer.WhitePoint += step;

            _vm.NotifyThresholdsChanged();
        }
        e.Handled = true;
    }
    
    /// <summary>
    /// Gestisce il click sullo sfondo della finestra per rimuovere il focus dalle TextBox.
    /// </summary>
    private void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Rimuove il focus dalle TextBox per triggerare il commit dei dati (ScaleFactor)
        this.Focus();
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e) => CenterImage();
    private void OnZoomInClicked(object? sender, RoutedEventArgs e) => _vm?.Viewport.ApplyZoomAtPoint(1.2, this.FindControl<Border>("PreviewBorder")!.Bounds.Center);
    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => _vm?.Viewport.ApplyZoomAtPoint(1.0 / 1.2, this.FindControl<Border>("PreviewBorder")!.Bounds.Center);
    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}