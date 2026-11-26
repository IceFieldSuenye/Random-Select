using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 3.78（snapshot-2）：主页样式预览控件。用真实控件树复刻主界面的卡池列表 / 封面 / 底部按钮，
    /// 尺寸由调用方设成主窗口客户区大小，主题与「主界面背景模糊」跟随当前设置。
    /// </summary>
    public partial class HomePreviewControl : UserControl
    {
        public HomePreviewControl()
        {
            InitializeComponent();
        }

        /// <summary>填预览数据。thumb 为 null 时按「默认」样式画纯文字卡片。</summary>
        public void SetPreview(string poolName, BitmapSource cover, BitmapSource thumb,
                               bool showText, Color? textColor, bool round, bool blurBackground)
        {
            PoolList.Children.Clear();

            // 背景模糊：与主界面一样用封面铺底（模糊由 XAML 里的 BlurEffect 完成）
            if (blurBackground && cover != null)
            {
                BgImage.Source = cover;
                BgImage.Opacity = 1.0;
            }
            else
            {
                BgImage.Source = null;
                BgImage.Opacity = 0;
            }

            // 中间封面
            CoverImage.Source = cover;
            RoundCorner.SetEnabled(CoverHost, round);

            PoolList.Children.Add(BuildPoolCard(poolName, thumb, showText, textColor, round));
        }

        private Border BuildPoolCard(string name, BitmapSource thumb, bool showText, Color? textColor, bool round)
        {
            const double width = 154.0;
            bool hasThumb = thumb != null && thumb.PixelWidth > 0 && thumb.PixelHeight > 0;

            double height = 60;
            if (hasThumb)
            {
                height = width * thumb!.PixelHeight / thumb.PixelWidth;
                height = Math.Max(46, Math.Min(260, height));
            }

            // 默认样式（无缩略图）与主界面 PoolButtonStyle 一致：无圆角、浅灰底、细边框
            var card = new Border
            {
                Width = width,
                Height = height,
                Margin = new Thickness(0, 0, 0, 10),
                CornerRadius = hasThumb && round ? new CornerRadius(9) : new CornerRadius(0),
                BorderThickness = new Thickness(hasThumb ? 2 : 1),
                BorderBrush = hasThumb
                    ? ThemeManager.GetBrush("Fg_0078D7")
                    : ThemeManager.GetBrush("Bd_CCCCCC"),
                ClipToBounds = true,
                Background = hasThumb
                    ? (Brush)new ImageBrush(thumb) { Stretch = Stretch.Uniform }
                    : ThemeManager.GetBrush("Bg_F0F0F0")
            };

            var grid = new Grid();

            if (hasThumb)
            {
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
                    CornerRadius = round ? new CornerRadius(7) : new CornerRadius(0)
                });
            }

            if (showText || !hasThumb)
            {
                var text = new TextBlock
                {
                    Text = name ?? "",
                    FontSize = 16,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 4, 6, 4),
                    Foreground = new SolidColorBrush(textColor ?? GetThemeTextColor())
                };
                if (hasThumb)
                {
                    text.Effect = new DropShadowEffect
                    {
                        BlurRadius = 8,
                        ShadowDepth = 0,
                        Opacity = 0.85,
                        Color = Colors.Black
                    };
                }
                grid.Children.Add(text);
            }

            card.Child = grid;
            return card;
        }

        private static Color GetThemeTextColor()
        {
            if (ThemeManager.GetBrush("Fg_333333") is SolidColorBrush b)
            {
                return b.Color;
            }
            return Color.FromRgb(0x33, 0x33, 0x33);
        }
    }
}