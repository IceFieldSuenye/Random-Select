using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using PathFigure = System.Windows.Media.PathFigure;
using LineSegment = System.Windows.Media.LineSegment;
using PathGeometry = System.Windows.Media.PathGeometry;
using System.Windows.Threading;

namespace RandomDrawer
{
    /// <summary>「查看抽取报告」页面：整理卡池历史记录并给出整体结果。</summary>
    public partial class GachaReportPage_UC : UserControl, IPageAnimation
    {
        private readonly Random _random = new Random();
        private DispatcherTimer _loadingTimer;
        private DateTime _loadingStart;
        private double _loadingDurationMs = 1200;
        private double _loadingProgress;
        private bool _analyzing;
        private string _reportPoolName = "";

        public GachaReportPage_UC()
        {
            InitializeComponent();
            IsVisibleChanged += (s, e) => { if (IsVisible) ResetToIntro(); };

            if (ReportScroll != null) ReportScroll.ScrollChanged += (s, e) => UpdatePageScrollBar();
        }

        private void PageScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (ReportScroll == null) return;
                if (Math.Abs(ReportScroll.VerticalOffset - PageScrollBar.Value) > 0.5)
                    ReportScroll.ScrollToVerticalOffset(PageScrollBar.Value);
            }
            catch { }
        }

        private void UpdatePageScrollBar()
        {
            try
            {
                if (PageScrollBar == null || ReportScroll == null) return;

                double max = Math.Max(0, ReportScroll.ExtentHeight - ReportScroll.ViewportHeight);
                PageScrollBar.Maximum = max;
                PageScrollBar.ViewportSize = Math.Max(1, ReportScroll.ViewportHeight);
                PageScrollBar.LargeChange = Math.Max(1, ReportScroll.ViewportHeight);
                PageScrollBar.SmallChange = 48;

                if (Math.Abs(PageScrollBar.Value - ReportScroll.VerticalOffset) > 0.5)
                    PageScrollBar.Value = ReportScroll.VerticalOffset;

                PageScrollBar.Visibility = (max > 1 && ReportPanel.Visibility == Visibility.Visible)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch { }
        }

        // ================================================================
        // 页面进入 / 退出动画
        // ================================================================
        // 进场动画期间先不渲染报告，等动画播完再显示（优先保证动画流畅）
        private bool _enterAnimating;
        private (GachaReportData Data, string PoolDir, System.Diagnostics.Stopwatch Timer)? _pendingReport;

        public void PlayEnterAnimation(Action onComplete)
        {
            try
            {
                _enterAnimating = true;

                Root.BeginAnimation(OpacityProperty, null);
                RootTransform.BeginAnimation(TranslateTransform.YProperty, null);

                if (App.IsAnimationDisabled)
                {
                    Root.Opacity = 1;
                    RootTransform.Y = 0;
                    FinishEnter(onComplete);
                    return;
                }

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var slide = new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Root.Opacity = 0;
                RootTransform.Y = 36;
                Root.BeginAnimation(OpacityProperty, fade);
                RootTransform.BeginAnimation(TranslateTransform.YProperty, slide);

                // 动画结束（或动画被禁用）后再收尾
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(330) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    FinishEnter(onComplete);
                };
                timer.Start();
            }
            catch { try { onComplete?.Invoke(); } catch { } }
        }

        /// <summary>进场动画结束：把动画期间算好的报告渲染出来。</summary>
        private void FinishEnter(Action onComplete)
        {
            _enterAnimating = false;

            var pending = _pendingReport;
            _pendingReport = null;
            if (pending.HasValue)
                ApplyReport(pending.Value.Data, pending.Value.PoolDir, pending.Value.Timer);

            onComplete?.Invoke();
        }

        public void PlayExitAnimation(Action onComplete)
        {
            StopLoading();
            try { StopGifPlayers(); } catch { }
            try { if (EntriesHost != null) EntriesHost.Children.Clear(); } catch { }
            onComplete?.Invoke();
        }

        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);

        // ================================================================
        // 初始状态（有缓存则直接进入报告）
        // ================================================================
        private void ResetToIntro()
        {
            StopLoading();
            _analyzing = false;

            LoadingPanel.Visibility = Visibility.Collapsed;
            ReportPanel.Visibility = Visibility.Collapsed;
            ReportPanel.Opacity = 0;
            ReportTransform.Y = 40;

            string poolDir = App.LastSelectedPoolDir ?? "";
            string poolName = string.IsNullOrEmpty(poolDir) ? "" : Path.GetFileName(poolDir);
            if (string.IsNullOrEmpty(poolName)) poolName = App.LastSelectedPoolName ?? "";

            TxtIntroPool.Text = string.IsNullOrEmpty(poolName) ? "" : $"当前卡池：{poolName}";

            if (ReportCache.HasCache(poolDir))
            {
                // 历史记录自上次查询后没有变化：跳过入口页与加载动画，直接展示
                IntroPanel.Visibility = Visibility.Collapsed;
                LoadingPanel.Visibility = Visibility.Collapsed;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { LoadAndShowReport(); } catch { }
                }), DispatcherPriority.Loaded);

                return;
            }

            IntroPanel.Visibility = Visibility.Visible;
            IntroPanel.Opacity = 1;
        }

        // ================================================================
        // 加载动画
        // ================================================================
        private void BtnView_Click(object sender, RoutedEventArgs e)
        {
            if (_analyzing) return;

            IntroPanel.Visibility = Visibility.Collapsed;
            StartLoading();
        }

        /// <summary>当前卡池是否存在可统计的记录。</summary>
        private bool HasAnyData()
        {
            try
            {
                string poolDir = App.LastSelectedPoolDir ?? "";
                string poolName = string.IsNullOrEmpty(poolDir) ? "" : Path.GetFileName(poolDir);
                var data = GachaReportAnalyzer.Analyze(poolDir, poolName);
                return data != null && data.HasData && data.Entries.Count > 0;
            }
            catch { return false; }
        }

        private void StartLoading()
        {
            _analyzing = true;
            ShowReportLoading(true);
            StartProgressAnimation();
            LoadAndShowReport();
        }

        /// <summary>进度条随机增长（最多到 99%），加载完成时再跳到 100%。</summary>
        private void StartProgressAnimation()
        {
            StopLoading();

            _loadingProgress = 0;
            _loadingDurationMs = 800 + _random.NextDouble() * 1200;
            _loadingStart = DateTime.UtcNow;
            LoadingBar.Value = 0;
            TxtLoadingPercent.Text = "0%";

            _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _loadingTimer.Tick += LoadingTimer_Tick;
            _loadingTimer.Start();
        }

        private void LoadingTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 随机增长到 99% 为止，真实完成由 ApplyReport 置为 100%
                double step = 1 + _random.NextDouble() * 3.5;
                _loadingProgress = Math.Min(99, _loadingProgress + step);

                LoadingBar.Value = _loadingProgress;
                TxtLoadingPercent.Text = $"{_loadingProgress:0}%";
            }
            catch { StopLoading(); }
        }

        private void ShowReportLoading(bool show)
        {
            try
            {
                if (LoadingPanel != null)
                    LoadingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void StopLoading()
        {
            try
            {
                if (_loadingTimer != null)
                {
                    _loadingTimer.Stop();
                    _loadingTimer.Tick -= LoadingTimer_Tick;
                    _loadingTimer = null;
                }
            }
            catch { }
        }

        private void LoadAndShowReport()
        {
            string poolDir = App.LastSelectedPoolDir ?? "";
            string poolName = string.IsNullOrEmpty(poolDir) ? "" : Path.GetFileName(poolDir);
            var reportTimer = System.Diagnostics.Stopwatch.StartNew();
            RuntimeLog.Info($"加载抽取报告：卡池={poolName}，目录={poolDir}");

            ShowReportLoading(true);
            StartProgressAnimation();

            // 统计放到后台线程，界面（Win10 转圈）保持流畅；结果回到 UI 线程再渲染
            System.Threading.Tasks.Task.Run(() =>
            {
                GachaReportData result;
                try
                {
                    result = GachaReportAnalyzer.Analyze(poolDir, poolName);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error(ex, $"整理抽取报告失败：卡池={poolName}");
                    result = new GachaReportData { SubTitle = "整理数据失败：" + ex.Message };
                }

                try
                {
                    Dispatcher.BeginInvoke(new Action(() => ApplyReport(result, poolDir, reportTimer)));
                }
                catch { }
            });
        }

        /// <summary>把后台算好的报告渲染到界面（必须在 UI 线程执行）。</summary>
        private void ApplyReport(GachaReportData data, string poolDir, System.Diagnostics.Stopwatch reportTimer)
        {
            // 进场动画还没播完：先缓存，等动画结束后再渲染，避免掉帧
            if (_enterAnimating)
            {
                _pendingReport = (data, poolDir, reportTimer);
                return;
            }

            _reportPoolName = data.PoolName ?? "";
            RenderReport(data);
            _analyzing = false;
            RuntimeLog.Info($"抽取报告完成：卡池={_reportPoolName}，条目={data.Entries?.Count ?? 0}，摘要={data.Summary}，耗时={reportTimer.ElapsedMilliseconds}ms");

            if (!string.IsNullOrEmpty(poolDir)) ReportCache.Save(poolDir);

            // 加载完成：进度条瞬间到 100%，短暂停留后隐藏遮罩并淡入报告
            StopLoading();
            _loadingProgress = 100;
            LoadingBar.Value = 100;
            TxtLoadingPercent.Text = "100%";

            var hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            hideTimer.Tick += (s, e) =>
            {
                hideTimer.Stop();
                ShowReportLoading(false);
                ShowReportPanelAnimated();
            };
            hideTimer.Start();
        }

        /// <summary>报告面板淡入 + 上滑。</summary>
        private void ShowReportPanelAnimated()
        {
            ReportPanel.Visibility = Visibility.Visible;
            ReportPanel.Opacity = 0;
            ReportTransform.Y = 40;

            if (App.IsAnimationDisabled)
            {
                ReportPanel.Opacity = 1;
                ReportTransform.Y = 0;
                return;
            }

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            ReportPanel.BeginAnimation(OpacityProperty, fade);
            ReportTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // ================================================================
        // 渲染
        // ================================================================
        private void RenderReport(GachaReportData data)
        {
            StopGifPlayers();
            TxtTitle.Text = string.IsNullOrEmpty(data.Title) ? "数据不足" : data.Title;
            TxtPoolLine.Text = string.IsNullOrEmpty(data.PoolName) ? "" : $"卡池：{data.PoolName}";

            string summary = string.IsNullOrWhiteSpace(data.Summary) ? data.SubTitle : data.Summary;
            TxtSummary.Text = summary ?? "";
            TxtSummary.Visibility = string.IsNullOrEmpty(summary) ? Visibility.Collapsed : Visibility.Visible;

            if (data.Keywords != null && data.Keywords.Count > 0)
            {
                TxtKeywords.Text = string.Join("  ·  ", data.Keywords);
                TxtKeywords.Visibility = Visibility.Visible;
            }
            else
            {
                TxtKeywords.Visibility = Visibility.Collapsed;
            }

            // ★ 无抽取记录 / 数据不足时：不显示分享按钮，也不显示「仅供娱乐」说明
            bool hasUsableReport = data.HasData
                && data.Entries != null && data.Entries.Count > 0
                && !string.Equals((data.Title ?? "").Trim(), "数据不足", StringComparison.Ordinal);

            if (BtnShare != null)
                BtnShare.Visibility = hasUsableReport ? Visibility.Visible : Visibility.Collapsed;

            if (TxtFooterNote != null)
                TxtFooterNote.Visibility = (hasUsableReport && !data.IsSimple) ? Visibility.Visible : Visibility.Collapsed;

            if (TxtFooterIcon != null)
                TxtFooterIcon.Visibility = (hasUsableReport && !data.IsSimple) ? Visibility.Visible : Visibility.Collapsed;

            EntriesHost.Children.Clear();
            if (data.IsSimple)
            {
                EntriesHost.HorizontalAlignment = HorizontalAlignment.Center;
                EntriesHost.MaxWidth = 720;
            }
            else
            {
                EntriesHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                EntriesHost.MaxWidth = 940;
                EntriesHost.Margin = new Thickness(0, 12, 0, 0);
            }

            if (data.IsSimple)
            {
                bool anyAvatar = data.Entries.Any(x => x.HasAvatar);
                EntriesHost.Children.Add(BuildSimpleHeader(anyAvatar));


                var simpleRows = new List<FrameworkElement>();
                int index = 0;
                foreach (var entry in data.Entries)
                {
                    index++;
                    simpleRows.Add(BuildSimpleRow(index, entry, anyAvatar));
                }
                AddRowsBatched(simpleRows);
                return;
            }

            var rows = new List<FrameworkElement>();

            // 已垫抽数（距上次出金）
            if (data.HasData && data.CurrentPity > 0)
            {
                rows.Add(BuildEntryRow(data, new GachaReportEntry
                {
                    Name = "？？？",
                    Pulls = data.CurrentPity,
                    IsUp = true
                }, showBadge: false));
            }

            // ★ 高级规则池：最新记录排在最上方（从新到旧）；「？？？」已垫抽数仍固定在最前。
            for (int i = data.Entries.Count - 1; i >= 0; i--)
                rows.Add(BuildEntryRow(data, data.Entries[i], showBadge: true));

            AddRowsBatched(rows);
        }

        // ================================================================
        // 分批添加行：避免一次性布局几百行造成「加载完成后卡一下」
        // ================================================================
        private List<FrameworkElement> _pendingRows;
        private int _pendingRowIndex;

        private void AddRowsBatched(List<FrameworkElement> rows)
        {
            _pendingRows = rows ?? new List<FrameworkElement>();
            _pendingRowIndex = 0;
            AddRowsBatch();
        }

        private void AddRowsBatch()
        {
            if (_pendingRows == null) return;

            const int batchSize = 10;
            int added = 0;
            while (_pendingRowIndex < _pendingRows.Count && added < batchSize)
            {
                EntriesHost.Children.Add(_pendingRows[_pendingRowIndex]);
                _pendingRowIndex++;
                added++;
            }

            if (_pendingRowIndex < _pendingRows.Count)
            {
                Dispatcher.BeginInvoke(new Action(AddRowsBatch), DispatcherPriority.Background);
            }
            else
            {
                _pendingRows = null;
            }
        }

        private static Brush NameBrushFor(GachaReportEntry entry)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.ColorHex))
                {
                    var c = (Color)ColorConverter.ConvertFromString(entry.ColorHex);
                    return new SolidColorBrush(c);
                }
            }
            catch { }

            return ThemeManager.GetBrush("Fg_333333");
        }
        private readonly List<GifPlayer> _gifPlayers = new List<GifPlayer>();

        /// <summary>创建头像画刷：GIF 自动播放动画，其它格式按静态图片处理。</summary>
        private Brush CreateAvatarBrush(string path, FrameworkElement host = null)
        {
            try
            {
                if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    var player = new GifPlayer(path);
                    if (player.HasFrames)
                    {
                        player.Host = host;
                        _gifPlayers.Add(player);
                        return player.Brush;
                    }
                }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();

                return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { return null; }
        }

        private void StopGifPlayers()
        {
            foreach (var player in _gifPlayers)
            {
                try { player.Stop(); } catch { }
            }

            _gifPlayers.Clear();
        }
        private FrameworkElement BuildSimpleRow(int index, GachaReportEntry entry, bool showAvatarColumn)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4), MinHeight = 40 };

            if (showAvatarColumn) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            int nameColumn = 0;

            if (showAvatarColumn)
            {
                nameColumn = 1;

                if (entry.HasAvatar)
                {
                    var avatar = new Border
                    {
                        Width = 32,
                        Height = 32,
                        CornerRadius = new CornerRadius(8),
                        ClipToBounds = true,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    try
                    {
                        avatar.Background = CreateAvatarBrush(entry.AvatarPath, avatar);
                    }
                    catch { }

                    Grid.SetColumn(avatar, 0);
                    row.Children.Add(avatar);
                }
            }

            var name = new TextBlock
            {
                Text = entry.Name ?? "",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = NameBrushFor(entry)
            };
            Grid.SetColumn(name, nameColumn);
            row.Children.Add(name);

            var count = new TextBlock
            {
                Text = $"{entry.Pulls} 次",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = ThemeManager.GetBrush("Fg_666666")
            };
            Grid.SetColumn(count, nameColumn + 1);
            row.Children.Add(count);

            return row;
        }

        private FrameworkElement BuildSimpleHeader(bool showAvatarColumn)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };

            if (showAvatarColumn) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            int nameColumn = showAvatarColumn ? 1 : 0;

            var name = new TextBlock
            {
                Text = "抽取对象",
                FontSize = 12,
                Foreground = ThemeManager.GetBrush("Fg_888888")
            };
            Grid.SetColumn(name, 0);
            if (showAvatarColumn) Grid.SetColumnSpan(name, 2);
            name.TextAlignment = TextAlignment.Center;
            row.Children.Add(name);

            var count = new TextBlock
            {
                Text = "次数",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = ThemeManager.GetBrush("Fg_888888")
            };
            Grid.SetColumn(count, nameColumn + 1);
            row.Children.Add(count);

            var host = new StackPanel();
            host.Children.Add(row);
            host.Children.Add(new Border
            {
                Height = 1,
                Background = ThemeManager.GetBrush("Bd_E5E7EB"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            return host;
        }


        private static Color ColorForProgress(GachaReportData data, int pulls)
        {
            if (pulls <= data.GreenMax) return Color.FromRgb(0x4C, 0xAF, 0x50);
            if (pulls <= data.YellowMax) return Color.FromRgb(0xD4, 0xA0, 0x17);
            return Color.FromRgb(0xE5, 0x39, 0x35);
        }

        /// <summary>斜纹画刷：2.40.0 版原始实现。</summary>
        private static DrawingBrush CreateStripeBrush(Color baseColor, out TranslateTransform transform)
        {
            transform = new TranslateTransform();

            try
            {
                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(
                    new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 24, 24))));

                var line = new LineGeometry(new Point(0, 24), new Point(24, 0));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)), 7)
                {
                    StartLineCap = PenLineCap.Flat,
                    EndLineCap = PenLineCap.Flat
                };
                group.Children.Add(new GeometryDrawing(null, pen, line));

                var brush = new DrawingBrush(group)
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, 24, 24),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, 24, 24),
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.None,
                    Transform = transform
                };

                RenderOptions.SetEdgeMode(brush, EdgeMode.Aliased);
                return brush;
            }
            catch { return null; }
        }
        private FrameworkElement BuildEntryRow(GachaReportData data, GachaReportEntry entry, bool showBadge)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6), MinHeight = 48 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            // ---- 头像（独立列） ----
            if (entry.HasAvatar)
            {
                var avatar = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };

                try
                {
                        avatar.Background = CreateAvatarBrush(entry.AvatarPath, avatar);
                }
                catch { }

                Grid.SetColumn(avatar, 0);
                row.Children.Add(avatar);
            }

            // ---- 名称（独立列，避免与进度条抢位置） ----
            var name = new TextBlock
            {
                Text = entry.Name ?? "",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Foreground = NameBrushFor(entry)
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            // ---- 进度条（斜纹 + 左移动画） ----
            Color color = ColorForProgress(data, entry.Pulls);
            double fraction = Math.Min(1.0, (double)entry.Pulls / Math.Max(1, data.ScaleMax));
            if (fraction < 0.10) fraction = 0.10;

            var barHost = new Grid
            {
                Height = 26,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 1 - fraction), GridUnitType.Star) });

            var track = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = ThemeManager.IsDark
                    ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumnSpan(track, 2);
            barHost.Children.Add(track);

            var fill = new Border
            {
                CornerRadius = new CornerRadius(6),
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true
            };

            TranslateTransform stripeMove;
            var stripeBrush = CreateStripeBrush(color, out stripeMove);

            if (stripeBrush != null)
            {
                fill.Background = stripeBrush;

                // 行数很多时不再逐行播放斜纹动画，几百条同时循环动画会让整个页面卡住
                if (!App.IsAnimationDisabled && data.Entries.Count <= 60)
                {
                    var anim = new DoubleAnimation(0, -24, TimeSpan.FromMilliseconds(700))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    stripeMove.BeginAnimation(TranslateTransform.XProperty, anim);
                }
            }
            else
            {
                fill.Background = new SolidColorBrush(color);
            }

            Grid.SetColumn(fill, 0);
            barHost.Children.Add(fill);

            var pullText = new TextBlock
            {
                Text = entry.Name == "？？？" ? $"已垫{entry.Pulls}抽" : $"{entry.Pulls}抽",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = ThemeManager.IsDark
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
            };
            Grid.SetColumnSpan(pullText, 2);
            barHost.Children.Add(pullText);

            Grid.SetColumn(barHost, 2);
            row.Children.Add(barHost);

            // ---- 歪标记（向右倾斜 10°） ----
            if (showBadge && !entry.IsUp)
            {
                var badge = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(17),
                    BorderThickness = new Thickness(2),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };

                badge.RenderTransform = new RotateTransform(15);

                badge.Child = new TextBlock
                {
                    Text = "歪",
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                Grid.SetColumn(badge, 3);
                row.Children.Add(badge);
            }

            return row;
        }

        // ================================================================
        // 分享：生成长图 → 保存到「图片\保存的图片」→ 预览弹窗
        // ================================================================
        private void BtnShare_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string poolDir = App.LastSelectedPoolDir ?? "";
                string historyFile = Path.Combine(poolDir, "history.txt");

                long historyTicks = 0;
                try
                {
                    if (File.Exists(historyFile))
                        historyTicks = File.GetLastWriteTime(historyFile).Ticks;
                }
                catch { }

                BitmapSource bmp = null;
                string saved = "";

                // ★ 历史记录未变化且上次生成的图片仍存在时，直接复用，不再重复渲染/保存
                if (historyTicks != 0 &&
                    ReportShareCache.TryGet(poolDir, out long cachedTicks, out string cachedPath) &&
                    cachedTicks == historyTicks)
                {
                    bmp = LoadShareImage(cachedPath);
                    if (bmp != null) saved = cachedPath;
                }

                if (bmp == null)
                {
                    bmp = RenderReportBitmap();
                    if (bmp == null) return;

                    saved = SaveReportJpg(bmp);
                    if (historyTicks != 0 && !string.IsNullOrEmpty(saved))
                        ReportShareCache.Save(poolDir, historyTicks, saved);
                }

                var dlg = new ReportShareDialog(bmp, saved) { Owner = Window.GetWindow(this) };
                bool shared = dlg.ShowDialog() == true;

                if (shared)
                {
                    ToastNotification.ShowSuccess(Window.GetWindow(this), "图片文件已复制到剪贴板");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "生成分享图片失败");
                try
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "生成分享图片失败：" + ex.Message,
                        "分享失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
        }

        private static BitmapSource LoadShareImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 把整份报告渲染为一张长图。
        /// ★ 使用 VisualBrush 按内容自身坐标取图（避免 RenderTargetBitmap 把元素在父容器中的
        ///   偏移一起画进去，导致右侧「歪」标记被裁掉），并按分块渲染后拼接，
        ///   避免超长图片超过渲染纹理上限而出现大片空白。
        /// </summary>
        private BitmapSource RenderReportBitmap()
        {
            if (ReportContent == null) return null;

            ReportContent.UpdateLayout();

            double contentW = ReportContent.ActualWidth;
            double contentH = ReportContent.ActualHeight;
            if (contentW <= 0) contentW = 940;
            if (contentH <= 0) contentH = 320;

            // 内容在父级（页面）中的位置：VisualBrush 的坐标空间包含这个偏移
            Point origin = new Point(0, 0);
            try
            {
                origin = ReportContent.TransformToAncestor(this).Transform(new Point(0, 0));
            }
            catch { }

            const double sidePad = 24;   // 左右留白，保证旋转后的「歪」标记完整
            const double topPad = 24;
            const double bottomPad = 24;
            const double outerPad = 26;  // 图片最外圈留白

            double width = contentW + sidePad * 2;
            double height = contentH + topPad + bottomPad;

            // 控制最终像素尺寸：避免超长图片空白 / 内存过大，同时尽量保持清晰
            double scale = 2.0;
            scale = Math.Min(scale, 60000.0 / Math.Max(1, height));
            scale = Math.Min(scale, Math.Sqrt(50_000_000.0 / Math.Max(1, width * height)));
            if (scale < 0.1) scale = 0.1;

            double totalW = width + outerPad * 2;
            double totalH = height + outerPad * 2;
            int outW = Math.Max(1, (int)Math.Ceiling(totalW * scale));
            int outH = Math.Max(1, (int)Math.Ceiling(totalH * scale));

            var bitmap = new WriteableBitmap(outW, outH, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null);

            var bg = ThemeManager.IsDark
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

            // 每次最多渲染 4000 像素高，避免单次 RenderTargetBitmap 过大
            const int maxTilePixelHeight = 4000;
            int destY = 0;
            while (destY < outH)
            {
                int tileH = Math.Min(maxTilePixelHeight, outH - destY);
                double tileDipH = tileH / scale;
                double y0 = destY / scale;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    // 背景（含外圈留白与内容区）
                    dc.DrawRectangle(bg, null, new Rect(0, 0, totalW, tileDipH));

                    // 当前分块对应的内容起点（相对于整张长图的顶部）
                    double srcY = y0 - outerPad;
                    var brush = new VisualBrush(ReportContent)
                    {
                        Stretch = Stretch.Fill,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(
                            origin.X - sidePad,
                            origin.Y - topPad + srcY,
                            width,
                            tileDipH),
                        Viewport = new Rect(0, 0, width, tileDipH)
                    };

                    dc.DrawRectangle(brush, null,
                        new Rect(outerPad, 0, width, tileDipH));
                }

                var tile = new RenderTargetBitmap(outW, tileH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                tile.Render(visual);

                int stride = outW * 4;
                byte[] pixels = new byte[stride * tileH];
                tile.CopyPixels(pixels, stride, 0);
                bitmap.WritePixels(new Int32Rect(0, destY, outW, tileH), pixels, stride, 0);

                destY += tileH;
            }

            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>保存为 jpg：优先「图片\保存的图片」，失败时回退到程序目录 / 程序数据目录。</summary>
        private string SaveReportJpg(BitmapSource bitmap)
        {
            var candidates = new List<string>();

            try
            {
                if (!string.IsNullOrWhiteSpace(App.ScreenshotDir))
                    candidates.Add(App.ScreenshotDir.Trim());
            }
            catch { }

            try
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (!string.IsNullOrEmpty(pictures))
                    candidates.Add(Path.Combine(pictures, "Saved Pictures"));
            }
            catch { }

            try { candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports")); } catch { }

            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Random Select", "Reports"));
            }
            catch { }

            string poolName = string.IsNullOrEmpty(_reportPoolName) ? "抽取报告" : _reportPoolName;
            foreach (char c in Path.GetInvalidFileNameChars()) poolName = poolName.Replace(c, '_');
            string fileName = $"抽取报告_{poolName}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";

            foreach (var dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);

                    string file = Path.Combine(dir, fileName);
                    var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                    var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
                    encoder.Frames.Add(BitmapFrame.Create(source));

                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        encoder.Save(fs);
                    }

                    return file;
                }
                catch { }
            }

            return "";
        }
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string target = string.IsNullOrEmpty(App.GachaReportReturnPage) ? "Main" : App.GachaReportReturnPage;
                App.GachaReportReturnPage = "Main";
                MessageBus.Publish(new AppMessages.NavigateTo(target));
            }
            catch { }
        }
    }
}
