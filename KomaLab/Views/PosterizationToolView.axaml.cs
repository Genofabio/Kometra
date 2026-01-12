using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KomaLab.ViewModels;
using System;
using System.Globalization;
using System.Threading.Tasks;
using PosterizationToolViewModel = KomaLab.ViewModels.Tools.PosterizationToolViewModel;

namespace KomaLab.Views
{
    public partial class PosterizationToolView : Window
    {
        private bool _isPanning;
        private Point? _lastPointerPos;

        public PosterizationToolView()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
        }

        private async void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // Piccolo ritardo per assicurarsi che il layout sia renderizzato
            await Task.Delay(100);
            if (DataContext is PosterizationToolViewModel vm)
            {
                var border = this.FindControl<Border>("PreviewBorder");
                if (border != null)
                {
                    vm.Viewport.ViewportSize = border.Bounds.Size;
                }
                vm.ResetView();
            }
        }

        /// <summary>
        /// Gestisce il ridimensionamento della finestra per adattare il viewport.
        /// Collegato all'evento SizeChanged="OnPreviewSizeChanged" nello XAML.
        /// </summary>
        private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                vm.Viewport.ViewportSize = e.NewSize;
            }
        }

        /// <summary>
        /// Validazione input manuale con logica di Clamp e Cross-Check (Nero < Bianco).
        /// Scatta quando si clicca fuori o quando viene chiamato this.Focus()
        /// </summary>
        private void OnValueInputLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox || DataContext is not PosterizationToolViewModel vm) return;

            string input = (textBox.Text ?? "").Replace(',', '.');
            var culture = CultureInfo.InvariantCulture;

            if (textBox.Name == "LevelsInput")
            {
                if (int.TryParse(input, out int levels))
                    vm.Levels = Math.Clamp(levels, 2, 64);
                else
                    vm.Levels = 64;
                textBox.Text = vm.Levels.ToString();
            }
            else if (textBox.Name == "BlackInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double black))
                {
                    // FIX: Il nero non può superare il bianco (lasciamo un margine di 1 unità)
                    // Se il bianco è 2000, il nero massimo accettabile è 1999.
                    double maxAllowed = Math.Max(vm.SliderMin, vm.WhitePoint - 1);
                    vm.BlackPoint = Math.Clamp(black, vm.SliderMin, maxAllowed);
                }
                else
                {
                    vm.BlackPoint = vm.SliderMin;
                }
                textBox.Text = vm.BlackPoint.ToString("F1", culture);
            }
            else if (textBox.Name == "WhiteInput")
            {
                if (double.TryParse(input, NumberStyles.Any, culture, out double white))
                {
                    // FIX: Il bianco non può scendere sotto il nero (lasciamo un margine di 1 unità)
                    // Se il nero è 1000, il bianco minimo accettabile è 1001.
                    double minAllowed = Math.Min(vm.SliderMax, vm.BlackPoint + 1);
                    vm.WhitePoint = Math.Clamp(white, minAllowed, vm.SliderMax);
                }
                else
                {
                    vm.WhitePoint = vm.SliderMax;
                }
                textBox.Text = vm.WhitePoint.ToString("F1", culture);
            }
        }

        /// <summary>
        /// NUOVO: Gestisce la pressione di INVIO.
        /// Toglie il focus dal TextBox spostandolo sulla Finestra.
        /// Questo fa scattare automaticamente OnValueInputLostFocus.
        /// </summary>
        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                this.Focus(); 
            }
        }

        // --- GESTIONE INTERAZIONE MOUSE (PAN & ZOOM) ---

        private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border border) return;
            var props = e.GetCurrentPoint(border).Properties;
            
            if (props.IsMiddleButtonPressed)
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
            vm.ApplyPan(delta.X, delta.Y);
            _lastPointerPos = currentPos;
        }

        private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is not PosterizationToolViewModel vm) return;
            if (sender is not Border border) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var mousePos = e.GetPosition(border);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                vm.ApplyZoomAtPoint(factor, mousePos);
                e.Handled = true;
                return;
            }

            double currentRange = Math.Max(100, vm.WhitePoint - vm.BlackPoint);
            double step = currentRange * 0.05;
            if (e.Delta.Y < 0) step = -step;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                double newBlack = vm.BlackPoint + step;
                vm.BlackPoint = Math.Clamp(newBlack, vm.SliderMin, vm.WhitePoint - 1);
            }
            else
            {
                double newWhite = vm.WhitePoint + step;
                vm.WhitePoint = Math.Clamp(newWhite, vm.BlackPoint + 1, vm.SliderMax);
            }
            e.Handled = true;
        }

        // --- GESTIONE CLICK PULSANTI HUD ---

        private void OnZoomInClicked(object? sender, RoutedEventArgs e) => (DataContext as PosterizationToolViewModel)?.ZoomIn();
        
        private void OnZoomOutClicked(object? sender, RoutedEventArgs e) => (DataContext as PosterizationToolViewModel)?.ZoomOut();
        
        private void OnResetViewClicked(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                var border = this.FindControl<Border>("PreviewBorder");
                if (border != null)
                {
                    vm.Viewport.ViewportSize = border.Bounds.Size;
                    vm.ResetView();
                }
            }
        }
    }
}