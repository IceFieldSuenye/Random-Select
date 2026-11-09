using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RandomDrawer
{
    /// <summary>
    /// 「毛玻璃」按钮工具：把页面背景渲染成模糊位图，再按按钮位置裁一块作为按钮底色。
    /// </summary>
    public static class GlassButtonStyler
    {
        public const double Pad = 24;

        /// <summary>生成整页大小的模糊背景图（带 Pad 外扩，避免模糊边缘透明）。</summary>
        public static ImageSource BuildBackdrop(FrameworkElement root, string imagePath)
        {
            try
            {
                if (root == null || string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return null;

                root.UpdateLayout();
                double w = root.ActualWidth;
                double h = root.ActualHeight;
                if (w < 8 || h < 8) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();

                int pw = (int)Math.Ceiling(w + Pad * 2);
                int ph = (int)Math.Ceiling(h + Pad * 2);
                if (pw <= 0 || ph <= 0) return null;

                var rect = new Rectangle
                {
                    Width = pw,
                    Height = ph,
                    Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
                    Effect = new BlurEffect { Radius = 18, KernelType = KernelType.Gaussian }
                };

                var canvas = new Grid { Width = pw, Height = ph };
                canvas.Children.Add(rect);
                canvas.Children.Add(new Rectangle
                {
                    Width = pw,
                    Height = ph,
                    Fill = new SolidColorBrush(Color.FromArgb(0x3A, 0x00, 0x00, 0x00))
                });

                canvas.Measure(new Size(pw, ph));
                canvas.Arrange(new Rect(0, 0, pw, ph));
                canvas.UpdateLayout();

                var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(canvas);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        /// <summary>把按钮底色换成模糊背景中对应位置的贴片。成功返回 true。</summary>
        public static bool Apply(FrameworkElement root, Button button, ImageSource backdrop)
        {
            try
            {
                if (root == null || button == null || backdrop == null) return false;

                button.UpdateLayout();
                Point pt = button.TransformToVisual(root).Transform(new Point(0, 0));
                double bw = button.ActualWidth > 2 ? button.ActualWidth : button.Width;
                double bh = button.ActualHeight > 2 ? button.ActualHeight : button.Height;
                if (bw < 2 || bh < 2) return false;

                button.Background = new ImageBrush(backdrop)
                {
                    Stretch = Stretch.Fill,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(pt.X + Pad, pt.Y + Pad, bw, bh)
                };
                return true;
            }
            catch { return false; }
        }

        public static bool IsLight(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 >= 0.55;
        }

        public static Color Scale(Color c, double factor)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, Math.Round(c.R * factor)),
                (byte)Math.Min(255, Math.Round(c.G * factor)),
                (byte)Math.Min(255, Math.Round(c.B * factor)));
        }
    }
}
