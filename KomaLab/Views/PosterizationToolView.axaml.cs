using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Globalization;
using PosterizationToolViewModel = KomaLab.ViewModels.Tools.PosterizationToolViewModel;

namespace KomaLab.Views
{
    public partial class PosterizationToolView : Window
    {
        private bool _isPanning;
        private Point? _lastPointerPos;
        private bool _isFirstLayout = true; // Flag per gestire l'inizializzazione automatica

        public PosterizationToolView()
        {
            InitializeComponent();
            
            // Registriamo l'evento Loaded in modo sincrono
            this.Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // Inizializziamo il focus sulla finestra per catturare i KeyBindings
            this.Focus();

            if (DataContext is PosterizationToolViewModel vm)
            {
                var border = this.FindControl<Border>("PreviewBorder");
                if (border != null && border.Bounds.Width > 0 && border.Bounds.Height > 0)
                {
                    // Se per caso le Bounds sono già pronte, inizializziamo subito
                    vm.Viewport.ViewportSize = border.Bounds.Size;
                    vm.ResetView();
                    _isFirstLayout = false;
                }
            }
        }

        /// <summary>
        /// Gestisce il ridimensionamento della finestra per adattare il viewport.
        /// Questo evento è più affidabile del Delay perché scatta non appena Avalonia calcola le dimensioni reali.
        /// </summary>
        private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (DataContext is PosterizationToolViewModel vm)
            {
                // Aggiorniamo sempre la dimensione del viewport nel ViewModel
                vm.Viewport.ViewportSize = e.NewSize;

                // Solo la prima volta che riceviamo dimensioni valide (>0), 
                // eseguiamo il ResetView per "incorniciare" l'immagine correttamente.
                if (_isFirstLayout && e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    vm.ResetView();
                    _isFirstLayout = false;
                }
            }
        }

        /// <summary>
        /// Validazione input manuale con logica di Clamp e Cross-Check (Nero < Bianco).
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
        /// Gestisce la pressione di INVIO nei TextBox per confermare i valori.
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

            // Zoom con Ctrl + Rotellina
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var mousePos = e.GetPosition(border);
                double factor = e.Delta.Y > 0 ? 1.1 : (1.0 / 1.1);
                vm.ApplyZoomAtPoint(factor, mousePos);
                e.Handled = true;
                return;
            }

            // Regolazione Soglie con Rotellina (Shift per il Nero, Default per il Bianco)
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