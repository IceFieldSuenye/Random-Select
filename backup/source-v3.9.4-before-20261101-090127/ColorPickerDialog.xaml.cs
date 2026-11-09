using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>模态颜色选择弹窗：常用色 + RGB 自定义颜色。</summary>
    public partial class ColorPickerDialog : FloatingDialogBase
    {
        private static readonly string[] PresetColors =
        {
            "#F0F0F0", "#FFFFFF", "#E0E0E0", "#BDBDBD", "#9E9E9E", "#757575", "#424242", "#000000",
            "#FFCDD2", "#F8BBD0", "#E1BEE7", "#D1C4E9", "#C5CAE9", "#BBDEFB", "#B3E5FC", "#B2EBF2",
            "#FFECB3", "#FFE0B2", "#FFCCBC", "#DCEDC8", "#C8E6C9", "#B2DFDB", "#B3E5FC", "#D1C4E9",
            "#FFEBEE", "#FCE4EC", "#F3E5F5", "#EDE7F6", "#E8EAF6", "#E3F2FD", "#E1F5FE", "#E0F7FA"
        };

        private bool _updating;

        public string SelectedHex { get; private set; } = "";
        public bool Cleared { get; private set; }

        public ColorPickerDialog(string title, string initialHex, bool allowClear = true)
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "选择颜色" : title;
            BtnClear.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;

            BuildPresets();

            Color color = Colors.Black;
            if (!string.IsNullOrWhiteSpace(initialHex))
            {
                try { color = (Color)ColorConverter.ConvertFromString(initialHex); } catch { }
            }
            SetColor(color);
        }

        private void BuildPresets()
        {
            foreach (string hex in PresetColors)
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(hex); } catch { continue; }

                var button = new Button
                {
                    Width = 28,
                    Height = 28,
                    Margin = new Thickness(3),
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
            _updating = true;
            try
            {
                SliderR.Value = color.R;
                SliderG.Value = color.G;
                SliderB.Value = color.B;
                TxtR.Text = color.R.ToString();
                TxtG.Text = color.G.ToString();
                TxtB.Text = color.B.ToString();
                PreviewBorder.Background = new SolidColorBrush(color);
            }
            finally { _updating = false; }
        }

        private Color CurrentColor() =>
            Color.FromRgb((byte)SliderR.Value, (byte)SliderG.Value, (byte)SliderB.Value);

        private void SliderRgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating) return;
            if (TxtR == null || TxtG == null || TxtB == null || PreviewBorder == null) return;
            _updating = true;
            try
            {
                TxtR.Text = ((int)SliderR.Value).ToString();
                TxtG.Text = ((int)SliderG.Value).ToString();
                TxtB.Text = ((int)SliderB.Value).ToString();
                PreviewBorder.Background = new SolidColorBrush(CurrentColor());
            }
            finally { _updating = false; }
        }

        private void TxtRgb_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating || TxtR == null || TxtG == null || TxtB == null || PreviewBorder == null) return;
            _updating = true;
            try
            {
                if (int.TryParse(TxtR.Text, out int r)) SliderR.Value = Math.Max(0, Math.Min(255, r));
                if (int.TryParse(TxtG.Text, out int g)) SliderG.Value = Math.Max(0, Math.Min(255, g));
                if (int.TryParse(TxtB.Text, out int b)) SliderB.Value = Math.Max(0, Math.Min(255, b));
                PreviewBorder.Background = new SolidColorBrush(CurrentColor());
            }
            finally { _updating = false; }
        }

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
