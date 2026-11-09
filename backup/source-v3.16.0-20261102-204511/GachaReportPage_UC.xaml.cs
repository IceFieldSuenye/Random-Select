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

            if (ReportScroll != null)
            {
                ReportScroll.ScrollChanged += (s, e) => { UpdatePageScrollBar(); UpdateStripeAnimations(); };
                // 窗口尺寸变化会改变换行 / 行高：丢掉坐标缓存，下一次重新算
                ReportScroll.SizeChanged += (s, e) =>
                {
                    _rowTops.Clear();
                    _runningStripes.Clear();
                    _rowTopsWidth = -1;
                    UpdateStripeAnimations();
                };
            }
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

        /// <summary>同一时刻最多同时播放的行内斜纹数量（再多只会白占渲染线程）。</summary>
        private const int MaxRunningStripes = 10;

        /// <summary>每行在 ReportContent 坐标里的顶边（增量缓存，只在新行追加时补齐）。</summary>
        private readonly List<double> _rowTops = new List<double>();
        private double _rowTopsWidth = -1;
        /// <summary>当前正在播放斜纹的行下标。</summary>
        private readonly HashSet<int> _runningStripes = new HashSet<int>();

        /// <summary>
        /// 增量维护每行的顶边坐标。
        /// ★ 旧实现每次滚动都对**全部**行做一次 TransformToAncestor：3400 行时一次滚动要算 3400 次，
        ///   界面一顿，下方的动画看起来就像没播。现在只补新追加的行，查询走二分。
        /// </summary>
        private void EnsureRowTops()
        {
            if (ReportContent == null) return;

            int count = _stripeAnimations.Count;
            double width = ReportContent.ActualWidth;

            // 行数变少（重新渲染）或宽度变化（换行高度可能变）→ 整表重算
            if (_rowTops.Count > count || (_rowTopsWidth >= 0 && Math.Abs(width - _rowTopsWidth) > 0.5))
            {
                _rowTops.Clear();
                _runningStripes.Clear();
            }
            _rowTopsWidth = width;

            for (int i = _rowTops.Count; i < count; i++)
            {
                var row = _stripeAnimations[i].Row;

                // ★ 行是「先全部 new 出来、再分批挂进可视树」的：
                //   还没进可视树 / 还没布局的行（ActualHeight=0）先不算，
                //   否则会把它们的顶边全压成上一行的值，二分出来的可视范围就永远是前几行。
                if (row.Parent == null || row.ActualHeight <= 0) break;

                double y;
                try { y = row.TransformToAncestor(ReportContent).Transform(new Point(0, 0)).Y; }
                catch { break; }

                // 顶边必须单调递增（StackPanel 垂直排列），否则二分不成立
                if (_rowTops.Count > 0 && y < _rowTops[_rowTops.Count - 1]) y = _rowTops[_rowTops.Count - 1];
                _rowTops.Add(y);
            }
        }

        /// <summary>二分：第一个顶边 &gt;= value 的行下标。</summary>
        private static int LowerBound(List<double> values, double value)
        {
            int lo = 0, hi = values.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (values[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private void StartStripe(int index)
        {
            var item = _stripeAnimations[index];
            if (item.Running) { _runningStripes.Add(index); return; }
            item.Running = true;
            _runningStripes.Add(index);
            item.Move.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -28.28, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopStripe(int index)
        {
            var item = _stripeAnimations[index];
            _runningStripes.Remove(index);
            if (!item.Running) return;
            item.Running = false;
            item.Move.BeginAnimation(TranslateTransform.XProperty, null);
        }

        /// <summary>
        /// 斜纹动画按需启动：只有当前可见的行播放，且同时最多 10 个。
        /// ★ 生成长图 / PDF 期间**不挂起**这些动画（3.14.0 起）：遮罩是半透明的，动画照常播。
        /// </summary>
        private void UpdateStripeAnimations()
        {
            try
            {
                if (_stripeAnimations.Count == 0 || App.IsAnimationDisabled) return;
                if (ReportScroll == null || ReportContent == null) return;

                double viewport = ReportScroll.ViewportHeight;
                if (viewport <= 0) return;

                double top = ReportScroll.VerticalOffset;
                _lastStripeOffset = top;

                double upper = top - 60;
                double bottom = top + viewport + 120;

                EnsureRowTops();
                if (_rowTops.Count == 0) return;

                // ① 算出这一屏要播的行（从二分结果往后取，最多 MaxRunningStripes 个）
                var desired = new List<int>(MaxRunningStripes);
                for (int i = LowerBound(_rowTops, upper); i < _rowTops.Count && desired.Count < MaxRunningStripes; i++)
                {
                    if (_rowTops[i] > bottom) break;
                    desired.Add(i);
                }

                // ② 已经离开可视范围 / 超出上限的先停
                if (_runningStripes.Count > 0)
                {
                    var running = new List<int>(_runningStripes);
                    foreach (int index in running)
                    {
                        if (!desired.Contains(index) || index >= _stripeAnimations.Count) StopStripe(index);
                    }
                }

                // ③ 新进可视范围的启动
                foreach (int index in desired)
                {
                    if (!_runningStripes.Contains(index)) StartStripe(index);
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
        private async void ApplyReport(GachaReportData data, string poolDir, System.Diagnostics.Stopwatch reportTimer)
        {
            EnsureUiSyncContext();
            try
            {
                // 进场动画还没播完：先缓存，等动画结束后再渲染，避免掉帧
                if (_enterAnimating)
                {
                    _pendingReport = (data, poolDir, reportTimer);
                    return;
                }

                // ★ 头像在后台按显示尺寸解码（冻结位图），UI 线程只做赋值：
                //   以前每建一行就在 UI 线程读文件 + 全尺寸解码，几十上百个头像会把进场动画拖住。
                await PreloadAvatarBitmapsAsync(data);

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
            catch (Exception ex)
            {
                // async void 里抛出的异常没人接，必须在这里收尾，否则界面会卡在「加载中」
                _analyzing = false;
                StopLoading();
                RuntimeLog.Error(ex, "渲染抽取报告失败");
                try
                {
                    ShowReportLoading(false);
                    ShowReportPanelAnimated();
                }
                catch { }
            }
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
            _runningStripes.Clear();
            _rowTops.Clear();
            _rowTopsWidth = -1;
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

            // ★ 每加一批就刷新一次「哪几行在可视范围」：否则报告很长时，
            //   批处理期间新增的行要等用户滚一下才会开始播斜纹动画（看起来就是「下面不播」）。
            Dispatcher.BeginInvoke(new Action(UpdateStripeAnimations), DispatcherPriority.Loaded);
        }

        /// <summary>静态画刷统一 Freeze()：去掉变更通知开销，也允许跨线程共享（动画目标不在此列）。</summary>
        private static SolidColorBrush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        private static Brush NameBrushFor(GachaReportEntry entry)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.ColorHex))
                {
                    var c = (Color)ColorConverter.ConvertFromString(entry.ColorHex);
                    return FrozenBrush(c);
                }
            }
            catch { }

            return ThemeManager.GetBrush("Fg_333333");
        }
        private readonly List<GifPlayer> _gifPlayers = new List<GifPlayer>();

        /// <summary>后台预解码好的头像（键 = 文件路径，值 = 已 Freeze 的位图）。</summary>
        private readonly Dictionary<string, BitmapSource> _avatarBitmaps =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 头像解码宽度（像素）：显示尺寸 44 DIP，长图最高按 4 倍密度取样（≈176 px），
        /// 取 192 既保证长图清晰，又不用把几 MB 的原图整张解进内存。
        /// </summary>
        private const int AvatarDecodePixelWidth = 192;

        /// <summary>在后台线程按显示尺寸预解码报告里的头像；GIF 仍交给 GifPlayer 播放。</summary>
        private async System.Threading.Tasks.Task PreloadAvatarBitmapsAsync(GachaReportData data)
        {
            _avatarBitmaps.Clear();
            if (data?.Entries == null || data.Entries.Count == 0) return;

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in data.Entries)
            {
                if (entry == null || !entry.HasAvatar) continue;
                if (string.Equals(Path.GetExtension(entry.AvatarPath), ".gif", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(entry.AvatarPath)) paths.Add(entry.AvatarPath);
            }
            if (paths.Count == 0) return;

            try
            {
                // 文件读取 + 解码都在线程池里做，UI 线程只在渲染时直接拿结果
                var decoded = await System.Threading.Tasks.Task.Run(() =>
                {
                    var map = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths)
                    {
                        try
                        {
                            var bmp = ImageHelper.LoadBitmapSized(path, AvatarDecodePixelWidth);
                            if (bmp != null) map[path] = bmp;
                        }
                        catch { }
                    }
                    return map;
                });

                foreach (var pair in decoded) _avatarBitmaps[pair.Key] = pair.Value;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "预解码头像失败");
            }
        }

        /// <summary>创建头像画刷：GIF 自动播放动画，其它格式用已冻结的位图（滚动/截图都很便宜）。</summary>
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

                // ① 预解码命中：直接复用冻结位图（常见路径）
                if (_avatarBitmaps.TryGetValue(path, out var cached) && cached != null)
                    return new ImageBrush(cached) { Stretch = Stretch.UniformToFill };

                // ② 未命中（例如临时新增的对象）：按显示尺寸补解码一次并缓存
                var bmp = ImageHelper.LoadBitmapSized(path, AvatarDecodePixelWidth);
                if (bmp != null)
                {
                    _avatarBitmaps[path] = bmp;
                    return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
            }
            catch { }
            return null;
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
                    FrozenBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, period, period))));
                group.Children.Add(new GeometryDrawing(
                    FrozenBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), null,
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
                    ? FrozenBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
                    : FrozenBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00)),
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
                fill.Background = FrozenBrush(color);
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
                    : FrozenBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
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
                    BorderBrush = FrozenBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
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
                    Foreground = FrozenBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
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
        // ★ 全程 async：渲染切成小块、每块之间 await Dispatcher.Yield()，
        //   文件编码 / PDF 打包丢到线程池；进度遮罩显示期间页面动画照常播放。
        // ================================================================
        private bool _shareBusy;

        private async void BtnShare_Click(object sender, RoutedEventArgs e)
        {
            if (_shareBusy) return;
            _shareBusy = true;
            EnsureUiSyncContext();
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
                    // 解码放后台：缓存图可能有几千万像素，UI 线程解码会顶住动画
                    string path = cachedPath;
                    bmp = await ImageHelper.LoadBitmapSizedAsync(path, SharePreviewDecodeWidth);
                    if (bmp != null) saved = cachedPath;
                }

                // ★ 拆分多张的缓存：同样内容不重复生成（加载慢时自动显示进度遮罩）
                bool cachedMulti = false;
                if (bmp == null && historyTicks != 0 &&
                    ReportShareCache.TryGetMulti(poolDir, out long multiTicks, out List<string> cachedPages) &&
                    multiTicks == historyTicks)
                {
                    var cachedImages = await LoadCachedImagesAsync(cachedPages, "正在加载分享图片…");
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
                        if (await TryShowCachedPdfAsync(poolDir, historyTicks)) return;

                        var choice = CustomMessageBox.ShowCustom(Window.GetWindow(this),
                            LongReportPrompt,
                            "图片过大", "拆分生成", "\uF571", "生成PDF", "\uEA90", MessageBoxImage.Warning, "取消");
                        if (choice == MessageBoxResult.Cancel) return;   // ✕ 关闭 = 取消，不生成

                        if (choice == MessageBoxResult.No)
                        {
                            await ExportPdfAsync();   // 「生成PDF」：分页渲染后打包成一个 PDF
                            return;
                        }

                        split = choice == MessageBoxResult.Yes;
                    }

                    if (!split)
                    {
                        var single = await RenderAndSaveSingleWithProgressAsync();
                        bmp = single.Bitmap;
                        saved = single.Saved;
                        if (bmp == null)
                        {
                            // 生成失败（位图 / 编码 / 内存）→ 提示改为多张
                            if (!AskSwitchToMulti()) return;
                            split = true;
                        }
                        else
                        {
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
                        var multi = await RenderAndSavePagesWithProgressAsync();
                        pageImages = multi.Pages;
                        pagePaths = multi.Paths;
                        if (pageImages.Count == 0) return;
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

                var retry = await RenderAndSavePagesWithProgressAsync();
                var pages = retry.Pages;
                var paths = retry.Paths;
                if (pages.Count == 0) return;
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
            finally
            {
                _shareBusy = false;
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
        /// <summary>
        /// 带进度遮罩渲染单张长图并保存 jpg；失败返回 (null, "")（不抛异常）。
        /// 渲染与保存都在同一次遮罩会话里完成：保存大图也要几秒，中间收起遮罩会让人以为没反应。
        /// </summary>
        private async System.Threading.Tasks.Task<(BitmapSource Bitmap, string Saved)> RenderAndSaveSingleWithProgressAsync()
        {
            await ShowShareProgressAsync(true);
            try
            {
                var bmp = await RenderReportBitmapAsync(percent => UpdateShareProgress(percent));
                if (bmp == null) return (null, "");

                string saved = await SaveReportJpgAsync(bmp);
                return (bmp, saved);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "单张长图生成失败（报告过大）");
                return (null, "");
            }
            finally
            {
                await ShowShareProgressAsync(false);
            }
        }

        /// <summary>带进度遮罩拆分渲染多张长图并逐张保存（同样一次遮罩会话内完成）。</summary>
        private async System.Threading.Tasks.Task<(List<BitmapSource> Pages, List<string> Paths)> RenderAndSavePagesWithProgressAsync()
        {
            await ShowShareProgressAsync(true);
            try
            {
                var pages = await RenderReportPagesAsync(SharePagePixelHeight, percent => UpdateShareProgress(percent))
                            ?? new List<BitmapSource>();

                var paths = new List<string>();
                for (int i = 0; i < pages.Count; i++)
                    paths.Add(await SaveReportJpgAsync(pages[i], pages.Count > 1 ? $"_{i + 1}" : ""));

                return (pages, paths);
            }
            finally
            {
                await ShowShareProgressAsync(false);
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

        /// <summary>
        /// 分享预览图的解码宽度：预览区最宽约 1200 DIP，按 1400 px 解码足够清晰，
        /// 内存与解码时间却比整张长图小一个数量级。解码统一走 <see cref="ImageHelper.LoadBitmapSized"/>。
        /// </summary>
        private const int SharePreviewDecodeWidth = 1400;

        /// <summary>
        /// 让消息队列跑一帧：把 UI 交还给渲染 / 输入 / 动画。
        /// ★ 用 await Dispatcher.Yield 而不是「PushFrame + Thread.Sleep」：
        ///   以前那种写法在同步循环里硬泵消息，块与块之间还要空转 85 ms，
        ///   既拖慢生成，也让页面动画只能一格一格跳。现在每个块结束后正常让出，
        ///   调度器自己决定先渲染帧还是先处理输入，动画能连续播。
        /// ★ 直接 await Dispatcher.Yield 的结果（不包一层 async Task）：
        ///   Dispatcher 的 awaiter 会显式把续体排回 UI 线程，不依赖当前线程有没有
        ///   SynchronizationContext（测试宿主里就没有）。
        /// </summary>
        private static System.Windows.Threading.DispatcherPriorityAwaitable YieldToUi()
        {
            return Dispatcher.Yield(DispatcherPriority.Background);
        }

        /// <summary>
        /// 保证当前线程装的是 WPF 的调度器同步上下文。
        /// 程序正常启动时 Application.Run 会装好；测试 / 二次宿主里可能没有，
        /// 缺了它的话 `await Task.Run(...)` 之后会落到线程池线程上，再碰 UI 就会抛跨线程异常。
        /// </summary>
        private void EnsureUiSyncContext()
        {
            try
            {
                if (!Dispatcher.CheckAccess()) return;
                if (System.Threading.SynchronizationContext.Current is DispatcherSynchronizationContext) return;
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher));
            }
            catch { }
        }

        /// <summary>显示 / 收起「正在生成长图」遮罩（不再挂起页面动画）。</summary>
        private async System.Threading.Tasks.Task ShowShareProgressAsync(bool show, string title = "正在生成长图…")
        {
            try
            {
                var host = Window.GetWindow(this) as MainWindow;
                if (host == null) return;

                if (show)
                {
                    _lastShareProgress = -1;
                    host.ShowShareBusy(title);
                    // 让淡入动画先走完一帧（此时 UI 线程是空闲的，动画照常播）
                    await YieldToUi();
                }
                else
                {
                    host.HideShareBusy();
                }
            }
            catch { }
        }

        private double _lastShareProgress = -1;

        /// <summary>
        /// 生成（长图 / PDF）时的进度回报：百分比变化（≥1%）才刷新一次。
        /// 不再整窗 UpdateLayout、也不再手动泵消息——渲染循环自己会让出 UI。
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
        // 渲染分块的像素高度：越小，UI 线程单次阻塞越短，动画与进度条就越顺
        private const int ShareTilePixelHeight = 800;

        /// <summary>
        /// 同步等待一个 async 渲染流程（只给测试 / 兼容旧调用点用）：
        /// 走嵌套消息循环，让 async 里的 Dispatcher.Yield 能正常恢复。
        /// 程序内部一律用 async 版本，不要再走这里。
        /// </summary>
        private static T RunWithNestedPump<T>(Func<System.Threading.Tasks.Task<T>> factory)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var task = factory();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.BeginInvoke(
                        new Action(() => frame.Continue = false), DispatcherPriority.Background),
                    System.Threading.Tasks.TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }

        /// <summary>
        /// 把报告拆成多张图：按「元素分组」分页（不把某一行切成两半），
        /// 并按目标像素宽度反推倍率（不压缩宽度分辨率）。
        /// ★ async 版：每渲染完一块就 await 让出 UI，遮罩下面的页面动画能连续播。
        /// </summary>
        private async System.Threading.Tasks.Task<List<BitmapSource>> RenderReportPagesAsync(int pagePixelHeight, Action<double> onProgress = null)
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
                ? FrozenBrush(Color.FromRgb(0x1E, 0x1E, 0x22))
                : FrozenBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

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
                    // ★ 每块 800 px：块越小，UI 线程单次阻塞越短，动画和进度条就越顺
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

                    // 每块回报一次进度并让出 UI（渲染帧 / 输入 / 页面动画都能正常进行）
                    try
                    {
                        double done = (p + (double)destY / Math.Max(1, pageH)) / Math.Max(1, ranges.Count);
                        onProgress?.Invoke(Math.Min(100.0, done * 100.0));
                    }
                    catch { }
                    await YieldToUi();
                }

                bitmap.Freeze();
                pages.Add(bitmap);
            }

            return pages;
        }

        /// <summary>
        /// 同步版分页渲染（只给测试 / 旧调用点用）：内部走 async 版本 + 嵌套消息循环。
        /// 程序内部请直接用 <see cref="RenderReportPagesAsync"/>。
        /// </summary>
        private List<BitmapSource> RenderReportPages(int pagePixelHeight, Action<double> onProgress = null)
        {
            return RunWithNestedPump(() => RenderReportPagesAsync(pagePixelHeight, onProgress)) ?? new List<BitmapSource>();
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
            // ★ 页眉左上角显示用户自定义的窗口标题（{…} 关键词在这里不展开、直接去掉）；
            //   没设置标题时仍然是「随机抽取器 版本号」。
            string app = WindowTitleFormatter.GetShareHeaderAppText(version);
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
                    ? FrozenBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))
                    : FrozenBrush(Color.FromRgb(0x60, 0x60, 0x60));

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
        /// 把整份报告渲染为一张长图（async 版：每块渲染完让出 UI，页面动画不停）。
        /// ★ 使用 VisualBrush 按内容自身坐标取图（避免 RenderTargetBitmap 把元素在父容器中的
        ///   偏移一起画进去，导致右侧「歪」标记被裁掉），并按分块渲染后拼接，
        ///   避免超长图片超过渲染纹理上限而出现大片空白。
        /// </summary>
        private async System.Threading.Tasks.Task<BitmapSource> RenderReportBitmapAsync(Action<double> onProgress = null)
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
                ? FrozenBrush(Color.FromRgb(0x1E, 0x1E, 0x22))
                : FrozenBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

            // ★ 每块 800 px：块越小，UI 线程单次阻塞越短，动画与进度条就越顺
            int destY = 0;
            while (destY < outH)
            {
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
                // 回报进度并让出 UI，动画和进度条都能继续走
                try { onProgress?.Invoke(Math.Min(100.0, destY * 100.0 / outH)); } catch { }
                await YieldToUi();
            }

            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// 同步版单张长图渲染（只给测试 / 旧调用点用）：内部走 async 版本 + 嵌套消息循环。
        /// 程序内部请直接用 <see cref="RenderReportBitmapAsync"/>。
        /// </summary>
        private BitmapSource RenderReportBitmap(Action<double> onProgress = null)
        {
            return RunWithNestedPump(() => RenderReportBitmapAsync(onProgress));
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

        /// <summary>
        /// 在后台线程把长图编码成 jpg 并落盘。
        /// ★ 位图在渲染结束时已经 Freeze()，编码 / 写文件不需要 UI 线程；
        ///   以前这一步占着 UI 线程，几千万像素的图要卡好几秒，动画直接停摆。
        /// </summary>
        private System.Threading.Tasks.Task<string> SaveReportJpgAsync(BitmapSource bitmap, string nameSuffix = "")
        {
            if (bitmap == null) return System.Threading.Tasks.Task.FromResult("");
            return System.Threading.Tasks.Task.Run(() => SaveReportJpg(bitmap, nameSuffix));
        }

        /// <summary>保存 PDF（和图片同一套候选目录）。</summary>
        private string SaveReportPdf(IList<BitmapSource> pages, Action<int, byte[]> onPageEncoded = null)
        {
            return SaveReportFile(".pdf", "", fs => ReportPdfWriter.SaveTo(fs, pages, 92, onPageEncoded));
        }

        /// <summary>在后台线程打包 PDF（每页 JPEG 编码 + 文件写入，同样不需要 UI 线程）。</summary>
        private System.Threading.Tasks.Task<string> SaveReportPdfAsync(IList<BitmapSource> pages, Action<int, byte[]> onPageEncoded = null)
        {
            return System.Threading.Tasks.Task.Run(() => SaveReportPdf(pages, onPageEncoded));
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
        /// ★ 全程 async：分页渲染让出 UI，PDF 编码 / 落盘在线程池里做。
        /// </summary>
        private async System.Threading.Tasks.Task ExportPdfAsync()
        {
            EnsureUiSyncContext();
            string poolDir = App.LastSelectedPoolDir ?? "";
            long historyTicks = 0;
            try
            {
                string historyFile = Path.Combine(poolDir, "history.txt");
                if (File.Exists(historyFile)) historyTicks = File.GetLastWriteTime(historyFile).Ticks;
            }
            catch { }

            // ① 缓存命中且文件完好 → 直接加载预览页，不重新生成
            if (await TryShowCachedPdfAsync(poolDir, historyTicks)) return;

            // ② 没有缓存或缓存损坏 → 重新生成（渲染 + 打包同一次遮罩会话，进度条不会中途消失）
            List<BitmapSource> pages = null;
            var pageCachePaths = new List<string>();
            string path = "";

            await ShowShareProgressAsync(true, "正在生成PDF...");
            try
            {
                pages = await RenderReportPagesAsync(SharePagePixelHeight, percent => UpdateShareProgress(percent));

                if (pages != null && pages.Count > 0)
                {
                    // 写 PDF 的同时把每页 JPEG 存进缓存目录，下次直接用来做预览（都在后台线程）
                    path = await SaveReportPdfAsync(pages, (index, jpeg) =>
                    {
                        string pagePath = ReportShareCache.SavePdfPage(poolDir, index, jpeg);
                        if (!string.IsNullOrEmpty(pagePath)) lock (pageCachePaths) pageCachePaths.Add(pagePath);
                    });
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "生成 PDF 失败（渲染 / 打包阶段）");
            }
            finally
            {
                await ShowShareProgressAsync(false);
            }

            if (pages == null || pages.Count == 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "生成 PDF 失败：没有可写入的内容。",
                    "生成失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
        private async System.Threading.Tasks.Task<bool> TryShowCachedPdfAsync(string poolDir, long historyTicks)
        {
            try
            {
                if (historyTicks == 0) return false;
                if (!ReportShareCache.TryGetPdf(poolDir, out long cachedTicks, out string cachedPdf, out List<string> cachedPages))
                    return false;
                if (cachedTicks != historyTicks) return false;
                if (!ReportShareCache.IsValidPdf(cachedPdf)) return false;

                var images = await LoadCachedImagesAsync(cachedPages, "正在加载PDF预览…");
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
        /// ★ 解码整段丢到线程池（按 1400 px 宽解码，内存小一个数量级），每解完一张通过
        ///   IProgress 分批回传进度；整个过程超过 220 ms 才显示遮罩，快的时候不闪一下。
        /// </summary>
        private async System.Threading.Tasks.Task<List<BitmapSource>> LoadCachedImagesAsync(IList<string> paths, string title)
        {
            var images = new List<BitmapSource>();
            if (paths == null || paths.Count == 0) return images;

            const int showAfterMs = 220;
            bool overlayShown = false;

            // Progress<T> 会回到创建它的 UI 线程，刚好用于进度回报
            var progress = new Progress<double>(percent =>
            {
                if (overlayShown) UpdateShareProgress(percent);
            });

            var decodeTask = System.Threading.Tasks.Task.Run(() =>
            {
                var decoded = new List<BitmapSource>(paths.Count);
                for (int i = 0; i < paths.Count; i++)
                {
                    decoded.Add(ImageHelper.LoadBitmapSized(paths[i], SharePreviewDecodeWidth));
                    ((IProgress<double>)progress).Report((i + 1) * 100.0 / paths.Count);
                }
                return decoded;
            });

            if (await System.Threading.Tasks.Task.WhenAny(decodeTask, System.Threading.Tasks.Task.Delay(showAfterMs)) != decodeTask)
            {
                await ShowShareProgressAsync(true, title);
                overlayShown = true;
            }

            try
            {
                images = await decodeTask;
            }
            finally
            {
                if (overlayShown) await ShowShareProgressAsync(false);
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
