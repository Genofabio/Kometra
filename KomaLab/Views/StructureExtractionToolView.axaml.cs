using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading; // Per Dispatcher
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImageProcessing;

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
            
            // 1. Sottoscrizione eventi VM (VM -> UI)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            // 2. Inizializzazione manuale TextBox
            UpdateUiValues();

            try 
            {
                await vm.ImageLoadedTcs.Task;
                UpdateUiValues(); // Aggiorna nuovamente dopo il caricamento
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
    // Aggiorna le TextBox quando il ViewModel cambia proprietà
    // =======================================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() => 
        {
            if (_vm == null) return;

            // Recuperiamo i controlli (Null check robusto)
            var rotBox = this.FindControl<TextBox>("RotationBox");
            var sxBox = this.FindControl<TextBox>("ShiftXBox");
            var syBox = this.FindControl<TextBox>("ShiftYBox");
            var kBox = this.FindControl<TextBox>("KernelSizeBox");

            // RVSF Single
            var rA1 = this.FindControl<TextBox>("RvsfA1Box");
            var rB1 = this.FindControl<TextBox>("RvsfB1Box");
            var rN1 = this.FindControl<TextBox>("RvsfN1Box");

            // RVSF Mosaic (Condividono valori con Single per Min, separati per Max)
            var rAMin = this.FindControl<TextBox>("RvsfAMinBox");
            var rAMax = this.FindControl<TextBox>("RvsfAMaxBox");
            var rBMin = this.FindControl<TextBox>("RvsfBMinBox");
            var rBMax = this.FindControl<TextBox>("RvsfBMaxBox");
            var rNMin = this.FindControl<TextBox>("RvsfNMinBox");
            var rNMax = this.FindControl<TextBox>("RvsfNMaxBox");

            switch (e.PropertyName)
            {
                case nameof(StructureExtractionToolViewModel.RotationAngle):
                    if (rotBox != null && !rotBox.IsFocused) rotBox.Text = _vm.RotationAngle.ToString("F1");
                    break;
                case nameof(StructureExtractionToolViewModel.ShiftX):
                    if (sxBox != null && !sxBox.IsFocused) sxBox.Text = _vm.ShiftX.ToString("F1");
                    break;
                case nameof(StructureExtractionToolViewModel.ShiftY):
                    if (syBox != null && !syBox.IsFocused) syBox.Text = _vm.ShiftY.ToString("F1");
                    break;
                case nameof(StructureExtractionToolViewModel.KernelSize):
                    if (kBox != null && !kBox.IsFocused) kBox.Text = _vm.KernelSize.ToString("F0");
                    break;

                // RVSF Logic (Aggiorna sia box Single che Min Mosaic)
                case nameof(StructureExtractionToolViewModel.RvsfA_1):
                    if (rA1 != null && !rA1.IsFocused) rA1.Text = _vm.RvsfA_1.ToString("F2");
                    if (rAMin != null && !rAMin.IsFocused) rAMin.Text = _vm.RvsfA_1.ToString("F2");
                    break;
                case nameof(StructureExtractionToolViewModel.RvsfA_2):
                    if (rAMax != null && !rAMax.IsFocused) rAMax.Text = _vm.RvsfA_2.ToString("F2");
                    break;

                case nameof(StructureExtractionToolViewModel.RvsfB_1):
                    if (rB1 != null && !rB1.IsFocused) rB1.Text = _vm.RvsfB_1.ToString("F2");
                    if (rBMin != null && !rBMin.IsFocused) rBMin.Text = _vm.RvsfB_1.ToString("F2");
                    break;
                case nameof(StructureExtractionToolViewModel.RvsfB_2):
                    if (rBMax != null && !rBMax.IsFocused) rBMax.Text = _vm.RvsfB_2.ToString("F2");
                    break;

                case nameof(StructureExtractionToolViewModel.RvsfN_1):
                    if (rN1 != null && !rN1.IsFocused) rN1.Text = _vm.RvsfN_1.ToString("F2");
                    if (rNMin != null && !rNMin.IsFocused) rNMin.Text = _vm.RvsfN_1.ToString("F2");
                    break;
                case nameof(StructureExtractionToolViewModel.RvsfN_2):
                    if (rNMax != null && !rNMax.IsFocused) rNMax.Text = _vm.RvsfN_2.ToString("F2");
                    break;
            }
        });
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        
        void SetText(string name, string val)
        {
            var box = this.FindControl<TextBox>(name);
            if (box != null) box.Text = val;
        }

        SetText("RotationBox", _vm.RotationAngle.ToString("F1"));
        SetText("ShiftXBox", _vm.ShiftX.ToString("F1"));
        SetText("ShiftYBox", _vm.ShiftY.ToString("F1"));
        SetText("KernelSizeBox", _vm.KernelSize.ToString("F0"));

        SetText("RvsfA1Box", _vm.RvsfA_1.ToString("F2"));
        SetText("RvsfAMinBox", _vm.RvsfA_1.ToString("F2"));
        SetText("RvsfAMaxBox", _vm.RvsfA_2.ToString("F2"));

        SetText("RvsfB1Box", _vm.RvsfB_1.ToString("F2"));
        SetText("RvsfBMinBox", _vm.RvsfB_1.ToString("F2"));
        SetText("RvsfBMaxBox", _vm.RvsfB_2.ToString("F2"));

        SetText("RvsfN1Box", _vm.RvsfN_1.ToString("F2"));
        SetText("RvsfNMinBox", _vm.RvsfN_1.ToString("F2"));
        SetText("RvsfNMaxBox", _vm.RvsfN_2.ToString("F2"));
    }

    // =======================================================================
    // GESTIONE MANUALE INPUT (UI -> VM)
    // Validazione, Commit e Revert su errore
    // =======================================================================

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox textBox) return;

        bool isValid = double.TryParse(textBox.Text, out double val);

        if (isValid)
        {
            switch (textBox.Name)
            {
                case "RotationBox": _vm.RotationAngle = val; break;
                case "ShiftXBox": _vm.ShiftX = val; break;
                case "ShiftYBox": _vm.ShiftY = val; break;
                case "KernelSizeBox": _vm.KernelSize = Math.Max(1, Math.Round(val)); break;

                // RVSF - A
                case "RvsfA1Box": 
                case "RvsfAMinBox": _vm.RvsfA_1 = val; break;
                case "RvsfAMaxBox": _vm.RvsfA_2 = val; break;

                // RVSF - B
                case "RvsfB1Box":
                case "RvsfBMinBox": _vm.RvsfB_1 = val; break;
                case "RvsfBMaxBox": _vm.RvsfB_2 = val; break;

                // RVSF - N
                case "RvsfN1Box":
                case "RvsfNMinBox": _vm.RvsfN_1 = val; break;
                case "RvsfNMaxBox": _vm.RvsfN_2 = val; break;
            }
        }
        else
        {
            // REVERT: Se parsing fallisce, ripristina valore dal VM
            switch (textBox.Name)
            {
                case "RotationBox": textBox.Text = _vm.RotationAngle.ToString("F1"); break;
                case "ShiftXBox": textBox.Text = _vm.ShiftX.ToString("F1"); break;
                case "ShiftYBox": textBox.Text = _vm.ShiftY.ToString("F1"); break;
                case "KernelSizeBox": textBox.Text = _vm.KernelSize.ToString("F0"); break;

                case "RvsfA1Box": 
                case "RvsfAMinBox": textBox.Text = _vm.RvsfA_1.ToString("F2"); break;
                case "RvsfAMaxBox": textBox.Text = _vm.RvsfA_2.ToString("F2"); break;

                case "RvsfB1Box": 
                case "RvsfBMinBox": textBox.Text = _vm.RvsfB_1.ToString("F2"); break;
                case "RvsfBMaxBox": textBox.Text = _vm.RvsfB_2.ToString("F2"); break;

                case "RvsfN1Box": 
                case "RvsfNMinBox": textBox.Text = _vm.RvsfN_1.ToString("F2"); break;
                case "RvsfNMaxBox": textBox.Text = _vm.RvsfN_2.ToString("F2"); break;
            }
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus(); // Toglie il focus -> Scatena OnManualInputCommit
            e.Handled = true;
        }
    }
    
    // =======================================================================
    // GESTIONE VIEWPORT E MOUSE (Standard)
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

        // ZOOM (Ctrl + Wheel)
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var pos = e.GetPosition(border);
            double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
            vm.Viewport.ApplyZoomAtPoint(factor, pos);
            e.Handled = true;
        }
        else
        {
            // STRETCHING ISTOGRAMMA
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
            double step = Math.Max(0.0001, baseStep);

            if (e.Delta.Y < 0) step = -step;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                // BLACK POINT
                double newBlack = vm.ActiveRenderer.BlackPoint + step;
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
                // WHITE POINT
                double newWhite = vm.ActiveRenderer.WhitePoint + step;
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