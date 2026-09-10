using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageResizerCSharp
{
    public partial class ColorPickerWindow : Window
    {
        public string SelectedHexColor { get; private set; } = "#BA575E";

        private double _currentHue = 356;
        private double _currentSaturation = 0.53;
        private double _currentValue = 0.73;

        private bool _isUpdating = false;
        private bool _isDraggingSpectrum = false;

        private static readonly (string Hex, string Label)[] PresetColors = new[]
        {
            ("#D81B1B", "Merah Pasfoto (Ganjil)"),
            ("#0066CC", "Biru Pasfoto (Genap)"),
            ("#8B0000", "Merah Maroon"),
            ("#001F3F", "Biru Navy"),
            ("#0099FF", "Biru Langit"),
            ("#008000", "Hijau SKCK"),
            ("#FFCC00", "Kuning Paspor"),
            ("#FFFFFF", "Putih Formal"),
            ("#CCCCCC", "Abu-abu Terang"),
            ("#64748B", "Abu-abu Sedang"),
            ("#1E293B", "Abu-abu Gelap"),
            ("#5D4037", "Coklat Formal"),
            ("#EA580C", "Oranye"),
            ("#7C3AED", "Ungu Formal")
        };

        public ColorPickerWindow(string initialHex = "#BA575E")
        {
            InitializeComponent();
            PopulatePalette();

            SelectedHexColor = NormalizeHex(initialHex);
            Loaded += (s, e) =>
            {
                SetColorFromHex(SelectedHexColor);
            };
        }

        private void PopulatePalette()
        {
            pnlPalette.Children.Clear();
            foreach (var (hex, label) in PresetColors)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
                var border = new Border
                {
                    Width = 23,
                    Height = 23,
                    CornerRadius = new CornerRadius(4),
                    Background = brush,
                    BorderBrush = (Brush)FindResource("BorderBrush"),
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(2),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{label} ({hex})"
                };

                border.MouseDown += (s, e) =>
                {
                    SetColorFromHex(hex);
                };

                pnlPalette.Children.Add(border);
            }
        }

        private void GridColorSpectrum_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateThumbPosition();
        }

        private void ColorSpectrum_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSpectrum = true;
            gridColorSpectrum.CaptureMouse();
            UpdateFromSpectrumPoint(e.GetPosition(gridColorSpectrum));
        }

        private void ColorSpectrum_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSpectrum)
            {
                UpdateFromSpectrumPoint(e.GetPosition(gridColorSpectrum));
            }
        }

        private void ColorSpectrum_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingSpectrum)
            {
                _isDraggingSpectrum = false;
                gridColorSpectrum.ReleaseMouseCapture();
            }
        }

        private void UpdateFromSpectrumPoint(Point pt)
        {
            double w = gridColorSpectrum.ActualWidth;
            double h = gridColorSpectrum.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double x = Math.Clamp(pt.X, 0, w);
            double y = Math.Clamp(pt.Y, 0, h);

            _currentSaturation = x / w;
            _currentValue = 1.0 - (y / h);

            Canvas.SetLeft(thumbSpectrum, x);
            Canvas.SetTop(thumbSpectrum, y);

            ApplyHsvColor();
        }

        private void SliderHue_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || rectHueBackground == null) return;

            _currentHue = sliderHue.Value;
            var pureHueColor = ColorFromHSV(_currentHue, 1.0, 1.0);
            rectHueBackground.Fill = new SolidColorBrush(pureHueColor);

            ApplyHsvColor();
        }

        private void ApplyHsvColor()
        {
            var color = ColorFromHSV(_currentHue, _currentSaturation, _currentValue);
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            SelectedHexColor = hex;

            boxSelectedColor.Background = new SolidColorBrush(color);

            _isUpdating = true;
            txtHexInput.Text = hex;
            _isUpdating = false;
        }

        private void SetColorFromHex(string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                ColorToHSV(color, out double h, out double s, out double v);

                _currentHue = h;
                _currentSaturation = s;
                _currentValue = v;

                _isUpdating = true;
                sliderHue.Value = _currentHue;
                rectHueBackground.Fill = new SolidColorBrush(ColorFromHSV(_currentHue, 1.0, 1.0));
                boxSelectedColor.Background = new SolidColorBrush(color);
                txtHexInput.Text = hex;
                SelectedHexColor = hex;
                _isUpdating = false;

                UpdateThumbPosition();
            }
            catch
            {
                // Ignore invalid hex
            }
        }

        private void UpdateThumbPosition()
        {
            double w = gridColorSpectrum.ActualWidth;
            double h = gridColorSpectrum.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double x = _currentSaturation * w;
            double y = (1.0 - _currentValue) * h;

            Canvas.SetLeft(thumbSpectrum, x);
            Canvas.SetTop(thumbSpectrum, y);
        }

        private void TxtHexInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || txtHexInput == null) return;

            string hex = txtHexInput.Text.Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;

            if (hex.Length == 7)
            {
                SetColorFromHex(hex);
            }
        }

        private void BtnCopyHex_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(SelectedHexColor);
                btnCopyHex.Content = "✓";
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5)
                };
                timer.Tick += (s, ev) =>
                {
                    btnCopyHex.Content = "Salin";
                    timer.Stop();
                };
                timer.Start();
            }
            catch
            {
                // Clipboard lock ignore
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string NormalizeHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "#BA575E";
            hex = hex.Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;
            return hex.Length == 7 ? hex.ToUpperInvariant() : "#BA575E";
        }

        public static Color ColorFromHSV(double hue, double saturation, double value)
        {
            int hi = Convert.ToInt32(Math.Floor(hue / 60.0)) % 6;
            double f = hue / 60.0 - Math.Floor(hue / 60.0);

            value = value * 255.0;
            byte v = Convert.ToByte(Math.Clamp(value, 0, 255));
            byte p = Convert.ToByte(Math.Clamp(value * (1.0 - saturation), 0, 255));
            byte q = Convert.ToByte(Math.Clamp(value * (1.0 - f * saturation), 0, 255));
            byte t = Convert.ToByte(Math.Clamp(value * (1.0 - (1.0 - f) * saturation), 0, 255));

            return hi switch
            {
                0 => Color.FromRgb(v, t, p),
                1 => Color.FromRgb(q, v, p),
                2 => Color.FromRgb(p, v, t),
                3 => Color.FromRgb(p, q, v),
                4 => Color.FromRgb(t, p, v),
                _ => Color.FromRgb(v, p, q),
            };
        }

        public static void ColorToHSV(Color color, out double hue, out double saturation, out double value)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            if (delta == 0) hue = 0;
            else if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);

            if (hue < 0) hue += 360;

            saturation = max == 0 ? 0 : delta / max;
            value = max;
        }
    }
}
