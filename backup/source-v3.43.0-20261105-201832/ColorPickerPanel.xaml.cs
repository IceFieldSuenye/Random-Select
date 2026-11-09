using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// 内嵌颜色面板（封面编辑页的「颜色」浮层内容）：
    /// 饱和度 / 明度方块 + 色相条 + 十六进制 + 常用色。
    ///
    /// 与 <see cref="ColorPickerDialog"/> 的区别：这里没有「清除 / 取消 / 确定」，
    /// 点选或拖动即生效；面板本身的底色 / 边框 / 文字全部取主题资源，深色模式自动跟随。
    /// </summary>
    public partial class ColorPickerPanel : UserControl
    {
        /// <summary>常用色：深色 / 灰阶在前，彩色在后（12 列 × 2 行）。</summary>
        private static readonly string[] PresetColors =
        {
            "#000000", "#404040", "#757575", "#9E9E9E", "#BDBDBD", "#E0E0E0", "#F5F5F5", "#FFFFFF", "#D32F2F", "#F57C00", "#FBC02D", "#388E3C",
            "#00796B", "#1976D2", "#303F9F", "#7B1FA2", "#C2185B", "#E53935", "#FB8C00", "#FDD835", "#43A047", "#00ACC1", "#1E88E5", "#8E24AA"
        };

        private bool _updating;
        private bool _draggingSquare;
        private bool _draggingHue;

        private double _hue;    // 0~360
        private double _sat;    // 0~1
        private double _val;    // 0~1

        /// <summary>颜色变化（拖动 / 输入 / 点常用色都会触发）。面板打开时由调用方实时应用。</summary>
        public event Action<Color> ColorChanged;

        /// <summary>点右上角关闭按钮（3.33）。</summary>
        public event Action CloseRequested;

        public ColorPickerPanel()
        {
            InitializeComponent();
            BuildPresets();
            SvSquare.SizeChanged += (s, e) => ApplyHsvToUi();
            HueBar.SizeChanged += (s, e) => ApplyHsvToUi();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            try { CloseRequested?.Invoke(); } catch { }
        }

        /// <summary>当前颜色（十六进制，不带透明度）。</summary>
        public Color CurrentColor => ColorUtils.FromHsv(_hue, _sat, _val);

        /// <summary>初始化面板：标题 + 当前颜色（不会触发 ColorChanged）。</summary>
        public void SetColor(Color color, string? title)
        {
            if (!string.IsNullOrEmpty(title) && TxtTitle != null) TxtTitle.Text = title;

            ColorUtils.ToHsv(color, out double h, out double s, out double v);
            // 灰色 / 黑色时色相没有意义，保留原色相，避免拖动色相条时跳变
            if (s > 0.0001 && v > 0.0001) _hue = h;
            _sat = s;
            _val = v;
            ApplyHsvToUi();
        }

        private void BuildPresets()
        {
            if (PresetGrid == null) return;

            foreach (string hex in PresetColors)
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(hex); } catch { continue; }

                var button = new Button
                {
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(color),
                    BorderThickness = new Thickness(1),
                    Style = TryFindResource("ColorChipStyle") as Style,
                    Tag = hex
                };
                // 边框跟随主题（深色模式下不能是浅灰）
                button.SetResourceReference(Control.BorderBrushProperty, "Bd_CCCCCC");
                button.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is string h)
                    {
                        try { SetColor((Color)ColorConverter.ConvertFromString(h), null); }
                        catch { return; }
                        RaiseChanged();
                    }
                };
                PresetGrid.Children.Add(button);
            }
        }

        /// <summary>把当前 HSV 同步到方块、色相条、十六进制与预览。</summary>
        private void ApplyHsvToUi()
        {
            _updating = true;
            try
            {
                Color color = ColorUtils.FromHsv(_hue, _sat, _val);
                if (SvHueRect != null) SvHueRect.Fill = new SolidColorBrush(ColorUtils.FromHsv(_hue, 1, 1));

                // 方块里的圆点：横向 = 饱和度，纵向 = 明度（上亮下暗）
                double squareW = Math.Max(1, SvSquare.ActualWidth);
                double squareH = Math.Max(1, SvSquare.ActualHeight);
                SvThumb.Margin = new Thickness(
                    Math.Max(0, _sat * (squareW - 12)),
                    Math.Max(0, (1 - _val) * (squareH - 12)), 0, 0);

                // 色相条上的指示条
                double hueH = Math.Max(1, HueBar.ActualHeight);
                HueThumb.Margin = new Thickness(0, Math.Max(0, Math.Min(hueH - 5, _hue / 360.0 * hueH - 2.5)), 0, 0);

                if (TxtHex != null)
                {
                    string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                    if (!string.Equals(TxtHex.Text, hex, StringComparison.OrdinalIgnoreCase)) TxtHex.Text = hex;
                }
                if (PreviewBorder != null) PreviewBorder.Background = new SolidColorBrush(color);
            }
            catch { }
            finally { _updating = false; }
        }

        private void RaiseChanged()
        {
            try { ColorChanged?.Invoke(CurrentColor); } catch { }
        }

        // ================================================================
        // 鼠标交互：方块（饱和度 / 明度）与色相条
        // ================================================================

        private void SvSquare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSquare = true;
            try { SvSquare.CaptureMouse(); } catch { }
            UpdateFromSquare(e.GetPosition(SvSquare));
            RaiseChanged();
            e.Handled = true;
        }

        private void SvSquare_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingSquare) return;
            UpdateFromSquare(e.GetPosition(SvSquare));
            RaiseChanged();
        }

        private void SvSquare_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingSquare = false;
            try { SvSquare.ReleaseMouseCapture(); } catch { }
        }

        private void UpdateFromSquare(Point p)
        {
            double w = Math.Max(1, SvSquare.ActualWidth);
            double h = Math.Max(1, SvSquare.ActualHeight);

            _sat = Math.Max(0, Math.Min(1, p.X / w));
            _val = Math.Max(0, Math.Min(1, 1 - p.Y / h));
            ApplyHsvToUi();
        }

        private void HueBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = true;
            try { HueBar.CaptureMouse(); } catch { }
            UpdateFromHue(e.GetPosition(HueBar));
            RaiseChanged();
            e.Handled = true;
        }

        private void HueBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingHue) return;
            UpdateFromHue(e.GetPosition(HueBar));
            RaiseChanged();
        }

        private void HueBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _draggingHue = false;
            try { HueBar.ReleaseMouseCapture(); } catch { }
        }

        private void UpdateFromHue(Point p)
        {
            double h = Math.Max(1, HueBar.ActualHeight);
            _hue = Math.Max(0, Math.Min(359.999, p.Y / h * 360.0));
            ApplyHsvToUi();
        }

        // ================================================================
        // 十六进制输入
        // ================================================================

        private void TxtHex_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating || TxtHex == null) return;

            string raw = (TxtHex.Text ?? "").Trim().TrimStart('#');
            if (raw.Length != 6) return;
            for (int i = 0; i < raw.Length; i++)
            {
                if (!Uri.IsHexDigit(raw[i])) return;
            }

            try
            {
                Color color = (Color)ColorConverter.ConvertFromString("#" + raw);
                ColorUtils.ToHsv(color, out double h, out double s, out double v);
                if (s > 0.0001 && v > 0.0001) _hue = h;
                _sat = s;
                _val = v;
                ApplyHsvToUi();
                RaiseChanged();
            }
            catch { }
        }
    }
}
