using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
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

            if (ReportScroll != null) ReportScroll.ScrollChanged += (s, e) => { UpdatePageScrollBar(); UpdateStripeAnimations(); };
        }

        /// <summary>把报告正文滚回顶部（进入页面 / 每次刷新报告时调用，不记忆上次位置）。</summary>
        private void ResetScrollToTop()
        {
            try
            {
                if (PageScrollBar != null) PageScrollBar.Value = 0;
                if (ReportScroll != null) ReportScroll.ScrollToVerticalOffset(0);
            }
            catch { }
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

        private sealed class StripeAnim
        {
            public StripeAnim(FrameworkElement row, TranslateTransform move) { Row = row; Move = move; }
            public FrameworkElement Row { get; }
            public TranslateTransform Move { get; }
            public bool Running { get; set; }
        }

        private readonly List<StripeAnim> _stripeAnimations = new List<StripeAnim>();
        private double _lastStripeOffset = double.NaN;

        /// <summary>生成长图期间挂起行内斜纹动画：遮罩盖住页面时它们只会白占渲染线程。</summary>
        private bool _stripeSuspended;

        /// <summary>暂停所有行内斜纹动画（生成长图时调用）。</summary>
        private void SuspendStripeAnimations()
        {
            try
            {
                _stripeSuspended = true;
                foreach (var item in _stripeAnimations)
                {
                    if (!item.Running) continue;
                    item.Running = false;
                    item.Move.BeginAnimation(TranslateTransform.XProperty, null);
                }
            }
            catch { }
        }

        /// <summary>恢复行内斜纹动画（生成长图结束后调用）。</summary>
        private void ResumeStripeAnimations()
        {
            try
            {
                _stripeSuspended = false;
                _lastStripeOffset = double.NaN;   // 强制下一帧重新判断可见行
                UpdateStripeAnimations();
            }
            catch { }
        }

        /// <summary>
        /// 斜纹动画按需启动：只有当前可见的行播放，且同时最多 10 个，避免几百条一起循环把页面拖卡。
        /// </summary>
        private void UpdateStripeAnimations()
        {
            try
            {
                if (_stripeSuspended) return;
                if (_stripeAnimations.Count == 0 || App.IsAnimationDisabled) return;

                double top = ReportScroll != null ? ReportScroll.VerticalOffset : 0;
                if (!double.IsNaN(_lastStripeOffset) && Math.Abs(top - _lastStripeOffset) < 60) return;
                _lastStripeOffset = top;

                double bottom = top + (ReportScroll != null ? ReportScroll.ViewportHeight : 0) + 120;

                int started = 0;
                foreach (var item in _stripeAnimations)
                {
                    bool visible;
                    try
                    {
                        double y = item.Row.TransformToAncestor(ReportContent).Transform(new Point(0, 0)).Y;
                        visible = y >= top - 60 && y <= bottom;
                    }
                    catch { visible = false; }

                    bool shouldRun = visible && started < 10;
                    if (shouldRun) started++;
                    if (shouldRun == item.Running) continue;

                    item.Running = shouldRun;
                    if (shouldRun)
                    {
                        item.Move.BeginAnimation(TranslateTransform.XProperty,
                            new DoubleAnimation(0, -28.28, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
                    }
                    else
                    {
                        item.Move.BeginAnimation(TranslateTransform.XProperty, null);
                    }
                }
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
            // ★ 每次进入报告页都回到顶部，不记忆上次的滚动位置
            ResetScrollToTop();

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

            // 内容刚挂上时滚动范围还没算好，等一次布局后再归零
            Dispatcher.BeginInvoke(new Action(() => { ResetScrollToTop(); UpdateStripeAnimations(); }), DispatcherPriority.Loaded);

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
            _stripeAnimations.Clear();
            _lastStripeOffset = double.NaN;
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
            // 颜色取自 Release\test.html：绿 #53B84A / 黄 #C2A73B / 红 #D84B43
            if (pulls <= data.GreenMax) return Color.FromRgb(0x53, 0xB8, 0x4A);
            if (pulls <= data.YellowMax) return Color.FromRgb(0xC2, 0xA7, 0x3B);
            return Color.FromRgb(0xD8, 0x4B, 0x43);
        }

        /// <summary>
        /// 斜纹画刷：完全照 Release\test.html 的参数来。
        /// 做法不是「在方块里画斜线」（斜线会在每个平铺块边界被裁断，接缝处出现断面），
        /// 而是先在未旋转的坐标里画竖条（10 宽 / 20 周期），再对整个无限平铺做 -45° 旋转——
        /// 旋转作用于平铺本身，所以任意位置都连续、无缝。
        /// </summary>
        private static DrawingBrush CreateStripeBrush(Color baseColor, out TranslateTransform transform)
        {
            transform = new TranslateTransform();

            try
            {
                const double period = 20;   // 条纹周期（旋转后垂直方向仍是 20）
                const double stripe = 10;   // 条纹宽度

                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(
                    new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, period, period))));
                group.Children.Add(new GeometryDrawing(
                    new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), null,
                    new RectangleGeometry(new Rect(0, 0, stripe, period))));

                var brush = new DrawingBrush(group)
                {
                    TileMode = TileMode.Tile,
                    Viewport = new Rect(0, 0, period, period),
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, period, period),
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.None,
                    // 先旋转（竖条变斜条），再水平平移（滚动）
                    Transform = new TransformGroup
                    {
                        Children =
                        {
                            new RotateTransform(-45),
                            transform
                        }
                    }
                };

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
                CornerRadius = new CornerRadius(4),
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
                CornerRadius = new CornerRadius(4),
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
                // 斜纹动画登记：由 UpdateStripeAnimations 按「当前可见 + 最多 10 个」启动
                if (!App.IsAnimationDisabled)
                    _stripeAnimations.Add(new StripeAnim(row, stripeMove));
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
                var pageImages = new List<BitmapSource>();
                var pagePaths = new List<string>();

                // ★ 历史记录未变化且上次生成的图片仍存在时，直接复用，不再重复渲染/保存
                if (historyTicks != 0 &&
                    ReportShareCache.TryGet(poolDir, out long cachedTicks, out string cachedPath) &&
                    cachedTicks == historyTicks)
                {
                    bmp = LoadShareImage(cachedPath, 1400);
                    if (bmp != null) saved = cachedPath;
                }

                // ★ 拆分多张的缓存：同样内容不重复生成（加载慢时自动显示进度遮罩）
                bool cachedMulti = false;
                if (bmp == null && historyTicks != 0 &&
                    ReportShareCache.TryGetMulti(poolDir, out long multiTicks, out List<string> cachedPages) &&
                    multiTicks == historyTicks)
                {
                    var cachedImages = LoadCachedImages(cachedPages, "正在加载分享图片…");
                    if (cachedImages.Count == cachedPages.Count && cachedImages.TrueForAll(i => i != null))
                    {
                        pageImages.AddRange(cachedImages);
                        pagePaths.AddRange(cachedPages);
                        cachedMulti = true;
                    }
                }

                if (bmp == null && !cachedMulti)
                {
                    // ★ 先估算长图高度：太高就询问是否拆成多张
                    double contentH = ReportContent != null ? ReportContent.ActualHeight : 0;
                    // 与实际分页渲染保持一致的估算：4 倍密度、每页 SharePagePixelHeight 像素
                    double projectedPx = (contentH + 100) * SharePagePixelScale;
                    bool split = false;

                    // ★ 两种模式都按原分辨率生成，这里只问「要不要拆」——成功与否交给真正的生成过程判断
                    if (projectedPx > ShareSingleMaxPixelHeight)
                    {
                        // ★ 已经有可用的 PDF 缓存：直接进 PDF 预览，不再弹「图片过大」打扰用户
                        if (TryShowCachedPdf(poolDir, historyTicks)) return;

                        var choice = CustomMessageBox.ShowCustom(Window.GetWindow(this),
                            LongReportPrompt,
                            "图片过大", "拆分生成", "\uF571", "生成PDF", "\uEA90", MessageBoxImage.Warning, "取消");
                        if (choice == MessageBoxResult.Cancel) return;   // ✕ 关闭 = 取消，不生成

                        if (choice == MessageBoxResult.No)
                        {
                            ExportPdf();   // 「生成PDF」：分页渲染后打包成一个 PDF
                            return;
                        }

                        split = choice == MessageBoxResult.Yes;
                    }

                    if (!split)
                    {
                        bmp = RenderSingleWithProgress();
                        if (bmp == null)
                        {
                            // 生成失败（位图 / 编码 / 内存）→ 提示改为多张
                            if (!AskSwitchToMulti()) return;
                            split = true;
                        }
                        else
                        {
                            saved = SaveReportJpg(bmp);
                            if (historyTicks != 0 && !string.IsNullOrEmpty(saved))
                                ReportShareCache.Save(poolDir, historyTicks, saved);

                            bool shared;
                            string copied;
                            if (TryShowShareDialog(new List<BitmapSource> { bmp }, new List<string> { saved }, out shared, out copied))
                            {
                                if (!string.IsNullOrEmpty(copied)) ToastNotification.ShowSuccess(Window.GetWindow(this), copied);
                                return;
                            }

                            // 显示阶段失败（超长位图送不进渲染通道）→ 提示改为多张
                            if (!AskSwitchToMulti()) return;
                            split = true;
                            bmp = null;
                        }
                    }

                    if (split)
                    {
                        pageImages = RenderPagesWithProgress();
                        if (pageImages.Count == 0) return;
                        for (int i = 0; i < pageImages.Count; i++)
                            pagePaths.Add(SaveReportJpg(pageImages[i], pageImages.Count > 1 ? $"_{i + 1}" : ""));
                        if (historyTicks != 0 && pagePaths.Count > 0)
                            ReportShareCache.SaveMulti(poolDir, historyTicks, pagePaths);
                    }
                }

                if (pageImages.Count == 0 && bmp == null) return;

                var displayImages = pageImages.Count > 0
                    ? new List<BitmapSource>(pageImages)
                    : new List<BitmapSource> { bmp };
                var displayPaths = pagePaths.Count > 0
                    ? new List<string>(pagePaths)
                    : new List<string> { saved };

                bool sharedFinal;
                string copiedFinal;
                if (TryShowShareDialog(displayImages, displayPaths, out sharedFinal, out copiedFinal))
                {
                    if (!string.IsNullOrEmpty(copiedFinal)) ToastNotification.ShowSuccess(Window.GetWindow(this), copiedFinal);
                    return;
                }

                // 缓存里的单张显示失败（例如旧版本存下的超长图）→ 重新拆成多张
                if (displayImages.Count != 1 || !AskSwitchToMulti()) return;

                var pages = RenderPagesWithProgress();
                if (pages.Count == 0) return;

                var paths = new List<string>();
                for (int i = 0; i < pages.Count; i++)
                    paths.Add(SaveReportJpg(pages[i], pages.Count > 1 ? $"_{i + 1}" : ""));
                if (historyTicks != 0 && paths.Count > 0)
                    ReportShareCache.SaveMulti(poolDir, historyTicks, paths);

                if (TryShowShareDialog(pages, paths, out bool shared2, out string copied2)
                    && !string.IsNullOrEmpty(copied2))
                    ToastNotification.ShowSuccess(Window.GetWindow(this), copied2);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "生成分享图片失败");

                // ★ 出错时先把残留的浮层收掉：
                //   否则失败的那一层（例如半成品/空白的分享弹窗）会一直盖在界面上，
                //   再叠一个「分享失败」就成了「空白弹窗 + 错误弹窗」。
                try { FloatingOverlay.CloseAll(); } catch { }

                try
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "生成分享图片失败：" + ex.Message,
                        "分享失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
        }

        /// <summary>单张长图按原分辨率（2 倍密度）输出时的像素尺寸。</summary>
        private static void GetSingleImageSize(double contentW, double contentH, out int outW, out int outH)
        {
            if (contentW <= 0) contentW = 940;
            if (contentH <= 0) contentH = 320;

            double totalW = contentW + ShareSidePad * 2 + ShareOuterPad * 2;
            double totalH = contentH + ShareTopPad + ShareBottomPad + ShareOuterPad * 2;

            outW = Math.Max(1, (int)Math.Ceiling(totalW * ShareSingleScale));
            outH = Math.Max(1, (int)Math.Ceiling(totalH * ShareSingleScale));
        }

        /// <summary>这张单张长图是否还在可生成范围内（JPEG 单边上限 + 位图内存上限）。</summary>
        private static bool IsSingleImageSizeUsable(int outW, int outH)
        {
            if (outW <= 0 || outH <= 0) return false;
            if (outH > ShareSingleHardMaxPixelHeight) return false;
            return (long)outW * outH <= ShareSingleMaxPixels;
        }

        /// <summary>单张生成失败后询问是否改为生成多张。</summary>
        private bool AskSwitchToMulti()
        {
            try
            {
                var r = CustomMessageBox.ShowCustom(Window.GetWindow(this),
                    "报告内容过长，无法在保持原有清晰度的前提下生成一张长图。\n是否拆分为多张图片生成？",
                    "单张长图过大", "拆分生成", "\uF571", "取消", "\uE711", MessageBoxImage.Warning);
                return r == MessageBoxResult.Yes;
            }
            catch { return false; }
        }
        /// <summary>带进度遮罩渲染单张长图；失败返回 null（不抛异常）。</summary>
        private BitmapSource RenderSingleWithProgress()
        {
            ShowShareProgress(true);
            try
            {
                return RenderReportBitmap(percent => UpdateShareProgress(percent));
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "单张长图生成失败（报告过大）");
                return null;
            }
            finally
            {
                ShowShareProgress(false);
            }
        }

        /// <summary>带进度遮罩拆分渲染多张长图。</summary>
        private List<BitmapSource> RenderPagesWithProgress()
        {
            ShowShareProgress(true);
            try
            {
                return RenderReportPages(SharePagePixelHeight, percent => UpdateShareProgress(percent)) ?? new List<BitmapSource>();
            }
            finally
            {
                ShowShareProgress(false);
            }
        }

        /// <summary>显示分享弹窗；返回 false 表示显示过程本身失败（残留浮层已清理）。</summary>
        private bool TryShowShareDialog(IList<BitmapSource> images, IList<string> paths, out bool shared, out string copiedMessage,
            string pdfPath = null)
        {
            shared = false;
            copiedMessage = "图片文件已复制到剪贴板";

            try
            {
                ReportShareDialog dlg = images != null && images.Count > 1
                    ? new ReportShareDialog(images, paths, pdfPath)
                    : new ReportShareDialog(images?[0], paths != null && paths.Count > 0 ? paths[0] : "", pdfPath);

                dlg.Owner = Window.GetWindow(this);

                shared = dlg.ShowDialog() == true;
                // 只有用户真的点过复制才算数，否则关掉弹窗不该弹「已复制」
                copiedMessage = dlg.Copied ? dlg.CopiedMessage : "";
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "显示分享弹窗失败");
                try { FloatingOverlay.CloseAll(); } catch { }
                return false;
            }
        }

        /// <summary>加载缓存里的图片；decodeWidth &gt; 0 时按该宽度解码（预览用，省内存也更快）。</summary>
        private static BitmapSource LoadShareImage(string path, int decodeWidth = 0)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }
            return null;
        }

        /// <summary>把消息队列跑一轮（含渲染与动画），让遮罩和进度条在长任务期间保持动画。</summary>
        private static void PumpUiOnce()
        {
            try
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            catch { }
        }

        private void ShowShareProgress(bool show, string title = "正在生成长图…")
        {
            try
            {
                var host = Window.GetWindow(this) as MainWindow;
                if (host == null) return;

                if (show)
                {
                    // 遮罩盖住页面时，行内斜纹动画没人看得见，只会和生成任务抢渲染线程
                    SuspendStripeAnimations();
                    _lastShareProgress = -1;

                    host.ShowShareBusy(title);
                    PumpUiOnce();
                    // 让淡入动画真的走完（重活开始后 UI 线程就没空渲染动画了）
                    for (int i = 0; i < 3; i++) { PumpUiOnce(); System.Threading.Thread.Sleep(50); }
                    PumpUiOnce();
                }
                else
                {
                    host.HideShareBusy();
                    ResumeStripeAnimations();
                }
            }
            catch { }
        }

        private double _lastShareProgress = -1;

        /// <summary>
        /// 生成（长图 / PDF）时的进度回报：百分比变化才刷新一次，并让 UI 走一帧。
        /// 这块节奏是固定的，不再跟随「视觉呈现模式」——生成期间页面动画本来就已经停掉了。
        /// 也不再整窗 UpdateLayout（那是最贵的一步，是之前卡顿的主因）。
        /// </summary>
        private void UpdateShareProgress(double percent)
        {
            try
            {
                double value = Math.Max(0, Math.Min(100, percent));
                if (value < 100 && _lastShareProgress >= 0 && value - _lastShareProgress < 1) return;
                _lastShareProgress = value;

                var host = Window.GetWindow(this) as MainWindow;
                host?.UpdateShareBusy(value);
                PumpUiOnce();
            }
            catch { }
        }

        // 分享长图：单张超过这个高度就建议拆成多张（像素）
        private const int ShareSingleMaxPixelHeight = 20000;
        // ★ 单张长图按原分辨率输出（2 倍密度），不做任何降采样。
        //   下面两个常量只用来判断「这一张图还能不能生成」：超出就直接提示用户改为生成多张长图。
        //   60000 是 JPEG / WIC 的单边上限，64 MP 是位图内存与 WPF 渲染的实际上限。
        private const int ShareSingleHardMaxPixelHeight = 60000;
        private const long ShareSingleMaxPixels = 64_000_000;
        // 单张长图的密度（不压缩画质）
        private const double ShareSingleScale = 2.0;
        // 单张 / 分页共用的留白（DIP）
        private const double ShareSidePad = 24;
        private const double ShareTopPad = 24;
        private const double ShareBottomPad = 24;
        private const double ShareOuterPad = 26;
        // 分页图顶部留给「随机抽取器 … 第X张，共N张」标注的高度（DIP）
        private const double SharePageHeaderPad = 44;
        // 「报告过长」询问弹窗的正文：只有最新版本用这一句，测试直接比对这条常量。
        private const string LongReportPrompt =
            "该抽取报告过长，无法生成完整长图。\n是否转换为生成PDF或拆分为多张图片生成？";
        // 拆图时每张的高度（像素）
        private const int SharePagePixelHeight = 8000;
        // 分页图片的目标像素宽度（越宽越清晰，内存与耗时也越大）
        private const double SharePagePixelWidth = 1200;
        // 估算用的密度系数（与上面的目标宽度大致对应）
        private const double SharePagePixelScale = 4.0;
        // 渲染分块的像素高度：越小，UI 线程单次阻塞越短，生成的进度条越顺（块太大就会一顿一顿）
        private const int ShareTilePixelHeight = 800;
        // 流畅优先模式下每块渲染的预算时间（毫秒）：不足就等一下，把每秒渲染的块数限制在 12 块左右
        private const int ShareTileBudgetMs = 85;

        /// <summary>
        /// 渲染完一块后让出界面：块渲染得快就按 85 ms 预算等一等，
        /// 等待期间连续泵消息，进度条和窗口就能一直刷新（这块节奏与「视觉呈现模式」无关）。
        /// </summary>
        private static void YieldAfterTile(System.Diagnostics.Stopwatch tileWatch)
        {
            try
            {
                if (tileWatch != null)
                {
                    int remain = ShareTileBudgetMs - (int)tileWatch.ElapsedMilliseconds;
                    int until = Environment.TickCount + Math.Max(0, remain);
                    while (Environment.TickCount < until)
                    {
                        PumpUiOnce();
                        System.Threading.Thread.Sleep(8);
                    }
                }
            }
            catch { }
            PumpUiOnce();
        }

        /// <summary>
        /// 把报告按固定像素高度切成多张图（超长报告用）。
        /// 复用与单张渲染相同的 VisualBrush 分块取图方式，只是把结果分到多个位图里。
        /// </summary>
        /// <summary>
        /// 把报告拆成多张图：按「元素分组」分页（不把某一行切成两半），
        /// 并按目标像素宽度反推倍率（不压缩宽度分辨率）。
        /// </summary>
        private List<BitmapSource> RenderReportPages(int pagePixelHeight, Action<double> onProgress = null)
        {
            var pages = new List<BitmapSource>();
            if (ReportContent == null) return pages;
            if (pagePixelHeight < 1000) pagePixelHeight = 1000;

            ReportContent.UpdateLayout();

            double contentW = ReportContent.ActualWidth;
            double contentH = ReportContent.ActualHeight;
            if (contentW <= 0) contentW = 940;
            if (contentH <= 0) contentH = 320;

            Point origin = new Point(0, 0);
            try { origin = ReportContent.TransformToAncestor(this).Transform(new Point(0, 0)); } catch { }

            const double sidePad = ShareSidePad;
            const double topPad = ShareTopPad;
            const double bottomPad = ShareBottomPad;
            const double outerPad = ShareOuterPad;
            const double headerPad = SharePageHeaderPad;

            double width = contentW + sidePad * 2;
            double totalW = width + outerPad * 2;

            // ★ 按目标像素宽度反推倍率（不再用整张高度压倍率），宽度不足时最多放到 10 倍
            double scale = Math.Max(2.0, Math.Min(10.0, SharePagePixelWidth / Math.Max(1.0, totalW)));
            int outW = Math.Max(1, (int)Math.Ceiling(totalW * scale));

            // 每页对应的内容高度（DIP）
            double pageHeightDip = pagePixelHeight / scale;

            var ranges = BuildPageRanges(pageHeightDip, contentH);
            if (ranges.Count == 0) ranges.Add(new Tuple<double, double>(0, contentH));

            // 同一批分页共用同一个时间戳（写在每张图的左上角）
            _shareTimestamp = DateTime.Now;

            var bg = ThemeManager.IsDark
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

            for (int p = 0; p < ranges.Count; p++)
            {
                double startDip = ranges[p].Item1;
                double sliceDip = ranges[p].Item2;
                bool isFirst = Math.Abs(startDip) < 0.5;
                bool isLast = Math.Abs(startDip + sliceDip - contentH) < 1.0;

                double extraTop = isFirst ? topPad : 0;
                double extraBottom = isLast ? bottomPad : 0;

                // ★ 页内布局：页眉 → 首屏留白 → 本页内容 → 末屏留白。
                //   以前这里把取图起点又多减了 topPad，导致每页第一行有一部分被页眉盖住/切掉；
                //   现在内容区严格从 contentTopInPage 开始，1:1 取图，不缩放也不平移。
                double contentTopInPage = headerPad + extraTop;
                double pageDipH = contentTopInPage + sliceDip + extraBottom;
                string header = BuildPageHeader(p + 1, ranges.Count);

                int pageH = Math.Max(1, (int)Math.Ceiling(pageDipH * scale));
                var bitmap = new WriteableBitmap(outW, pageH, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null);

                int destY = 0;
                while (destY < pageH)
                {
                    var tileWatch = System.Diagnostics.Stopwatch.StartNew();
                    // ★ 每块 800 px：块越小，UI 线程单次阻塞越短，进度条/窗口就不会「一顿一顿」
                    int tileH = Math.Min(ShareTilePixelHeight, pageH - destY);
                    double tileDipH = tileH / scale;
                    double localDip = destY / scale;   // 本页内的偏移（DIP）

                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(bg, null, new Rect(0, 0, totalW, tileDipH));

                        // 本块与「本页内容区」的交集，按 1:1 取图
                        double visTop = Math.Max(contentTopInPage, localDip);
                        double visBottom = Math.Min(contentTopInPage + sliceDip, localDip + tileDipH);

                        if (visBottom - visTop > 0.5)
                        {
                            double destYInTile = visTop - localDip;
                            double srcY = startDip + (visTop - contentTopInPage);
                            double h = visBottom - visTop;

                            var brush = new VisualBrush(ReportContent)
                            {
                                Stretch = Stretch.Fill,
                                AlignmentX = AlignmentX.Left,
                                AlignmentY = AlignmentY.Top,
                                ViewboxUnits = BrushMappingMode.Absolute,
                                ViewportUnits = BrushMappingMode.Absolute,
                                // ★ Viewbox / Viewport 都用绝对坐标：Viewport 必须锚在「实际的绘制位置」上，
                                //   否则 WPF 会按画面里的绝对坐标去取样，等于又偏移了一次页眉高度
                                //   （表现出来的就是标题被跳掉、整页内容上移一截）。
                                Viewbox = new Rect(origin.X - sidePad, origin.Y + srcY, width, h),
                                Viewport = new Rect(outerPad, destYInTile, width, h)
                            };

                            dc.DrawRectangle(brush, null, new Rect(outerPad, destYInTile, width, h));
                        }

                        // 页眉只画在每页的第一块上
                        if (destY == 0) DrawPageHeader(dc, header, totalW);
                    }

                    var tile = new RenderTargetBitmap(outW, tileH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    tile.Render(visual);

                    // 直接写进位图的后备缓冲，省掉一次 byte[] 拷贝（大图时这一步很贵）
                    WriteTileInto(bitmap, tile, destY, outW, tileH);

                    destY += tileH;

                    // 每块回报一次进度并让 UI 走一帧，窗口不会整段卡死
                    try
                    {
                        double done = (p + (double)destY / Math.Max(1, pageH)) / Math.Max(1, ranges.Count);
                        onProgress?.Invoke(Math.Min(100.0, done * 100.0));
                    }
                    catch { }
                    YieldAfterTile(tileWatch);
                }

                bitmap.Freeze();
                pages.Add(bitmap);
            }

            return pages;
        }

        /// <summary>把一块渲染结果写进目标位图的指定纵坐标（不经过托管数组拷贝）。</summary>
        private static void WriteTileInto(WriteableBitmap target, RenderTargetBitmap tile, int destY, int outW, int tileH)
        {
            int stride = outW * 4;
            try
            {
                target.Lock();
                try
                {
                    tile.CopyPixels(new Int32Rect(0, 0, outW, tileH),
                        IntPtr.Add(target.BackBuffer, destY * stride), stride * tileH, stride);
                    target.AddDirtyRect(new Int32Rect(0, destY, outW, tileH));
                }
                finally { target.Unlock(); }
            }
            catch
            {
                // 后备缓冲不可用时退回托管数组拷贝
                var pixels = new byte[stride * tileH];
                tile.CopyPixels(pixels, stride, 0);
                target.WritePixels(new Int32Rect(0, destY, outW, tileH), pixels, stride, 0);
            }
        }

        /// <summary>分页图左上角的标注文字：随机抽取器 {版本} 卡池：{卡池} 第X张/共N张 生成时间。</summary>
        /// <summary>
        /// 分页图左上角的标注：随机抽取器 {版本}   卡池：{卡池}   第X张/共N张   {生成时间}。
        /// 板块之间固定 3 个空格（用户要求的排版）。
        /// </summary>
        private string BuildPageHeader(int index, int total)
        {
            string version = GetAppVersionText();
            string pool = string.IsNullOrWhiteSpace(_reportPoolName) ? "未知卡池" : _reportPoolName.Trim();
            string app = string.IsNullOrEmpty(version) ? "随机抽取器" : $"随机抽取器 {version}";
            string time = _shareTimestamp.ToString("yyyy/M/d H:mm:ss");
            return $"{app}   卡池：{pool}   第{index}张/共{total}张   {time}";
        }

        /// <summary>本次生成长图的时间（同一批分页共用一个时间戳）。</summary>
        private DateTime _shareTimestamp = DateTime.Now;

        /// <summary>把程序版本号格式化成 3.x.y（用于长图标注）。</summary>
        private static string GetAppVersionText()
        {
            try
            {
                var asm = typeof(App).Assembly;

                var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');
                    if (plus >= 0) info = info.Substring(0, plus);
                    if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
                }

                var v = asm.GetName().Version;
                if (v != null)
                {
                    if (v.Revision > 0) return v.ToString();
                    return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
                }
            }
            catch { }
            return "";
        }

        /// <summary>在页眉区域画出标注文字（左上角）。</summary>
        private static void DrawPageHeader(DrawingContext dc, string text, double totalW)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;

                var typeface = new Typeface(
                    new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

                var brush = ThemeManager.IsDark
                    ? new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))
                    : new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));

                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 13, brush, 1.0)
                {
                    MaxTextWidth = Math.Max(160, totalW - ShareOuterPad * 2 - 8),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };

                double y = Math.Max(6, (SharePageHeaderPad - ft.Height) / 2);
                dc.DrawText(ft, new Point(ShareOuterPad + 4, y));
            }
            catch { }
        }

        /// <summary>
        /// 按「对象」分页：逐个对象看它在内容里的实际布局位置（含 Margin！），
        /// 放不下就换页，绝不把一个对象拆到两页；返回每页的 (起始 DIP, 高度 DIP)。
        /// 只累加 ActualHeight 会漏掉行间距，几十行之后偏差几百 DIP，页尾就会把行切掉。
        /// </summary>
        private List<Tuple<double, double>> BuildPageRanges(double pageHeightDip, double contentH)
        {
            var ranges = new List<Tuple<double, double>>();
            try
            {
                if (!(ReportContent is Panel root)) return ranges;
                if (pageHeightDip <= 0) pageHeightDip = 1000;

                // 每个可切分单元的 (top, bottom)，坐标相对 ReportContent
                var spans = new List<Tuple<double, double>>();

                void AddSpan(UIElement element)
                {
                    if (!(element is FrameworkElement fe)) return;
                    double h = fe.ActualHeight;
                    if (h <= 0) return;

                    double top = 0;
                    try { top = fe.TransformToAncestor(root).Transform(new Point(0, 0)).Y; } catch { }
                    if (top < 0) top = 0;
                    spans.Add(new Tuple<double, double>(top, top + h));
                }

                foreach (UIElement child in root.Children)
                {
                    double h = (child as FrameworkElement)?.ActualHeight ?? 0;
                    // 单个子元素比一页还高，且它是容器：下钻一层，用它的子元素做切点
                    if (h > pageHeightDip && child is Panel inner && inner.Children.Count > 1)
                    {
                        foreach (UIElement grand in inner.Children) AddSpan(grand);
                    }
                    else
                    {
                        AddSpan(child);
                    }
                }

                if (spans.Count == 0)
                {
                    ranges.Add(new Tuple<double, double>(0, Math.Max(1, contentH)));
                    return ranges;
                }

                double pageStart = 0;
                int itemsInPage = 0;

                foreach (var span in spans)
                {
                    // 本页已经有对象，且再放下一个会超出一页 → 在它的「顶边」处分页
                    if (itemsInPage > 0 && span.Item2 - pageStart > pageHeightDip)
                    {
                        double cut = Math.Max(span.Item1, pageStart + 1);
                        ranges.Add(new Tuple<double, double>(pageStart, cut - pageStart));
                        pageStart = cut;
                        itemsInPage = 0;
                    }

                    itemsInPage++;
                }

                if (contentH > pageStart + 1)
                    ranges.Add(new Tuple<double, double>(pageStart, contentH - pageStart));

                if (ranges.Count == 0)
                    ranges.Add(new Tuple<double, double>(0, Math.Max(1, contentH)));
            }
            catch { }
            return ranges;
        }
        /// <summary>
        /// 把整份报告渲染为一张长图。
        /// ★ 使用 VisualBrush 按内容自身坐标取图（避免 RenderTargetBitmap 把元素在父容器中的
        ///   偏移一起画进去，导致右侧「歪」标记被裁掉），并按分块渲染后拼接，
        ///   避免超长图片超过渲染纹理上限而出现大片空白。
        /// </summary>
        private BitmapSource RenderReportBitmap(Action<double> onProgress = null)
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

            const double sidePad = ShareSidePad;   // 左右留白，保证旋转后的「歪」标记完整
            const double topPad = ShareTopPad;
            const double bottomPad = ShareBottomPad;
            const double outerPad = ShareOuterPad;  // 图片最外圈留白

            double width = contentW + sidePad * 2;
            double height = contentH + topPad + bottomPad;

            double totalW = width + outerPad * 2;

            // ★ 单张长图始终按原分辨率（2 倍密度）输出，不压缩画质、不降分辨率。
            //   尺寸一旦超出位图 / JPEG 的物理上限就直接抛「生成失败」，由调用方提示改为生成多张；
            //   提前判掉也能避免先分配几个 GB 的位图把机器拖住。
            double scale = ShareSingleScale;
            GetSingleImageSize(contentW, contentH, out int outW, out int outH);

            if (!IsSingleImageSizeUsable(outW, outH))
                throw new InvalidOperationException($"单张长图尺寸超出可生成范围：{outW}×{outH}");

            var bitmap = new WriteableBitmap(outW, outH, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null);

            var bg = ThemeManager.IsDark
                ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

            // ★ 每块 800 px：块越小，UI 线程单次阻塞越短，进度条就不会「一顿一顿」
            int destY = 0;
            while (destY < outH)
            {
                var tileWatch = System.Diagnostics.Stopwatch.StartNew();
                int tileH = Math.Min(ShareTilePixelHeight, outH - destY);
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

                WriteTileInto(bitmap, tile, destY, outW, tileH);

                destY += tileH;
                // 回报进度，让进度条动起来
                try { onProgress?.Invoke(Math.Min(100.0, destY * 100.0 / outH)); } catch { }
                YieldAfterTile(tileWatch);
            }

            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>保存为 jpg：优先「图片\保存的图片」，失败时回退到程序目录 / 程序数据目录。</summary>
        private string SaveReportJpg(BitmapSource bitmap, string nameSuffix = "")
        {
            return SaveReportFile(".jpg", nameSuffix, fs =>
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(fs);
            });
        }

        /// <summary>保存 PDF（和图片同一套候选目录）。</summary>
        private string SaveReportPdf(IList<BitmapSource> pages, Action<int, byte[]> onPageEncoded = null)
        {
            return SaveReportFile(".pdf", "", fs => ReportPdfWriter.SaveTo(fs, pages, 92, onPageEncoded));
        }

        /// <summary>
        /// 把报告写进候选目录里的第一个可写位置：
        /// 截图目录 → 我的图片\Saved Pictures → 程序目录\Reports → 程序数据目录\Reports。
        /// </summary>
        private string SaveReportFile(string extension, string nameSuffix, Action<FileStream> write)
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
            string fileName = $"抽取报告_{poolName}{nameSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";

            foreach (var dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);

                    string file = Path.Combine(dir, fileName);
                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        write(fs);
                    }

                    return file;
                }
                catch { }
            }

            return "";
        }

        /// <summary>
        /// 「生成PDF」：先查缓存（同一份历史记录 + PDF 没损坏就直接复用），否则分页渲染并打包成多页 PDF，
        /// 最后用同一个预览弹窗展示。
        /// </summary>
        private void ExportPdf()
        {
            string poolDir = App.LastSelectedPoolDir ?? "";
            long historyTicks = 0;
            try
            {
                string historyFile = Path.Combine(poolDir, "history.txt");
                if (File.Exists(historyFile)) historyTicks = File.GetLastWriteTime(historyFile).Ticks;
            }
            catch { }

            // ① 缓存命中且文件完好 → 直接加载预览页，不重新生成
            if (TryShowCachedPdf(poolDir, historyTicks)) return;

            // ② 没有缓存或缓存损坏 → 重新生成
            List<BitmapSource> pages;
            ShowShareProgress(true, "正在生成PDF...");
            try
            {
                pages = RenderReportPages(SharePagePixelHeight, percent => UpdateShareProgress(percent));
            }
            finally
            {
                ShowShareProgress(false);
            }

            if (pages == null || pages.Count == 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "生成 PDF 失败：没有可写入的内容。",
                    "生成失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 写 PDF 的同时把每页 JPEG 存进缓存目录，下次直接用来做预览
            var pageCachePaths = new List<string>();
            string path = SaveReportPdf(pages, (index, jpeg) =>
            {
                string pagePath = ReportShareCache.SavePdfPage(poolDir, index, jpeg);
                if (!string.IsNullOrEmpty(pagePath)) pageCachePaths.Add(pagePath);
            });

            if (string.IsNullOrEmpty(path))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "生成 PDF 失败：保存目录不可写。",
                    "生成失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RuntimeLog.Info($"报告已导出为 PDF：{path}（{pages.Count} 页）");

            if (historyTicks != 0 && pageCachePaths.Count == pages.Count)
                ReportShareCache.SavePdf(poolDir, historyTicks, path, pageCachePaths);

            ShowPdfPreview(pages, path);
        }

        /// <summary>
        /// 有可用的 PDF 缓存（时间戳一致 + PDF 完好 + 预览页都在）时直接打开 PDF 预览并返回 true。
        /// 分享按钮和「生成PDF」都走这里，避免重复生成、也避免已有缓存时还弹「图片过大」。
        /// </summary>
        private bool TryShowCachedPdf(string poolDir, long historyTicks)
        {
            try
            {
                if (historyTicks == 0) return false;
                if (!ReportShareCache.TryGetPdf(poolDir, out long cachedTicks, out string cachedPdf, out List<string> cachedPages))
                    return false;
                if (cachedTicks != historyTicks) return false;
                if (!ReportShareCache.IsValidPdf(cachedPdf)) return false;

                var images = LoadCachedImages(cachedPages, "正在加载PDF预览…");
                if (images.Count != cachedPages.Count) return false;
                foreach (var img in images) if (img == null) return false;

                RuntimeLog.Info($"复用 PDF 缓存：{cachedPdf}（{cachedPages.Count} 页）");
                ShowPdfPreview(images, cachedPdf);
                return true;
            }
            catch { return false; }
        }

        /// <summary>用分享弹窗预览 PDF（传入用于预览的页面图片 + PDF 路径）。</summary>
        private void ShowPdfPreview(IList<BitmapSource> pages, string pdfPath)
        {
            try
            {
                bool shared;
                string copied;
                TryShowShareDialog(pages, new List<string>(new string[pages.Count]), out shared, out copied, pdfPath);
                if (!string.IsNullOrEmpty(copied))
                    ToastNotification.ShowSuccess(Window.GetWindow(this), copied);
            }
            catch { }
        }

        /// <summary>
        /// 从缓存路径加载预览用的图片。
        /// 「智能判断」：整个过程超过 220 ms 才把进度遮罩显示出来，快的时候不闪一下。
        /// 顺手把解码宽度限制在 1400 px——预览用足够清晰，解码和内存却小一个数量级。
        /// </summary>
        private List<BitmapSource> LoadCachedImages(IList<string> paths, string title)
        {
            var images = new List<BitmapSource>();
            if (paths == null || paths.Count == 0) return images;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool overlayShown = false;
            const int showAfterMs = 220;

            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (!overlayShown && watch.ElapsedMilliseconds >= showAfterMs)
                    {
                        ShowShareProgress(true, title);
                        overlayShown = true;
                    }

                    images.Add(LoadShareImage(paths[i], 1400));

                    if (overlayShown) UpdateShareProgress((i + 1) * 100.0 / paths.Count);
                    PumpUiOnce();
                }
            }
            finally
            {
                if (overlayShown) ShowShareProgress(false);
            }

            return images;
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
