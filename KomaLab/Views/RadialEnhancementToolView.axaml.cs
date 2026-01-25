using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading; // Per Dispatcher
using System;
using System.ComponentModel;
using KomaLab.ViewModels.ImageProcessing;

namespace KomaLab.Views;

public partial class RadialEnhancementToolView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;
    private RadialEnhancementToolViewModel? _vm;

    public RadialEnhancementToolView()
    {
        InitializeComponent();
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded; // Importante per pulire gli eventi
        
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null) previewBorder.SizeChanged += OnPreviewSizeChanged;
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadialEnhancementToolViewModel vm)
        {
            _vm = vm;
            // 1. Sottoscriviamo ai cambiamenti del VM (per aggiornare la UI se cambia il raggio automatico)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            // 2. Inizializziamo i valori nelle TextBox manualmente
            UpdateUiValues();

            try 
            {
                await vm.ImageLoadedTcs.Task;
                // Rieseguiamo update valori perché il raggio potrebbe essere stato calcolato dopo il load dell'immagine
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

    // --- GESTIONE MANUALE DATI (VM -> UI) ---

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Se il VM cambia una proprietà, aggiorniamo la TextBox corrispondente
        // Usiamo Dispatcher per sicurezza thread-safe
        Dispatcher.UIThread.InvokeAsync(() => 
        {
            var rBox = this.FindControl<TextBox>("RadiusBox");
            var tBox = this.FindControl<TextBox>("ThetaBox");
            var sBox = this.FindControl<TextBox>("SigmaBox");
            var cBox = this.FindControl<TextBox>("ContrastBox");

            if (_vm == null) return;

            switch (e.PropertyName)
            {
                case nameof(RadialEnhancementToolViewModel.RadiusPixels):
                    if (rBox != null && !rBox.IsFocused) rBox.Text = _vm.RadiusPixels.ToString();
                    break;
                case nameof(RadialEnhancementToolViewModel.ThetaPixels):
                    if (tBox != null && !tBox.IsFocused) tBox.Text = _vm.ThetaPixels.ToString();
                    break;
                case nameof(RadialEnhancementToolViewModel.SigmaRejection):
                    if (sBox != null && !sBox.IsFocused) sBox.Text = _vm.SigmaRejection.ToString("F1");
                    break;
                case nameof(RadialEnhancementToolViewModel.ContrastScale):
                    if (cBox != null && !cBox.IsFocused) cBox.Text = _vm.ContrastScale.ToString("F1");
                    break;
            }
        });
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        
        var rBox = this.FindControl<TextBox>("RadiusBox");
        var tBox = this.FindControl<TextBox>("ThetaBox");
        var sBox = this.FindControl<TextBox>("SigmaBox");
        var cBox = this.FindControl<TextBox>("ContrastBox");

        if (rBox != null) rBox.Text = _vm.RadiusPixels.ToString();
        if (tBox != null) tBox.Text = _vm.ThetaPixels.ToString();
        if (sBox != null) sBox.Text = _vm.SigmaRejection.ToString("F1");
        if (cBox != null) cBox.Text = _vm.ContrastScale.ToString("F1");
    }

    // --- GESTIONE MANUALE INPUT (UI -> VM) ---

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox textBox) return;

        // Tenta il parsing
        // Usiamo CurrentCulture per rispettare virgola/punto del sistema dell'utente
        bool isValid = double.TryParse(textBox.Text, out double value);

        if (isValid)
        {
            // Aggiorna il VM in base a quale TextBox ha generato l'evento
            switch (textBox.Name)
            {
                case "RadiusBox": _vm.RadiusPixels = value; break;
                case "ThetaBox": _vm.ThetaPixels = value; break;
                case "SigmaBox": _vm.SigmaRejection = value; break;
                case "ContrastBox": _vm.ContrastScale = value; break;
            }
        }
        else
        {
            // FALLIMENTO: Revert al valore attuale del VM
            // Poiché non abbiamo aggiornato il VM, lui ha ancora il valore vecchio valido.
            // Riscriviamo semplicemente quel valore nella TextBox.
            switch (textBox.Name)
            {
                case "RadiusBox": textBox.Text = _vm.RadiusPixels.ToString(); break;
                case "ThetaBox": textBox.Text = _vm.ThetaPixels.ToString(); break;
                case "SigmaBox": textBox.Text = _vm.SigmaRejection.ToString("F1"); break;
                case "ContrastBox": textBox.Text = _vm.ContrastScale.ToString("F1"); break;
            }
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Togliere il focus chiama OnManualInputCommit automaticamente
            this.Focus(); 
            e.Handled = true;
        }
    }
    
    
    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is RadialEnhancementToolViewModel vm)
        {
            vm.Viewport.ViewportSize = e.NewSize;
            vm.Viewport.ResetView();
        }
    }

    private void CenterImage()
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is RadialEnhancementToolViewModel vm && previewBorder != null)
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
        if (!_isPanning || DataContext is not RadialEnhancementToolViewModel vm) return;
        var border = sender as Border;
        if (border == null || _lastPointerPos == null) return;
        var currentPos = e.GetPosition(border);
        var delta = currentPos - _lastPointerPos.Value;
        _lastPointerPos = currentPos;
        vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not RadialEnhancementToolViewModel vm || vm.ActiveRenderer == null || vm.IsBusy) return;
        var border = sender as Border;
        if (border == null) return;

        // ZOOM
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var pos = e.GetPosition(border);
            double factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
            vm.Viewport.ApplyZoomAtPoint(factor, pos);
            e.Handled = true;
        }
        else
        {
            // SOGLIE RADIOMETRICHE (Versione Robusta)
            // 1. Calcolo step dinamico (5% del range attuale)
            double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
            double step = Math.Max(0.0001, baseStep); // Minimo step per evitare stallo su float piccoli

            if (e.Delta.Y < 0) step = -step;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                // BLACK POINT
                double newBlack = vm.ActiveRenderer.BlackPoint + step;
                if (step > 0)
                {
                    // Evita l'accavallamento col White Point
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
                    // Evita l'accavallamento col Black Point
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
        if (DataContext is RadialEnhancementToolViewModel vm && border != null)
            vm.Viewport.ApplyZoomAtPoint(1.2, border.Bounds.Center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (DataContext is RadialEnhancementToolViewModel vm && border != null)
            vm.Viewport.ApplyZoomAtPoint(1.0 / 1.2, border.Bounds.Center);
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}