using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImageProcessing;

namespace KomaLab.Views;

public partial class ImageEnhancementToolView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;
    private ImageEnhancementToolViewModel? _vm;

    public ImageEnhancementToolView()
    {
        InitializeComponent();
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null) previewBorder.SizeChanged += OnPreviewSizeChanged;
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageEnhancementToolViewModel vm)
        {
            _vm = vm;
            // Sottoscriviamo per ricevere aggiornamenti dal VM
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            // Sottoscrizione all'evento per il fit automatico
            _vm.RequestFitToScreen += OnRequestFitToScreen;
            
            // Inizializza i valori nelle TextBox all'avvio
            UpdateUiValues();

            try 
            {
                await vm.ImageLoadedTcs.Task;
                UpdateUiValues(); 
                
                // Fit iniziale all'apertura
                CenterImage();
            }
            catch { }
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.RequestFitToScreen -= OnRequestFitToScreen;
        
            // Disposizione manuale sicura
            _vm.Dispose(); 
            _vm = null;
        }
    
        // Stacca il DataContext
        this.DataContext = null;
    
        // Pulizia eventi controlli
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null) previewBorder.SizeChanged -= OnPreviewSizeChanged;
    }

    private void OnRequestFitToScreen()
    {
        // Eseguiamo sul thread UI per sicurezza
        Dispatcher.UIThread.InvokeAsync(CenterImage);
    }

    // =======================================================================
    // GESTIONE MANUALE DATI (VM -> UI)
    // =======================================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() => 
        {
            if (_vm == null) return;

            switch (e.PropertyName)
            {
                // Larson-Sekanina
                case nameof(ImageEnhancementToolViewModel.RotationAngle): UpdateBox("RotationBox", _vm.RotationAngle.ToString("F1")); break;
                case nameof(ImageEnhancementToolViewModel.ShiftX): UpdateBox("ShiftXBox", _vm.ShiftX.ToString("F1")); break;
                case nameof(ImageEnhancementToolViewModel.ShiftY): UpdateBox("ShiftYBox", _vm.ShiftY.ToString("F1")); break;

                // Azimuthal / Radial / M.C.M. / R.W.M.
                case nameof(ImageEnhancementToolViewModel.RadialSubsampling): UpdateBox("SubsamplingBox", _vm.RadialSubsampling.ToString()); break;
                case nameof(ImageEnhancementToolViewModel.RadialMaxRadius): UpdateBox("McmRadiusBox", _vm.RadialMaxRadius.ToString()); break;
                case nameof(ImageEnhancementToolViewModel.BackgroundValue): UpdateBox("BgValueBox", _vm.BackgroundValue.ToString("F2")); break;
                
                case nameof(ImageEnhancementToolViewModel.AzimuthalRejSigma): UpdateBox("AzimuthalRejBox", _vm.AzimuthalRejSigma.ToString("F1")); break;
                case nameof(ImageEnhancementToolViewModel.AzimuthalNormSigma): UpdateBox("AzimuthalNormBox", _vm.AzimuthalNormSigma.ToString("F1")); break;

                // RVSF (Single & Mosaic Min)
                case nameof(ImageEnhancementToolViewModel.RvsfA_1): 
                    UpdateBox("RvsfA1Box", _vm.RvsfA_1.ToString("F2")); 
                    UpdateBox("RvsfAMinBox", _vm.RvsfA_1.ToString("F2"));
                    break;
                case nameof(ImageEnhancementToolViewModel.RvsfB_1): 
                    UpdateBox("RvsfB1Box", _vm.RvsfB_1.ToString("F2")); 
                    UpdateBox("RvsfBMinBox", _vm.RvsfB_1.ToString("F2"));
                    break;
                case nameof(ImageEnhancementToolViewModel.RvsfN_1): 
                    UpdateBox("RvsfN1Box", _vm.RvsfN_1.ToString("F2")); 
                    UpdateBox("RvsfNMinBox", _vm.RvsfN_1.ToString("F2"));
                    break;

                // RVSF (Mosaic Max)
                case nameof(ImageEnhancementToolViewModel.RvsfA_2): UpdateBox("RvsfAMaxBox", _vm.RvsfA_2.ToString("F2")); break;
                case nameof(ImageEnhancementToolViewModel.RvsfB_2): UpdateBox("RvsfBMaxBox", _vm.RvsfB_2.ToString("F2")); break;
                case nameof(ImageEnhancementToolViewModel.RvsfN_2): UpdateBox("RvsfNMaxBox", _vm.RvsfN_2.ToString("F2")); break;

                // Frangi
                case nameof(ImageEnhancementToolViewModel.FrangiSigma): UpdateBox("FrangiSigmaBox", _vm.FrangiSigma.ToString("F2")); break;
                case nameof(ImageEnhancementToolViewModel.FrangiBeta): UpdateBox("FrangiBetaBox", _vm.FrangiBeta.ToString("F2")); break;
                case nameof(ImageEnhancementToolViewModel.FrangiC): UpdateBox("FrangiCBox", _vm.FrangiC.ToString("F4")); break;

                // Tensor
                case nameof(ImageEnhancementToolViewModel.TensorSigma): UpdateBox("TensorSigmaBox", _vm.TensorSigma.ToString()); break;
                case nameof(ImageEnhancementToolViewModel.TensorRho): UpdateBox("TensorRhoBox", _vm.TensorRho.ToString()); break;

                // Top Hat
                case nameof(ImageEnhancementToolViewModel.TopHatKernelSize): UpdateBox("TopHatBox", _vm.TopHatKernelSize.ToString()); break;

                // Clahe
                case nameof(ImageEnhancementToolViewModel.ClaheClipLimit): UpdateBox("ClaheClipBox", _vm.ClaheClipLimit.ToString("F1")); break;
                case nameof(ImageEnhancementToolViewModel.ClaheTileSize): UpdateBox("ClaheTileBox", _vm.ClaheTileSize.ToString()); break;

                // Local Norm
                case nameof(ImageEnhancementToolViewModel.LocalNormWindowSize): UpdateBox("LocalNormWindowBox", _vm.LocalNormWindowSize.ToString()); break;
                case nameof(ImageEnhancementToolViewModel.LocalNormIntensity): UpdateBox("LocalNormIntensityBox", _vm.LocalNormIntensity.ToString("F1")); break;

                // Generic Kernel
                case nameof(ImageEnhancementToolViewModel.KernelSize): UpdateBox("KernelSizeBox", _vm.KernelSize.ToString()); break;
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
        
        UpdateBox("RotationBox", _vm.RotationAngle.ToString("F1"));
        UpdateBox("ShiftXBox", _vm.ShiftX.ToString("F1"));
        UpdateBox("ShiftYBox", _vm.ShiftY.ToString("F1"));
        
        UpdateBox("SubsamplingBox", _vm.RadialSubsampling.ToString());
        UpdateBox("McmRadiusBox", _vm.RadialMaxRadius.ToString());
        UpdateBox("BgValueBox", _vm.BackgroundValue.ToString("F2"));
        UpdateBox("AzimuthalRejBox", _vm.AzimuthalRejSigma.ToString("F1"));
        UpdateBox("AzimuthalNormBox", _vm.AzimuthalNormSigma.ToString("F1"));

        // RVSF Single
        UpdateBox("RvsfA1Box", _vm.RvsfA_1.ToString("F2"));
        UpdateBox("RvsfB1Box", _vm.RvsfB_1.ToString("F2"));
        UpdateBox("RvsfN1Box", _vm.RvsfN_1.ToString("F2"));

        // RVSF Mosaic
        UpdateBox("RvsfAMinBox", _vm.RvsfA_1.ToString("F2"));
        UpdateBox("RvsfAMaxBox", _vm.RvsfA_2.ToString("F2"));
        UpdateBox("RvsfBMinBox", _vm.RvsfB_1.ToString("F2"));
        UpdateBox("RvsfBMaxBox", _vm.RvsfB_2.ToString("F2"));
        UpdateBox("RvsfNMinBox", _vm.RvsfN_1.ToString("F2"));
        UpdateBox("RvsfNMaxBox", _vm.RvsfN_2.ToString("F2"));

        UpdateBox("FrangiSigmaBox", _vm.FrangiSigma.ToString("F2"));
        UpdateBox("FrangiBetaBox", _vm.FrangiBeta.ToString("F2"));
        UpdateBox("FrangiCBox", _vm.FrangiC.ToString("F4"));

        UpdateBox("TensorSigmaBox", _vm.TensorSigma.ToString());
        UpdateBox("TensorRhoBox", _vm.TensorRho.ToString());

        UpdateBox("TopHatBox", _vm.TopHatKernelSize.ToString());

        UpdateBox("ClaheClipBox", _vm.ClaheClipLimit.ToString("F1"));
        UpdateBox("ClaheTileBox", _vm.ClaheTileSize.ToString());

        UpdateBox("LocalNormWindowBox", _vm.LocalNormWindowSize.ToString());
        UpdateBox("LocalNormIntensityBox", _vm.LocalNormIntensity.ToString("F1"));

        UpdateBox("KernelSizeBox", _vm.KernelSize.ToString());
    }

    // =======================================================================
    // GESTIONE MANUALE INPUT (UI -> VM)
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
                case "RotationBox": _vm.RotationAngle = d; break;
                case "ShiftXBox": _vm.ShiftX = d; break;
                case "ShiftYBox": _vm.ShiftY = d; break;
                
                case "SubsamplingBox": _vm.RadialSubsampling = Math.Max(1, i); break;
                case "McmRadiusBox": _vm.RadialMaxRadius = Math.Max(0, i); break;
                case "BgValueBox": _vm.BackgroundValue = d; break;
                case "AzimuthalRejBox": _vm.AzimuthalRejSigma = d; break;
                case "AzimuthalNormBox": _vm.AzimuthalNormSigma = d; break;

                case "RvsfA1Box": _vm.RvsfA_1 = d; break;
                case "RvsfB1Box": _vm.RvsfB_1 = d; break;
                case "RvsfN1Box": _vm.RvsfN_1 = d; break;

                // Mosaico RVSF (Range)
                case "RvsfAMinBox": _vm.RvsfA_1 = d; break;
                case "RvsfAMaxBox": _vm.RvsfA_2 = d; break;
                case "RvsfBMinBox": _vm.RvsfB_1 = d; break;
                case "RvsfBMaxBox": _vm.RvsfB_2 = d; break;
                case "RvsfNMinBox": _vm.RvsfN_1 = d; break;
                case "RvsfNMaxBox": _vm.RvsfN_2 = d; break;

                case "FrangiSigmaBox": _vm.FrangiSigma = d; break;
                case "FrangiBetaBox": _vm.FrangiBeta = d; break;
                case "FrangiCBox": _vm.FrangiC = d; break;

                case "TensorSigmaBox": _vm.TensorSigma = i; break;
                case "TensorRhoBox": _vm.TensorRho = i; break;

                case "TopHatBox": _vm.TopHatKernelSize = i; break;

                case "ClaheClipBox": _vm.ClaheClipLimit = d; break;
                case "ClaheTileBox": _vm.ClaheTileSize = i; break;

                case "LocalNormWindowBox": _vm.LocalNormWindowSize = i; break;
                case "LocalNormIntensityBox": _vm.LocalNormIntensity = d; break;

                case "KernelSizeBox": _vm.KernelSize = i; break;
            }
        }
        else
        {
            // Revert se input invalido
            UpdateUiValues(); 
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus(); // Toglie focus dalla box per triggerare LostFocus/Commit
            e.Handled = true;
        }
    }

    // =======================================================================
    // GESTIONE VIEWPORT E MOUSE
    // =======================================================================

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm != null) { _vm.Viewport.ViewportSize = e.NewSize; _vm.Viewport.ResetView(); }
    }

    private void CenterImage()
    {
        var b = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && b != null) 
        { 
            _vm.Viewport.ViewportSize = b.Bounds.Size; 
            _vm.Viewport.ResetView(); 
        }
    }
    
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var border = sender as Border; 
        if (border == null) return;

        var properties = e.GetCurrentPoint(border).Properties;

        // Panning attivato con tasto centrale
        if (properties.IsMiddleButtonPressed) 
        { 
            _isPanning = true; 
            _lastPointerPos = e.GetPosition(border); 
            e.Pointer.Capture(border); 
            this.Cursor = new Cursor(StandardCursorType.SizeAll); 
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning) { _isPanning = false; e.Pointer.Capture(null); this.Cursor = Cursor.Default; }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _vm == null) return;
        var b = sender as Border;
        if (b == null || _lastPointerPos == null) return;
        var pos = e.GetPosition(b);
        var d = pos - _lastPointerPos.Value;
        _lastPointerPos = pos;
        _vm.Viewport.ApplyPan(d.X, d.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_vm == null || _vm.ActiveRenderer == null || _vm.IsBusy) return;
        var b = sender as Border; if (b == null) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            double f = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
            _vm.Viewport.ApplyZoomAtPoint(f, e.GetPosition(b));
        }
        else
        {
            double r = Math.Abs(_vm.ActiveRenderer.WhitePoint - _vm.ActiveRenderer.BlackPoint);
            double s = (r > 1e-6 ? r * 0.05 : 0.001) * (e.Delta.Y < 0 ? -1 : 1);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) 
                _vm.ActiveRenderer.BlackPoint += s;
            else 
                _vm.ActiveRenderer.WhitePoint += s;
        }
        e.Handled = true;
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e) => CenterImage();
    
    private void OnZoomInClicked(object? sender, RoutedEventArgs e) 
    {
        var b = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && b != null) _vm.Viewport.ApplyZoomAtPoint(1.2, b.Bounds.Center);
    }
    
    private void OnZoomOutClicked(object? sender, RoutedEventArgs e) 
    {
        var b = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && b != null) _vm.Viewport.ApplyZoomAtPoint(1.0/1.2, b.Bounds.Center);
    }
    
    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}