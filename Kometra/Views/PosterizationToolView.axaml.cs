using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Globalization;
using ImageProcessing_PosterizationToolViewModel = Kometra.ViewModels.ImageProcessing.PosterizationToolViewModel;
using PosterizationToolViewModel = Kometra.ViewModels.ImageProcessing.PosterizationToolViewModel;

namespace Kometra.Views
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
        }

        private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is ImageProcessing_PosterizationToolViewModel vm)
            {
                vm.Viewport.ViewportSize = e.NewSize;

                // SE VUOI CHE SI ADATTI SEMPRE AL RESIZE (togli il check _isFirstLayout se vuoi comportamento tipo visualizzatore foto standard)
                if (e.NewSize.Width > 0)
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
            if (sender is not TextBox textBox || DataContext is not ImageProcessing_PosterizationToolViewModel vm) return;

            string input = (textBox.Text ?? "").Replace(',', '.');
            var culture = CultureInfo.InvariantCulture;

            if (textBox.Name == "LevelsInput")
            {
                if (int.TryParse(input, out int levels))
                    vm.Levels = Math.Clamp(levels, 2, 256);
                textBox.Text = vm.Levels.ToString();
            }
            else if (textBox.Name == "BlackInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double black))
                    vm.BlackPoint = black; // PASSA DAL VM, NON DAL RENDERER
                textBox.Text = vm.BlackPoint.ToString("F1", culture);
            }
            else if (textBox.Name == "WhiteInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double white))
                    vm.WhitePoint = white; // PASSA DAL VM, NON DAL RENDERER
                textBox.Text = vm.WhitePoint.ToString("F1", culture);
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
            if (DataContext is not ImageProcessing_PosterizationToolViewModel vm) return;
            
            var currentPos = e.GetPosition(border);
            var delta = currentPos - _lastPointerPos.Value;
            
            // Delega al Viewport Manager del ViewModel
            vm.Viewport.ApplyPan(delta.X, delta.Y);
            
            _lastPointerPos = currentPos;
        }

        private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is not ImageProcessing_PosterizationToolViewModel vm || vm.ActiveRenderer == null) return;
            if (sender is not Border border) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var mousePos = e.GetPosition(border);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
                e.Handled = true;
                return;
            }

            // Gestione Soglie con rotellina
            double currentRange = Math.Max(100, vm.WhitePoint - vm.BlackPoint);
            double step = currentRange * 0.05;
            if (e.Delta.Y < 0) step = -step;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                vm.BlackPoint += step; // USA IL VM
            else
                vm.WhitePoint += step; // USA IL VM
    
            e.Handled = true;
        }

        // =======================================================================
        // PULSANTI HUD (VIEWPORT COMMANDS)
        // =======================================================================

        // --- GESTIONE CLICK PULSANTI HUD ---

        private void OnZoomInClicked(object? sender, RoutedEventArgs e) 
        {
            if (DataContext is ImageProcessing_PosterizationToolViewModel vm)
            {
                // Calcoliamo il centro del viewport corrente per uno zoom bilanciato
                var center = new Point(vm.Viewport.ViewportSize.Width / 2, vm.Viewport.ViewportSize.Height / 2);
                vm.Viewport.ApplyZoomAtPoint(1.2, center);
            }
        }

        private void OnZoomOutClicked(object? sender, RoutedEventArgs e) 
        {
            if (DataContext is ImageProcessing_PosterizationToolViewModel vm)
            {
                // Calcoliamo il centro del viewport corrente per uno zoom bilanciato
                var center = new Point(vm.Viewport.ViewportSize.Width / 2, vm.Viewport.ViewportSize.Height / 2);
                vm.Viewport.ApplyZoomAtPoint(0.8, center);
            }
        }
        
        private void OnResetViewClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ImageProcessing_PosterizationToolViewModel vm)
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