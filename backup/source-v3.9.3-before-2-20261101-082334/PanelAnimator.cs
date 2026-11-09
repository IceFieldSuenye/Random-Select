using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace RandomDrawer
{
    /// <summary>容器高度平滑展开/收起的动画工具。</summary>
    public static class PanelAnimator
    {
        public const int DurationMs = 180;

        /// <summary>
        /// 通过动画 <see cref="FrameworkElement.MaxHeight"/> 平滑展开/收起容器。
        /// 容器需要设置 ClipToBounds="True" 才会裁掉溢出内容。
        /// </summary>
        public static void Animate(FrameworkElement box, bool expand, double limit = 400, Action onCompleted = null)
        {
            try
            {
                if (box == null) { onCompleted?.Invoke(); return; }

                if (double.IsNaN(limit) || double.IsInfinity(limit) || limit <= 0) limit = 400;

                double from = box.MaxHeight;
                if (double.IsNaN(from) || double.IsInfinity(from)) from = box.ActualHeight;
                from = Math.Max(0, from);

                double to = 0;

                if (expand)
                {
                    double width = box.ActualWidth > 0 ? box.ActualWidth : 320;

                    // 先解除自身的高度限制再测量，否则量到的永远是被裁掉后的高度（只会显示第一行）
                    double savedMaxHeight = box.MaxHeight;
                    try
                    {
                        box.MaxHeight = double.PositiveInfinity;
                        box.Measure(new Size(width, double.PositiveInfinity));
                        to = box.DesiredSize.Height;
                    }
                    finally
                    {
                        box.MaxHeight = savedMaxHeight;
                    }

                    if (double.IsNaN(to) || double.IsInfinity(to) || to <= 0) to = limit;
                    to = Math.Min(limit, to);
                }

                var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(DurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = expand ? EasingMode.EaseOut : EasingMode.EaseIn },
                    FillBehavior = FillBehavior.Stop
                };

                animation.Completed += (s, e) =>
                {
                    try { box.MaxHeight = to; } catch { }
                    onCompleted?.Invoke();
                };

                box.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
            }
            catch { onCompleted?.Invoke(); }
        }
    }
}
