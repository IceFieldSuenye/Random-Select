using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RandomDrawer
{
    public static class DrawResultRenderer
    {
        /// <summary>静态画刷统一 Freeze()：不参与动画的画刷冻结以后没有变更通知开销。</summary>
        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private static readonly Brush TextDeepGray = FrozenBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly Brush ListBorderBrush = FrozenBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        private const string CategoryColumnName = "_Category";
        private const string ColorColumnName = "_ColorHex";

        // ★ 当前活动的 GIF 播放器（用于停止旧动画，避免计时器泄漏）
        private static readonly List<GifPlayer> _activeGifPlayers = new List<GifPlayer>();

        // host.SizeChanged / Loaded 的处理器每次渲染都会捕获旧视觉树，必须显式解绑
        private static readonly List<Action> _hostCleanups = new List<Action>();

        // 大量抽取时同头像会被重复解码；缓存冻结位图，显著降低内存与 CPU 峰值
        private static readonly Dictionary<string, BitmapImage> _bitmapCache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private const int MaxBitmapCache = 64;

        public static async System.Threading.Tasks.Task RenderAsync(Grid host, List<DrawResultData> items, DrawResultDisplayMode mode,
            HighlightConfig highlight = null, PersonalizationSettings settings = null, string poolDir = "",
            BitmapSource glassBackdrop = null, FrameworkElement glassRoot = null,
            Action<int, int> progress = null, Func<bool> cancelled = null)
        {
            if (host == null) return;

            // ★ 每次渲染前停止所有旧的 GIF 计时器
            StopAllAnimations();

            // 解绑上一次渲染挂在 host 上的闭包，避免旧结果树无法回收
            foreach (var cleanup in _hostCleanups)
            {
                try { cleanup(); } catch { }
            }
            _hostCleanups.Clear();

            host.Children.Clear();
            host.ColumnDefinitions.Clear();
            host.RowDefinitions.Clear();
            host.Tag = null;
            if (items == null || items.Count == 0) return;

            switch (mode)
            {
                case DrawResultDisplayMode.Text: RenderText(host, items, highlight); progress?.Invoke(items.Count, items.Count); break;
                case DrawResultDisplayMode.List:
                    await RenderListAsync(host, items, highlight, settings, poolDir, progress, cancelled);
                    break;
                case DrawResultDisplayMode.CardGrid:
                default:
                    await RenderCardGridAsync(host, items, highlight, settings, glassBackdrop, glassRoot, progress, cancelled);
                    break;
            }

            if (Environment.GetEnvironmentVariable("DSH_MEMORY_DIAG") == "1")
            {
                try
                {
                    RuntimeLog.Info($"DIAG result render mode={mode} items={items.Count} " +
                        $"children={host.Children.Count} hostHandlers={_hostCleanups.Count} " +
                        $"managed={GC.GetTotalMemory(false) / 1024 / 1024}MB");
                }
                catch { }
            }
        }

        /// <summary>★ 停止所有活动的 GIF 计时器（页面退出或重新渲染时调用）。</summary>
        public static void StopAllAnimations()
        {
            foreach (var p in _activeGifPlayers)
            {
                try { p.Stop(); } catch { }
            }
            _activeGifPlayers.Clear();
        }

        /// <summary>未指定颜色时的默认文字色：深色模式下自动变亮。</summary>

        private static Brush DefaultTextBrush =>

            ThemeManager.IsDark ? ThemeManager.GetBrush("Fg_333333") : TextDeepGray;


        private static Brush GetObjectBrush(DrawResultData item, HighlightConfig highlight)
        {
            if (item == null) return DefaultTextBrush;

            if (!string.IsNullOrEmpty(item.ColorHex))
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(item.ColorHex);
                    return new SolidColorBrush(c);
                }
                catch { }
            }

            if (highlight != null && highlight.Enabled)
            {
                var b = highlight.GetBrushByCategory(item.Category);
                if (b != null) return b;
            }

            return DefaultTextBrush;
        }

        private static void RenderText(Grid host, List<DrawResultData> items, HighlightConfig highlight)
        {
            var item = items[0];
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };

            Brush nameBrush = GetObjectBrush(item, highlight);

            var nameTb = new TextBlock
            {
                Text = item.ObjectName ?? "",
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 42,
                FontWeight = FontWeights.SemiBold,
                Foreground = nameBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 720,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(nameTb);

            for (int i = 0; i < item.OtherFields.Count && i < item.OtherHeaders.Count; i++)
            {
                string h = item.OtherHeaders[i] ?? "";
                string v = item.OtherFields[i] ?? "";
                if (string.IsNullOrWhiteSpace(h) && string.IsNullOrWhiteSpace(v)) continue;

                var subTb = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(h) ? v : $"{h}：{v}",
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    FontSize = 18,
                    Foreground = nameBrush,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                panel.Children.Add(subTb);
            }



            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(panel, 0);
            Grid.SetColumn(panel, 0);
            host.Children.Add(panel);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            if (App.IsAnimationDisabled) { panel.Opacity = 1; } else { panel.BeginAnimation(UIElement.OpacityProperty, fade); }
        }

        /// <summary>列表分页渲染状态（用于滚动到底部时继续追加，避免一次性布局上万行）。</summary>
        private sealed class ListRenderState
        {
            public Grid Host;
            public Grid BodyGrid;
            public List<DrawResultData> Items;
            public List<string[]> RowValues;
            public double[] Widths;
            public int ColCount;
            public Brush BodyFg;
            public Brush LineBrush;
            public HighlightConfig Highlight;
            public int BuiltRows;
            public Border Spacer;
            public double RowHeight = 46;
        }

        private static ListRenderState _listState;

        /// <summary>列表模式每次渲染的条数，滚动到底部再追加下一页。</summary>
        public const int ListPageSize = 120;

        private static async System.Threading.Tasks.Task RenderListAsync(Grid host, List<DrawResultData> items, HighlightConfig highlight, PersonalizationSettings settings, string poolDir,
            Action<int, int> progress = null, Func<bool> cancelled = null)
        {
            // 收集需要展示的附加列
            var headers = new List<string>();
            foreach (var item in items)
            {
                if (item.OtherHeaders == null) continue;
                foreach (var h in item.OtherHeaders)
                {
                    if (string.IsNullOrWhiteSpace(h)) continue;
                    if (!headers.Contains(h)) headers.Add(h);
                }
            }

            var bgMode = settings?.EffectiveBackgroundMode ?? BackgroundMode.Default;
            bool isUserCustomized = bgMode != BackgroundMode.Default;
            bool dark = ThemeManager.IsDark;

            bool customLight = true;
            if (bgMode == BackgroundMode.Color)
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(settings.BackgroundColor);
                    customLight = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 >= 0.55;
                }
                catch { customLight = true; }
            }

            Brush headerFg = ThemeManager.GetBrush("Fg_6B7280");
            Brush bodyFg = ThemeManager.GetBrush(dark ? "Fg_E8E8E8" : "Fg_1F2937");
            Brush lineBrush = new SolidColorBrush(dark
                ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x16, 0x00, 0x00, 0x00));

            Color headerBase = (ThemeManager.GetBrush("Bg_F9FAFB") as SolidColorBrush)?.Color ?? Color.FromRgb(0xF9, 0xFA, 0xFB);
            byte headerAlpha = bgMode == BackgroundMode.Image
                ? (byte)0xB0
                : (bgMode == BackgroundMode.Color ? (customLight ? (byte)0x8C : (byte)0xC8) : (byte)0xFF);

            // ---------- 列宽（按内容测量，表头与数据共用） ----------
            string objectHeader = items.FirstOrDefault()?.ObjectHeader;
            if (string.IsNullOrWhiteSpace(objectHeader)) objectHeader = "抽取对象";
            var titleCells = new List<string> { "序号", objectHeader };
            titleCells.AddRange(headers);
            int colCount = titleCells.Count;
            var widths = new double[colCount];

            for (int c = 0; c < colCount; c++)
                widths[c] = MeasureTextWidth(titleCells[c], true);

            var rowValues = new List<string[]>();
            for (int r = 0; r < items.Count; r++)
            {
                // ★ 预测量阶段同样分批让出 UI 线程，避免大数量结果时动画卡死
                if (r > 0 && r % 20 == 0)
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);

                var item = items[r];
                var values = new string[colCount];
                values[0] = item.Index.ToString();
                values[1] = item.ObjectName ?? "";
                for (int i = 0; i < headers.Count; i++)
                {
                    string value = "";
                    int idx = item.OtherHeaders != null ? item.OtherHeaders.IndexOf(headers[i]) : -1;
                    if (idx >= 0 && item.OtherFields != null && idx < item.OtherFields.Count)
                        value = item.OtherFields[idx] ?? "";
                    values[i + 2] = value;
                }
                rowValues.Add(values);

                for (int c = 0; c < colCount; c++)
                    widths[c] = Math.Max(widths[c], MeasureTextWidth(values[c], false));
            }

            const double cellPadding = 36;
            for (int c = 0; c < colCount; c++)
                widths[c] = Math.Max(c == 0 ? 76 : 64, widths[c] + cellPadding);

            double tableWidth = widths.Sum();

            // ---------- 冻结的表头 ----------
            var headerGrid = new Grid { Width = tableWidth, Height = 46 };
            for (int c = 0; c < colCount; c++)
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[c]) });
            headerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });

            var headerBg = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(headerAlpha, headerBase.R, headerBase.G, headerBase.B)),
                CornerRadius = new CornerRadius(8, 8, 0, 0)
            };
            Grid.SetRow(headerBg, 0);
            Grid.SetColumnSpan(headerBg, colCount);
            headerGrid.Children.Add(headerBg);

            for (int c = 0; c < colCount; c++)
                AddListCell(headerGrid, 0, c, titleCells[c], headerFg, true);

            var headerLine = new Border { Height = 1, Background = lineBrush, VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetRow(headerLine, 0);
            Grid.SetColumnSpan(headerLine, colCount);
            headerGrid.Children.Add(headerLine);

            var headerScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                Focusable = false,
                Content = headerGrid
            };

            // ---------- 数据行 ----------
            var bodyGrid = new Grid { Width = tableWidth };
            for (int c = 0; c < colCount; c++)
                bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[c]) });

            // 先只渲染第一页：一次性建上万行会让整个界面（包括加载动画）卡住
            var state = new ListRenderState
            {
                Host = host,
                BodyGrid = bodyGrid,
                Items = items,
                RowValues = rowValues,
                Widths = widths,
                ColCount = colCount,
                BodyFg = bodyFg,
                LineBrush = lineBrush,
                Highlight = highlight
            };
            _listState = state;

            var bodyScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                PanningMode = PanningMode.VerticalOnly,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Content = bodyGrid
            };

            // 页面通过 host.Tag 拿到列表的 ScrollViewer，用整高的贴边滚动条控制它
            host.Tag = bodyScroll;

            // 一次性（分批让出 UI）渲染全部行：加载遮罩一直显示到渲染结束

            // 表头跟随内容横向滚动
            bodyScroll.ScrollChanged += (s, e) =>
            {
                try { headerScroll.ScrollToHorizontalOffset(bodyScroll.HorizontalOffset); }
                catch { }
            };

            var brushHeaderForBar = new SolidColorBrush(Color.FromArgb(headerAlpha, headerBase.R, headerBase.G, headerBase.B));

            // 表头 + 内容 + 贯穿两者的纵向滚动条
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(headerScroll, 0);
            Grid.SetColumn(headerScroll, 0);
            Grid.SetRow(bodyScroll, 1);
            Grid.SetColumn(bodyScroll, 0);
            contentGrid.Children.Add(headerScroll);
            contentGrid.Children.Add(bodyScroll);

            // 表头右侧填补（滚动条未出现时保持表头连续）
            var headerFiller = new Border
            {
                Background = brushHeaderForBar,
                CornerRadius = new CornerRadius(0, 8, 0, 0)
            };
            Grid.SetRow(headerFiller, 0);
            Grid.SetColumn(headerFiller, 1);
            contentGrid.Children.Add(headerFiller);

            // 滚动条只占内容区，不压住表头；表头右侧由 headerFiller 填成表头色
            var vBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Width = 12,
                Margin = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Minimum = 0,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(vBar, 10);
            Grid.SetRow(vBar, 1);
            Grid.SetRowSpan(vBar, 1);
            Grid.SetColumn(vBar, 1);
            contentGrid.Children.Add(vBar);

            bodyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

            bodyScroll.ScrollChanged += (s, e) =>
            {
                try
                {
                    double max = Math.Max(0, bodyScroll.ExtentHeight - bodyScroll.ViewportHeight);
                    vBar.Maximum = max;
                    vBar.ViewportSize = Math.Max(1, bodyScroll.ViewportHeight);
                    vBar.LargeChange = Math.Max(1, bodyScroll.ViewportHeight);
                    vBar.SmallChange = 32;

                    if (Math.Abs(vBar.Value - bodyScroll.VerticalOffset) > 0.5)
                        vBar.Value = bodyScroll.VerticalOffset;

                    vBar.Visibility = max > 1 ? Visibility.Visible : Visibility.Collapsed;
                }
                catch { }
            };

            vBar.ValueChanged += (s, e) =>
            {
                try
                {
                    if (Math.Abs(bodyScroll.VerticalOffset - vBar.Value) > 0.5)
                        bodyScroll.ScrollToVerticalOffset(vBar.Value);
                }
                catch { }
            };

            // ★ 自适应缩放：内容较少时整体放大到可用区域内的最大值（不出现滚动条）。
            //   注意：行是挂载后分批建的，所以这里的初始尺寸只是表头，
            //   每批建完后再重新测量（RecomputeAutoScale）才能得到真实尺寸。
            contentGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size baseSize = contentGrid.DesiredSize;
            contentGrid.LayoutTransform = new ScaleTransform(1, 1);

            void RecomputeAutoScale()
            {
                try
                {
                    contentGrid.LayoutTransform = new ScaleTransform(1, 1);
                    contentGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    baseSize = contentGrid.DesiredSize;
                    ApplyAutoScale();
                }
                catch { }
            }

            void ApplyAutoScale()
            {
                try
                {
                    if (baseSize.Width < 2 || baseSize.Height < 2) return;

                    double availW = host.ActualWidth - 64;
                    double availH = host.ActualHeight - 28;
                    if (availW < 2 || availH < 2) return;

                    double scale = Math.Min(availW / baseSize.Width, availH / baseSize.Height);
                    if (scale > 2.2) scale = 2.2;
                    if (scale < 1) scale = 1;

                    if (contentGrid.LayoutTransform is ScaleTransform current
                        && Math.Abs(current.ScaleX - scale) < 0.01)
                        return;

                    contentGrid.LayoutTransform = new ScaleTransform(scale, scale);
                }
                catch { }
            }

            // ---------- 外框：圆角 + 半透明 ----------
            Brush borderBg;
            if (!isUserCustomized)
            {
                borderBg = ThemeManager.GetBrush("Bg_FFFFFF");
            }
            else if (bgMode == BackgroundMode.Color)
            {
                borderBg = new SolidColorBrush(Color.FromArgb(customLight ? (byte)0x59 : (byte)0x99, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                borderBg = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            }

            FrameworkElement content = contentGrid;

            // 图片背景：局部毛玻璃
            if (bgMode == BackgroundMode.Image)
            {
                string bgPath = settings.BackgroundImage;
                if (!string.IsNullOrEmpty(bgPath) && !Path.IsPathRooted(bgPath) && !string.IsNullOrEmpty(poolDir))
                {
                    string combined = Path.Combine(poolDir, bgPath);
                    if (File.Exists(combined)) bgPath = combined;
                }

                if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
                {
                    var bmp = LoadBitmap(bgPath);
                    if (bmp != null)
                    {
                        var bgRect = new System.Windows.Shapes.Rectangle
                        {
                            Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
                            Effect = new BlurEffect { Radius = 20, KernelType = KernelType.Gaussian },
                            Margin = new Thickness(-20)
                        };

                        var maskRect = new System.Windows.Shapes.Rectangle
                        {
                            Fill = dark
                                ? new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00))
                                : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
                        };

                        var bgGrid = new Grid { ClipToBounds = true };
                        bgGrid.Children.Add(bgRect);
                        bgGrid.Children.Add(maskRect);

                        var layered = new Grid();
                        layered.Children.Add(bgGrid);
                        layered.Children.Add(contentGrid);
                        content = layered;
                    }
                }
            }

            var border = new Border
            {
                Background = borderBg,
                BorderBrush = new SolidColorBrush(dark
                    ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x26, 0x00, 0x00, 0x00)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Opacity = 0,
                Child = content,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 圆角裁剪：让内容四角真正跟着外框圆角
            border.SizeChanged += (s, e) =>
            {
                try
                {
                    if (border.ActualWidth <= 0 || border.ActualHeight <= 0) return;
                    border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), 8, 8);
                }
                catch { }
            };

            // 外框随内容自适应大小；超过可用区域时封顶并交给滚动条
            void UpdateBorderLimit()
            {
                try
                {
                    double availW = host.ActualWidth;
                    double availH = host.ActualHeight;
                    if (availW > 0) border.MaxWidth = Math.Max(160, availW - 24);
                    if (availH > 0) border.MaxHeight = Math.Max(160, availH - 16);
                }
                catch { }
            }

            UpdateBorderLimit();
            ApplyAutoScale();
            SizeChangedEventHandler hostSizeHandler = (s, e) => { UpdateBorderLimit(); ApplyAutoScale(); };
            RoutedEventHandler hostLoadedHandler = (s, e) =>
            {
                try { host.Dispatcher.BeginInvoke(new Action(() => { UpdateBorderLimit(); ApplyAutoScale(); }), DispatcherPriority.Background); } catch { }
            };
            host.SizeChanged += hostSizeHandler;
            host.Loaded += hostLoadedHandler;
            _hostCleanups.Add(() =>
            {
                host.SizeChanged -= hostSizeHandler;
                host.Loaded -= hostLoadedHandler;
            });

            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(border, 0);
            Grid.SetColumn(border, 0);
            host.Children.Add(border);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            if (App.IsAnimationDisabled) { border.Opacity = 1; } else { border.BeginAnimation(UIElement.OpacityProperty, fade); }

            // 先挂到界面上，再逐批建行：这样每批的 UpdateLayout 才是真正生效的增量排版，
            // 不会出现「进度到 100% 后再一次性排版几秒」的卡死。
            await BuildListRowsAsync(state, 0, rowValues.Count, progress, cancelled);

            // 行建完之后重新量一次真实尺寸：行多时不放大（保持 1:1），行少时才自适应放大
            RecomputeAutoScale();
        }

        private static double MeasureTextWidth(string text, bool isHeader)
        {
            try
            {
                var tb = new TextBlock
                {
                    Text = text ?? "",
                    FontSize = isHeader ? 13 : 14,
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    TextWrapping = TextWrapping.NoWrap
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return tb.DesiredSize.Width;
            }
            catch { return 0; }
        }

        /// <summary>把 [from, to) 区间的行追加到列表里（分批让出 UI 线程）。</summary>
        private static async System.Threading.Tasks.Task BuildListRowsAsync(ListRenderState st, int from, int to,
            Action<int, int> progress = null, Func<bool> cancelled = null)
        {
            if (st == null || st.BodyGrid == null) return;

            int total = st.RowValues?.Count ?? 0;
            to = Math.Min(to, total);

            // 流畅优先：每 20 行「排版 + 让出」；加载速度优先：每 200 行才让出、且不逐批排版
            bool smoothMode = App.RenderPreference != App.RenderPreferenceKind.Fast;
            int listBatchSize = smoothMode ? 20 : 200;

            for (int r = from; r < to; r++)
            {
                // 只有「流畅优先」才分批：排版 + 让出 UI + 上报进度；
                // 「加载速度优先」完全不打断，一口气把全部行建完（最后统一排版）。
                if (smoothMode && r > from && (r - from) % listBatchSize == 0)
                {
                    // 把布局也算进进度里，避免所有行建完后再一次性布局造成卡死
                    try { st.Host?.UpdateLayout(); } catch { }
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    progress?.Invoke(r, total);
                    if (cancelled != null && cancelled()) return;
                }

                var item = st.Items[r];
                var values = st.RowValues[r];
                int row = r;

                st.BodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Brush rowBrush = null;
                if (!string.IsNullOrEmpty(item.ColorHex))
                {
                    try { rowBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.ColorHex)); } catch { }
                }
                if (rowBrush == null && st.Highlight != null && st.Highlight.Enabled)
                    rowBrush = st.Highlight.GetBrushByCategory(item.Category ?? "");

                for (int c = 0; c < st.ColCount; c++)
                    AddListCell(st.BodyGrid, row, c, values[c], rowBrush ?? st.BodyFg, false);

                var line = new Border { Height = 1, Background = st.LineBrush, VerticalAlignment = VerticalAlignment.Bottom };
                Grid.SetRow(line, row);
                Grid.SetColumnSpan(line, st.ColCount);
                st.BodyGrid.Children.Add(line);

                st.BuiltRows = row + 1;
            }

            try { st.Host?.UpdateLayout(); } catch { }
            progress?.Invoke(Math.Min(to, total), total);
        }

        /// <summary>按「还剩多少行没渲染」撑高占位条，让滚动条长度反映全部内容。</summary>
        private static void UpdateListSpacer(ListRenderState st, int total)
        {
            try
            {
                if (st?.Spacer == null) return;

                // 用已渲染行的平均高度估算，没测量到就用默认值
                if (st.BodyGrid != null && st.BuiltRows > 0 && st.BodyGrid.ActualHeight > 0)
                {
                    double avg = st.BodyGrid.ActualHeight / st.BuiltRows;
                    if (avg > 8 && avg < 400) st.RowHeight = avg;
                }

                int remain = Math.Max(0, total - st.BuiltRows);
                st.Spacer.Height = remain * st.RowHeight;
            }
            catch { }
        }

        private static void AddListCell(Grid table, int row, int column, string text, Brush foreground, bool isHeader)
        {
            var cell = new TextBlock
            {
                Text = text ?? "",
                FontSize = isHeader ? 13 : 14,
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(18, isHeader ? 0 : 13, 18, isHeader ? 0 : 13),
                TextWrapping = TextWrapping.NoWrap,
                Foreground = foreground
            };

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            table.Children.Add(cell);
        }
        private static async System.Threading.Tasks.Task RenderCardGridAsync(Grid host, List<DrawResultData> items,
            HighlightConfig highlight, PersonalizationSettings settings, BitmapSource glassBackdrop, FrameworkElement glassRoot,
            Action<int, int> progress = null, Func<bool> cancelled = null)
        {
            int count = items.Count;
            int rows, cols;
            int[] rowCounts;

            if (count == 1) { rows = 1; cols = 1; rowCounts = new[] { 1 }; }
            else if (count <= 5) { rows = 1; cols = count; rowCounts = new[] { count }; }
            else if (count <= 8)
            {
                rows = 2;
                int up = (count + 1) / 2;
                int down = count - up;
                rowCounts = new[] { up, down };
                cols = up;
            }
            else
            {
                rows = (count + 4) / 5;
                rowCounts = new int[rows];
                for (int r = 0; r < rows; r++) { int remain = count - r * 5; rowCounts[r] = Math.Min(5, remain); }
                cols = 5;
            }

            var innerGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };
            for (int r = 0; r < rows; r++) innerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < cols; c++) innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool smoothMode = App.RenderPreference != App.RenderPreferenceKind.Fast;
            int cardBatchSize = smoothMode ? 6 : 30;

            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                int rowCount = rowCounts[r];
                if (rowCount <= 0) continue;
                int startCol = (cols - rowCount) / 2;
                for (int c = 0; c < rowCount; c++)
                {
                    if (idx >= items.Count) break;
                    // ★ 头像/立绘解码可能较慢，定期让出 UI 线程
                    // 加载速度优先：不打断，一口气建完全部卡片
                    if (smoothMode && idx > 0 && idx % cardBatchSize == 0)
                    {
                        try { host?.UpdateLayout(); } catch { }
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                        progress?.Invoke(idx, items.Count);
                        if (cancelled != null && cancelled()) return;
                    }

                    var it = items[idx++];
                    var card = BuildCard(it, highlight, settings, glassBackdrop, glassRoot);
                    Grid.SetRow(card, r);
                    Grid.SetColumn(card, startCol + c);
                    innerGrid.Children.Add(card);
                }
            }

            // 卡片区只限制宽度（换行由宽度决定），高度交给外层 ScrollViewer 滚动
            void UpdateGridLimit()
            {
                try
                {
                    double availW = host.ActualWidth;
                    if (availW > 0) innerGrid.MaxWidth = Math.Max(160, availW - 24);
                }
                catch { }
            }

            UpdateGridLimit();
            SizeChangedEventHandler gridSizeHandler = (s, e) => UpdateGridLimit();
            host.SizeChanged += gridSizeHandler;
            _hostCleanups.Add(() => host.SizeChanged -= gridSizeHandler);

            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(innerGrid, 0);
            Grid.SetColumn(innerGrid, 0);
            host.Children.Add(innerGrid);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            if (App.IsAnimationDisabled) { innerGrid.Opacity = 1; } else { innerGrid.BeginAnimation(UIElement.OpacityProperty, fade); }
        }

        private static UIElement BuildCard(DrawResultData item, HighlightConfig highlight,
            PersonalizationSettings settings, BitmapSource glassBackdrop, FrameworkElement glassRoot)
        {
            var outer = new StackPanel
            {
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Brush nameBrush = ThemeManager.IsDark ? ThemeManager.GetBrush("Fg_333333") : TextDeepGray;
            Brush cardBorderBrush = ThemeManager.GetBrush("Bd_E5E7EB");
            double cardBorderThickness = 1;

            if (!string.IsNullOrEmpty(item.ColorHex))
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(item.ColorHex);
                    var b = new SolidColorBrush(c);
                    nameBrush = b;
                    cardBorderBrush = b;
                    cardBorderThickness = 2;
                }
                catch { }
            }
            else if (highlight != null && highlight.Enabled)
            {
                var b = highlight.GetBrushByCategory(item.Category);
                if (b != null)
                {
                    nameBrush = b;
                    cardBorderBrush = b;
                    cardBorderThickness = 2;
                }
            }

            var bgMode = settings?.EffectiveBackgroundMode ?? BackgroundMode.Default;
            Brush cardBackground = ThemeManager.GetBrush("Bg_FFFFFF");
            bool imageBackground = bgMode == BackgroundMode.Image;

            if (bgMode == BackgroundMode.Color && settings != null)
            {
                try
                {
                    Color c = (Color)ColorConverter.ConvertFromString(settings.BackgroundColor);
                    bool light = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 >= 0.55;
                    cardBackground = new SolidColorBrush(light
                        ? Color.FromArgb(0x38, 0x00, 0x00, 0x00)
                        : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
                }
                catch { }
            }
            else if (imageBackground && glassBackdrop == null)
            {
                // 图片背景但没有可用模糊图时，使用半透明深色底保证默认头像可见
                cardBackground = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00));
            }

            var cardBorder = new Border
            {
                Width = 120,
                Height = 120,
                CornerRadius = new CornerRadius(16),
                Background = cardBackground,
                BorderBrush = cardBorderBrush,
                BorderThickness = new Thickness(cardBorderThickness),
                ClipToBounds = true
            };

            string avatarPath = item.AvatarPath ?? "";
            bool hasAvatar = !string.IsNullOrEmpty(avatarPath) && File.Exists(avatarPath);
            bool avatarApplied = false;

            if (hasAvatar)
            {
                string ext = Path.GetExtension(avatarPath).ToLowerInvariant();
                if (ext == ".gif")
                {
                    var player = new GifPlayer(avatarPath);
                    if (player.HasFrames)
                    {
                        player.Host = cardBorder;
                        _activeGifPlayers.Add(player);
                        cardBorder.Background = player.Brush;
                        avatarApplied = true;
                    }
                    else
                    {
                        player.Stop();
                    }
                }

                if (!avatarApplied)
                {
                    var bmp = LoadBitmap(avatarPath, 240);
                    if (bmp != null)
                    {
                        cardBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                        avatarApplied = true;
                    }
                }
            }

            if (!avatarApplied)
            {
                bool useGlass = imageBackground && glassBackdrop != null && glassRoot != null;
                if (useGlass)
                {
                    var glassBrush = new ImageBrush(glassBackdrop)
                    {
                        Stretch = Stretch.Fill,
                        ViewboxUnits = BrushMappingMode.Absolute
                    };
                    cardBorder.Background = glassBrush;
                    cardBorder.LayoutUpdated += (s, e) =>
                        UpdateGlassCrop(cardBorder, glassBrush, glassBackdrop, glassRoot);
                }

                var fallbackGrid = new Grid();
                if (useGlass)
                {
                    fallbackGrid.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00)),
                        CornerRadius = new CornerRadius(16),
                        IsHitTestVisible = false
                    });
                }
                fallbackGrid.Children.Add(new TextBlock
                {
                    // 报告卡片可能在未连接到窗口的视觉树上渲染（不会触发 Loaded），
                    // 所以这里直接取已解析的图标文本与字体，避免出现方框。
                    Text = IconSupport.Resolve("\uE77B"),
                    FontFamily = App.IconFontFamily,
                    FontSize = 56,
                    Foreground = useGlass
                        ? Brushes.White
                        : ThemeManager.GetBrush("Fg_D1D5DB"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                cardBorder.Child = fallbackGrid;
            }

            outer.Children.Add(cardBorder);

            var name = new TextBlock
            {
                Text = item.ObjectName ?? "",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = nameBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 140,
                Margin = new Thickness(0, 8, 0, 0)
            };
            outer.Children.Add(name);

            return outer;
        }

        private const double CardGlassPad = 24;

        private static void UpdateGlassCrop(Border card, ImageBrush brush, BitmapSource bitmap, FrameworkElement root)
        {
            try
            {
                if (card == null || brush == null || bitmap == null || root == null) return;
                if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return;

                Point point = card.TranslatePoint(new Point(0, 0), root);
                double sourceWidth = Math.Max(1, root.ActualWidth + CardGlassPad * 2);
                double sourceHeight = Math.Max(1, root.ActualHeight + CardGlassPad * 2);
                double sx = bitmap.Width / sourceWidth;
                double sy = bitmap.Height / sourceHeight;

                brush.Viewbox = new Rect(
                    (point.X + CardGlassPad) * sx,
                    (point.Y + CardGlassPad) * sy,
                    card.ActualWidth * sx,
                    card.ActualHeight * sy);
            }
            catch { }
        }

        private static BitmapImage LoadBitmap(string path, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            string cacheKey = path + "|" + decodeWidth + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            bool cacheable = decodeWidth > 0;
            if (cacheable)
            {
                lock (_bitmapCache)
                {
                    if (_bitmapCache.TryGetValue(cacheKey, out var cached)) return cached;
                }
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                if (cacheable) CacheBitmap(cacheKey, bmp);
                return bmp;
            }
            catch { }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }
                bmp.Freeze();
                if (cacheable) CacheBitmap(cacheKey, bmp);
                return bmp;
            }
            catch { }

            return null;
        }

        private static void CacheBitmap(string key, BitmapImage bitmap)
        {
            if (bitmap == null) return;
            lock (_bitmapCache)
            {
                if (_bitmapCache.Count >= MaxBitmapCache) _bitmapCache.Clear();
                _bitmapCache[key] = bitmap;
            }
        }
    }

    /// <summary>
    /// ★ GIF 播放器。
    /// </summary>
    /// <summary>
    /// GIF 播放器：所有实例共用一个 DispatcherTimer，避免多计时器拖慢 UI。
    /// ★ 3.14.1：解码结果按文件共享（报告里同一个头像可能出现几十行，不能每行都解一遍）；
    ///   可见性判定按 300 ms 节流并缓存滚动容器，否则上千个播放器每 33 ms 各走一次可视树会拖死页面。
    /// </summary>
    public class GifPlayer
    {
        private static readonly List<GifPlayer> _players = new List<GifPlayer>();
        private static DispatcherTimer _sharedTimer;

        /// <summary>同一张 GIF 的解码帧缓存（键 = 路径 + 修改时间，文件换了会自动失效）。</summary>
        private static readonly Dictionary<string, IReadOnlyList<BitmapFrame>> _frameCache =
            new Dictionary<string, IReadOnlyList<BitmapFrame>>(StringComparer.OrdinalIgnoreCase);
        private const int MaxCachedGifFiles = 32;

        /// <summary>可见性复检间隔：滚动时最多每 300 ms 判一次，够快也不至于拖慢界面。</summary>
        public const int HostVisibilityCheckMs = 300;

        public ImageBrush Brush { get; }

        private FrameworkElement _host;

        /// <summary>承载该 GIF 的元素；用于判断是否在滚动视口内。</summary>
        public FrameworkElement Host
        {
            get => _host;
            set
            {
                _host = value;
                _scrollHost = null;
                _nextVisibilityCheck = DateTime.MinValue;   // 换了宿主：立刻重新判一次
            }
        }

        private IReadOnlyList<BitmapFrame> _frames;
        private int _frameIndex;
        private DateTime _nextDue;

        // 可见性判定缓存（避免每帧都遍历可视树）
        private ScrollViewer _scrollHost;
        private bool _hostVisible = true;
        private DateTime _nextVisibilityCheck;

        public bool HasFrames => _frames != null && _frames.Count > 0;

        public GifPlayer(string path)
        {
            Brush = new ImageBrush { Stretch = Stretch.UniformToFill };

            try
            {
                _frames = GetFrames(path);
                if (_frames == null || _frames.Count == 0)
                {
                    _frames = null;
                    return;
                }

                _frameIndex = 0;
                Brush.ImageSource = _frames[0];

                if (_frames.Count > 1)
                {
                    _nextDue = DateTime.UtcNow + GetFrameDelay(_frames[0]);
                    Register(this);
                }
            }
            catch
            {
                _frames = null;
            }
        }

        /// <summary>按「路径 + 修改时间」缓存解码帧；同一张 GIF 只解码一次。</summary>
        private static IReadOnlyList<BitmapFrame> GetFrames(string path)
        {
            string key = path;
            try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
            catch { }

            lock (_frameCache)
            {
                if (_frameCache.TryGetValue(key, out var cached)) return cached;
            }

            IReadOnlyList<BitmapFrame> frames = null;
            try
            {
                var decoder = new GifBitmapDecoder(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count > 0)
                {
                    var list = new List<BitmapFrame>(decoder.Frames.Count);
                    foreach (var frame in decoder.Frames)
                    {
                        if (frame.CanFreeze)
                        {
                            try { frame.Freeze(); } catch { }
                        }
                        list.Add(frame);
                    }
                    frames = list;
                }
            }
            catch { frames = null; }

            lock (_frameCache)
            {
                if (_frameCache.Count >= MaxCachedGifFiles) _frameCache.Clear();
                _frameCache[key] = frames;   // 解码失败也缓存（避免反复重试坏文件）
            }
            return frames;
        }

        /// <summary>停止播放（从共享计时器中移除）。</summary>
        public void Stop() => Unregister(this);

        private void Advance()
        {
            if (_frames == null || _frames.Count <= 1) return;

            _frameIndex = (_frameIndex + 1) % _frames.Count;
            var frame = _frames[_frameIndex];

            try { Brush.ImageSource = frame; }
            catch { }

            _nextDue = DateTime.UtcNow + GetFrameDelay(frame);
        }

        /// <summary>只有位于 ScrollViewer 可见区域内的 GIF 才推进帧（判定结果按 HostVisibilityCheckMs 缓存）。</summary>
        private bool IsHostVisible()
        {
            var host = _host;
            if (host == null) return true;

            var now = DateTime.UtcNow;
            if (now < _nextVisibilityCheck) return _hostVisible;

            _nextVisibilityCheck = now.AddMilliseconds(HostVisibilityCheckMs);
            _hostVisible = ComputeHostVisible(host);
            return _hostVisible;
        }

        private bool ComputeHostVisible(FrameworkElement host)
        {
            try
            {
                if (!host.IsVisible) return false;
                if (host.ActualHeight <= 0 && host.ActualWidth <= 0) return false;

                var sv = _scrollHost;
                if (sv == null)
                {
                    DependencyObject parent = host;
                    while (parent != null && !(parent is ScrollViewer))
                        parent = VisualTreeHelper.GetParent(parent);
                    sv = parent as ScrollViewer;
                    _scrollHost = sv;   // 缓存：下次不用再走一遍可视树
                }

                if (sv != null)
                {
                    Point pt = host.TransformToAncestor(sv).Transform(new Point(0, 0));
                    if (pt.Y + host.ActualHeight < 0 || pt.Y > sv.ViewportHeight) return false;
                    if (pt.X + host.ActualWidth < 0 || pt.X > sv.ViewportWidth) return false;
                }

                return true;
            }
            catch { return true; }
        }

        private static void Register(GifPlayer player)
        {
            if (_players.Contains(player)) return;

            _players.Add(player);
            EnsureTimer();
        }

        private static void Unregister(GifPlayer player)
        {
            _players.Remove(player);

            if (_players.Count != 0) return;

            try
            {
                if (_sharedTimer != null)
                {
                    _sharedTimer.Stop();
                    _sharedTimer = null;
                }
            }
            catch { }
        }

        private static void EnsureTimer()
        {
            if (_sharedTimer != null) return;

            _sharedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _sharedTimer.Tick += (s, e) =>
            {
                var now = DateTime.UtcNow;

                for (int i = _players.Count - 1; i >= 0; i--)
                {
                    var player = _players[i];
                    if (now < player._nextDue) continue;          // 还没到这一帧的时间，连可见性都不用判
                    if (player.IsHostVisible()) player.Advance();
                }
            };

            _sharedTimer.Start();
        }

        private static TimeSpan GetFrameDelay(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata meta)
                {
                    var delayObj = meta.GetQuery("/grctlext/Delay");
                    if (delayObj != null)
                    {
                        int delay = Convert.ToInt32(delayObj);
                        int ms = delay * 10;
                        if (ms < 20) ms = 100;
                        return TimeSpan.FromMilliseconds(ms);
                    }
                }
            }
            catch { }

            return TimeSpan.FromMilliseconds(100);
        }
    }
}
