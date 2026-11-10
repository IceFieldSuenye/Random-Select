using System;
using System.Windows;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// 封面圆角裁剪：给「承载封面的控件」套上统一 12px 的圆角裁剪。
    ///
    /// 只裁静态图是不够的：视频（MediaElement）和 GIF（逐帧 Image）都在同一个承载控件里，
    /// 所以这里裁的是承载控件本身（Clip + 尺寸变化时重算），三种媒体一起生效。
    /// 关闭时把 Clip 清成 null，不做任何裁剪。
    /// </summary>
    public static class RoundCorner
    {
        /// <summary>圆角半径（与需求约定一致：12px）。</summary>
        public const double Radius = 12;

        /// <summary>当前是否已挂上尺寸监听。</summary>
        private static readonly DependencyProperty HookedProperty =
            DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(RoundCorner),
                new PropertyMetadata(false));

        /// <summary>内容尺寸未知时是否必须先不裁剪（避免按整块控件裁、把封面切歪）。</summary>
        private static readonly DependencyProperty RequireContentProperty =
            DependencyProperty.RegisterAttached("RequireContent", typeof(bool), typeof(RoundCorner),
                new PropertyMetadata(false));

        /// <summary>内容尺寸（为空 = 未知，按整个控件裁剪）。</summary>
        private static readonly DependencyProperty ContentSizeProperty =
            DependencyProperty.RegisterAttached("ContentSize", typeof(Size), typeof(RoundCorner),
                new PropertyMetadata(Size.Empty));

        /// <summary>设置圆角开关；控件尺寸变化时自动重算裁剪区域。</summary>
        public static void SetEnabled(FrameworkElement host, bool enabled)
        {
            Attach(host, enabled, Size.Empty, false);
        }

        /// <summary>
        /// 按「内容按 Uniform / UniformToFill 适配后的实际显示矩形」裁剪。
        /// 主界面大封面是 Uniform 显示，图片常常带黑边：直接裁整个控件时，
        /// 圆角落在黑边角落上，看起来像没生效，所以这里按图片真正占的那块矩形裁。
        /// </summary>
        public static void SetEnabledForContent(FrameworkElement host, bool enabled, Size contentSize)
        {
            Attach(host, enabled, contentSize, true);
        }

        private static void Attach(FrameworkElement host, bool enabled, Size contentSize, bool requireContent)
        {
            if (host == null) return;

            try
            {
                host.SetValue(ContentSizeProperty, contentSize);
                host.SetValue(RequireContentProperty, requireContent);

                bool hooked = (bool)host.GetValue(HookedProperty);
                if (enabled && !hooked)
                {
                    host.SizeChanged += OnHostSizeChanged;
                    host.SetValue(HookedProperty, true);
                }
                else if (!enabled && hooked)
                {
                    host.SizeChanged -= OnHostSizeChanged;
                    host.SetValue(HookedProperty, false);
                }

                Apply(host, enabled);
            }
            catch { }
        }

        private static void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement host) Apply(host, true);
        }

        private static void Apply(FrameworkElement host, bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    host.Clip = null;
                    return;
                }

                double w = host.ActualWidth;
                double h = host.ActualHeight;
                if (w <= 0 || h <= 0)
                {
                    // 还没排版：清掉旧裁剪，等 SizeChanged 再补
                    host.Clip = null;
                    return;
                }

                Rect rect = new Rect(0, 0, w, h);

                // 已知内容尺寸时，按 Uniform 适配后的显示矩形裁（黑边不参与圆角）
                var content = (Size)host.GetValue(ContentSizeProperty);
                bool requireContent = (bool)host.GetValue(RequireContentProperty);

                if (requireContent && (content.Width <= 0 || content.Height <= 0))
                {
                    // 还不知道封面真实尺寸：先不裁，等尺寸到了再套圆角。
                    // 否则会按整块承载控件去裁，封面看起来像被切掉一圈。
                    host.Clip = null;
                    return;
                }

                if (content.Width > 0 && content.Height > 0)
                {
                    double scale = Math.Min(w / content.Width, h / content.Height);
                    double cw = content.Width * scale;
                    double ch = content.Height * scale;
                    if (cw > 0 && ch > 0)
                        rect = new Rect((w - cw) / 2.0, (h - ch) / 2.0, cw, ch);
                }

                // 半径不能超过短边的一半，否则 RectangleGeometry 会画出异常形状
                double radius = Math.Min(Radius, Math.Min(rect.Width, rect.Height) / 2.0);
                host.Clip = new RectangleGeometry(rect, radius, radius);
            }
            catch { }
        }
    }
}
