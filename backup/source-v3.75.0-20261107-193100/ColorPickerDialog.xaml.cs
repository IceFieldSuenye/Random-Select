using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// 模态颜色选择弹窗（3.64 回滚到上一版样式）：
    /// 最上面是一条横向的预览色条，下面是饱和度 / 明度方块 + 色相条 +
    /// 十六进制 / RGB（可切 HSV）输入 + 常用色。
    /// </summary>
    public partial class ColorPickerDialog : FloatingDialogBase
    {
        private static readonly string[] PresetColors =
        {
            "#F0F0F0", "#FFFFFF", "#E0E0E0", "#BDBDBD", "#9E9E9E", "#757575", "#424242", "#000000", "#D32F2F", "#1976D2",
            "#FFCDD2", "#F8BBD0", "#E1BEE7", "#D1C4E9", "#C5CAE9", "#BBDEFB", "#B3E5FC", "#B2EBF2", "#C8E6C9", "#FFE0B2",
            "#FFEBEE", "#FCE4EC", "#F3E5F5", "#EDE7F6", "#E8EAF6", "#E3F2FD", "#E1F5FE", "#E0F7FA", "#DCEDC8", "#FFCCBC"
        };

        private bool _updating;
        private bool _draggingSquare;
        private bool _draggingHue;

        /// <summary>数值输入区当前是 HSV（色相 / 饱和度 / 明度）还是 RGB。</summary>
        private bool _hsvMode;

        private double _hue;    // 0~360
        private double _sat;    // 0~1
        private double _val;    // 0~1

        public string SelectedHex { get; private set; } = "";
        public bool Cleared { get; private set; }

        public ColorPickerDialog(string title, string initialHex, bool allowClear = true)
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "编辑颜色" : title;
            BtnClear.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;

            BuildPresets();

            Color color = Colors.Black;
            if (!string.IsNullOrWhiteSpace(initialHex))
            {
                try { color = (Color)ColorConverter.ConvertFromString(initialHex); } catch { }
            }
            SetColor(color);

            if (ComboMode != null && ComboMode.SelectedIndex < 0) ComboMode.SelectedIndex = 0;

            // 构造时还没布局，方块尺寸是 0：等真正显示出来后再摆一次指示点
            Loaded += (s, e) => ApplyHsvToUi();
            SvSquare.SizeChanged += (s, e) => ApplyHsvToUi();
            HueBar.SizeChanged += (s, e) => ApplyHsvToUi();
        }

        // ================================================================
        // 初始化 / 同步
        // ================================================================

        private void BuildPresets()
        {
            foreach (string hex in PresetColors)
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(hex); } catch { continue; }

                var button = new Button
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(color),
                    BorderBrush = ThemeManager.GetBrush("Bd_CCCCCC"),
                    BorderThickness = new Thickness(1),
                    Style = TryFindResource("ColorChipStyle") as Style,
                    Tag = hex,
                    ToolTip = hex
                };
                button.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is string h)
                    {
                        try { SetColor((Color)ColorConverter.ConvertFromString(h)); } catch { }
                    }
                };
                PresetGrid.Children.Add(button);
            }
        }

        private void SetColor(Color color)
        {
            ToHsv(color, out double h, out double s, out double v);

            // 灰色 / 黑色时色相没有意义，保留原来的色相，避免拖动色相条时跳变
            if (s > 0.0001 && v > 0.0001) _hue = h;
            _sat = s;
            _val = v;

            ApplyHsvToUi();
        }

        /// <summary>把当前 HSV 同步到方块、色相条、数值与预览。</summary>
        private void ApplyHsvToUi()
        {
            _updating = true;
            try
            {
                Color color = FromHsv(_hue, _sat, _val);
                SvHueRect.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));

                // 方块里的圆点：横向 = 饱和度，纵向 = 明度（上亮下暗）
                double squareW = Math.Max(1, SvSquare.ActualWidth);
                double squareH = Math.Max(1, SvSquare.ActualHeight);
                SvThumb.Margin = new Thickness(
                    Math.Max(0, _sat * (squareW - 12)),
                    Math.Max(0, (1 - _val) * (squareH - 12)), 0, 0);

                // 色相条上的指示条
                double hueH = Math.Max(1, HueBar.ActualHeight);
                HueThumb.Margin = new Thickness(0, Math.Max(0, Math.Min(hueH - 5, _hue / 360.0 * hueH - 2.5)), 0, 0);

                TxtHex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

                if (_hsvMode)
                {
                    TxtRLabel.Text = "色相";
                    TxtGLabel.Text = "饱和度";
                    TxtBLabel.Text = "明度";
                    TxtR.Text = Math.Round(_hue).ToString(CultureInfo.InvariantCulture);
                    TxtG.Text = Math.Round(_sat * 100).ToString(CultureInfo.InvariantCulture);
                    TxtB.Text = Math.Round(_val * 100).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    TxtRLabel.Text = "红色";
                    TxtGLabel.Text = "绿色";
                    TxtBLabel.Text = "蓝色";
                    TxtR.Text = color.R.ToString(CultureInfo.InvariantCulture);
                    TxtG.Text = color.G.ToString(CultureInfo.InvariantCulture);
                    TxtB.Text = color.B.ToString(CultureInfo.InvariantCulture);
                }

                PreviewBorder.Background = new SolidColorBrush(color);
            }
            finally { _updating = false; }
        }

        private Color CurrentColor() => FromHsv(_hue, _sat, _val);

        /// <summary>RGB / HSV 切换：换标签与数值，颜色本身不变。</summary>
        private void ComboMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || ComboMode == null) return;
            _hsvMode = ComboMode.SelectedIndex == 1;
            ApplyHsvToUi();
        }

        // ================================================================
        // 鼠标交互：方块（饱和度 / 明度）与色相条
        // ================================================================

        private void SvSquare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _draggingSquare = true;
            SvSquare.CaptureMouse();
            UpdateFromSquare(e.GetPosition(SvSquare));
        }

        private void SvSquare_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingSquare) return;
            UpdateFromSquare(e.GetPosition(SvSquare));
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
            HueBar.CaptureMouse();
            UpdateFromHue(e.GetPosition(HueBar));
        }

        private void HueBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingHue) return;
            UpdateFromHue(e.GetPosition(HueBar));
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
        // 文本输入
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
                ToHsv(color, out double h, out double s, out double v);
                if (s > 0.0001 && v > 0.0001) _hue = h;
                _sat = s;
                _val = v;
                ApplyHsvToUi();
            }
            catch { }
        }

        /// <summary>三项数值：RGB 时是 0~255，HSV 时是 色相 0~359 / 饱和度、明度 0~100。</summary>
        private void TxtComponents_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating || TxtR == null || TxtG == null || TxtB == null) return;

            if (!int.TryParse(TxtR.Text, out int a) ||
                !int.TryParse(TxtG.Text, out int b) ||
                !int.TryParse(TxtB.Text, out int c)) return;

            if (_hsvMode)
            {
                _hue = Math.Max(0, Math.Min(359.999, a));
                _sat = Math.Max(0, Math.Min(1, b / 100.0));
                _val = Math.Max(0, Math.Min(1, c / 100.0));
                ApplyHsvToUi();
                return;
            }

            Color color = Color.FromRgb((byte)Math.Max(0, Math.Min(255, a)),
                                        (byte)Math.Max(0, Math.Min(255, b)),
                                        (byte)Math.Max(0, Math.Min(255, c)));
            ToHsv(color, out double hh, out double ss, out double vv);
            if (ss > 0.0001 && vv > 0.0001) _hue = hh;
            _sat = ss;
            _val = vv;
            ApplyHsvToUi();
        }

        // ================================================================
        // HSV 换算（与内嵌颜色面板共用 ColorUtils 的实现）
        // ================================================================

        private static void ToHsv(Color c, out double h, out double s, out double v)
            => ColorUtils.ToHsv(c, out h, out s, out v);

        private static Color FromHsv(double h, double s, double v)
            => ColorUtils.FromHsv(h, s, v);

        // ================================================================
        // 按钮
        // ================================================================

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            Cleared = true;
            SelectedHex = "";
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Color c = CurrentColor();
            SelectedHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Cleared = false;
            DialogResult = true;
        }
    }
}
