using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImageProcessing;
using System.Threading.Tasks;

namespace KomaLab.Views;

public partial class StructureExtractionToolView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;
    private StructureExtractionToolViewModel? _vm;

    public StructureExtractionToolView()
    {
        InitializeComponent();
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null) previewBorder.SizeChanged += OnPreviewSizeChanged;
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StructureExtractionToolViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            UpdateUiValues();

            try 
            {
                await vm.ImageLoadedTcs.Task;
                UpdateUiValues(); 
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
            _vm = null;
        }
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
                // Larson & Median
                case nameof(_vm.RotationAngle): UpdateTextBox("RotationBox", _vm.RotationAngle.ToString("F1")); break;
                case nameof(_vm.ShiftX): UpdateTextBox("ShiftXBox", _vm.ShiftX.ToString("F1")); break;
                case nameof(_vm.ShiftY): UpdateTextBox("ShiftYBox", _vm.ShiftY.ToString("F1")); break;
                case nameof(_vm.KernelSize): UpdateTextBox("KernelSizeBox", _vm.KernelSize.ToString("F0")); break;

                // RVSF
                case nameof(_vm.RvsfA_1):
                    UpdateTextBox("RvsfA1Box", _vm.RvsfA_1.ToString("F2"));
                    UpdateTextBox("RvsfAMinBox", _vm.RvsfA_1.ToString("F2"));
                    break;
                case nameof(_vm.RvsfA_2): UpdateTextBox("RvsfAMaxBox", _vm.RvsfA_2.ToString("F2")); break;
                case nameof(_vm.RvsfB_1):
                    UpdateTextBox("RvsfB1Box", _vm.RvsfB_1.ToString("F2"));
                    UpdateTextBox("RvsfBMinBox", _vm.RvsfB_1.ToString("F2"));
                    break;
                case nameof(_vm.RvsfB_2): UpdateTextBox("RvsfBMaxBox", _vm.RvsfB_2.ToString("F2")); break;
                case nameof(_vm.RvsfN_1):
                    UpdateTextBox("RvsfN1Box", _vm.RvsfN_1.ToString("F2"));
                    UpdateTextBox("RvsfNMinBox", _vm.RvsfN_1.ToString("F2"));
                    break;
                case nameof(_vm.RvsfN_2): UpdateTextBox("RvsfNMaxBox", _vm.RvsfN_2.ToString("F2")); break;

                // Frangi Filter
                case nameof(_vm.FrangiSigma): UpdateTextBox("FrangiSigmaBox", _vm.FrangiSigma.ToString("F2")); break;
                case nameof(_vm.FrangiBeta): UpdateTextBox("FrangiBetaBox", _vm.FrangiBeta.ToString("F2")); break;
                case nameof(_vm.FrangiC): UpdateTextBox("FrangiCBox", _vm.FrangiC.ToString("F4")); break;

                // Structure Tensor
                case nameof(_vm.TensorSigma): UpdateTextBox("TensorSigmaBox", _vm.TensorSigma.ToString()); break;
                case nameof(_vm.TensorRho): UpdateTextBox("TensorRhoBox", _vm.TensorRho.ToString()); break;

                // Top-Hat
                case nameof(_vm.TopHatKernelSize): UpdateTextBox("TopHatBox", _vm.TopHatKernelSize.ToString()); break;

                // CLAHE
                case nameof(_vm.ClaheClipLimit): UpdateTextBox("ClaheClipBox", _vm.ClaheClipLimit.ToString("F1")); break;
                case nameof(_vm.ClaheTileSize): UpdateTextBox("ClaheTileBox", _vm.ClaheTileSize.ToString()); break;

                // LSN (Local Norm)
                case nameof(_vm.LocalNormWindowSize): UpdateTextBox("LocalNormWindowBox", _vm.LocalNormWindowSize.ToString()); break;
                case nameof(_vm.LocalNormIntensity): UpdateTextBox("LocalNormIntensityBox", _vm.LocalNormIntensity.ToString("F1")); break;
            }
        });
    }

    private void UpdateTextBox(string name, string text)
    {
        var box = this.FindControl<TextBox>(name);
        if (box != null && !box.IsFocused) box.Text = text;
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        
        var boxes = new (string Name, string Value)[]
        {
            ("RotationBox", _vm.RotationAngle.ToString("F1")),
            ("ShiftXBox", _vm.ShiftX.ToString("F1")),
            ("ShiftYBox", _vm.ShiftY.ToString("F1")),
            ("KernelSizeBox", _vm.KernelSize.ToString("F0")),
            ("RvsfA1Box", _vm.RvsfA_1.ToString("F2")),
            ("RvsfAMinBox", _vm.RvsfA_1.ToString("F2")),
            ("RvsfAMaxBox", _vm.RvsfA_2.ToString("F2")),
            ("RvsfB1Box", _vm.RvsfB_1.ToString("F2")),
            ("RvsfBMinBox", _vm.RvsfB_1.ToString("F2")),
            ("RvsfBMaxBox", _vm.RvsfB_2.ToString("F2")),
            ("RvsfN1Box", _vm.RvsfN_1.ToString("F2")),
            ("RvsfNMinBox", _vm.RvsfN_1.ToString("F2")),
            ("RvsfNMaxBox", _vm.RvsfN_2.ToString("F2")),
            ("FrangiSigmaBox", _vm.FrangiSigma.ToString("F2")),
            ("FrangiBetaBox", _vm.FrangiBeta.ToString("F2")),
            ("FrangiCBox", _vm.FrangiC.ToString("F4")),
            ("TensorSigmaBox", _vm.TensorSigma.ToString()),
            ("TensorRhoBox", _vm.TensorRho.ToString()),
            ("TopHatBox", _vm.TopHatKernelSize.ToString()),
            ("ClaheClipBox", _vm.ClaheClipLimit.ToString("F1")),
            ("ClaheTileBox", _vm.ClaheTileSize.ToString()),
            ("LocalNormWindowBox", _vm.LocalNormWindowSize.ToString()),
            ("LocalNormIntensityBox", _vm.LocalNormIntensity.ToString("F1"))
        };

        foreach (var (name, value) in boxes)
        {
            var box = this.FindControl<TextBox>(name);
            if (box != null) box.Text = value;
        }
    }

    // =======================================================================
    // GESTIONE MANUALE INPUT (UI -> VM)
    // =======================================================================

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox textBox) return;

        bool isDouble = double.TryParse(textBox.Text, out double dVal);
        bool isInt = int.TryParse(textBox.Text, out int iVal);

        if (isDouble || isInt)
        {
            switch (textBox.Name)
            {
                case "RotationBox": _vm.RotationAngle = dVal; break;
                case "ShiftXBox": _vm.ShiftX = dVal; break;
                case "ShiftYBox": _vm.ShiftY = dVal; break;
                case "KernelSizeBox": _vm.KernelSize = (int)Math.Max(1, Math.Round(dVal)); break;
                
                case "RvsfA1Box": 
                case "RvsfAMinBox": _vm.RvsfA_1 = dVal; break;
                case "RvsfAMaxBox": _vm.RvsfA_2 = dVal; break;
                case "RvsfB1Box":
                case "RvsfBMinBox": _vm.RvsfB_1 = dVal; break;
                case "RvsfBMaxBox": _vm.RvsfB_2 = dVal; break;
                case "RvsfN1Box":
                case "RvsfNMinBox": _vm.RvsfN_1 = dVal; break;
                case "RvsfNMaxBox": _vm.RvsfN_2 = dVal; break;
                
                case "FrangiSigmaBox": _vm.FrangiSigma = Math.Max(0.1, dVal); break;
                case "FrangiBetaBox": _vm.FrangiBeta = Math.Clamp(dVal, 0.1, 1.0); break;
                case "FrangiCBox": _vm.FrangiC = dVal; break;
                
                case "TensorSigmaBox": _vm.TensorSigma = Math.Max(1, iVal); break;
                case "TensorRhoBox": _vm.TensorRho = Math.Max(1, iVal); break;
                
                case "TopHatBox": _vm.TopHatKernelSize = Math.Max(3, iVal); break;
                
                case "ClaheClipBox": _vm.ClaheClipLimit = Math.Max(0, dVal); break;
                case "ClaheTileBox": _vm.ClaheTileSize = Math.Max(2, iVal); break;
                
                case "LocalNormWindowBox": _vm.LocalNormWindowSize = Math.Max(3, iVal); break;
                case "LocalNormIntensityBox": _vm.LocalNormIntensity = dVal; break;
            }
        }
        
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
        if (DataContext is StructureExtractionToolViewModel vm)
        {
            vm.Viewport.ViewportSize = e.NewSize;
            vm.Viewport.ResetView();
        }
    }

    private void CenterImage()
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is StructureExtractionToolViewModel vm && previewBorder != null)
        {
            vm.Viewport.ViewportSize = previewBorder.Bounds.Size;
            vm.Viewport.ResetView();
        }
    }
    
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var border = sender as Border;
        if (border == null) return;
        var props = e.GetCurrentPoint(border).Properties;
        if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPointerPos = e.GetPosition(border);
            e.Pointer.Capture(border);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || DataContext is not StructureExtractionToolViewModel vm) return;
        var border = sender as Border;
        if (border == null || _lastPointerPos == null) return;
        var currentPos = e.GetPosition(border);
        var delta = currentPos - _lastPointerPos.Value;
        _lastPointerPos = currentPos;
        vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not StructureExtractionToolViewModel vm || vm.ActiveRenderer == null || vm.IsBusy) return;
        var border = sender as Border;
        if (border == null) return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var pos = e.GetPosition(border);
            double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
            vm.Viewport.ApplyZoomAtPoint(factor, pos);
            e.Handled = true;
        }
        else
        {
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            double baseStep = (currentRange > 1e-7) ? currentRange * 0.05 : 0.001;
            double step = e.Delta.Y < 0 ? -baseStep : baseStep;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                double newBlack = vm.ActiveRenderer.BlackPoint + step;
                vm.ActiveRenderer.BlackPoint = step > 0 
                    ? Math.Min(newBlack, vm.ActiveRenderer.WhitePoint - (Math.Abs(step) * 0.1)) 
                    : newBlack;
            }
            else
            {
                double newWhite = vm.ActiveRenderer.WhitePoint + step;
                vm.ActiveRenderer.WhitePoint = step < 0 
                    ? Math.Max(newWhite, vm.ActiveRenderer.BlackPoint + (Math.Abs(step) * 0.1)) 
                    : newWhite;
            }
            e.Handled = true;
        }
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e) => CenterImage();

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (DataContext is StructureExtractionToolViewModel vm && border != null)
            vm.Viewport.ApplyZoomAtPoint(1.2, border.Bounds.Center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (DataContext is StructureExtractionToolViewModel vm && border != null)
            vm.Viewport.ApplyZoomAtPoint(1.0 / 1.2, border.Bounds.Center);
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}