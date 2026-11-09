using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RandomDrawer
{
    /// <summary>
    /// 窗口顶部气泡通知：圆角卡片从窗口上边缘「钻」出来，停靠在窗口顶部正中，再缩回去。
    ///
    /// ★ 3.15.1：以前是挂在窗口外的 Popup，窗口一动气泡就不跟手，而且它的起始位置比窗口顶部还高，
    ///   滑入 / 滑出的前半段会飘到标题栏甚至窗口外面去（用户看到的就是「没有严格贴在窗口顶部」）。
    ///   现在改成直接叠在窗口根容器里的图层：由窗口自己裁剪，出现和消失都严格贴着上边缘。
    /// </summary>
    public static class ToastNotification
    {
        /// <summary>收起时向上多移这么多像素，保证完全看不见。</summary>
        private const double HiddenExtra = 8;

        // 必须高于 FloatingOverlay 的 5000：弹窗打开时气泡也要能被看到（和以前的 Popup 行为一致）
        private const int LayerZIndex = 6000;

        private static readonly Dictionary<Window, FrameworkElement> _activeLayers =
            new Dictionary<Window, FrameworkElement>();

        /// <summary>在窗口顶部显示一条成功提示（绿色对勾）。</summary>
        public static void ShowSuccess(Window owner, string message, double durationMs = 2400)
        {
            Show(owner, message, "\uE73E", Color.FromRgb(0x2F, 0xA8, 0x4F), durationMs);
        }

        /// <summary>在窗口顶部显示一条提醒（橙色叉号），用于「当前不能这样做」这类反馈。</summary>
        public static void ShowWarning(Window owner, string message, double durationMs = 2400)
        {
            Show(owner, message, "\uE711", Color.FromRgb(0xE0, 0x6C, 0x2B), durationMs);
        }

        private static void Show(Window owner, string message, string iconGlyph, Color iconColor, double durationMs)
        {
            try
            {
                if (owner == null) return;
                if (owner.Content is not Panel root) return;   // 没有可叠的根容器就不显示

                message = (message ?? "").Trim();
                if (message.Length > 0 && !message.EndsWith("。"))
                    message += "。";

                // 同一窗口只保留一条：新的来了先把旧的收掉，避免叠在一起
                RemoveLayer(owner);

                var icon = new TextBlock
                {
                    Text = iconGlyph,
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
                    Background = new SolidColorBrush(iconColor),
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
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(18),
                    Padding = new Thickness(16, 10, 20, 10),
                    Child = row,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };

                // 先放到「屏幕外」再挂进窗口：否则第一帧会在顶部闪一下
                var move = new TranslateTransform(0, -200);
                card.RenderTransform = move;
                card.Opacity = 0;

                // 覆盖整个窗口的透明图层：由窗口负责裁剪，气泡不会跑到窗口外面
                var layer = new Grid { ClipToBounds = true, IsHitTestVisible = false };
                Panel.SetZIndex(layer, LayerZIndex);
                layer.Children.Add(card);

                root.Children.Add(layer);
                _activeLayers[owner] = layer;

                owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        double height = card.ActualHeight > 0 ? card.ActualHeight : card.DesiredSize.Height;
                        if (height <= 0) height = 44;

                        double hiddenY = -(height + HiddenExtra);
                        move.Y = hiddenY;
                        card.Opacity = 0;

                        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                        move.BeginAnimation(TranslateTransform.YProperty,
                            new DoubleAnimation(hiddenY, 0, TimeSpan.FromMilliseconds(260))
                            {
                                EasingFunction = easeOut
                            });
                        card.BeginAnimation(UIElement.OpacityProperty,
                            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                            {
                                EasingFunction = easeOut
                            });

                        var timer = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(Math.Max(600, durationMs))
                        };

                        timer.Tick += (s2, e2) =>
                        {
                            timer.Stop();

                            var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };

                            var slideUp = new DoubleAnimation(0, hiddenY, TimeSpan.FromMilliseconds(220))
                            {
                                EasingFunction = easeIn
                            };

                            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
                            {
                                EasingFunction = easeIn
                            };

                            fadeOut.Completed += (s3, e3) => RemoveLayer(owner);

                            move.BeginAnimation(TranslateTransform.YProperty, slideUp);
                            card.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                        };

                        timer.Start();
                    }
                    catch { RemoveLayer(owner); }
                }), DispatcherPriority.Loaded);
            }
            catch { }
        }

        /// <summary>把当前窗口上的气泡图层摘掉（重复调用安全）。</summary>
        private static void RemoveLayer(Window owner)
        {
            try
            {
                if (owner == null) return;
                if (!_activeLayers.TryGetValue(owner, out var layer)) return;

                _activeLayers.Remove(owner);
                if (owner.Content is Panel root && layer.Parent == root)
                    root.Children.Remove(layer);
                else if (layer.Parent is Panel parent)
                    parent.Children.Remove(layer);
            }
            catch { }
        }
    }
}
