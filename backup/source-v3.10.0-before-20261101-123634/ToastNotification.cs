using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RandomDrawer
{
    /// <summary>窗口顶部向下弹出的圆角气泡通知（纯位移，无淡入淡出、无阴影）。</summary>
    public static class ToastNotification
    {
        private const double HiddenOffset = -28;

        /// <summary>在窗口顶部显示一条成功提示。</summary>
        public static void ShowSuccess(Window owner, string message, double durationMs = 2400)
        {
            try
            {
                if (owner == null) return;
                message = (message ?? "").Trim();
                if (message.Length > 0 && !message.EndsWith("。"))
                    message += "。";

                var icon = new TextBlock
                {
                    Text = "\uE73E",
                    FontFamily = App.IconFontFamily,
                    FontSize = 13,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var iconCircle = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(Color.FromRgb(0x2F, 0xA8, 0x4F)),
                    Child = icon,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };

                var text = new TextBlock
                {
                    Text = message,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeManager.GetBrush("Fg_333333")
                };

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(iconCircle);
                row.Children.Add(text);

                var card = new Border
                {
                    Background = ThemeManager.GetBrush("Bg_FFFFFF"),
                    // 在深色模式下不再绘制浅色边框（否则气泡边缘会出现异常白边）
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(18),
                    Padding = new Thickness(16, 10, 20, 10),
                    Child = row
                };

                var move = new TranslateTransform(0, HiddenOffset);
                card.RenderTransform = move;
                card.Opacity = 0;

                var popup = new Popup
                {
                    PlacementTarget = owner,
                    Placement = PlacementMode.Relative,
                    HorizontalOffset = 0,
                    VerticalOffset = 12,
                    AllowsTransparency = true,
                    StaysOpen = true,
                    Child = card
                };

                card.Loaded += (s, e) =>
                {
                    try
                    {
                        popup.HorizontalOffset = Math.Max(0, (owner.ActualWidth - card.ActualWidth) / 2);

                        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                        move.BeginAnimation(TranslateTransform.YProperty,
                            new DoubleAnimation(HiddenOffset, 0, TimeSpan.FromMilliseconds(260))
                            {
                                EasingFunction = easeOut
                            });
                        card.BeginAnimation(UIElement.OpacityProperty,
                            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                            {
                                EasingFunction = easeOut
                            });

                        var timer = new System.Windows.Threading.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(Math.Max(600, durationMs))
                        };

                        timer.Tick += (s2, e2) =>
                        {
                            timer.Stop();

                            var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                            var slideUp = new DoubleAnimation(0, HiddenOffset, TimeSpan.FromMilliseconds(220))
                            {
                                EasingFunction = easeIn
                            };

                            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                            {
                                EasingFunction = easeIn
                            };

                            fadeOut.Completed += (s3, e3) =>
                            {
                                try { popup.IsOpen = false; } catch { }
                            };

                            move.BeginAnimation(TranslateTransform.YProperty, slideUp);
                            card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                        };

                        timer.Start();
                    }
                    catch { }
                };

                popup.IsOpen = true;
            }
            catch { }
        }
    }
}
