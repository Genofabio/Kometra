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

                if (e.NewSize.Width > 0)
                {
                    vm.Viewport.ResetView();
                    _isFirstLayout = false;
                }
            }
        }

        /// <summary>
        /// Validazione input manuale. 
        /// Allarga dinamicamente i limiti della UI se l'utente digita intenzionalmente
        /// un valore fuori scala.
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
                {
                    if (black < vm.SliderMin) vm.SliderMin = black - Math.Abs(black * 0.2);
                    if (black > vm.SliderMax) vm.SliderMax = black + Math.Abs(black * 0.2);
                    vm.BlackPoint = black;
                }
                
                textBox.Text = vm.BlackPoint.ToString("F4", culture);
            }
            else if (textBox.Name == "WhiteInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double white))
                {
                    if (white < vm.SliderMin) vm.SliderMin = white - Math.Abs(white * 0.2);
                    if (white > vm.SliderMax) vm.SliderMax = white + Math.Abs(white * 0.2);
                    vm.WhitePoint = white; 
                }
                
                textBox.Text = vm.WhitePoint.ToString("F4", culture);
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

            // Calcolo range e passo base (molto più sensibile: 0.5% del range totale)
            double totalRange = Math.Abs(vm.SliderMax - vm.SliderMin);
            if (totalRange < 1e-4) totalRange = 1.0; 
            
            double baseStep = totalRange * 0.005; 
            bool isScrollingUp = e.Delta.Y > 0;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                // Modifica Soglia NERO
                if (isScrollingUp)
                {
                    // Aumentando il nero, ci si avvicina al bianco
                    double remainingDistance = vm.WhitePoint - vm.BlackPoint;
                    
                    // Deceleratore: non supera mai il 20% della distanza rimanente
                    double actualStep = Math.Min(baseStep, remainingDistance * 0.2);

                    // Margine di sicurezza di 1e-5 per non innescare il push del ViewModel
                    vm.BlackPoint = Math.Min(vm.BlackPoint + actualStep, vm.WhitePoint - 1e-5);
                }
                else
                {
                    // Diminuendo il nero, ci si allontana (nessun problema)
                    vm.BlackPoint -= baseStep;
                }
            }
            else
            {
                // Modifica Soglia BIANCO
                if (!isScrollingUp)
                {
                    // Diminuendo il bianco, ci si avvicina al nero
                    double remainingDistance = vm.WhitePoint - vm.BlackPoint;
                    
                    // Deceleratore: non supera mai il 20% della distanza rimanente
                    double actualStep = Math.Min(baseStep, remainingDistance * 0.2);

                    // Margine di sicurezza di 1e-5 per non innescare il push del ViewModel
                    vm.WhitePoint = Math.Max(vm.WhitePoint - actualStep, vm.BlackPoint + 1e-5);
                }
                else
                {
                    // Aumentando il bianco, ci si allontana (nessun problema)
                    vm.WhitePoint += baseStep;
                }
            }
    
            e.Handled = true;
        }

        // =======================================================================
        // PULSANTI HUD (VIEWPORT COMMANDS)
        // =======================================================================

        private void OnZoomInClicked(object? sender, RoutedEventArgs e) 
        {
            if (DataContext is ImageProcessing_PosterizationToolViewModel vm)
            {
                var center = new Point(vm.Viewport.ViewportSize.Width / 2, vm.Viewport.ViewportSize.Height / 2);
                vm.Viewport.ApplyZoomAtPoint(1.2, center);
            }
        }

        private void OnZoomOutClicked(object? sender, RoutedEventArgs e) 
        {
            if (DataContext is ImageProcessing_PosterizationToolViewModel vm)
            {
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