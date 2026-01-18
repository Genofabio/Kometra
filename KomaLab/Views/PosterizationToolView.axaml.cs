using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Globalization;
using PosterizationToolViewModel = KomaLab.ViewModels.Tools.PosterizationToolViewModel;

namespace KomaLab.Views
{
    public partial class PosterizationToolView : Window
    {
        private bool _isPanning;
        private Point? _lastPointerPos;
        private bool _isFirstLayout = true;

        public PosterizationToolView()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            this.Focus();

            if (DataContext is PosterizationToolViewModel vm)
            {
                var border = this.FindControl<Border>("PreviewBorder");
                if (border != null && border.Bounds.Width > 0 && border.Bounds.Height > 0)
                {
                    vm.Viewport.ViewportSize = border.Bounds.Size;
                    vm.Viewport.ResetView();
                    _isFirstLayout = false;
                }
            }
        }

        private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                vm.Viewport.ViewportSize = e.NewSize;

                if (_isFirstLayout && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    vm.Viewport.ResetView();
                    _isFirstLayout = false;
                }
            }
        }

        /// <summary>
        /// Validazione input manuale. 
        /// Nota: BlackPoint e WhitePoint sono ora gestiti tramite l'ActiveRenderer del ViewModel.
        /// </summary>
        private void OnValueInputLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox || DataContext is not PosterizationToolViewModel vm) return;
            if (vm.ActiveRenderer == null) return;

            string input = (textBox.Text ?? "").Replace(',', '.');
            var culture = CultureInfo.InvariantCulture;

            if (textBox.Name == "LevelsInput")
            {
                if (int.TryParse(input, out int levels))
                    vm.Levels = Math.Clamp(levels, 2, 256); // Range esteso per posterizzazione fine
                else
                    vm.Levels = 64;
                textBox.Text = vm.Levels.ToString();
            }
            else if (textBox.Name == "BlackInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double black))
                {
                    // Clamp basato sui limiti del Renderer attivo
                    double maxAllowed = Math.Max(0, vm.ActiveRenderer.WhitePoint - 1);
                    vm.ActiveRenderer.BlackPoint = Math.Clamp(black, 0, maxAllowed);
                }
                textBox.Text = vm.ActiveRenderer.BlackPoint.ToString("F1", culture);
            }
            else if (textBox.Name == "WhiteInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double white))
                {
                    // Clamp basato sui limiti del Renderer attivo
                    double minAllowed = Math.Min(65535, vm.ActiveRenderer.BlackPoint + 1);
                    vm.ActiveRenderer.WhitePoint = Math.Clamp(white, minAllowed, 65535);
                }
                textBox.Text = vm.ActiveRenderer.WhitePoint.ToString("F1", culture);
            }
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) this.Focus(); 
        }

        // =======================================================================
        // INTERAZIONE VIEWPORT (PAN & ZOOM DELEGATI)
        // =======================================================================

        private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border border) return;
            var props = e.GetCurrentPoint(border).Properties;
            
            if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
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
            if (!_isPanning || _lastPointerPos == null || sender is not Border border) return;
            if (DataContext is not PosterizationToolViewModel vm) return;
            
            var currentPos = e.GetPosition(border);
            var delta = currentPos - _lastPointerPos.Value;
            
            // Delega al Viewport Manager del ViewModel
            vm.Viewport.ApplyPan(delta.X, delta.Y);
            
            _lastPointerPos = currentPos;
        }

        private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is not PosterizationToolViewModel vm) return;
            if (sender is not Border border) return;

            // 1. ZOOM (Ctrl + Wheel)
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var mousePos = e.GetPosition(border);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
                e.Handled = true;
                return;
            }

            // 2. SOGLIE (Wheel standard)
            if (vm.ActiveRenderer == null) return;

            double currentRange = Math.Max(100, vm.ActiveRenderer.WhitePoint - vm.ActiveRenderer.BlackPoint);
            double step = currentRange * 0.05;
            if (e.Delta.Y < 0) step = -step;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                vm.ActiveRenderer.BlackPoint = Math.Clamp(vm.ActiveRenderer.BlackPoint + step, 0, vm.ActiveRenderer.WhitePoint - 1);
            }
            else
            {
                vm.ActiveRenderer.WhitePoint = Math.Clamp(vm.ActiveRenderer.WhitePoint + step, vm.ActiveRenderer.BlackPoint + 1, 65535);
            }
            e.Handled = true;
        }

        // =======================================================================
        // PULSANTI HUD (VIEWPORT COMMANDS)
        // =======================================================================

        // --- GESTIONE CLICK PULSANTI HUD ---

        private void OnZoomInClicked(object? sender, RoutedEventArgs e) 
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                // Calcoliamo il centro del viewport corrente per uno zoom bilanciato
                var center = new Point(vm.Viewport.ViewportSize.Width / 2, vm.Viewport.ViewportSize.Height / 2);
                vm.Viewport.ApplyZoomAtPoint(1.2, center);
            }
        }

        private void OnZoomOutClicked(object? sender, RoutedEventArgs e) 
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                // Calcoliamo il centro del viewport corrente per uno zoom bilanciato
                var center = new Point(vm.Viewport.ViewportSize.Width / 2, vm.Viewport.ViewportSize.Height / 2);
                vm.Viewport.ApplyZoomAtPoint(0.8, center);
            }
        }
        
        private void OnResetViewClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                var border = this.FindControl<Border>("PreviewBorder");
                if (border != null)
                {
                    vm.Viewport.ViewportSize = border.Bounds.Size;
                    vm.Viewport.ResetView();
                }
            }
        }
    }
}