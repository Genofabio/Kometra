using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Kometra.ViewModels.ImageProcessing;

namespace Kometra.Views;

public partial class CropToolView : Window
{
    private bool _hasLoaded;
    private Point? _lastPointerPosForPanning;
    private bool _isPanning;

    public CropToolView()
    {
        InitializeComponent();
        
        this.Loaded += OnWindowLoaded;
        this.Closing += OnWindowClosing;
        
        // Risoluzione riferimenti controlli
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder != null)
        {
            previewBorder.SizeChanged += OnPreviewSizeChanged;
        }

        // Hook manuale pulsanti
        var zoomInButton = this.FindControl<Button>("ZoomInButton");
        var zoomOutButton = this.FindControl<Button>("ZoomOutButton");
        var resetViewButton = this.FindControl<Button>("ResetViewButton");
        var resetThresholdsButton = this.FindControl<Button>("ResetThresholdsButton");

        if (zoomInButton != null) zoomInButton.Click += OnZoomInClicked;
        if (zoomOutButton != null) zoomOutButton.Click += OnZoomOutClicked;
        if (resetViewButton != null) resetViewButton.Click += OnResetViewClicked;
        if (resetThresholdsButton != null) resetThresholdsButton.Click += OnResetThresholdsClicked;
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _hasLoaded = true;
        
        if (DataContext is CropToolViewModel vm)
        {
            vm.RequestClose += Close;
            
            // LOGICA DI CENTRATURA INIZIALE ROBUSTA
            var border = this.FindControl<Border>("PreviewBorder");
            if (border != null)
            {
                int attempts = 0;
                while (attempts < 20) 
                {
                    bool hasSize = border.Bounds.Width > 0 && border.Bounds.Height > 0;
                    bool hasImage = vm.ActiveRenderer?.Image != null;

                    if (hasSize && hasImage)
                    {
                        vm.Viewport.ViewportSize = border.Bounds.Size;
                        vm.ResetView();
                        break;
                    }

                    await Task.Delay(100);
                    attempts++;
                }
            }
        }
    }
    
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is CropToolViewModel vm)
        {
            vm.RequestClose -= Close;
        }
    }

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_hasLoaded) return;
        if (DataContext is CropToolViewModel vm)
        {
            vm.Viewport.ViewportSize = e.NewSize;
            vm.ResetView(); // Adatta l'immagine quando si ridimensiona la finestra
        }
    }

    private bool IsInteractionBlocked()
    {
        if (DataContext is CropToolViewModel vm)
        {
            return vm.IsBusy;
        }
        return true;
    }

    // =======================================================================
    // GESTIONE TASTIERA (NOVITÀ)
    // =======================================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Se l'utente sta scrivendo in una TextBox (es. dimensioni), non intercettare Invio
        if (e.Source is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        // Gestione Tasto INVIO -> Immagine Successiva
        if (e.Key == Key.Enter)
        {
            if (DataContext is CropToolViewModel vm && vm.Navigator.CanMoveNext)
            {
                vm.Navigator.NextCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);
    }

    // =======================================================================
    // GESTIONE POINTER (Panning, Click, Zoom)
    // =======================================================================

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractionBlocked()) return;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not CropToolViewModel vm || previewBorder == null) return;

        var props = e.GetCurrentPoint(previewBorder).Properties;

        // --- PANNING (Middle Click) ---
        if (props.IsMiddleButtonPressed)
        {
            _lastPointerPosForPanning = e.GetPosition(previewBorder);
            _isPanning = true;
            e.Pointer.Capture(previewBorder);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        // --- CLICK DESTRO (Rimuovi Centro) ---
        else if (props.IsRightButtonPressed)
        {
            vm.ClearCenter();
            e.Handled = true;
        }
        // --- CLICK SINISTRO (Imposta Centro) ---
        else if (props.IsLeftButtonPressed)
        {
            if (vm.ActiveRenderer?.Image == null) return;

            var viewportPos = e.GetPosition(previewBorder);

            // Calcolo coordinata immagine reale
            double imageX = (viewportPos.X - vm.Viewport.OffsetX) / vm.Viewport.Scale;
            double imageY = (viewportPos.Y - vm.Viewport.OffsetY) / vm.Viewport.Scale;

            // Clamp coordinate
            if (vm.Viewport.ImageSize.Width > 0 && vm.Viewport.ImageSize.Height > 0)
            {
                imageX = Math.Clamp(imageX, 0, vm.Viewport.ImageSize.Width);
                imageY = Math.Clamp(imageY, 0, vm.Viewport.ImageSize.Height);
                vm.SetCenter(new Point(imageX, imageY));
            }
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
        if (!_isPanning || DataContext is not CropToolViewModel vm || previewBorder == null) return;

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

        vm.Viewport.ApplyPan(delta.X, delta.Y);
        e.Handled = true;
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (IsInteractionBlocked()) return;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (e.Handled || DataContext is not CropToolViewModel vm || previewBorder == null) return;
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

        // SOGLIE RADIOMETRICHE
        double currentRange = Math.Abs(vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
        double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
        double step = Math.Max(0.0001, baseStep);

        if (e.Delta.Y < 0) step = -step;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Modifica BLACK POINT
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
            // Modifica WHITE POINT
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

    // =======================================================================
    // HANDLERS BOTTONI
    // =======================================================================

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not CropToolViewModel vm || previewBorder == null) return;
        var center = new Point(previewBorder.Bounds.Width / 2, previewBorder.Bounds.Height / 2);
        vm.Viewport.ApplyZoomAtPoint(1.2, center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (DataContext is not CropToolViewModel vm || previewBorder == null) return;
        var center = new Point(previewBorder.Bounds.Width / 2, previewBorder.Bounds.Height / 2);
        vm.Viewport.ApplyZoomAtPoint(1 / 1.2, center);
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CropToolViewModel vm) vm.ResetView();
    }

    private async void OnResetThresholdsClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CropToolViewModel vm) await vm.ResetThresholds();
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }
}