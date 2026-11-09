using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RandomDrawer
{
    /// <summary>
    /// 窗口内浮动界面宿主：
    ///   · 不再使用任何独立弹窗（Window.ShowDialog）；
    ///   · 浮层覆盖在主窗口内，半透明遮罩 + 可拖动卡片；
    ///   · 卡片尺寸随内容自动计算，超过窗口时出现滚动条；
    ///   · 使用 DispatcherFrame 提供同步（模态）等待，调用方代码无需 async 化；
    ///   · 支持多层嵌套（例如在密码框里再弹出提示）。
    /// </summary>
    public static class FloatingOverlay
    {
        private class Entry
        {
            public Grid Layer;
            public Canvas Canvas;
            public Border Card;
            public Grid CardHost;
            public DispatcherFrame Frame;
            public Window Host;
            public Window FallbackWindow;
            public FrameworkElement Content;
            public bool Closable;
            public SizeChangedEventHandler HostResizeHandler;
        }

        private static readonly List<Entry> _stack = new List<Entry>();
        private static Window _keyHost;
        private static readonly KeyEventHandler _keyHandler = OnHostPreviewKeyDown;

        private static Entry _dragEntry;
        private static Point _dragStart;
        private static Point _dragOrigin;

        public static bool IsOpen => _stack.Count > 0;

        // ================================================================
        // 显示
        // ================================================================

        /// <summary>
        /// 显示一个浮动界面并同步等待其关闭。
        /// </summary>
        /// <param name="owner">宿主窗口（一般为 MainWindow）。</param>
        /// <param name="content">要显示的内容（UserControl）。</param>
        /// <param name="title">标题栏文字。</param>
        /// <param name="preferredWidth">期望宽度，0 表示完全自适应内容。</param>
        /// <param name="closable">是否允许通过 ✕ / ESC 关闭。</param>
        public static void Show(Window owner, FrameworkElement content, string title,
                                double preferredWidth = 0, bool closable = true, string titleIcon = null)
        {
            if (content == null) return;

            Window host = owner ?? Application.Current?.MainWindow;

            var entry = new Entry
            {
                Host = host,
                Content = content,
                Closable = closable
            };

            if (host == null || !(host.Content is Panel))
            {
                ShowInFallbackWindow(entry, title, preferredWidth);
                return;
            }

            BuildLayer(entry, title, preferredWidth, titleIcon);

            var frame = new DispatcherFrame();
            entry.Frame = frame;

            _stack.Add(entry);
            AttachKeys(host);

            Dispatcher.PushFrame(frame);
        }

        private static void BuildLayer(Entry entry, string title, double preferredWidth, string titleIcon)
        {
            Window host = entry.Host;
            var root = host.Content as Panel;

            var layer = new Grid();
            layer.SetResourceReference(Panel.BackgroundProperty, "Ui.Backdrop");

            var canvas = new Canvas();
            layer.Children.Add(canvas);

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };
            card.SetResourceReference(Border.BackgroundProperty, "Ui.CardBg");
            card.SetResourceReference(Border.BorderBrushProperty, "Ui.CardBorder");
            card.UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(card, TextRenderingMode.ClearType);

            // 阴影单独放在卡片后面：只模糊阴影本身，卡片文字保持清晰
            var shadowBorder = new Border
            {
                CornerRadius = new CornerRadius(13),
                Background = Brushes.Black,
                Opacity = 0.55,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 26,
                    ShadowDepth = 5,
                    Direction = 270,
                    Opacity = 0.75
                }
            };

            // ---- 标题栏 ----
            var titleText = new TextBlock
            {
                Text = title ?? "",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "Ui.TitleText");

            var titleBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0)
            };

            if (!string.IsNullOrEmpty(titleIcon))
            {
                var iconText = new TextBlock
                {
                    Text = titleIcon,
                    FontFamily = App.IconFontFamily,
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                iconText.SetResourceReference(TextBlock.ForegroundProperty, "Fg_0078D7");
                titleBar.Children.Add(iconText);
            }
            titleBar.Children.Add(titleText);

            var closeBtn = new Button
            {
                Content = "\u2715",
                FontSize = 12,
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                ToolTip = "关闭",
                Cursor = Cursors.Arrow
            };
            closeBtn.SetResourceReference(Control.ForegroundProperty, "Fg_666666");
            closeBtn.Click += (s, e) => Close();

            var headerGrid = new Grid { Height = 44, Cursor = Cursors.SizeAll };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(titleBar, 0);
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(titleBar);
            headerGrid.Children.Add(closeBtn);
            if (!entry.Closable) closeBtn.Visibility = Visibility.Collapsed;

            var headerBorder = new Border { BorderThickness = new Thickness(0) };
            headerBorder.Child = headerGrid;

            // ---- 内容 ----
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Padding = new Thickness(0)
            };
            scroll.Content = entry.Content;

            var dock = new DockPanel();
            DockPanel.SetDock(headerBorder, Dock.Top);
            dock.Children.Add(headerBorder);
            dock.Children.Add(scroll);
            card.Child = dock;

            // ---- 尺寸自适应 ----
            double hostW = host.ActualWidth > 0 ? host.ActualWidth : 960;
            double hostH = host.ActualHeight > 0 ? host.ActualHeight : 640;
            double maxW = Math.Max(300, hostW * 0.92);
            double maxH = Math.Max(220, hostH * 0.92);

            if (preferredWidth > 0)
            {
                card.Width = Math.Min(preferredWidth, maxW);
            }
            else
            {
                card.MinWidth = Math.Min(300, maxW);
            }
            card.MaxWidth = maxW;
            card.MaxHeight = maxH;

            var cardHost = new Grid();
            cardHost.Children.Add(shadowBorder);
            cardHost.Children.Add(card);

            Canvas.SetLeft(cardHost, 12);
            Canvas.SetTop(cardHost, 12);
            canvas.Children.Add(cardHost);

            // ---- 拖拽 ----
            headerGrid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                _dragEntry = entry;
                _dragStart = e.GetPosition(canvas);
                double l = Canvas.GetLeft(cardHost); if (double.IsNaN(l)) l = 0;
                double t = Canvas.GetTop(cardHost); if (double.IsNaN(t)) t = 0;
                _dragOrigin = new Point(l, t);
                headerGrid.CaptureMouse();
                e.Handled = true;
            };
            headerGrid.MouseMove += (s, e) =>
            {
                if (!ReferenceEquals(_dragEntry, entry)) return;
                if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(headerGrid); return; }

                Point p = e.GetPosition(canvas);
                double nx = _dragOrigin.X + (p.X - _dragStart.X);
                double ny = _dragOrigin.Y + (p.Y - _dragStart.Y);

                double cw = card.ActualWidth > 0 ? card.ActualWidth : 320;
                double ch = card.ActualHeight > 0 ? card.ActualHeight : 200;
                double maxX = Math.Max(0, canvas.ActualWidth - cw);
                double maxY = Math.Max(0, canvas.ActualHeight - ch);
                Canvas.SetLeft(cardHost, Math.Min(Math.Max(0, nx), maxX));
                Canvas.SetTop(cardHost, Math.Min(Math.Max(0, ny), maxY));
            };
            headerGrid.MouseLeftButtonUp += (s, e) => EndDrag(headerGrid);
            headerGrid.LostMouseCapture += (s, e) => { if (ReferenceEquals(_dragEntry, entry)) _dragEntry = null; };

            // ---- 内容尺寸变化时保持中心不动 ----
            // 高度交给布局即时生效：做高度动画时底部按钮会跟着移动，还会短暂闪出滚动条。
            card.SizeChanged += (s, e) =>
            {
                if (entry.CardHost == null) return;
                if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
                if (ReferenceEquals(_dragEntry, entry)) return;

                double l = Canvas.GetLeft(cardHost); if (double.IsNaN(l)) l = 0;
                double t = Canvas.GetTop(cardHost); if (double.IsNaN(t)) t = 0;

                // 宽度变化时水平方向也保持中心
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                    Canvas.SetLeft(cardHost, l - (e.NewSize.Width - e.PreviousSize.Width) / 2);

                // 高度变化时垂直方向保持中心
                Canvas.SetTop(cardHost, t - (e.NewSize.Height - e.PreviousSize.Height) / 2);
                ClampCard(entry);
            };

            entry.Layer = layer;
            entry.Canvas = canvas;
            entry.Card = card;
            entry.CardHost = cardHost;

            // 跟随宿主窗口尺寸变化：重新限制最大尺寸、保持相对中心并夹紧在窗口内
            entry.HostResizeHandler = (s, e) => OnHostSizeChanged(entry, e);
            host.SizeChanged += entry.HostResizeHandler;

            if (root != null)
            {
                root.Children.Add(layer);
                Panel.SetZIndex(layer, 5000);

                if (layer is FrameworkElement fe)
                {
                    fe.HorizontalAlignment = HorizontalAlignment.Stretch;
                    fe.VerticalAlignment = VerticalAlignment.Stretch;
                }
                if (root is Grid g)
                {
                    Grid.SetRowSpan(layer, Math.Max(1, g.RowDefinitions.Count));
                    Grid.SetColumnSpan(layer, Math.Max(1, g.ColumnDefinitions.Count));
                }

                layer.Measure(new Size(hostW, hostH));
                layer.Arrange(new Rect(0, 0, hostW, hostH));
                layer.UpdateLayout();
                if (App.IconCompatActive)
                    try { IconSupport.NormalizeTree(layer); } catch { }
                CenterCard(entry);
            }

            // ---- 打开动画：向上淡入 ----
            layer.Opacity = App.IsAnimationDisabled ? 1 : 0;
            double centerTop = Canvas.GetTop(cardHost);
            if (double.IsNaN(centerTop)) centerTop = 0;

            if (!App.IsAnimationDisabled)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Canvas.SetTop(cardHost, centerTop + 18);
                var move = new DoubleAnimation(centerTop + 18, centerTop, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = ease
                };
                move.Completed += (s, e) =>
                {
                    try
                    {
                        cardHost.BeginAnimation(Canvas.TopProperty, null);
                        Canvas.SetTop(cardHost, centerTop);
                    }
                    catch { }
                };
                cardHost.BeginAnimation(Canvas.TopProperty, move);
                layer.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            }

            host.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { FocusFirst(entry.Content); }
                catch { }
            }), DispatcherPriority.Loaded);
        }

        /// <summary>把浮动卡片限制在程序窗口内部。</summary>
        private static void OnHostSizeChanged(Entry entry, SizeChangedEventArgs e)
        {
            if (entry == null || entry.Card == null || entry.CardHost == null || entry.Canvas == null) return;

            double maxW = Math.Max(300, e.NewSize.Width * 0.92);
            double maxH = Math.Max(220, e.NewSize.Height * 0.92);
            entry.Card.MaxWidth = maxW;
            entry.Card.MaxHeight = maxH;

            if (ReferenceEquals(_dragEntry, entry))
            {
                ClampCard(entry);
                return;
            }

            double l = Canvas.GetLeft(entry.CardHost); if (double.IsNaN(l)) l = 0;
            double t = Canvas.GetTop(entry.CardHost); if (double.IsNaN(t)) t = 0;
            entry.CardHost.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(entry.CardHost, l + (e.NewSize.Width - e.PreviousSize.Width) / 2);
            Canvas.SetTop(entry.CardHost, t + (e.NewSize.Height - e.PreviousSize.Height) / 2);
            ClampCard(entry);
        }

        private static void ClampCard(Entry entry)
        {
            if (entry == null || entry.CardHost == null || entry.Canvas == null) return;

            double cw = entry.Card.ActualWidth > 0 ? entry.Card.ActualWidth : entry.CardHost.ActualWidth;
            double ch = entry.Card.ActualHeight > 0 ? entry.Card.ActualHeight : entry.CardHost.ActualHeight;
            double w = entry.Canvas.ActualWidth;
            double h = entry.Canvas.ActualHeight;

            double l = Canvas.GetLeft(entry.CardHost); if (double.IsNaN(l)) l = 0;
            double t = Canvas.GetTop(entry.CardHost); if (double.IsNaN(t)) t = 0;
            Canvas.SetLeft(entry.CardHost, Math.Min(Math.Max(0, l), Math.Max(0, w - cw)));
            Canvas.SetTop(entry.CardHost, Math.Min(Math.Max(0, t), Math.Max(0, h - ch)));
        }

        private static void CenterCard(Entry entry)        {
            if (entry.Card == null || entry.Canvas == null) return;
            entry.Canvas.UpdateLayout();

            double cw = entry.Card.ActualWidth;
            double ch = entry.Card.ActualHeight;
            if (cw <= 0) cw = entry.Card.DesiredSize.Width;
            if (ch <= 0) ch = entry.Card.DesiredSize.Height;

            double w = entry.Canvas.ActualWidth;
            double h = entry.Canvas.ActualHeight;
            if (w <= 0) w = entry.Host.ActualWidth;
            if (h <= 0) h = entry.Host.ActualHeight;

            var target = (UIElement)entry.CardHost ?? entry.Card;
            Canvas.SetLeft(target, Math.Max(0, (w - cw) / 2));
            Canvas.SetTop(target, Math.Max(0, (h - ch) / 2));
        }

        private static void EndDrag(UIElement capture)
        {
            try { capture.ReleaseMouseCapture(); } catch { }
            _dragEntry = null;
        }

        // ================================================================
        // 关闭
        // ================================================================

        public static void Close()
        {
            if (_stack.Count == 0) return;

            int index = _stack.Count - 1;
            Entry entry = _stack[index];
            _stack.RemoveAt(index);

            try
            {
                if (entry.Host != null && entry.HostResizeHandler != null)
                    entry.Host.SizeChanged -= entry.HostResizeHandler;
            }
            catch { }

            try
            {
                if (entry.Layer != null)
                {
                    var layer = entry.Layer;
                    var host = entry.CardHost;

                    if (App.IsAnimationDisabled || host == null)
                    {
                        try { (layer.Parent as Panel)?.Children.Remove(layer); } catch { }
                    }
                    else
                    {
                        // 关闭动画：向下淡出
                        double top = Canvas.GetTop(host);
                        if (double.IsNaN(top)) top = 0;

                        var move = new DoubleAnimation(top, top + 18, TimeSpan.FromMilliseconds(140))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                        };
                        host.BeginAnimation(Canvas.TopProperty, move);

                        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
                        fade.Completed += (s, e) =>
                        {
                            try { (layer.Parent as Panel)?.Children.Remove(layer); } catch { }
                        };
                        layer.BeginAnimation(UIElement.OpacityProperty, fade);
                    }
                }
            }
            catch { }

            if (entry.FallbackWindow != null)
            {
                try { entry.FallbackWindow.Close(); } catch { }
            }

            if (_stack.Count == 0) DetachKeys();

            if (entry.Frame != null) entry.Frame.Continue = false;
        }

        /// <summary>宿主窗口关闭时强制结束所有浮层，避免 DispatcherFrame 卡死。</summary>
        public static void CloseAll()
        {
            while (_stack.Count > 0)
            {
                var e = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                try
                {
                    if (e.Host != null && e.HostResizeHandler != null)
                        e.Host.SizeChanged -= e.HostResizeHandler;
                }
                catch { }
                try { (e.Layer?.Parent as Panel)?.Children.Remove(e.Layer); } catch { }
                try { e.FallbackWindow?.Close(); } catch { }
                if (e.Frame != null) e.Frame.Continue = false;
            }
            DetachKeys();
        }

        // ================================================================
        // 键盘
        // ================================================================

        private static void AttachKeys(Window host)
        {
            if (_keyHost == host) return;
            DetachKeys();
            _keyHost = host;
            _keyHost.PreviewKeyDown += _keyHandler;
        }

        private static void DetachKeys()
        {
            if (_keyHost == null) return;
            try { _keyHost.PreviewKeyDown -= _keyHandler; } catch { }
            _keyHost = null;
        }

        private static void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_stack.Count == 0) return;
            Entry top = _stack[_stack.Count - 1];

            if (e.Key == Key.Escape)
            {
                if (top.Closable) CancelTop(top);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (TryInvokeDefaultButton(top.Card))
                {
                    e.Handled = true;
                }
            }
        }

        private static void CancelTop(Entry top)
        {
            if (top.Content is FloatingDialogBase dialog)
            {
                dialog.DialogResult = false;
                return;
            }
            Close();
        }

        private static bool TryInvokeDefaultButton(DependencyObject root)
        {
            if (root == null) return false;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node is Button btn && btn.IsDefault && btn.IsVisible && btn.IsEnabled)
                {
                    btn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    return true;
                }
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
            return false;
        }

        private static void FocusFirst(FrameworkElement content)
        {
            if (content == null) return;
            if (content.IsKeyboardFocusWithin) return;

            var queue = new Queue<DependencyObject>();
            queue.Enqueue(content);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                // 只把焦点交给文本输入框，避免焦点跳到按钮导致内容被滚动、或出现焦点框
                if ((node is TextBox || node is PasswordBox) && node is Control c
                    && c.Focusable && c.IsVisible && c.IsEnabled)
                {
                    if (c.Focus()) return;
                }
                int count = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        // ================================================================
        // 无主窗口时的兜底（极少触发）
        // ================================================================

        private static void ShowInFallbackWindow(Entry entry, string title, double preferredWidth)
        {
            var win = new Window
            {
                Title = title ?? "提示",
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Content = entry.Content,
                MinWidth = 320
            };
            if (preferredWidth > 0) win.Width = preferredWidth;
            entry.FallbackWindow = win;

            var frame = new DispatcherFrame();
            entry.Frame = frame;
            _stack.Add(entry);
            win.Closed += (s, e) => { if (entry.Frame != null) entry.Frame.Continue = false; };

            try { win.Show(); } catch { }
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>
    /// 浮动界面基类：保留原 Window 弹窗的常用成员（Title / Owner / DialogResult / Close / ShowDialog），
    /// 让调用方代码无需改动即可继续使用。
    /// </summary>
    public class FloatingDialogBase : UserControl
    {
        private bool? _dialogResult;

        public Window Owner { get; set; }

        /// <summary>实际用于承载浮层的窗口（优先显式 Owner，其次视觉树上的窗口）。</summary>
        public Window OwnerWindow => Owner ?? Window.GetWindow(this) ?? Application.Current?.MainWindow;

        public string Title { get; set; } = "提示";

        /// <summary>标题栏左侧图标（Segoe Fluent Icons 字符），为空则不显示。</summary>
        public string TitleIcon { get; set; }

        /// <summary>期望宽度，0 表示按内容自动。</summary>
        public double DialogWidth { get; set; }

        public bool CanClose { get; set; } = true;

        public bool? DialogResult
        {
            get => _dialogResult;
            set
            {
                _dialogResult = value;
                FloatingOverlay.Close();
            }
        }

        public bool? ShowDialog()
        {
            FloatingOverlay.Show(Owner, this, Title, DialogWidth, CanClose, TitleIcon);
            return _dialogResult;
        }

        public void Close() => FloatingOverlay.Close();
    }
}
