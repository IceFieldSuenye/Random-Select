using Microsoft.Win32;
using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RandomDrawer
{
    public partial class MainPage_UC : UserControl, IPageAnimation, IHandleEscape, IDrawButtonsHost
    {
        public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
            "IsSelected", typeof(bool), typeof(MainPage_UC), new PropertyMetadata(false));

        public static void SetIsSelected(UIElement element, bool value) => element.SetValue(IsSelectedProperty, value);
        public static bool GetIsSelected(UIElement element) => (bool)element.GetValue(IsSelectedProperty);

        private static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg" };

        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".jfif" };

        private DrawEngine Engine
        {
            get
            {
                if (DrawSession.Current == null) DrawSession.Current = new DrawSession();
                return DrawSession.Current.Engine;
            }
        }

        private List<PoolInfo> _allPools = new List<PoolInfo>();
        private int _currentPoolIndex = -1;
        private bool _showReportButton;
        private bool _initialized = false;

        private DispatcherTimer _gifTimer;
        private GifBitmapDecoder _gifDecoder;
        private int _gifFrameIndex;
        /// <summary>3.37：当前 GIF 的路径与离开时的帧号 —— 返回主界面时从原帧继续，不跳回第 0 帧。</summary>
        private string _gifCurrentPath = "";
        private int _gifResumeIndex;

        private Storyboard _syncStoryboard;
        private Storyboard _videoLoadingStoryboard;
        private DispatcherTimer _videoLoadTimeoutTimer;
        private static readonly TimeSpan VideoLoadTimeout = TimeSpan.FromSeconds(8);

        private bool _firstVideoLoaded = false;
        private MediaElement _activeVideo;
        private MediaElement _bufferVideo;
        private MediaPlayer _videoPreloader;

        /// <summary>3.59：当前封面视频是否带声音（只在状态变化时通知主窗口）。</summary>
        private bool _coverAudioState = false;


        private string _lastLoadedCoverPath = "";

        // 批注层缓存（路径 + 修改时间），避免每次切卡池都重新解码
        private string _lastOverlayPath = "";
        private long _lastOverlayTicks = -1;

        // 主界面「调整封面」→ 封面编辑页：记住是哪个卡池发起的
        private string _coverEditorPoolDir = "";

        // 视频 / GIF 封面的实时模糊背景（3.32：GPU BlurEffect + 每帧更新画面）
        private DispatcherTimer _bgBlurTimer;
        private GifBitmapDecoder _bgBlurGif;
        private int _bgBlurGifIndex;
        /// <summary>3.36：背景 GIF 是否直接共用封面的解码器（共用帧号 → 画面完全同步）。</summary>
        private bool _bgBlurSharesCoverGif;
        private MediaPlayer _bgBlurPlayer;
        private bool _bgBlurApplied;
        /// <summary>3.40.1：背景播放器刚打开时过滤黑帧，避免切换卡池的一瞬间背景变黑。</summary>
        private DateTime _bgBlurPlayerOpenedUtc = DateTime.MinValue;
        private BlurEffect _bgBlurEffect;
        private bool _bgBlurRenderingHooked;
        /// <summary>当前实时模糊背景的封面路径：同一封面只改模糊强度时不要重启播放器。</summary>
        private string _bgBlurSourcePath = "";

        private bool _isAnimating = false;
        private bool _uiHidden = false;

        private bool _drawButtonsVisible = true;
        private bool _shouldShowAnimationCheckbox = false;
        private bool _statsAdvancedActive;

        private static readonly TimeSpan BtnShowYDuration = TimeSpan.FromMilliseconds(280);
        private static readonly TimeSpan BtnShowOpacityDuration = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan BtnHideDuration = TimeSpan.FromMilliseconds(200);
        private const double BtnHiddenY = 40;

        /// <summary>封面图解码宽度：显示区远小于 1600 px，按这个尺寸解码可以少占一个数量级的内存。</summary>
        /// <remarks>低性能模式再降到 1200 px，切卡池时的解码耗时和内存都更低。</remarks>
        private static int CoverDecodePixelWidth => App.LowEndMode ? 1200 : 1600;

        private Action<AppMessages.PageActivated> _onPageActivated;
        private Action<AppMessages.PoolListChanged> _onPoolListChanged;
        private Action<AppMessages.PersonalizationFinished> _onPersonalizationFinished;
        private Action<AppMessages.UiOptionsChanged> _onUiOptionsChanged;
        private Action<AppMessages.CoverEditorFinished> _onCoverEditorFinished;

        private bool _subscribed = false;
        private bool _themeHooked;

        /// <summary>3.61：主题切换后底部底衬颜色要立刻跟着变（浅色半透明白 / 深色灰）。</summary>
        private void HookThemeChanged()
        {
            if (_themeHooked) return;
            _themeHooked = true;
            try { ThemeManager.ThemeChanged += OnThemeChanged; } catch { }
        }

        private void UnhookThemeChanged()
        {
            if (!_themeHooked) return;
            _themeHooked = false;
            try { ThemeManager.ThemeChanged -= OnThemeChanged; } catch { }
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            try { UpdateStatusTextContrast(); } catch { }
        }

        private PersonalizationSettings _currentPersonalization = new PersonalizationSettings();

        public MainPage_UC()
        {
            InitializeComponent();
            _activeVideo = DisplayVideoA;
            _bufferVideo = DisplayVideoB;

            _onPageActivated = OnPageActivated;
            _onPoolListChanged = OnPoolListChanged;
            _onPersonalizationFinished = OnPersonalizationFinished;
            _onUiOptionsChanged = OnUiOptionsChanged;
            _onCoverEditorFinished = OnCoverEditorFinished;

            Loaded += MainPage_UC_Loaded;
            Unloaded += MainPage_UC_Unloaded;

            // 窗口大小变化时封面显示矩形也会变，圆角裁剪要跟着重算
            try { CoverVisualHost.SizeChanged += (s, e) => ApplyCoverRounding(); } catch { }
        }

        /// <summary>3.64：当前是「简洁」布局还是「详细」布局。</summary>
        private bool IsCompactLayout => App.MainPageLayout == App.MainPageLayoutKind.Compact;

        /// <summary>3.64：两套底部布局的可见性（布局切换 / 卡池变化 / 入场动画时调用）。
        /// 3.65：抽取过程中按钮收起时用 Hidden（保留占位），避免底部布局高度变化导致跳动；
        /// 只有「这套布局本身不用 / 这块内容本来就没有」才用 Collapsed。
        /// 3.68：简洁布局下「查看抽取报告」收进面板，原位换成「不重复抽取」开关。</summary>
        private void ApplyBottomChromeVisibility()
        {
            try
            {
                bool compact = IsCompactLayout;
                bool show = _drawButtonsVisible;
                bool hasPool = _currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count;

                if (StatusHost != null)
                    StatusHost.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (CompactStatsHost != null)
                    CompactStatsHost.Visibility = compact
                        ? (show ? Visibility.Visible : Visibility.Hidden)
                        : Visibility.Collapsed;

                if (AnimRow != null)
                    AnimRow.Visibility = _shouldShowAnimationCheckbox
                        ? (show ? Visibility.Visible : Visibility.Hidden)
                        : Visibility.Collapsed;

                if (BtnViewReport != null)
                    BtnViewReport.Visibility = (!compact && _showReportButton)
                        ? (show ? Visibility.Visible : Visibility.Hidden)
                        : Visibility.Collapsed;

                if (NoRepeatRow != null)
                    NoRepeatRow.Visibility = (compact && hasPool && !_statsAdvancedActive)
                        ? (show ? Visibility.Visible : Visibility.Hidden)
                        : Visibility.Collapsed;

                if (ChkDrawAnimationShell != null)
                {
                    bool any = _shouldShowAnimationCheckbox
                               || (!compact && _showReportButton)
                               || (compact && hasPool && !_statsAdvancedActive);
                    ChkDrawAnimationShell.Visibility = any
                        ? (show ? Visibility.Visible : Visibility.Hidden)
                        : Visibility.Collapsed;
                }

                UpdatePopupActions();
            }
            catch { }
        }

        /// <summary>3.68：「抽取详情」面板里的三个操作跟随当前状态显示。
        /// 3.68.1：高级抽卡规则卡池没有「不重复抽取 / 重置 / 本次抽取详情」，这几个按钮一起收起。</summary>
        private void UpdatePopupActions()
        {
            try
            {
                bool noRepeat = !_statsAdvancedActive
                                && (ChkNoRepeat?.IsChecked == true || ChkNoRepeatCompact?.IsChecked == true);
                var actionsVis = noRepeat ? Visibility.Visible : Visibility.Collapsed;
                if (BtnPopupReset != null) BtnPopupReset.Visibility = actionsVis;
                if (BtnPopupDetail != null) BtnPopupDetail.Visibility = actionsVis;
                if (RowPopupDetail != null) RowPopupDetail.Visibility = actionsVis;
                if (BtnPopupReport != null)
                    BtnPopupReport.Visibility = _showReportButton ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>3.64：按「选项 → 常规 → 外观 → 主页布局」切换底部（简洁 / 详细）。
        /// 3.68.1：简洁布局的两个开关再缩小一档并左对齐。
        /// 3.70：简洁布局底行固定成按钮高度（40），开关行再多也不会把底部按钮顶上去；
        /// 开关块按内容自适应高度，1 个 / 2 个开关时底衬都贴合内容、与按钮垂直居中。</summary>
        private void ApplyLayoutMode()
        {
            try
            {
                bool compact = IsCompactLayout;

                // 简洁布局：左区不再撑 96 高，底行跟着变矮，按钮会往下走。
                // 底部留 8 而不是 2 —— 略微靠近窗口底边，但不碰到。
                BottomPanelRoot.Margin = compact
                    ? new Thickness(0, 8, 0, 8)
                    : new Thickness(0, 8, 0, 4);

                // 简洁布局：底行固定 44（和「1 次抽取」按钮同高附近），按钮位置不随开关行数变化；
                // 开关缩到 0.6、行高 22，两行正好 44，不会被底行裁掉
                BottomPanelRoot.Height = compact ? 44 : double.NaN;
                BottomPanelRoot.VerticalAlignment = compact ? VerticalAlignment.Top : VerticalAlignment.Stretch;

                // 3.74.0：详细布局的状态区固定 112 高。高级卡池最多 4 行状态文字（累计 / UP3星 / 3星 / 保底），
                // 原来 96 偶尔放不下，文字会被窗口底边裁断。固定后无论哪个卡池，底部高度和按钮位置都不变。
                if (StatusHost != null)
                    StatusHost.Height = compact ? double.NaN : 112;

                // 开关大小 / 行高 / 对齐方式按布局切换
                double toggleScale = compact ? 0.6 : 0.82;
                // 3.74.0：详细布局的两个开关要用同一档缩放（以前只给「抽取动画」赋值，
                // 「不重复抽取」靠 XAML 里的 0.82，一旦样式/布局改动就会一大一小）
                if (ChkDrawAnimation != null)
                    ChkDrawAnimation.LayoutTransform = new ScaleTransform(toggleScale, toggleScale);
                if (ChkNoRepeat != null)
                    ChkNoRepeat.LayoutTransform = new ScaleTransform(toggleScale, toggleScale);
                if (ChkNoRepeatCompact != null)
                    ChkNoRepeatCompact.LayoutTransform = new ScaleTransform(0.6, 0.6);
                if (AnimRow != null)
                {
                    AnimRow.Height = compact ? 22 : 30;
                    AnimRow.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                    AnimRow.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
                }
                if (ChkDrawAnimationShell != null)
                {
                    ChkDrawAnimationShell.MinHeight = 0;
                    ChkDrawAnimationShell.Padding = compact
                        ? new Thickness(8, 0, 8, 0)
                        : new Thickness(8, 6, 8, 6);
                }

                ApplyBottomChromeVisibility();
                ApplyAcrylicButtons(App.MainBackgroundBlur);
                UpdateStatusTextContrast();
                try { UpdateLayout(); } catch { }
            }
            catch { }
        }

        /// <summary>3.64：入场动画作用在「整块」上——简洁是左区两块，详细是状态区和右侧开关块。</summary>
        private List<FrameworkElement> DrawButtonAnimationTargets()
        {
            var targets = new List<FrameworkElement>();
            if (BtnDraw1 != null) targets.Add(BtnDraw1);
            if (BtnDrawMulti != null) targets.Add(BtnDrawMulti);

            if (IsCompactLayout)
            {
                if (CompactStatsHost != null) targets.Add(CompactStatsHost);
                if (ChkDrawAnimationShell != null) targets.Add(ChkDrawAnimationShell);
            }
            else
            {
                if (ChkDrawAnimationShell != null && (_shouldShowAnimationCheckbox || _showReportButton))
                    targets.Add(ChkDrawAnimationShell);
                if (PanelStatsNormal != null && !_statsAdvancedActive) targets.Add(PanelStatsNormal);
                if (PanelStatsAdvanced != null && _statsAdvancedActive) targets.Add(PanelStatsAdvanced);
            }
            return targets;
        }

        public void SetDrawButtonsVisible(bool visible)
        {
            if (_drawButtonsVisible == visible) return;
            _drawButtonsVisible = visible;
            AnimateDrawButtons(visible);
        }

        public void SetDrawButtonsVisibleInstant(bool visible)
        {
            _drawButtonsVisible = visible;
            ApplyBottomChromeVisibility();

            foreach (var el in DrawButtonAnimationTargets())
            {
                StopDrawButtonAnimation(el);
                if (el.RenderTransform is TranslateTransform t) t.Y = 0;
                el.Opacity = 1;
                el.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }

            // 3.64：必须在可见性摆好之后再刷新底衬 —— 否则「收起」时状态区已经 Hidden，
            // 底衬却还按旧的可见状态算，会残留一条边。
            try { UpdateStatusTextContrast(); } catch { }
        }
        private void AnimateDrawButtons(bool visible)
        {
            var targets = DrawButtonAnimationTargets();
            if (targets.Count == 0) return;

            // 3.64：布局切换后重新摆一次可见性（开关行 / 查看报告按钮可能刚被切进来）
            ApplyBottomChromeVisibility();
            if (visible)
            {
                foreach (var el in targets) el.Visibility = Visibility.Visible;
            }
            try { UpdateStatusTextContrast(); } catch { }

            if (App.IsAnimationDisabled)
            {
                foreach (var el in targets)
                {
                    StopDrawButtonAnimation(el);
                    // ★ 关键：把位移与透明度复位，避免上一次动画残留导致按钮错位。
                    if (el.RenderTransform is TranslateTransform resetT) resetT.Y = 0;
                    el.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
                    el.Opacity = 1;
                }
                return;
            }

            if (visible)
            {
                // 先让按钮进入布局并完成一次排版，再启动动画，避免首帧跳变/卡顿
                foreach (var el in targets)
                {
                    el.Visibility = Visibility.Visible;
                    el.Opacity = 0;
                }
                UpdateLayout();
            }

            foreach (var el in targets)
                AnimateSingleDrawButton(el, visible);
        }

        private static void StopDrawButtonAnimation(FrameworkElement el)
        {
            try
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                if (el.RenderTransform is TranslateTransform t)
                    t.BeginAnimation(TranslateTransform.YProperty, null);
            }
            catch { }
        }

        private static void AnimateSingleDrawButton(FrameworkElement el, bool visible)
        {
            var t = el.RenderTransform as TranslateTransform;
            if (t == null)
            {
                t = new TranslateTransform();
                el.RenderTransform = t;
            }

            el.BeginAnimation(UIElement.OpacityProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            if (visible)
            {
                el.Visibility = Visibility.Visible;
                el.Opacity = 0;
                t.Y = BtnHiddenY;

                var yAnim = new DoubleAnimation(BtnHiddenY, 0, BtnShowYDuration)
                { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
                var opAnim = new DoubleAnimation(0, 1, BtnShowOpacityDuration)
                { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };

                t.BeginAnimation(TranslateTransform.YProperty, yAnim);
                el.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
            else
            {
                double startY = t.Y;
                double startOpacity = el.Opacity;
                if (startOpacity <= 0) startOpacity = 1;

                var yAnim = new DoubleAnimation(startY, BtnHiddenY, BtnHideDuration)
                { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd };
                var opAnim = new DoubleAnimation(startOpacity, 0, BtnHideDuration)
                { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd };

                bool fired = false;
                Action finish = () =>
                {
                    if (fired) return;
                    fired = true;
                    try
                    {
                        el.BeginAnimation(UIElement.OpacityProperty, null);
                        t.BeginAnimation(TranslateTransform.YProperty, null);
                        el.Opacity = 1;
                        t.Y = 0;
                        // 3.65：用 Hidden 而不是 Collapsed —— 收起时保留占位，
                        // 否则底行高度会变、按钮整体往下跳（点「多次抽取」时最明显）。
                        el.Visibility = Visibility.Hidden;
                    }
                    catch { }
                };

                opAnim.Completed += (s, e) => finish();

                t.BeginAnimation(TranslateTransform.YProperty, yAnim);
                el.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
        }

        private void Subscribe()
        {
            {
            };
            if (_subscribed) return;
            _subscribed = true;
            MessageBus.Subscribe(_onPageActivated);
            MessageBus.Subscribe(_onPoolListChanged);
            MessageBus.Subscribe(_onPersonalizationFinished);
            MessageBus.Subscribe(_onUiOptionsChanged);
            MessageBus.Subscribe(_onCoverEditorFinished);
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            MessageBus.Unsubscribe(_onPageActivated);
            MessageBus.Unsubscribe(_onPoolListChanged);
            MessageBus.Unsubscribe(_onPersonalizationFinished);
            MessageBus.Unsubscribe(_onUiOptionsChanged);

            // ★ 关键：从主界面进入封面编辑页时，MainPage 会先从 PageHost 移除并触发 Unloaded；
            //   编辑页「保存封面」发的 CoverEditorFinished 正好在这个空档里。如果这里也退订，
            //   主界面就永远收不到保存结果（表现为「右键调整封面 → 保存」没有生效）。
            //   MainPage 与窗口同生命周期，保留这一个订阅不会泄漏。
            //   （回到主界面时 Loaded 会重新 Subscribe，MessageBus 内部按 handler 去重。）
        }

        private async void MainPage_UC_Loaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            HookThemeChanged();
            HookStatsWindow();   // 3.65：固定「抽取详情」面板后，点别处要能关掉

            StartPoolWatcher();

            var hostWindow = Window.GetWindow(this);
            if (hostWindow != null && _reportRowHooked == false)
            {
                _reportRowHooked = true;
            }

            if (_initialized) return;
            _initialized = true;

            App.LoadSettings();

            // 3.64：先按「主页布局」摆好底部（简洁 / 详细）
            ApplyLayoutMode();

            bool quickLoaded = false;
            try { quickLoaded = TryQuickLoadLastPool(); }
            catch { quickLoaded = false; }
            if (!quickLoaded) UpdateUIState();

            ShowSyncingIndicator(true);
            List<PoolInfo> scanned = null;
            string scanError = null;
            var scanErrors = new List<string>();
            try { scanned = await Task.Run(() => ScanAllPoolsData(scanErrors)); }
            catch (Exception ex) { scanned = null; scanError = ex.Message; }

            if (scanned != null && scanned.Count > 0)
            {
                _allPools = scanned;
                UpdateUIState();
                if (!RestoreLastSelection())
                {
                    for (int i = 0; i < _allPools.Count; i++)
                        if (_allPools[i].ItemCount > 0) { SelectPool(i); break; }
                }
            }
            else if (!quickLoaded) { _allPools.Clear(); UpdateUIState(); }

            ShowSyncingIndicator(false);
            if (!string.IsNullOrEmpty(scanError)) scanErrors.Insert(0, "读取卡池失败：\n\n" + scanError);
            foreach (var err in scanErrors) RuntimeLog.Error(err);
            if (scanErrors.Count > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        CustomMessageBox.Show(Window.GetWindow(this),
                            string.Join("\n\n", scanErrors) + "\n\n请检查卡池目录是否存在、是否有读取权限。",
                            "读取卡池失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { }
                }), DispatcherPriority.Background);
            }
            UpdateDrawCountDisplay();
            ApplyPoolUiState();
            TxtResetHyperlink.Visibility = ChkNoRepeat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (TxtDrawDetailLink != null)
                TxtDrawDetailLink.Visibility = ChkNoRepeat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MainPage_UC_Unloaded(object sender, RoutedEventArgs e)
        {
            Unsubscribe();
            UnhookThemeChanged();
            UnhookStatsWindow();
            try { if (DrawStatsPopup != null) DrawStatsPopup.IsOpen = false; } catch { }

            StopPoolWatcher();
            // 3.34：离开主界面时保留最后一帧模糊画面（视频 / GIF 也保持糊的），
            // 返回时不会先闪出一张放大的原始帧。
            try { StopRealtimeBlur(keepVisual: true); } catch { }
            try { StopGifPlayback(); } catch { }
            try { ShowVideoLoading(false); } catch { }
            try { StopAllVideos(); } catch { }
            // 3.59：离开主界面后封面视频不再出声，恢复背景音乐（否则选项页等会一直静音）
            PublishCoverAudioState(false);
            try { DisposeVideoPreloader(); } catch { }
        }

        private void DisposeVideoPreloader()
        {
            if (_videoPreloader != null)
            {
                try { _videoPreloader.Stop(); } catch { }
                try { _videoPreloader.Close(); } catch { }
                _videoPreloader = null;
            }
        }

        private void StopAllVideos()
        {
            try
            {
                if (_activeVideo != null) { _activeVideo.Stop(); _activeVideo.Source = null; _activeVideo.Visibility = Visibility.Collapsed; _activeVideo.Opacity = 0; }
            }
            catch { }
            try
            {
                if (_bufferVideo != null) { _bufferVideo.Stop(); _bufferVideo.Source = null; _bufferVideo.Visibility = Visibility.Collapsed; _bufferVideo.Opacity = 0; }
            }
            catch { }
        }


        public bool TryHandleEscape()
        {
            // 3.65：「抽取详情」面板打开时，Esc 先把它关掉
            if (DrawStatsPopup != null && DrawStatsPopup.IsOpen)
            {
                HideDrawStatsPanel();
                return true;
            }
            return false;
        }

        public void PlayEnterAnimation(Action onComplete)
        {
            _isAnimating = false;
            _uiHidden = true;

            // 进入页面时先按即将展示的卡池刷新「抽卡动画」复选框，
            // 避免从上一页（其它卡池）残留的显示状态闪一下。
            RefreshAnimationCheckboxForSelectedPool();

            // 3.37：返回主界面时先什么都不动 —— 离开时封面与背景都保留了最后一帧，
            // 动画过程中如果重启播放器，背景会重新从第 0 帧开始（看起来「闪一下」）。
            // 封面 / 背景统一在动画结束后重载，并由 SyncRealtimeBlurToCover 对齐。

            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = 1;

            LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
            LeftPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);
            CenterPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);
            BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            BottomPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);

            LeftPanelTransform.X = -300;
            LeftPanelRoot.Opacity = 0;
            CenterPanelRoot.Opacity = 0;
            BottomPanelTransform.Y = 400;
            SetDrawButtonsVisibleInstant(true);   // 底部面板整体滑入，内部按钮不再单独播放动画。
            BottomPanelRoot.Opacity = 0;

            Action afterAnim = () =>
            {
                onComplete?.Invoke();

                // ★ 从其它页面回来时强制重新加载当前卡池封面：
                //   离开主界面时视频 / GIF 都被停掉了，原来靠「DisplayImage 还可见」来判断要不要重启，
                //   一旦控件状态被清空就再也不恢复（切换卡池才能好）。现在直接按当前卡池重新加载。
                // 3.37：封面先重载（GIF 解码器就绪），背景随后启动，两者共用同一份数据。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count) return;
                        ReloadCurrentCover(force: true);
                        UpdatePoolBackground(_allPools[_currentPoolIndex]);
                    }
                    catch { }
                }), DispatcherPriority.Background);
            };

            AnimateUI(false, afterAnim);
        }

        public void PlayExitAnimation(Action onComplete)
        {
            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = 1;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fade.Completed += (s, e) =>
            {
                try
                {
                    LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
                    LeftPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);
                    CenterPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);
                    BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
                    BottomPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);

                    LeftPanelTransform.X = -300;
                    LeftPanelRoot.Opacity = 0;
                    CenterPanelRoot.Opacity = 0;
                    BottomPanelTransform.Y = this.ActualHeight + 120;
                    BottomPanelRoot.Opacity = 0;

                    _uiHidden = true;
                    _isAnimating = false;

                    this.BeginAnimation(UIElement.OpacityProperty, null);
                    this.Opacity = 1;
                }
                catch { }
                finally { _isAnimating = false; }
                onComplete?.Invoke();
            };
            this.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);

        /// <summary>3.41：进入主界面时各块同时开始（中间 / 底部不再延迟入场）。</summary>
        private static readonly TimeSpan EnterCenterDelay = TimeSpan.Zero;
        private static readonly TimeSpan EnterBottomDelay = TimeSpan.Zero;

        private void AnimateUI(bool hide, Action onComplete = null)
        {
            double leftTo = hide ? -300 : 0;
            double bottomTo = hide ? this.ActualHeight + 120 : 0;
            double opacityTo = hide ? 0 : 1;

            if (App.IsAnimationDisabled || _isAnimating)
            {
                LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
                LeftPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);
                CenterPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);
                BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
                BottomPanelRoot.BeginAnimation(UIElement.OpacityProperty, null);

                LeftPanelTransform.X = leftTo;
                LeftPanelRoot.Opacity = opacityTo;
                CenterPanelRoot.Opacity = opacityTo;
                BottomPanelTransform.Y = bottomTo;
                BottomPanelRoot.Opacity = opacityTo;

                _isAnimating = false;
                _uiHidden = hide;

                // 与动画版保持一致的时序：延后一帧再回调，避免视觉布局在页面状态就绪前处理
                if (onComplete != null)
                    Dispatcher.BeginInvoke(new Action(() => onComplete()), DispatcherPriority.Background);
                return;
            }

            _isAnimating = true;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            const int total = 5;
            int finished = 0;
            Action onDone = () =>
            {
                finished++;
                if (finished >= total)
                {
                    _isAnimating = false;
                    _uiHidden = hide;
                    onComplete?.Invoke();
                }
            };

            if (hide)
            {
                var dur = TimeSpan.FromMilliseconds(200);
                AnimateTranslate(LeftPanelTransform, TranslateTransform.XProperty, leftTo, dur, ease, onDone, TimeSpan.Zero);
                AnimateOpacity(LeftPanelRoot, opacityTo, dur, ease, onDone, TimeSpan.Zero);
                AnimateOpacity(CenterPanelRoot, opacityTo, dur, ease, onDone, TimeSpan.Zero);
                AnimateTranslate(BottomPanelTransform, TranslateTransform.YProperty, bottomTo, dur, ease, onDone, TimeSpan.Zero);
                AnimateOpacity(BottomPanelRoot, opacityTo, dur, ease, onDone, TimeSpan.Zero);
            }
            else
            {
                var durLeft = TimeSpan.FromMilliseconds(220);
                var durCenter = TimeSpan.FromMilliseconds(200);
                var durBottom = TimeSpan.FromMilliseconds(230);
                // 3.41：底部面板不再延迟入场，与左侧 / 中间控件同一时刻开始
                var delayCenter = EnterCenterDelay;
                var delayBottom = EnterBottomDelay;

                AnimateTranslate(LeftPanelTransform, TranslateTransform.XProperty, leftTo, durLeft, easeOut, onDone, TimeSpan.Zero);
                AnimateOpacity(LeftPanelRoot, opacityTo, durLeft, easeOut, onDone, TimeSpan.Zero);
                AnimateOpacity(CenterPanelRoot, opacityTo, durCenter, easeOut, onDone, delayCenter);
                AnimateTranslate(BottomPanelTransform, TranslateTransform.YProperty, bottomTo, durBottom, easeOut, onDone, delayBottom);
                AnimateOpacity(BottomPanelRoot, opacityTo, durCenter, easeOut, onDone, delayBottom);
            }
        }

        private static void AnimateTranslate(Animatable target, DependencyProperty prop, double to, TimeSpan dur, IEasingFunction ease, Action onComplete, TimeSpan beginTime)
        {
            var anim = new DoubleAnimation { To = to, Duration = dur, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd, BeginTime = beginTime };
            anim.Completed += (s, e) => onComplete?.Invoke();
            target.BeginAnimation(prop, anim);
        }

        private static void AnimateOpacity(UIElement target, double to, TimeSpan dur, IEasingFunction ease, Action onComplete, TimeSpan beginTime)
        {
            var anim = new DoubleAnimation { To = to, Duration = dur, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd, BeginTime = beginTime };
            anim.Completed += (s, e) => onComplete?.Invoke();
            target.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        public void SelectNextPool()
        {
            if (_allPools.Count == 0) return;
            int next = _currentPoolIndex < 0 ? 0 : (_currentPoolIndex + 1) % _allPools.Count;
            if (next == _currentPoolIndex) return;
            SelectPool(next);
        }

        public void SelectPrevPool()
        {
            if (_allPools.Count == 0) return;
            int prev = _currentPoolIndex < 0 ? (_allPools.Count - 1) : (_currentPoolIndex - 1 + _allPools.Count) % _allPools.Count;
            if (prev == _currentPoolIndex) return;
            SelectPool(prev);
        }

        private void OnPageActivated(AppMessages.PageActivated msg)
        {
            if (msg == null || msg.PageKey != "Main") return;
            UpdateDrawAnimationCheckboxVisibility();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_initialized) return;
                RefreshPoolsKeepingSelection(forceRebuild: false);
                RefreshPoolContextMenus();

                // ★ 3.31：从选项页回来时，全局的「主界面背景模糊」/强度可能刚改过
                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                {
                    try { UpdatePoolBackground(_allPools[_currentPoolIndex]); } catch { }
                }
            }), DispatcherPriority.Background);
        }

        /// <summary>右键菜单样式可能已在选项页修改，回到主界面时重建缓存菜单。</summary>
        private void RefreshPoolContextMenus()
        {
            try
            {
                foreach (var child in PoolButtonsPanel.Children)
                {
                    if (child is Button btn &&
                        int.TryParse(btn.Tag?.ToString(), out int idx) && idx >= 0 && idx < _allPools.Count)
                        btn.ContextMenu = BuildPoolContextMenu(idx);
                }
            }
            catch { }
        }

        private void OnPoolListChanged(AppMessages.PoolListChanged msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_initialized) return;
                RefreshPoolsKeepingSelection(forceRebuild: true);
            }), DispatcherPriority.Background);
        }

        private void OnPersonalizationFinished(AppMessages.PersonalizationFinished msg)
        {
            if (msg == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_initialized) return;
                string targetDir = App.LastSelectedPoolDir;
                if (string.IsNullOrEmpty(targetDir)) return;

                int idx = _allPools.FindIndex(p => string.Equals(p.Dir, targetDir, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return;
                var pool = _allPools[idx];

                try { if (PoolArchive.NeedsUnpack(pool.Dir)) PoolArchive.UnpackFromArchive(pool.Dir); }
                catch { }

                if (idx == _currentPoolIndex)
                {
                    LoadPersonalizationForPool(pool);
                    UpdatePoolBackground(pool);
                    UpdateDrawCountDisplay();
                }
            }), DispatcherPriority.Background);
        }

        private void OnUiOptionsChanged(AppMessages.UiOptionsChanged msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 3.64：主页布局 / 背景模糊改了要立刻重新摆底部
                ApplyLayoutMode();
                UpdateDrawCountDisplay();
                UpdateReportButton();
                RefreshPoolContextMenus();

                // ★ 主界面正显示时改了「禁用动画」等外观选项：封面（视频 / GIF）可能已经被停掉，
                //   这里重新加载一次，避免要切卡池才恢复。
                if (IsVisible) { try { ReloadCurrentCover(); } catch { } }

                // ★ 3.31：选项 → 常规 → 外观里的「主界面背景模糊」与强度，改完立刻生效
                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                {
                    try { UpdatePoolBackground(_allPools[_currentPoolIndex]); } catch { }
                }
            }), DispatcherPriority.Background);
        }

        private void RefreshPoolsFromMenu()
        {
            try
            {
                RuntimeLog.Info("手动刷新卡池列表");
                RefreshPoolsKeepingSelection(true);
                ToastNotification.ShowSuccess(Window.GetWindow(this), "刷新成功");
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "刷新失败：" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowsePoolFolderAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;
            try
            {
                string dir = _allPools[index].Dir;
                if (!Directory.Exists(dir))
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "卡池文件夹不存在：\n\n" + dir,
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                RuntimeLog.Info($"浏览卡池文件夹：{_allPools[index].UniqueName}，目录={dir}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + dir + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "浏览卡池文件夹失败");
                CustomMessageBox.Show(Window.GetWindow(this), "无法打开文件夹：" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool _refreshingPools;
        private bool _pendingPoolRefresh;
        private bool _pendingPoolRefreshForce;

        /// <summary>
        /// 刷新卡池列表：扫描放到后台线程（卡池多、或在网络盘上时不再卡住 UI），
        /// 扫描完再回 UI 线程套用结果；同一时刻只允许一次刷新。
        /// 刷新期间来的请求会被记下来，结束后补跑一次 —— 否则「解锁 / 改名 / 删除」
        /// 这类改动可能正好撞上自动刷新而被丢掉，别的页面就不同步了。
        /// </summary>
        private async void RefreshPoolsKeepingSelection(bool forceRebuild)
        {
            if (_refreshingPools)
            {
                _pendingPoolRefresh = true;
                _pendingPoolRefreshForce |= forceRebuild;
                return;
            }

            _refreshingPools = true;

            var oldPools = _allPools;
            int oldCurrentIdx = _currentPoolIndex;

            try
            {
                List<PoolInfo> scanned;
                try { scanned = await Task.Run(() => ScanAllPoolsData()); }
                catch (Exception ex)
                {
                    RuntimeLog.Error(ex, "扫描卡池目录失败");
                    return;
                }

                ApplyScannedPools(oldPools, oldCurrentIdx, scanned, forceRebuild);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "刷新卡池列表失败");
            }
            finally
            {
                _refreshingPools = false;

                if (_pendingPoolRefresh)
                {
                    bool force = _pendingPoolRefreshForce;
                    _pendingPoolRefresh = false;
                    _pendingPoolRefreshForce = false;

                    Dispatcher.BeginInvoke(new Action(() => RefreshPoolsKeepingSelection(force)),
                        DispatcherPriority.Background);
                }
            }
        }

        /// <summary>
        /// 判断「列表里显示的东西」有没有变（名字 / 锁状态 / 条目数 / 封面）。
        /// 变了就必须重建按钮，否则会出现「已经取消加密，但主页还显示锁图标」这类不同步。
        /// </summary>
        private static bool PoolsVisualChanged(List<PoolInfo> oldPools, List<PoolInfo> newPools)
        {
            if (oldPools.Count != newPools.Count) return true;

            for (int i = 0; i < oldPools.Count; i++)
            {
                var a = oldPools[i];
                var b = newPools[i];
                if (!string.Equals(a.UniqueName, b.UniqueName, StringComparison.Ordinal)) return true;
                if (a.IsLocked != b.IsLocked) return true;
                if (a.ItemCount != b.ItemCount) return true;
                if (!string.Equals(a.CoverPath ?? "", b.CoverPath ?? "", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private void ApplyScannedPools(List<PoolInfo> oldPools, int oldCurrentIdx,
                                       List<PoolInfo> newPools, bool forceRebuild)
        {
            var oldDirs = oldPools.Select(p => p.Dir).ToList();
            string lastDir = App.LastSelectedPoolDir;

            var newDirs = newPools.Select(p => p.Dir).ToList();
            bool sameDirs = oldDirs.Count == newDirs.Count
                && oldDirs.Zip(newDirs, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(x => x);

            if (sameDirs && !forceRebuild)
            {
                string curDir = (oldCurrentIdx >= 0 && oldCurrentIdx < oldPools.Count) ? oldPools[oldCurrentIdx].Dir : null;
                _allPools = newPools;

                if (!string.IsNullOrEmpty(curDir))
                {
                    int newIdx = _allPools.FindIndex(p => string.Equals(p.Dir, curDir, StringComparison.OrdinalIgnoreCase));
                    if (newIdx >= 0)
                    {
                        _currentPoolIndex = newIdx;
                        var curPool = _allPools[newIdx];

                        bool contentChanged = false;
                        if (oldCurrentIdx >= 0 && oldCurrentIdx < oldPools.Count)
                        {
                            string oldHash = oldPools[oldCurrentIdx].ContentHash ?? "";
                            string newHash = curPool.ContentHash ?? "";
                            contentChanged = !string.Equals(oldHash, newHash, StringComparison.Ordinal);
                        }
                        if (contentChanged)
                        {
                            Engine.ResetDrawnIndices();
                        }

                        try { if (PoolArchive.NeedsUnpack(curPool.Dir)) PoolArchive.UnpackFromArchive(curPool.Dir); } catch { }

                        // ★ 目录集合没变也可能「显示内容」变了（取消加密、改名、换封面…），
                        //   这时必须重建按钮，否则主页会一直显示旧的锁图标 / 旧名字。
                        if (PoolsVisualChanged(oldPools, newPools))
                        {
                            UpdateUIState();
                            UpdatePoolUI();
                        }

                        LoadPersonalizationForPool(curPool);
                        UpdatePoolBackground(curPool);
                        ApplyPoolUiState();
                        UpdateDrawCountDisplay();
                        try { LoadCoverForPool(curPool); } catch { }
                        return;
                    }
                }

                UpdateUIState();
                for (int i = 0; i < _allPools.Count; i++)
                    if (_allPools[i].ItemCount > 0) { SelectPool(i); break; }
                UpdateDrawCountDisplay();
                return;
            }

            _allPools = newPools;
            UpdateUIState();

            if (!string.IsNullOrEmpty(lastDir))
            {
                int idx = _allPools.FindIndex(p => string.Equals(p.Dir, lastDir, StringComparison.OrdinalIgnoreCase));
                int oldLastIdx = oldPools.FindIndex(p => string.Equals(p.Dir, lastDir, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    if (idx != _currentPoolIndex || forceRebuild)
                    {
                        _currentPoolIndex = idx;

                        if (oldLastIdx >= 0 && oldLastIdx < oldPools.Count)
                        {
                            string oldHash = oldPools[oldLastIdx].ContentHash ?? "";
                            string newHash = _allPools[idx].ContentHash ?? "";
                            if (!string.Equals(oldHash, newHash, StringComparison.Ordinal))
                                Engine.ResetDrawnIndices();
                        }

                        UpdatePoolUI();
                        try { LoadCoverForPool(_allPools[idx]); } catch { }
                        LoadPersonalizationForPool(_allPools[idx]);
                        UpdatePoolBackground(_allPools[idx]);
                    }
                    else
                    {
                        UpdatePoolUI();
                        try { LoadCoverForPool(_allPools[idx]); } catch { }
                    }
                    UpdateDrawCountDisplay();
                    return;
                }
            }

            bool anySelected = false;
            if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
            {
                if (string.Equals(_allPools[_currentPoolIndex].Dir, lastDir, StringComparison.OrdinalIgnoreCase))
                {
                    anySelected = true;
                }
            }

            if (!anySelected)
            {
                if (_allPools.Count > 0)
                {
                    int firstIdx = -1;
                    for (int i = 0; i < _allPools.Count; i++)
                    {
                        if (_allPools[i].ItemCount > 0) { firstIdx = i; break; }
                    }
                    if (firstIdx < 0) firstIdx = 0;
                    SelectPool(firstIdx);
                }
                else
                {
                    _currentPoolIndex = -1;
                    UpdatePoolUI();
                    GrayPlaceholder.Visibility = Visibility.Visible;
                    EmptyStatePanel.Visibility = Visibility.Visible;
                    DisplayImage.Visibility = Visibility.Collapsed;
                    HideDefaultCoverName();
                    App.LastSelectedPoolDir = "";
                    App.LastSelectedPoolName = "";
                    App.SaveSettings();
                }
            }
            UpdateDrawCountDisplay();
        }

        private bool TryQuickLoadLastPool()
        {
            string dir = App.LastSelectedPoolDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var pool = LoadSinglePoolFromDirStatic(dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);
            if (pool == null) return false;
            _allPools.Clear();
            _allPools.Add(pool);
            UpdateUIState();
            SelectPool(0);
            return true;
        }

        private bool RestoreLastSelection()
        {
            if (!string.IsNullOrEmpty(App.LastSelectedPoolDir))
            {
                int idx = _allPools.FindIndex(p => p.Dir == App.LastSelectedPoolDir);
                if (idx >= 0) { SelectPool(idx); return true; }
            }
            if (!string.IsNullOrEmpty(App.LastSelectedPoolName))
            {
                int idx = _allPools.FindIndex(p => p.UniqueName == App.LastSelectedPoolName || p.Name == App.LastSelectedPoolName);
                if (idx >= 0) { SelectPool(idx); return true; }
            }
            return false;
        }

        /// <summary>卡池解析缓存：目录 → (文件戳, 解析结果)，文件没变就不重复解析。</summary>
        private static readonly Dictionary<string, (string Stamp, PoolInfo Pool)> PoolScanCache =
            new Dictionary<string, (string, PoolInfo)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>3.67.1：扫描串行化。后台刷新与手动刷新（F5 / 菜单 / 测试兜底）可能同时调用，
        /// 两边一起写 <see cref="PoolScanCache"/> 会抛「集合已被修改」并让扫描结果变成空列表。</summary>
        private static readonly object PoolScanLock = new object();

        /// <summary>用数据文件 / 封面 / 个性化文件的时间戳与大小做「是否变化」的快速判断。</summary>
        private static string ComputePoolStamp(string dir)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var name in new[] { "data.txt", "data.lock", "data.zip", "personalization.json", "gacha_settings.ini" })
            {
                try
                {
                    var fi = new FileInfo(Path.Combine(dir, name));
                    sb.Append(name).Append('=')
                      .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks + ":" + fi.Length : "-")
                      .Append(';');
                }
                catch { }
            }

            try
            {
                var covers = PoolCover.GetCoverFiles(dir);
                sb.Append("covers=").Append(string.Join(",", covers));
            }
            catch { }

            return sb.ToString();
        }

        private static List<PoolInfo> ScanAllPoolsData(List<string> errors = null)
        {
            // 扫描会读写静态缓存，必须串行（见 PoolScanLock）
            lock (PoolScanLock)
            {
                return ScanAllPoolsDataCore(errors);
            }
        }

        private static List<PoolInfo> ScanAllPoolsDataCore(List<string> errors)
        {
            var result = new List<PoolInfo>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var baseDir in App.SearchDirectories)
            {
                if (!Directory.Exists(baseDir)) continue;
                RuntimeLog.Info($"扫描卡池目录：{baseDir}");

                IEnumerable<string> dirs;
                try { dirs = Directory.GetDirectories(baseDir); }
                catch (Exception ex)
                {
                    errors?.Add($"读取卡池目录失败：\n{baseDir}\n\n{ex.Message}");
                    RuntimeLog.Error(ex, $"读取卡池目录失败：{baseDir}");
                    continue;
                }

                foreach (var dir in dirs.OrderBy(d => d))
                {
                    string stamp = ComputePoolStamp(dir);
                    bool fromCache = PoolScanCache.TryGetValue(dir, out var entry) && entry.Stamp == stamp;
                    PoolInfo pool;

                    if (fromCache)
                    {
                        pool = entry.Pool;
                    }
                    else
                    {
                        pool = LoadSinglePoolFromDirStatic(dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase), errors);
                        if (pool != null) PoolScanCache[dir] = (stamp, pool);
                    }

                    if (pool == null) continue;

                    // 唯一名称需要按本次扫描的完整列表重新分配（其它卡池可能改名/新增）
                    string unique = pool.Name;
                    int suffix = 2;
                    while (usedNames.Contains(unique))
                    {
                        unique = pool.Name + " [" + suffix + "]";
                        suffix++;
                    }
                    usedNames.Add(unique);
                    pool.UniqueName = unique;

                    result.Add(pool);
                    if (!fromCache)
                        RuntimeLog.Info($"读取卡池：{pool.UniqueName}，目录={pool.Dir}，条目={pool.ItemCount}，锁定={pool.IsLocked}，封面={(string.IsNullOrEmpty(pool.CoverPath) ? "无" : "有")}");
                }
            }
            try
            {
                var alive = new HashSet<string>(result.Select(p => p.Dir), StringComparer.OrdinalIgnoreCase);
                foreach (var key in PoolScanCache.Keys.ToList())
                {
                    if (!alive.Contains(key)) PoolScanCache.Remove(key);
                }
            }
            catch { }

            RuntimeLog.Info($"扫描卡池完成：{result.Count} 个（搜索目录 {App.SearchDirectories.Count} 个）");
            if (errors != null)
                foreach (var error in errors) RuntimeLog.Error("卡池读取：" + error);
            return result;
        }
        private static PoolInfo LoadSinglePoolFromDirStatic(string dir, HashSet<string> usedNames, List<string> errors = null)
        {
            try
            {
                string poolName = Path.GetFileName(dir);
                string content = string.Empty;
                string lockFile = Path.Combine(dir, "data.lock");
                string zipFile = Path.Combine(dir, "data.zip");
                string txtFile = Path.Combine(dir, "data.txt");
                bool hasAnyDataFile = File.Exists(txtFile) || File.Exists(lockFile) || File.Exists(zipFile);
                if (!hasAnyDataFile) return null;

                if (File.Exists(txtFile))
                {
                    try { content = CryptoUtils.ReadAllTextWithEncoding(txtFile); }
                    catch (Exception ex) { errors?.Add($"读取卡池失败：\n{dir}\n\n{ex.Message}"); }
                }
                if (string.IsNullOrEmpty(content) && (File.Exists(lockFile) || File.Exists(zipFile)))
                {
                    try { content = PoolArchive.ReadDataTxtFromArchive(dir); }
                    catch (Exception ex) { errors?.Add($"读取卡池失败：\n{dir}\n\n{ex.Message}"); }
                }
                if (string.IsNullOrEmpty(content) && File.Exists(lockFile))
                {
                    try { content = CryptoUtils.DecryptString(File.ReadAllText(lockFile)); }
                    catch (Exception ex) { errors?.Add($"读取卡池失败：\n{dir}\n\n{ex.Message}"); }
                }

                if (string.IsNullOrEmpty(content))
                {
                    errors?.Add($"读取卡池失败：\n{dir}\n\ndata.txt / data.lock / data.zip 无法读取或内容为空。");
                    return null;
                }
                bool isLocked = File.Exists(lockFile);

                string uniqueName = poolName;
                int suffix = 2;
                while (usedNames.Contains(uniqueName)) { uniqueName = $"{poolName} [{suffix}]"; suffix++; }
                usedNames.Add(uniqueName);

                var pool = new PoolInfo { Name = poolName, UniqueName = uniqueName, Dir = dir, IsLocked = isLocked };
                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (lines.Count > 0)
                {
                    pool.Headers = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    int probIndex = pool.Headers.IndexOf("概率");
                    pool.HasProbability = probIndex >= 0;

                    for (int i = 1; i < lines.Count; i++)
                    {
                        var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) continue;
                        var item = new DrawItem { Fields = parts.ToList() };
                        if (pool.HasProbability && probIndex < parts.Length)
                            item.Probability = ParseProbabilitySafeStatic(parts[probIndex]);
                        pool.Items.Add(item);
                    }
                }

                // ★ 主界面要播放原始封面（GIF / 视频），批注层单独叠在上面；
                //   不能拿 cover_preview.png（静态合成图），否则动画封面会被定格。
                string originalCover = PoolCover.GetOriginalCoverPath(dir);
                if (!string.IsNullOrEmpty(originalCover)) pool.CoverPath = originalCover;
                else
                {
                    var covers = PoolCover.GetCoverFiles(dir);
                    if (covers.Length > 0) pool.CoverPath = covers[0];
                }

                pool.ItemCount = pool.Items.Count;
                pool.ContentHash = ComputeContentHash(content);

                return pool;
            }
            catch (Exception ex)
            {
                errors?.Add($"读取卡池失败：\n{dir}\n\n{ex.Message}");
                return null;
            }
        }

        private static string ComputeContentHash(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            try
            {
                string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
                    var hash = sha.ComputeHash(bytes);
                    var sb = new System.Text.StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        private static double ParseProbabilitySafeStatic(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            bool hasPercent = s.EndsWith("%");
            if (hasPercent) s = s.TrimEnd('%').Trim();
            if (!double.TryParse(s, out double v)) return 0;
            return hasPercent ? v / 100.0 : v;
        }

        private void ShowSyncingIndicator(bool show)
        {
            if (SyncIndicator == null) return;
            SyncIndicator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                if (_syncStoryboard == null) _syncStoryboard = (Storyboard)FindResource("SyncRingStoryboard");
                if (show) { try { _syncStoryboard.Stop(this); } catch { } _syncStoryboard.Begin(this, true); }
                else { try { _syncStoryboard.Stop(this); } catch { } try { _syncStoryboard.Remove(this); } catch { } }
            }
            catch { }
        }

        private System.Windows.Threading.DispatcherTimer _poolWatchTimer;
        private string _lastPoolSignature = "";
        private void UpdateUIState()
        {
            bool hasAnyPool = _allPools.Count > 0;
            GrayPlaceholder.Visibility = hasAnyPool ? Visibility.Collapsed : Visibility.Visible;
            EmptyStatePanel.Visibility = hasAnyPool ? Visibility.Collapsed : Visibility.Visible;

            // 无卡池时隐藏底部状态文字，并把抽取按钮变灰
            if (BottomStatsGrid != null)
                BottomStatsGrid.Visibility = hasAnyPool ? Visibility.Visible : Visibility.Collapsed;
            if (BtnDraw1 != null) BtnDraw1.IsEnabled = hasAnyPool;
            if (BtnDrawMulti != null) BtnDrawMulti.IsEnabled = hasAnyPool;

            try { if (_activeVideo != null) _activeVideo.Stop(); } catch { }
            DisplayImage.Visibility = Visibility.Collapsed;
            if (_activeVideo != null) _activeVideo.Visibility = Visibility.Collapsed;
            HideDefaultCoverName();

            PoolButtonsPanel.Children.Clear();
            for (int i = 0; i < _allPools.Count; i++)
            {
                Button btn = new Button
                {
                    Content = BuildPoolButtonContent(_allPools[i]),
                    Style = (Style?)TryFindResource(App.MainBackgroundBlur ? "AcrylicPoolButtonStyle" : "PoolButtonStyle")
                            ?? (Style?)TryFindResource("PoolButtonStyle"),
                    Tag = i
                };
                btn.Click += BtnPool_Click;
                AttachTouchContextMenu(btn);
                btn.ContextMenu = BuildPoolContextMenu(i);
                PoolButtonsPanel.Children.Add(btn);
            }

            // 3.33：卡池列表重建后，其它主界面按钮也按当前背景模糊开关刷新样式
            ApplyAcrylicButtons(App.MainBackgroundBlur);
        }

        private static object BuildPoolButtonContent(PoolInfo pool)
        {
            string displayText = pool?.UniqueName ?? "";
            bool isLocked = pool != null && pool.IsLocked;

            var dock = new DockPanel { LastChildFill = true };

            if (isLocked)
            {
                var lockIcon = new TextBlock
                {
                    Text = "\uE72E",
                    FontFamily = App.IconFontFamily,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 6, 4)
                };
                DockPanel.SetDock(lockIcon, Dock.Left);
                dock.Children.Add(lockIcon);
            }

            var textBlock = new TextBlock
            {
                Text = displayText,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };

            dock.Children.Add(textBlock);
            return dock;
        }

        private void DrawDetail_Click(object sender, MouseButtonEventArgs e) => ShowDrawDetailDialog();

        private void ShowDrawDetailDialog()
        {
            try
            {
                var session = DrawSession.Current;
                PoolInfo pool = session?.Pool;
                if (pool == null && _currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                    pool = _allPools[_currentPoolIndex];

                var window = Window.GetWindow(this);
                if (pool?.Items == null || pool.Items.Count == 0)
                {
                    CustomMessageBox.Show(window, "当前没有可展示的卡池数据。", "本次抽取详情", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var drawn = new List<string>();
                var undrawn = new List<string>();

                for (int i = 0; i < pool.Items.Count; i++)
                {
                    string name = pool.Items[i].Fields != null && pool.Items[i].Fields.Count > 0
                        ? pool.Items[i].Fields[0]
                        : pool.Items[i].DisplayName;

                    if (Engine.DrawnIndices.Contains(i)) drawn.Add(name);
                    else undrawn.Add(name);
                }

                var dialog = new DrawDetailDialog(drawn, undrawn) { Owner = window };
                dialog.ShowDialog();
            }
            catch { }
        }
        /// <summary>触摸屏：长按卡池按钮等同于右键，可打开卡池菜单。</summary>
        private static void AttachTouchContextMenu(Button btn)
        {
            try
            {
                var holdTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(650)
                };

                Point startPoint = new Point(0, 0);
                bool fired = false;

                holdTimer.Tick += (s, e) =>
                {
                    holdTimer.Stop();
                    fired = true;

                    try
                    {
                        if (btn.ContextMenu == null) return;
                        btn.ContextMenu.PlacementTarget = btn;
                        btn.ContextMenu.IsOpen = true;
                    }
                    catch { }
                };

                btn.PreviewTouchDown += (s, e) =>
                {
                    try
                    {
                        fired = false;
                        startPoint = e.GetTouchPoint(btn).Position;
                        holdTimer.Stop();
                        holdTimer.Start();
                    }
                    catch { }
                };

                btn.PreviewTouchMove += (s, e) =>
                {
                    try
                    {
                        Point p = e.GetTouchPoint(btn).Position;
                        if ((p - startPoint).Length > 14) holdTimer.Stop();
                    }
                    catch { }
                };

                btn.PreviewTouchUp += (s, e) =>
                {
                    try
                    {
                        holdTimer.Stop();

                        // 长按后松手，避免同时触发选中
                        if (fired) e.Handled = true;
                    }
                    catch { }
                };

                // ★ 卡池列表刷新时按钮会被重建：离开视觉树就停掉计时器，
                //   否则每个旧按钮的 650ms 计时器会一直被 Dispatcher 持有。
                btn.Unloaded += (s, e) =>
                {
                    try { holdTimer.Stop(); } catch { }
                };
            }
            catch { }
        }
        private void PoolListBlank_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 只有真正点在空白区域时才使用空白菜单；点在卡池按钮上交给按钮自己的菜单。
            if (FindButtonAncestor(e.OriginalSource as DependencyObject) != null) return;

            var menu = BuildBlankPoolContextMenu();
            if (menu == null) return;

            menu.PlacementTarget = sender as UIElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>卡池列表空白处的右键菜单：新建卡池 / 导入 / 刷新。</summary>
        private ContextMenu BuildBlankPoolContextMenu()
        {
            var menu = new ContextMenu();
            bool modern = App.UseModernContextMenu;
            if (modern && TryFindResource("Win11ContextMenuStyle") is Style modernMenu)
                menu.Style = modernMenu;
            else if (!modern && TryFindResource("LegacyContextMenuStyle") is Style legacyMenu)
                menu.Style = legacyMenu;

            var newPool = CreatePoolMenuItem("新建卡池", "N", "\uE710", () =>
                MessageBus.Publish(new AppMessages.OpenPoolEditor("")));
            var import = CreatePoolMenuItem("导入", "F", "\uE838", ImportPoolFromFile);
            var refresh = CreatePoolMenuItem("刷新", "R", "\uE72C", RefreshPoolsFromMenu);
            menu.Items.Add(newPool);
            menu.Items.Add(import);
            menu.Items.Add(refresh);

            var itemStyle = TryFindResource(modern ? "Win11MenuItemStyle" : "LegacyMenuItemStyle") as Style;
            if (itemStyle != null)
            {
                newPool.Style = itemStyle;
                import.Style = itemStyle;
                refresh.Style = itemStyle;
            }

            menu.PreviewKeyDown += (s, ev) =>
            {
                Key key = ev.Key == Key.ImeProcessed ? ev.ImeProcessedKey : ev.Key;
                if (key != Key.N && key != Key.R && key != Key.F) return;
                ev.Handled = true;
                menu.IsOpen = false;
                try
                {
                    if (key == Key.N) MessageBus.Publish(new AppMessages.OpenPoolEditor(""));
                    else if (key == Key.F) ImportPoolFromFile();
                    else RefreshPoolsFromMenu();
                }
                catch { }
            };

            // 菜单期间挂起中文输入法，避免关掉菜单后输入法卡住
            ContextMenuImeGuard.Attach(menu);

            return menu;
        }

        private static Button FindButtonAncestor(DependencyObject node)
        {
            while (node != null)
            {
                if (node is Button button) return button;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return null;
        }

        private ContextMenu BuildPoolContextMenu(int index)
        {
            bool modern = App.UseModernContextMenu;
            var menu = new ContextMenu();
            if (modern && TryFindResource("Win11ContextMenuStyle") is Style menuStyle)
                menu.Style = menuStyle;
            else if (!modern && TryFindResource("LegacyContextMenuStyle") is Style legacyMenuStyle)
                menu.Style = legacyMenuStyle;

            var newPool = CreatePoolMenuItem("新建卡池", "N", "\uE710", () =>
                MessageBus.Publish(new AppMessages.OpenPoolEditor("")));
            var refresh = CreatePoolMenuItem("刷新", "R", "\uE72C", RefreshPoolsFromMenu);
            var browse = CreatePoolMenuItem("浏览文件夹", "B", "\uE838", () => BrowsePoolFolderAt(index));
            var export = CreatePoolMenuItem("导出", "G", "\uE8A7", () => ExportPoolAt(index));
            var edit = CreatePoolMenuItem("编辑卡池信息", "E", "\uE70F", () => EditPoolAt(index));
            var rename = CreatePoolMenuItem("重命名", "M", "\uE8AC", () => RenamePoolAt(index));
            var cover = CreatePoolMenuItem("调整封面", "C", "\uE91B", () => PickCoverAt(index));
            var history = CreatePoolMenuItem("查看历史记录", "H", "\uE81C", () => OpenPoolHistoryAt(index));
            var delete = CreatePoolMenuItem("删除", "D", "\uE74D", () => DeletePoolAt(index));

            menu.Items.Add(newPool);
            menu.Items.Add(refresh);
            menu.Items.Add(browse);
            menu.Items.Add(export);
            menu.Items.Add(new Separator());
            // 3.74.0：选项里勾了「隐藏『编辑卡池』」时，右键菜单里的入口也要一起收起
            // （以前只隐藏了选项页的「编辑」按钮，这里漏了，设置看起来像没生效）
            if (App.IsEditPoolVisible) menu.Items.Add(edit);
            menu.Items.Add(rename);
            menu.Items.Add(cover);
            menu.Items.Add(history);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);

            if (modern)
            {
                var itemStyle = TryFindResource("Win11MenuItemStyle") as Style;
                var sepStyle = TryFindResource("Win11SeparatorStyle") as Style;
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem mi && itemStyle != null) mi.Style = itemStyle;
                    else if (item is Separator sep && sepStyle != null) sep.Style = sepStyle;
                }
            }
            else
            {
                var itemStyle = TryFindResource("LegacyMenuItemStyle") as Style;
                var sepStyle = TryFindResource("LegacySeparatorStyle") as Style;
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem mi && itemStyle != null) mi.Style = itemStyle;
                    else if (item is Separator sep && sepStyle != null) sep.Style = sepStyle;
                }
            }

            // 快捷键：不使用 AccessText（与中文输入法冲突），改为菜单内直接处理按键
            menu.PreviewKeyDown += (s, e) =>
            {
                Action action = null;
                Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

                switch (key)
                {
                    case Key.N: action = () => MessageBus.Publish(new AppMessages.OpenPoolEditor("")); break;
                    case Key.R: action = RefreshPoolsFromMenu; break;
                    case Key.B: action = () => BrowsePoolFolderAt(index); break;
                    case Key.G: action = () => ExportPoolAt(index); break;
                    case Key.E: if (App.IsEditPoolVisible) action = () => EditPoolAt(index); break;
                    case Key.M: action = () => RenamePoolAt(index); break;
                    case Key.C: action = () => PickCoverAt(index); break;
                    case Key.H: action = () => OpenPoolHistoryAt(index); break;
                    case Key.D: action = () => DeletePoolAt(index); break;
                }

                if (action == null) return;

                e.Handled = true;
                menu.IsOpen = false;
                try { action(); } catch { }
            };

            menu.Closed += (s, e) =>
            {
                // 菜单关闭后把焦点还给原目标，避免输入法停留在菜单焦点状态。
                try { InputMethod.SetIsInputMethodEnabled(menu, true); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (menu.PlacementTarget is IInputElement target)
                            Keyboard.Focus(target);
                        else
                        {
                            var win = Window.GetWindow(this);
                            win?.Activate();
                        }
                    }
                    catch { }
                }), DispatcherPriority.Input);
            };

            // 鼠标移出菜单后立即关闭，避免菜单长期占据键盘焦点导致输入法卡住。
            bool menuHovered = false;
            menu.MouseEnter += (s, e) => menuHovered = true;
            menu.MouseLeave += (s, e) =>
            {
                if (!menuHovered) return;
                menuHovered = false;
                try { menu.IsOpen = false; } catch { }
            };

            // 菜单期间挂起中文输入法，避免关掉菜单后输入法卡住
            ContextMenuImeGuard.Attach(menu);

            return menu;
        }

        /// <summary>
        /// 导出卡池：直接用「选项 → 卡池」的导出弹窗（勾选卡池、加密卡池验密码、进度、结果提示都在里面）。
        /// 右键哪个卡池就默认勾上哪个。
        /// </summary>
        private void ExportPoolAt(int index)
        {
            try
            {
                if (index < 0 || index >= _allPools.Count) return;
                var window = Window.GetWindow(this);
                if (_allPools.Count == 0)
                {
                    CustomMessageBox.Show(window, "当前没有可导出的卡池。", "导出卡池",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var pools = _allPools.Select(p => (Name: p.UniqueName, Dir: p.Dir)).ToList();
                var dlg = new ExportPoolsDialog(pools, _allPools[index].Dir) { Owner = window };
                dlg.ShowDialog();

                if (dlg.ExportedCount > 0)
                    ToastNotification.ShowSuccess(window, $"已导出 {dlg.ExportedCount} 个卡池");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "打开导出卡池弹窗失败");
                CustomMessageBox.Show(Window.GetWindow(this), "导出失败：\n" + ex.Message, "导出卡池",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 导入卡池：直接用「选项 → 卡池」的导入弹窗（支持拖拽 / 多选 / 进度 / 失败汇总）。
        /// </summary>
        private void ImportPoolFromFile()
        {
            try
            {
                var window = Window.GetWindow(this);

                string target = "";
                try
                {
                    string current = CurrentPoolDir();
                    if (!string.IsNullOrEmpty(current)) target = Path.GetDirectoryName(current) ?? "";
                }
                catch { }

                if (string.IsNullOrEmpty(target) || !App.IsValidDirectoryPath(target) || !App.IsDirectoryWritable(target))
                {
                    try
                    {
                        target = App.SearchDirectories?.FirstOrDefault(d =>
                            App.IsValidDirectoryPath(d) && App.IsDirectoryWritable(d)) ?? "";
                    }
                    catch { target = ""; }
                }

                if (string.IsNullOrEmpty(target))
                {
                    CustomMessageBox.Show(window,
                        "没有可以写入的卡池目录。\n\n请先到「选项 → 系统」添加一个卡池列表目录，并确认它有写入权限。",
                        "无法导入", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    return;
                }

                var dlg = new ImportPoolsDialog(target) { Owner = window };
                if (dlg.ShowDialog() != true) return;

                RefreshPoolsFromMenu();
                if (dlg.ImportedCount > 0)
                    ToastNotification.ShowSuccess(window, $"已导入 {dlg.ImportedCount} 个卡池");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "打开导入卡池弹窗失败");
                CustomMessageBox.Show(Window.GetWindow(this), "导入失败：\n" + ex.Message, "导入卡池",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static MenuItem CreatePoolMenuItem(string header, string gesture, string glyph, Action action)
        {
            var item = new MenuItem
            {
                // 传统写法：名称后直接写(字母)，不作为访问键，避免与中文输入法冲突
                Header = $"{header}({gesture})",
                Icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = App.IconFontFamily,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            item.Click += (s, e) => { try { action(); } catch { } };
            return item;
        }

        /// <summary>右键「调整封面」：和卡池页的「修改封面」一样，进入封面编辑页。</summary>
        private void PickCoverAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            var pool = _allPools[index];
            _coverEditorPoolDir = pool.Dir;

            // 换封面不属于「修改名单」，按产品要求不校验密码
            MessageBus.Publish(new AppMessages.OpenCoverEditor(pool.Dir, PoolCover.GetOriginalCoverPath(pool.Dir)));
            MessageBus.Publish(new AppMessages.NavigateTo("CoverEditor"));
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a ?? ""), Path.GetFullPath(b ?? ""),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>封面编辑页在主界面发起时：把底图 / 批注层 / 两个开关写回卡池，然后刷新封面。</summary>
        private void OnCoverEditorFinished(AppMessages.CoverEditorFinished msg)
        {
            if (msg == null) return;
            if (!msg.Saved)
            {
                // 取消 / 关闭编辑页：不要留下「待写回」的卡池
                _coverEditorPoolDir = "";
                return;
            }

            string dir = _coverEditorPoolDir;
            if (string.IsNullOrEmpty(dir)) return;
            if (!string.IsNullOrEmpty(msg.PoolDir) && !SamePath(msg.PoolDir, dir)) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _coverEditorPoolDir = "";

                    // 3.38：编辑默认封面时没有封面文件，只写批注层 / 圆角
                    bool useDefaultCover = msg.UseDefaultCover;
                    if (!useDefaultCover
                        && (string.IsNullOrEmpty(msg.CoverPath) || !File.Exists(msg.CoverPath))) return;

                    if (!useDefaultCover) PoolCover.SaveCover(dir, msg.CoverPath);
                    if (!string.IsNullOrEmpty(msg.OverlayPath) && File.Exists(msg.OverlayPath))
                        PoolCover.SaveOverlay(dir, msg.OverlayPath);
                    else
                        PoolCover.SaveOverlay(dir, "");
                    PoolCover.SetBlurBackground(dir, msg.BlurBackground);
                    PoolCover.SetRoundCover(dir, msg.RoundCover);

                    var pool = _allPools.FirstOrDefault(p => SamePath(p.Dir, dir));
                    if (pool != null)
                    {
                        pool.CoverPath = PoolCover.GetOriginalCoverPath(dir) ?? "";
                        if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count
                            && SamePath(_allPools[_currentPoolIndex].Dir, dir))
                        {
                            _lastLoadedCoverPath = null;
                            try { LoadCoverForPool(pool); } catch { }
                            UpdatePoolBackground(pool);
                        }
                    }

                    MessageBus.Publish(new AppMessages.PoolListChanged());
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error(ex, "应用封面编辑结果失败（主界面）");
                }
            }));
        }
        /// <summary>
        /// 加密卡池的统一密码校验：编辑 / 重命名 / 换封面 都必须先过这一关。
        /// （删除按产品要求不需要密码，所以不调用这里。）
        /// </summary>
        private bool ConfirmLockedPoolAccess(string poolDir, string action)
        {
            if (!PoolFileOps.IsPoolLocked(poolDir)) return true;

            var window = Window.GetWindow(this);
            string lockedName = Path.GetFileName(poolDir);

            var pwd = new PasswordDialog($"“{lockedName}”需要验证密码才能{action}") { Owner = window };
            var pwdResult = pwd.ShowDialog();

            if (pwd.ForgotPasswordRequested)
                return TryResetPasswordBySecretQA(poolDir, lockedName);

            if (pwdResult != true) return false;

            if (!CryptoUtils.VerifyPassword(lockedName, pwd.Password))
            {
                CustomMessageBox.Show(window, "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            return true;
        }

        private void EditPoolAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            var pool = _allPools[index];

            // 编辑加密卡池：必须先验证密码
            if (!ConfirmLockedPoolAccess(pool.Dir, "修改")) return;

            // 走到这里：未加密，或密码已验证通过
            MessageBus.Publish(new AppMessages.OpenPoolEditor(pool.Dir, fromMainPage: true, passwordVerified: true));
        }

        private void RenamePoolAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            var pool = _allPools[index];

            var window = Window.GetWindow(this);
            string lockedName = Path.GetFileName(pool.Dir);

            // 锁定的卡池：先验证密码，再改名。
            if (!ConfirmLockedPoolAccess(pool.Dir, "重命名")) return;

            string oldName = Path.GetFileName(pool.Dir);
            string input = InputDialog.Show(Window.GetWindow(this), "重命名卡池", "请输入新的卡池名称：", oldName);
            if (string.IsNullOrWhiteSpace(input)) return;

            input = input.Trim();
            if (input == oldName) return;

            if (input.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "名称中不能包含 \\ / : * ? \" < > | 等字符。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string parent = Path.GetDirectoryName(pool.Dir);
            string target = Path.Combine(parent, input);
            if (Directory.Exists(target))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "同名的卡池已存在，请换一个名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Directory.Move(pool.Dir, target);
                CryptoUtils.MovePasswordHash(oldName, input);

                if (string.Equals(App.LastSelectedPoolDir, pool.Dir, StringComparison.OrdinalIgnoreCase))
                {
                    App.LastSelectedPoolDir = target;
                    App.SaveSettings();
                }

                RefreshPoolsKeepingSelection(true);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "重命名失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenPoolHistoryAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            App.LastSelectedPoolDir = _allPools[index].Dir;
            App.SaveSettings();
            MessageBus.Publish(new AppMessages.NavigateTo("History"));
        }

        private void DeletePoolAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            var pool = _allPools[index];
            string name = pool.UniqueName ?? Path.GetFileName(pool.Dir);

            var window = Window.GetWindow(this);

            // 删除按产品要求不校验密码（即使卡池设置了密码），只做一次普通确认。
            var result = CustomMessageBox.Show(Window.GetWindow(this), $"确定要删除「{name}」吗？此操作不可恢复。",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question,
                yesText: "删除", noText: "取消", yesIcon: "\uE74D");
            if (result != MessageBoxResult.Yes) return;

            bool ok = true;
            string error = "";

            ProgressDialog.Run(Window.GetWindow(this), "正在删除卡池", $"正在删除「{name}」。", report =>
            {
                try { PoolFileOps.DeleteWithProgress(pool.Dir, report); }
                catch (Exception ex) { ok = false; error = ex.Message; }
            });

            if (!ok)
            {
                RuntimeLog.Error($"删除卡池失败：{name}，{error}");
                CustomMessageBox.Show(Window.GetWindow(this), "删除失败：" + error, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RuntimeLog.Info($"删除卡池成功：{name}，目录={pool.Dir}");

            if (string.Equals(App.LastSelectedPoolDir, pool.Dir, StringComparison.OrdinalIgnoreCase))
            {
                App.LastSelectedPoolDir = "";
                App.SaveSettings();
            }

            RefreshPoolsKeepingSelection(true);
        }
        /// <summary>外部增删卡池文件夹时，界面跟随刷新（不锁文件夹）。</summary>
        private void StartPoolWatcher()
        {
            try
            {
                StopPoolWatcher();

                UpdatePoolWatchSignature();

                _poolWatchTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1200)
                };
                _poolWatchTimer.Tick += (s, e) =>
                {
                    try
                    {
                        string sig = PoolWatcher.BuildSignature();
                        if (sig == _lastPoolSignature) return;

                        _lastPoolSignature = sig;
                        RefreshPoolsKeepingSelection(false);
                    }
                    catch { }
                };
                _poolWatchTimer.Start();
            }
            catch { }
        }

        private void StopPoolWatcher()
        {
            try
            {
                if (_poolWatchTimer == null) return;
                _poolWatchTimer.Stop();
                _poolWatchTimer = null;
            }
            catch { }
        }

        private void UpdatePoolWatchSignature()
        {
            try { _lastPoolSignature = PoolWatcher.BuildSignature(); } catch { }
        }
        private void UpdatePoolUI()
        {
            ApplyPoolUiState();
            UpdateReportButton();
            UpdatePoolWatchSignature();

            foreach (Button btn in PoolButtonsPanel.Children)
            {
                if (int.TryParse(btn.Tag?.ToString(), out int idx))
                {
                    SetIsSelected(btn, idx == _currentPoolIndex);
                }
            }
        }

        private void BtnPool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int index))
                SelectPool(index);
        }

        private void SelectPool(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            var pool = _allPools[index];
            EnsurePoolItemsLoaded(pool);
            if (DrawSession.Current != null) DrawSession.Current.Pool = pool;
            ReleaseOtherPoolItems(index);

            _currentPoolIndex = index;
            Engine.ResetDrawnIndices();
            RuntimeLog.Info($"选择卡池：{pool.UniqueName}，条目={pool.ItemCount}");
            UpdatePoolUI();
            GrayPlaceholder.Visibility = Visibility.Collapsed;

            try
            {
                if (PoolArchive.NeedsUnpack(pool.Dir))
                {
                    PoolArchive.UnpackFromArchive(pool.Dir);
                    var covers = PoolCover.GetCoverFiles(pool.Dir);
                    pool.CoverPath = covers.Length > 0 ? covers[0] : "";
                }
            }
            catch { }

            LoadPersonalizationForPool(pool);

            // 3.37：封面先同步更新，背景随后跟上。
            // 以前封面包在 Dispatcher.BeginInvoke(Background) 里，点选卡池时背景已经变了、
            // 封面还停在上一张，看起来像「封面延迟更新」。
            try { LoadCoverForPool(pool); } catch { }

            // ★ 点选卡池时也要刷新「模糊背景」，否则只有从别的页面回来才生效
            UpdatePoolBackground(pool);

            App.LastSelectedPoolName = pool.UniqueName;
            App.LastSelectedPoolDir = pool.Dir;
            App.SaveSettings();

            MessageBus.Publish(new AppMessages.CurrentPoolChanged(pool.Dir));

            UpdateDrawCountDisplay();
        }

        private static void EnsurePoolItemsLoaded(PoolInfo pool)
        {
            if (pool == null || pool.ItemCount <= 0 || pool.Items.Count > 0) return;

            try
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pool.UniqueName };
                var loaded = LoadSinglePoolFromDirStatic(pool.Dir, usedNames);
                if (loaded == null || loaded.Items.Count == 0) return;

                pool.Headers = loaded.Headers;
                pool.Items = loaded.Items;
                pool.HasProbability = loaded.HasProbability;
                pool.CoverPath = loaded.CoverPath;
                pool.ContentHash = loaded.ContentHash;
            }
            catch { }
        }

        private void ReleaseOtherPoolItems(int selectedIndex)
        {
            for (int i = 0; i < _allPools.Count; i++)
            {
                if (i == selectedIndex) continue;
                var pool = _allPools[i];
                if (pool?.Items == null || pool.Items.Count == 0) continue;
                if (ReferenceEquals(pool, DrawSession.Current?.Pool)) continue;

                pool.Items.Clear();
                pool.Items = new List<DrawItem>();
            }
        }

        /// <summary>
        /// 3.33：背景模糊开启时把主界面按钮切成亚克力样式（半透明填充 + 可见轮廓线）；
        /// 关闭时恢复原来的默认样式。卡池列表按钮有自己的选中 / 悬浮配色。
        /// </summary>
        private void ApplyAcrylicButtons(bool acrylic)
        {
            try
            {
                var buttonStyle = acrylic ? TryFindResource("AcrylicButtonStyle") as Style : null;
                var poolStyle = (acrylic ? TryFindResource("AcrylicPoolButtonStyle")
                                         : TryFindResource("PoolButtonStyle")) as Style;
                // 3.34：「查看抽取报告」保持无边框链接外观（亚克力只用在悬浮底色上），
                // 不再跟着其它按钮套 AcrylicButtonStyle，否则会多出一圈方框。
                // 3.64：「抽取详情」「查看抽取报告」在简洁布局里也是同款链接按钮。
                var reportStyle = (acrylic ? TryFindResource("AcrylicReportLinkButtonStyle")
                                           : TryFindResource("ReportLinkButtonStyle")) as Style;
                // 3.74.1：「抽取详情」外层有半透明底衬，模糊开启时改用不带内部悬浮色块的样式，
                // 加深交给底衬自己处理
                var statsStyle = (acrylic ? TryFindResource("AcrylicShellLinkButtonStyle")
                                          : TryFindResource("ReportLinkButtonStyle")) as Style;

                foreach (var btn in new[] { BtnOptions, BtnHistory, BtnDraw1, BtnDrawMulti })
                {
                    if (btn == null) continue;
                    if (buttonStyle != null) btn.Style = buttonStyle;
                    else btn.ClearValue(FrameworkElement.StyleProperty);
                }

                if (reportStyle != null)
                {
                    if (BtnViewReport != null) BtnViewReport.Style = reportStyle;
                    if (BtnDrawStats != null) BtnDrawStats.Style = statsStyle ?? reportStyle;
                }

                if (EmptyStatePanel != null)
                {
                    foreach (var btn in EmptyStatePanel.Children.OfType<Button>())
                    {
                        if (buttonStyle != null) btn.Style = buttonStyle;
                        else btn.ClearValue(FrameworkElement.StyleProperty);
                    }
                }

                if (PoolButtonsPanel != null && poolStyle != null)
                {
                    foreach (var btn in PoolButtonsPanel.Children.OfType<Button>())
                        btn.Style = poolStyle;
                }
            }
            catch { }
        }

        private void UpdatePoolBackground(PoolInfo pool)
        {
            try
            {
                // 3.33：背景模糊开启时，主界面按钮统一切成亚克力（半透明 + 轮廓线）
                ApplyAcrylicButtons(App.MainBackgroundBlur);

                if (PoolBgImage == null) return;

                bool use = pool != null && App.MainBackgroundBlur;
                string cover = use ? PoolCover.GetOriginalCoverPath(pool.Dir) : "";

                if (!use)
                {
                    StopRealtimeBlur();
                    PoolBgImage.Source = null;
                    FadePoolBackground(0);
                    UpdateStatusTextContrast();
                    return;
                }

                // 3.33：卡池没有封面时主界面显示的是内置默认封面，背景模糊也用同一张
                if (string.IsNullOrEmpty(cover) || !File.Exists(cover))
                {
                    ApplyStaticPoolBackground("", isDefaultCover: true);
                    return;
                }

                string ext = Path.GetExtension(cover).ToLowerInvariant();
                bool isGif = ext == ".gif";
                bool isVideo = VideoExts.Contains(ext);

                // GIF / 视频：每帧取当前画面，背景与封面同步
                if (isGif || isVideo)
                {
                    StartRealtimeBlur(cover, isVideo);
                    return;
                }

                // 3.34：静态封面用「提前算好并缓存的模糊图」当背景。
                // 以前是把原图交给 GPU BlurEffect，切页回来时效果层要重新栅格化，
                // 会先看到一张被放大、没糊开的画面（看起来像像素化），过一帧才恢复。
                ApplyStaticPoolBackground(cover, isDefaultCover: false);
            }
            catch { }
        }

        // ================================================================
        // 3.34：静态封面 / 默认封面的模糊背景缓存
        // 模糊本身要几十毫秒（拖动强度滑块时会反复触发），所以分两条路：
        //   ① 缓存命中 → 直接贴位图，不挂 GPU 效果（切页回来第一帧就是糊的）；
        //   ② 缓存没有 → 先用原图 + GPU 模糊顶上（立刻可见），再延迟 220 ms 算一次缓存版换上。
        // ================================================================

        private readonly Dictionary<string, BitmapSource> _staticBlurCache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>背景模糊的工作宽度：低配设备用 640，普通设备 960。</summary>
        private static double BlurWorkWidth => App.LowEndMode ? 640 : 960;

        /// <summary>背景模糊缓存张数上限：低配设备少缓存几张，省内存。</summary>
        private static int MaxStaticBlurCache => App.LowEndMode ? 4 : 8;

        private DispatcherTimer? _staticBlurTimer;
        private string _staticBlurPendingKey = "";
        private string _staticBlurPendingPath = "";
        private bool _staticBlurPendingDefault;

        private static string StaticBlurKey(string coverPath, bool isDefaultCover)
        {
            int radius = Math.Max(1, (int)Math.Round(App.MainBackgroundBlurRadius));

            // 文件被换掉（同名覆盖）时缓存必须失效，所以把「最后修改时间」也算进 key
            string stamp = "";
            if (!isDefaultCover)
            {
                try { stamp = File.GetLastWriteTimeUtc(coverPath).Ticks.ToString(); } catch { }
            }

            return (isDefaultCover ? "<default>" : coverPath) + "|" + radius + "|" + stamp;
        }

        private BitmapSource? LoadStaticBlurSource(string coverPath, bool isDefaultCover)
        {
            try
            {
                return isDefaultCover
                    ? LoadEmbeddedImage("Assets/default_cover.jpg")
                    : (ImageHelper.LoadBitmapSized(coverPath, 1920) ?? ImageBlurUtil.LoadSource(coverPath));
            }
            catch { return null; }
        }

        /// <summary>缓存命中就直接用；没命中时切换卡池直接算好再换，首次进入先用 GPU 模糊顶上。</summary>
        private void ApplyStaticPoolBackground(string coverPath, bool isDefaultCover)
        {
            string key = StaticBlurKey(coverPath, isDefaultCover);

            if (_staticBlurCache.TryGetValue(key, out var cached) && cached != null)
            {
                CancelStaticBlurBuild();
                // 3.37：保留当前背景画面（连模糊层一起），换图在同一帧内完成，不闪
                StopRealtimeBlur(keepVisual: true);
                SetStaticPoolBackground(cached);
                return;
            }

            StopRealtimeBlur(keepVisual: true);

            var raw = LoadStaticBlurSource(coverPath, isDefaultCover);
            if (raw == null)
            {
                PoolBgImage.Source = null;
                FadePoolBackground(0);
                UpdateStatusTextContrast();
                return;
            }

            // 3.40.1：切换卡池时旧背景还在，直接同步算好模糊图再换。
            // 以前这里给旧画面挂上 GPU BlurEffect 再换 Source，效果层重建会有一帧空白，
            // 深色主题 / 封面较暗时看起来就是「模糊背景黑一下」。
            bool hasBackground = PoolBgImage.Source != null && PoolBgImage.Opacity > 0.01;
            if (hasBackground)
            {
                var blurred = ImageBlurUtil.MakeBlurred(raw, App.MainBackgroundBlurRadius * 0.9, BlurWorkWidth) ?? raw;
                if (_staticBlurCache.Count >= MaxStaticBlurCache) _staticBlurCache.Clear();
                _staticBlurCache[key] = blurred;
                SetStaticPoolBackground(blurred);
                return;
            }

            // 首次进入主界面（还没有背景）：先用原图 + GPU 模糊立刻可见，再排队算缓存版
            ApplyPoolBgBlur();
            if (!ReferenceEquals(PoolBgImage.Source, raw)) PoolBgImage.Source = raw;
            FadePoolBackground(1);

            ScheduleStaticBlurBuild(coverPath, isDefaultCover, key);
        }

        private void ScheduleStaticBlurBuild(string coverPath, bool isDefaultCover, string key)
        {
            try
            {
                _staticBlurPendingKey = key;
                _staticBlurPendingPath = coverPath;
                _staticBlurPendingDefault = isDefaultCover;

                if (_staticBlurTimer == null)
                {
                    _staticBlurTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                    _staticBlurTimer.Tick += StaticBlurTimer_Tick;
                }

                // 拖动强度滑块时每一步都会走到这里：重新计时，停下来才真正算一次
                _staticBlurTimer.Stop();
                _staticBlurTimer.Start();
            }
            catch { }
        }

        private void CancelStaticBlurBuild()
        {
            try
            {
                _staticBlurPendingKey = "";
                if (_staticBlurTimer != null) _staticBlurTimer.Stop();
            }
            catch { }
        }

        private void StaticBlurTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_staticBlurTimer != null) _staticBlurTimer.Stop();

                string key = _staticBlurPendingKey;
                if (string.IsNullOrEmpty(key) || !App.MainBackgroundBlur) return;

                string path = _staticBlurPendingPath;
                bool isDefault = _staticBlurPendingDefault;

                // 排队期间又换了卡池 / 强度：这次就不算了，等新的那次
                if (!string.Equals(StaticBlurKey(path, isDefault), key, StringComparison.OrdinalIgnoreCase)) return;

                var src = LoadStaticBlurSource(path, isDefault);
                if (src == null) return;

                // 工作宽度 960：模糊本身会抹掉细节，960 再放大到窗口足够细腻
                // 低配设备降到 640，缩小计算量与内存占用
                var blurWatch = System.Diagnostics.Stopwatch.StartNew();
                var blurred = ImageBlurUtil.MakeBlurred(src, App.MainBackgroundBlurRadius * 0.9, BlurWorkWidth) ?? src;
                blurWatch.Stop();
                RuntimeLog.Info($"背景模糊生成：{(isDefault ? "默认封面" : Path.GetFileName(path))}，{blurred.PixelWidth}x{blurred.PixelHeight}，强度={App.MainBackgroundBlurRadius:0}，耗时={blurWatch.ElapsedMilliseconds}ms");

                if (_staticBlurCache.Count >= MaxStaticBlurCache) _staticBlurCache.Clear();
                _staticBlurCache[key] = blurred;

                // 只有「主界面还在用这个封面 + 这张背景」时才把 GPU 模糊换成缓存位图
                if (IsVisible && _currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                {
                    string current = PoolCover.GetOriginalCoverPath(_allPools[_currentPoolIndex].Dir);
                    bool stillCurrent = isDefault
                        ? string.IsNullOrEmpty(current) || !File.Exists(current)
                        : string.Equals(current, path, StringComparison.OrdinalIgnoreCase);

                    if (stillCurrent && string.Equals(_staticBlurPendingKey, key, StringComparison.OrdinalIgnoreCase))
                        SetStaticPoolBackground(blurred);
                }
            }
            catch { }
        }

        /// <summary>把已经糊好的静态背景贴上去（不再挂 GPU 模糊，避免切页回来重新栅格化）。</summary>
        private void SetStaticPoolBackground(BitmapSource bitmap)
        {
            try
            {
                if (PoolBgImage == null || bitmap == null) return;

                bool same = ReferenceEquals(PoolBgImage.Source, bitmap);
                PoolBgImage.Effect = null;
                PoolBgImage.Source = bitmap;

                // 同一张图（例如刚从别的页面回来）不再重新淡入，直接显示，避免闪一下
                if (same && PoolBgImage.Opacity >= 0.99) PoolBgImage.Opacity = 1;
                else FadePoolBackground(1);

                UpdateStatusTextContrast();
            }
            catch { }
        }

        // ================================================================
        // 视频 / GIF 封面的「实时模糊背景」（3.32：逐帧跟随）
        // ================================================================

        /// <summary>
        /// 3.61：背景模糊开启时，底部状态文字、右侧「抽取动画」「查看抽取报告」加一层底衬。
        /// 3.63：底衬改成与其它按钮一致的亚克力材质（半透明填充）。
        /// 3.64：取消灰色细边框；「抽取动画」与「查看抽取报告」共用一块底衬；
        /// 简洁布局下左区换成「抽取详情 / 查看抽取报告」两个按钮，用同一款底衬。
        /// 文字颜色仍然交还给主题资源，
        /// 「获得 3 星」「UP 3 星」「保底提示」这些有特殊含义的颜色一律不动。
        /// </summary>
        private void UpdateStatusTextContrast()
        {
            try
            {
                if (StatusScrim == null || BottomStatsGrid == null) return;

                bool compact = IsCompactLayout;
                bool statsVisible = !compact
                                    && _drawButtonsVisible
                                    && (PanelStatsNormal?.Visibility == Visibility.Visible
                                        || PanelStatsAdvanced?.Visibility == Visibility.Visible);

                bool blurActive = App.MainBackgroundBlur
                                  && PoolBgImage != null
                                  && PoolBgImage.Source != null
                                  && PoolBgImage.Opacity > 0.01
                                  // 没有卡池时状态区是收起的，不要留一块空底衬
                                  && BottomStatsGrid.Visibility == Visibility.Visible
                                  && statsVisible;

                // 文字颜色跟随主题：清掉这里以前设过的颜色，让 XAML 里的主题资源重新生效
                ResetStatusTextColors();

                Brush fill = blurActive ? ThemeManager.GetBrush("Ui.AcrylicFill") : null;

                StatusScrim.Background = fill ?? Brushes.Transparent;
                StatusScrim.BorderThickness = new Thickness(0);   // 3.64：不再画灰色边框

                Brush chrome = App.MainBackgroundBlur
                               && PoolBgImage != null
                               && PoolBgImage.Source != null
                               && PoolBgImage.Opacity > 0.01
                    ? (ThemeManager.GetBrush("Ui.AcrylicFill"))
                    : null;
                ApplyChromeScrim(chrome);
            }
            catch { }
        }

        /// <summary>右侧开关块 / 简洁布局按钮块的共用底衬（不画边框）。</summary>
        private void ApplyChromeScrim(Brush fill)
        {
            try
            {
                if (ChkDrawAnimationShell != null)
                {
                    bool visible = _drawButtonsVisible
                                   && ChkDrawAnimationShell.Visibility == Visibility.Visible;
                    ChkDrawAnimationShell.Background = (fill != null && visible) ? fill : Brushes.Transparent;
                    ChkDrawAnimationShell.BorderThickness = new Thickness(0);
                }
                if (CompactStatsShell != null)
                {
                    bool visible = _drawButtonsVisible && IsCompactLayout;
                    // 3.74.1：悬浮「抽取详情」时整块半透明底衬一起加深（用 AcrylicFillHover），
                    // 而不是只在里面那小块按钮上盖一层深色
                    Brush shellFill = fill;
                    if (shellFill != null && visible && CompactStatsShell.IsMouseOver)
                        shellFill = ThemeManager.GetBrush("Ui.AcrylicFillHover");
                    CompactStatsShell.Background = (shellFill != null && visible) ? shellFill : Brushes.Transparent;
                    CompactStatsShell.BorderThickness = new Thickness(0);
                }
                if (BtnViewReport != null)
                {
                    BtnViewReport.Background = Brushes.Transparent;
                    BtnViewReport.BorderThickness = new Thickness(0);
                    BtnViewReport.BorderBrush = Brushes.Transparent;
                }
                if (BtnDrawStats != null)
                {
                    BtnDrawStats.Background = Brushes.Transparent;
                    BtnDrawStats.BorderThickness = new Thickness(0);
                    BtnDrawStats.BorderBrush = Brushes.Transparent;
                }
            }
            catch { }
        }

        /// <summary>
        /// 文字颜色跟随主题：把 XAML 里的主题资源重新挂回去。
        /// 不能用 ClearValue —— 它会把 XAML 里的 DynamicResource 一起清掉，
        /// 深色主题下文字会变回默认黑色，压在灰底衬上几乎看不清。
        /// </summary>
        private void ResetStatusTextColors()
        {
            try
            {
                if (TxtNoRepeatLabel != null)
                    TxtNoRepeatLabel.SetResourceReference(TextBlock.ForegroundProperty, "Fg_000000");
                if (TxtDrawCountNormal != null)
                    TxtDrawCountNormal.SetResourceReference(TextBlock.ForegroundProperty, "Fg_000000");
                if (TxtDrawCountAdvanced != null)
                    TxtDrawCountAdvanced.SetResourceReference(TextBlock.ForegroundProperty, "Fg_000000");

                // 「重置」「本次抽取详情」两个链接里的图标与文字
                SetForegroundResourceInTree(TxtResetHyperlink, "Fg_000000");
                SetForegroundResourceInTree(TxtDrawDetailLink, "Fg_000000");
            }
            catch { }
        }

        private static void SetForegroundResourceInTree(DependencyObject root, string key)
        {
            if (root == null) return;
            if (root is TextBlock block) block.SetResourceReference(TextBlock.ForegroundProperty, key);
            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(root); } catch { }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(root, i); } catch { }
                if (child != null) SetForegroundResourceInTree(child, key);
            }
        }

        /// <summary>把当前模糊强度应用到背景图（GPU BlurEffect；改动强度立刻生效）。</summary>
        private void ApplyPoolBgBlur()
        {
            try
            {
                if (PoolBgImage == null) return;

                if (_bgBlurEffect == null)
                {
                    _bgBlurEffect = new BlurEffect
                    {
                        KernelType = KernelType.Gaussian,
                        RenderingBias = RenderingBias.Performance
                    };
                }

                // 滑块 1~100 → 屏幕上的模糊半径（与原 CPU 模糊的视觉强度接近）
                _bgBlurEffect.Radius = Math.Max(1.0, App.MainBackgroundBlurRadius * 0.9);
                PoolBgImage.Effect = _bgBlurEffect;

                UpdateStatusTextContrast();
            }
            catch { }
        }

        /// <summary>
        /// GIF / 视频封面背景：
        /// 视频在 CompositionTarget.Rendering 里逐帧截取当前画面；
        /// GIF 按每一帧自己的延迟推进。画面直接作为 Image.Source，模糊交给 GPU，
        /// 所以不用每帧重算高斯模糊（之前 400 ms 才更新一次，看起来像没同步）。
        /// </summary>
        private void StartRealtimeBlur(string coverPath, bool isVideo)
        {
            // 同一个封面只是调模糊强度（选项页拖动滑块）：不重启 MediaPlayer / GIF，避免画面从头开始
            if (string.Equals(_bgBlurSourcePath, coverPath, StringComparison.OrdinalIgnoreCase)
                && (_bgBlurPlayer != null || _bgBlurGif != null))
            {
                ApplyPoolBgBlur();
                return;
            }

            // 3.37：切换实时背景时保留上一帧（含模糊层），新帧就绪后直接替换，避免闪一下
            StopRealtimeBlur(keepVisual: true);
            _bgBlurSourcePath = coverPath;
            _bgBlurApplied = false;
            ApplyPoolBgBlur();

            try
            {
                if (isVideo)
                {
                    _bgBlurPlayer = new MediaPlayer { Volume = 0 };
                    _bgBlurPlayer.IsMuted = true;
                    // 3.33：主界面封面视频会循环播放；背景播放器也必须循环，
                    // 否则第二次播放时停在最后一帧，背景不再变化。
                    _bgBlurPlayer.MediaEnded += OnBgBlurMediaEnded;
                    _bgBlurPlayer.Open(new Uri(coverPath, UriKind.Absolute));
                    _bgBlurPlayerOpenedUtc = DateTime.UtcNow;
                    _bgBlurPlayer.Play();

                    // 3.36：封面视频已经在播时（封面没有被重载），直接把背景对齐到它的进度
                    SyncRealtimeBlurToCover(_activeVideo);
                }
                else
                {
                    // 3.36：封面 GIF 自己就在逐帧播放，背景直接共用它的解码器与帧号，
                    // 不再单独开一套计时器 —— 否则两边各自从第 0 帧开始，回来时永远差几帧。
                    _bgBlurGif = _gifDecoder;
                    _bgBlurGifIndex = _gifFrameIndex;
                    _bgBlurSharesCoverGif = _bgBlurGif != null;

                    if (_bgBlurGif == null)
                    {
                        _bgBlurGif = ImageHelper.LoadGifFromFile(coverPath);
                        _bgBlurGifIndex = 0;
                        _bgBlurSharesCoverGif = false;
                    }

                    if (_bgBlurGif == null || _bgBlurGif.Frames.Count == 0)
                    {
                        StopRealtimeBlur();
                        FadePoolBackground(0);
                        return;
                    }
                }
            }
            catch
            {
                StopRealtimeBlur();
                FadePoolBackground(0);
                return;
            }

            // 先糊出第一帧，避免等第一帧回调才出现背景
            SafeUpdateRealtimeBlurFrame();

            // 「禁用动画」时封面本身是静止的，取一帧就够
            if (App.IsAnimationDisabled)
            {
                StopRealtimeBlur();
                return;
            }

            if (isVideo)
            {
                if (!_bgBlurRenderingHooked)
                {
                    CompositionTarget.Rendering += OnBgBlurRendering;
                    _bgBlurRenderingHooked = true;
                }
            }
            else
            {
                // GIF：按帧延迟推进，每换一帧就换背景。
                // 3.36：已经和封面共用解码器时由封面的计时器驱动，这里不再重复启动。
                if (!_bgBlurSharesCoverGif)
                {
                    _bgBlurTimer = new DispatcherTimer { Interval = GetFrameDelay(_bgBlurGif.Frames[0]) };
                    _bgBlurTimer.Tick += BgBlurGifTimer_Tick;
                    _bgBlurTimer.Start();
                }
            }
        }

        private void BgBlurGifTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 3.36：封面 GIF 已经加载出来了 —— 交给封面的帧计时器驱动，不再各走各的
                if (_gifDecoder != null)
                {
                    AttachBackgroundToCoverGif();
                    return;
                }

                if (_bgBlurGif == null || _bgBlurGif.Frames.Count == 0) return;
                _bgBlurGifIndex = (_bgBlurGifIndex + 1) % _bgBlurGif.Frames.Count;
                SafeUpdateRealtimeBlurFrame();

                if (_bgBlurTimer != null && _bgBlurGif != null && _bgBlurGif.Frames.Count > 0)
                    _bgBlurTimer.Interval = GetFrameDelay(_bgBlurGif.Frames[_bgBlurGifIndex]);
            }
            catch { }
        }

        /// <summary>
        /// 3.36：把背景 GIF 接到封面的解码器上 —— 同一个解码器、同一个帧号，
        /// 背景和封面就是同一帧画面，不会因为两边启动时间不同而错位。
        /// </summary>
        private void AttachBackgroundToCoverGif()
        {
            if (_gifDecoder == null) return;
            if (_gifDecoder.Frames.Count == 0) return;

            if (!ReferenceEquals(_bgBlurGif, _gifDecoder) || !_bgBlurSharesCoverGif)
            {
                _bgBlurGif = _gifDecoder;
                _bgBlurSharesCoverGif = true;

                try
                {
                    if (_bgBlurTimer != null)
                    {
                        _bgBlurTimer.Stop();
                        _bgBlurTimer.Tick -= BgBlurGifTimer_Tick;
                        _bgBlurTimer = null;
                    }
                }
                catch { }
            }

            _bgBlurGifIndex = _gifFrameIndex;
            SafeUpdateRealtimeBlurFrame();
        }

        private void OnBgBlurRendering(object sender, EventArgs e)
        {
            if (_bgBlurPlayer == null) return;
            SafeUpdateRealtimeBlurFrame();
        }

        /// <summary>背景视频循环：播完回到开头继续，否则第二次播放会停在最后一帧。</summary>
        private void OnBgBlurMediaEnded(object? sender, EventArgs e)
        {
            try
            {
                if (_bgBlurPlayer == null) return;
                _bgBlurPlayer.Position = TimeSpan.Zero;
                _bgBlurPlayer.Play();
            }
            catch { }
        }

        /// <summary>
        /// 3.36：背景播放器是独立的一套 MediaPlayer（为了逐帧抓画面），它往往比封面更早开始。
        /// 封面真正出画面后，把背景对齐到封面的播放位置，两者就不会一直错位。
        /// </summary>
        private void SyncRealtimeBlurToCover(MediaElement? cover)
        {
            try
            {
                if (_bgBlurPlayer == null || cover == null) return;

                if (cover.Source is not Uri coverUri) return;
                if (!string.Equals(coverUri.LocalPath, _bgBlurSourcePath, StringComparison.OrdinalIgnoreCase)) return;

                TimeSpan pos = TimeSpan.Zero;
                try { pos = cover.Position; } catch { }

                try { _bgBlurPlayer.Position = pos; } catch { }
                _bgBlurPlayer.Play();
            }
            catch { }
        }

        /// <summary>
        /// 停止实时模糊背景（视频 / GIF）。
        /// keepVisual = true 时保留最后一帧画面和 GPU 模糊效果：离开主界面时用，
        /// 这样返回主界面时不会先看到一张被放大的 320px 原始帧（看起来像素化）。
        /// </summary>
        private void StopRealtimeBlur(bool keepVisual = false)
        {
            try
            {
                if (_bgBlurRenderingHooked)
                {
                    CompositionTarget.Rendering -= OnBgBlurRendering;
                    _bgBlurRenderingHooked = false;
                }
            }
            catch { }

            try
            {
                if (_bgBlurTimer != null)
                {
                    _bgBlurTimer.Stop();
                    _bgBlurTimer.Tick -= BgBlurGifTimer_Tick;
                    _bgBlurTimer = null;
                }
            }
            catch { }

            _bgBlurGif = null;
            _bgBlurGifIndex = 0;
            if (_bgBlurPlayer != null)
            {
                try { _bgBlurPlayer.MediaEnded -= OnBgBlurMediaEnded; } catch { }
                try { _bgBlurPlayer.Stop(); } catch { }
                try { _bgBlurPlayer.Close(); } catch { }
                _bgBlurPlayer = null;
            }
            _bgBlurApplied = false;
            _bgBlurSourcePath = "";
            _bgBlurSharesCoverGif = false;

            if (!keepVisual)
            {
                try { if (PoolBgImage != null) PoolBgImage.Effect = null; } catch { }
            }
        }

        private void SafeUpdateRealtimeBlurFrame()
        {
            try
            {
                if (PoolBgImage == null) return;

                BitmapSource frame = null;

                if (_bgBlurPlayer != null)
                {
                    int vw = _bgBlurPlayer.NaturalVideoWidth;
                    int vh = _bgBlurPlayer.NaturalVideoHeight;
                    if (vw <= 0 || vh <= 0) return;   // 视频还没解出画面

                    // 背景本身是模糊的：截帧宽度 320 就够，越小每帧越便宜
                    int w = 320;
                    int h = Math.Max(1, (int)Math.Round(w * (double)vh / vw));

                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                        dc.DrawVideo(_bgBlurPlayer, new Rect(0, 0, w, h));

                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(visual);
                    rtb.Freeze();
                    if (HasVisiblePixels(rtb) && !IsOpeningBlackFrame(rtb)) frame = rtb;
                }
                else if (_bgBlurGif != null && _bgBlurGif.Frames.Count > 0)
                {
                    frame = _bgBlurGif.Frames[_bgBlurGifIndex % _bgBlurGif.Frames.Count];
                }

                if (frame == null) return;

                ApplyPoolBgBlur();
                PoolBgImage.Source = frame;
                if (!_bgBlurApplied)
                {
                    FadePoolBackground(1);
                    _bgBlurApplied = true;
                }
            }
            catch { }
        }

        /// <summary>3.40.1：背景播放器刚打开的一小段时间内出现的纯黑帧不要拿去当背景。</summary>
        private bool IsOpeningBlackFrame(BitmapSource bmp)
        {
            try
            {
                if (_bgBlurPlayer == null) return false;
                if ((DateTime.UtcNow - _bgBlurPlayerOpenedUtc).TotalMilliseconds > 800) return false;
                return IsNearlyBlack(bmp);
            }
            catch { return false; }
        }

        /// <summary>采样判断整帧是否接近纯黑（视频刚打开时的空白帧）。</summary>
        private static bool IsNearlyBlack(BitmapSource bmp)
        {
            try
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w < 4 || h < 4) return false;

                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[w * h * 4];
                converted.CopyPixels(pixels, w * 4, 0);

                long sum = 0;
                int count = 0;
                int stepY = Math.Max(1, h / 8);
                int stepX = Math.Max(1, w / 8);
                for (int y = 0; y < h; y += stepY)
                {
                    for (int x = 0; x < w; x += stepX)
                    {
                        int i = (y * w + x) * 4;
                        sum += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                        count++;
                    }
                }

                return count > 0 && sum / count < 12;
            }
            catch { return false; }
        }

        /// <summary>视频还没解出画面时整帧是透明的，这种帧不要拿去当背景。</summary>
        private static bool HasVisiblePixels(BitmapSource bmp)
        {
            try
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w < 4 || h < 4) return false;

                var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[w * h * 4];
                converted.CopyPixels(pixels, w * 4, 0);

                for (int y = 0; y < h; y += Math.Max(1, h / 8))
                {
                    for (int x = 0; x < w; x += Math.Max(1, w / 8))
                    {
                        int i = (y * w + x) * 4;
                        if (pixels[i + 3] > 8) return true;
                    }
                }
                return false;
            }
            catch { return true; }
        }

        private void FadePoolBackground(double target)
        {
            try
            {
                if (App.IsAnimationDisabled)
                {
                    PoolBgImage.Opacity = target;
                    return;
                }

                var anim = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                PoolBgImage.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { }
        }
        private void LoadPersonalizationForPool(PoolInfo pool)
        {
            try
            {
                if (pool == null) { _currentPersonalization = new PersonalizationSettings(); return; }
                try { if (PoolArchive.NeedsUnpack(pool.Dir)) PoolArchive.UnpackFromArchive(pool.Dir); } catch { }

                string persFile = Path.Combine(pool.Dir, "personalization.json");
                if (File.Exists(persFile))
                {
                    var loaded = PersonalizationSettings.Load(persFile);
                    _currentPersonalization = PoolArchive.ExpandAssetPaths(loaded, pool.Dir);
                }
                else _currentPersonalization = new PersonalizationSettings();
            }
            catch { _currentPersonalization = new PersonalizationSettings(); }
            UpdateDrawAnimationCheckboxVisibility();
        }

        /// <summary>
        /// 直接读取「即将选中卡池」的个性化配置来判断是否需要显示复选框，
        /// 不再依赖可能还是上一个卡池的内存状态。
        /// </summary>
        private void RefreshAnimationCheckboxForSelectedPool()
        {
            try
            {
                bool has = false;
                string dir = App.LastSelectedPoolDir;

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string persFile = Path.Combine(dir, "personalization.json");
                    if (File.Exists(persFile))
                    {
                        var loaded = PersonalizationSettings.Load(persFile);
                        if (loaded?.Items != null)
                        {
                            foreach (var it in loaded.Items)
                            {
                                if (it == null) continue;
                                if (!string.IsNullOrEmpty(it.AvatarFile) || !string.IsNullOrEmpty(it.IllustrationFile))
                                {
                                    has = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                _shouldShowAnimationCheckbox = has;

                // 3.64：开关换成了 OptionToggle，可见性交给外层「抽取动画」那一行
                if (ChkDrawAnimation != null) ChkDrawAnimation.Visibility = Visibility.Visible;
                ApplyBottomChromeVisibility();
                try { UpdateStatusTextContrast(); } catch { }
            }
            catch { }
        }

        private bool HasPersonalizationContent()
        {
            var s = _currentPersonalization;
            if (s == null) return false;
            if (s.Items == null || s.Items.Count == 0) return false;

            foreach (var it in s.Items)
            {
                if (it == null) continue;
                if (!string.IsNullOrEmpty(it.AvatarFile)) return true;
                if (!string.IsNullOrEmpty(it.IllustrationFile)) return true;
            }
            return false;
        }

        private void UpdateDrawAnimationCheckboxVisibility()
        {
            if (ChkDrawAnimation == null) return;

            _shouldShowAnimationCheckbox = HasPersonalizationContent();

            try
            {
                ChkDrawAnimation.BeginAnimation(UIElement.OpacityProperty, null);
                ChkDrawAnimationShell?.BeginAnimation(UIElement.OpacityProperty, null);
            }
            catch { }
            ChkDrawAnimation.Opacity = 1;
            if (ChkDrawAnimationShell != null) ChkDrawAnimationShell.Opacity = 1;

            // 3.64：开关换成了 OptionToggle，用外层「抽取动画」那一行控制可见性
            ApplyBottomChromeVisibility();
            try { UpdateStatusTextContrast(); } catch { }
        }

        private void HideDefaultCoverName()
        {
            if (TxtDefaultCoverName != null) TxtDefaultCoverName.Visibility = Visibility.Collapsed;
        }

        private static BitmapImage LoadEmbeddedImage(string resourcePath)
        {
            return EmbeddedImageLoader.Load(resourcePath);
        }

        /// <summary>
        /// 强制重新加载当前卡池的封面。
        /// ★ 必须绕开 <see cref="LoadCoverForPool"/> 开头「封面路径没变就直接返回」的短路：
        ///   从选项页回来 / 切换动画开关后，视频与 GIF 已经被停掉，短路会让封面再也不出现。
        /// 400 ms 内重复调用只算一次，避免「进入动画 + 选项变更通知」同时触发两遍。
        /// </summary>
        private void ReloadCurrentCover(bool force = false)
        {
            try
            {
                if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count) return;
                // 3.36：进入动画时要跳过 400 ms 防抖，否则封面会晚于背景开始播放
                if (!force && (DateTime.UtcNow - _lastCoverReloadUtc).TotalMilliseconds < 400) return;

                _lastCoverReloadUtc = DateTime.UtcNow;
                _lastLoadedCoverPath = null;
                LoadCoverForPool(_allPools[_currentPoolIndex]);
            }
            catch { }
        }

        private DateTime _lastCoverReloadUtc = DateTime.MinValue;

        /// <summary>当前封面内容尺寸（图片 / GIF 的像素尺寸、视频的自然尺寸），用于圆角按实际显示区域裁剪。</summary>
        private Size _coverContentSize = Size.Empty;
        /// <summary>当前卡池是否开启「圆角封面」。</summary>
        private bool _coverRoundEnabled;

        private void LoadCoverForPool(PoolInfo pool)
        {
            if (pool == null) return;

            // 3.53：批注层必须和封面本体「同一时刻」出现：
            //   - 图片 / GIF：是同步解码的，加载完立刻叠批注；
            //   - 视频：帧是异步来的，一律等这一层真正出画面（MediaOpened / SwapVideoLayers）
            //     再叠批注，否则返回主界面时会看到「编辑的内容先出来、视频黑屏/旧画面」的错位。
            HideCoverOverlay();
            bool waitVideoFrame = false;
            try
            {
                waitVideoFrame = LoadCoverForPoolCore(pool);
            }
            finally
            {
                if (!waitVideoFrame) UpdateCoverOverlay(pool.Dir);
            }
        }

        /// <summary>先把批注层藏起来（不清缓存，稍后 UpdateCoverOverlay 直接显示即可）。</summary>
        private void HideCoverOverlay()
        {
            try
            {
                if (CoverOverlayImage == null) return;
                CoverOverlayImage.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>加载封面本体。返回 true 表示「视频帧还没出来，批注层要等画面就绪再叠」。</summary>
        private bool LoadCoverForPoolCore(PoolInfo pool)
        {
            // 3.33：主界面显示「封面原图 + 透明批注层」。
            // 之前用 pool.CoverPath（优先 cover_preview.png 合成图）再叠加批注层，
            // 图片封面会把批注叠两遍；再次编辑后还会出现旧批注与合成图不同步的错位。
            string coverPath = PoolCover.GetOriginalCoverPath(pool.Dir);
            if (string.IsNullOrEmpty(coverPath)) coverPath = pool.CoverPath ?? "";

            // ★ 「圆角封面」是每个卡池自己的设置：先记录开关，再走封面加载的短路判断，
            //   切换卡池时圆角状态才能跟着变。
            _coverRoundEnabled = PoolCover.IsRoundCover(pool.Dir);
            ApplyCoverRounding();

            if (!string.IsNullOrEmpty(coverPath)
                && string.Equals(coverPath, _lastLoadedCoverPath, StringComparison.OrdinalIgnoreCase))
            {
                if (DisplayImage.Visibility == Visibility.Visible && DisplayImage.Source != null)
                    return false;
                if (_activeVideo != null && _activeVideo.Visibility == Visibility.Visible && _activeVideo.Source != null)
                    return false;
                if (_bufferVideo != null && _bufferVideo.Source is Uri bufUri
                    && string.Equals(bufUri.LocalPath, coverPath, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            _lastLoadedCoverPath = coverPath;

            bool hasCover = !string.IsNullOrEmpty(coverPath) && File.Exists(coverPath);
            string ext = hasCover ? Path.GetExtension(coverPath).ToLowerInvariant() : "";
            bool isVideo = hasCover && VideoExts.Contains(ext);
            bool isGif = hasCover && ext == ".gif";
            bool isImage = hasCover && ImageExts.Contains(ext) && !isGif;

            if (isVideo)
            {
                bool alreadyPlaying = false;
                try
                {
                    var cur = _activeVideo?.Source as Uri;
                    if (cur != null && string.Equals(cur.LocalPath, coverPath, StringComparison.OrdinalIgnoreCase) && _activeVideo.Visibility == Visibility.Visible)
                        alreadyPlaying = true;
                }
                catch { }

                if (alreadyPlaying) return false;
                HideDefaultCoverName();

                if (!_firstVideoLoaded)
                {
                    ShowVideoLoading(true);
                    StopGifPlayback();
                    DisplayImage.Visibility = Visibility.Collapsed;
                    DisplayImage.Source = null;
                    try
                    {
                        _activeVideo.Visibility = Visibility.Visible;
                        _activeVideo.Opacity = 1;
                        _activeVideo.Source = new Uri(coverPath);
                    }
                    catch { ShowVideoLoading(false); ShowDefaultCover(pool.Name); return false; }
                }
                else
                {
                    CancelPendingVideoPreload();
                    try
                    {
                        _bufferVideo.Stop();
                        _bufferVideo.Source = null;
                        _bufferVideo.Opacity = 0;
                        _bufferVideo.Visibility = Visibility.Visible;
                        _bufferVideo.Source = new Uri(coverPath);
                    }
                    catch { ShowDefaultCover(pool.Name); return false; }
                }

                // 视频帧是异步来的：批注层交给 MediaOpened / SwapVideoLayers 叠
                return true;
            }

            CancelPendingVideoPreload();
            ShowVideoLoading(false);
            StopGifPlayback();

            // 封面不是视频（图片 / GIF / 默认封面）→ 不会再叠加封面视频的声音
            PublishCoverAudioState(false);

            try { _activeVideo.Stop(); } catch { }
            try { _activeVideo.Source = null; } catch { }
            try { _activeVideo.Visibility = Visibility.Collapsed; } catch { }
            try { _activeVideo.Opacity = 0; } catch { }
            try { _bufferVideo.Stop(); } catch { }
            try { _bufferVideo.Source = null; } catch { }
            try { _bufferVideo.Visibility = Visibility.Collapsed; } catch { }
            try { _bufferVideo.Opacity = 0; } catch { }

            if (isGif)
            {
                try
                {
                    HideDefaultCoverName();
                    DisplayImage.Visibility = Visibility.Visible;
                    StartGifPlayback(coverPath);
                    if (DisplayImage.Source == null) ShowDefaultCover(pool.Name);
                }
                catch { ShowDefaultCover(pool.Name); }
                return false;
            }

            if (isImage)
            {
                try
                {
                    HideDefaultCoverName();
                    // ★ 按显示尺寸解码：封面图动辄 4K，整张解进内存再缩到几百 DIP 显示是纯浪费
                    var bitmap = ImageHelper.LoadBitmapSized(coverPath, CoverDecodePixelWidth);
                    if (bitmap != null)
                    {
                        DisplayImage.Source = bitmap;
                        DisplayImage.Visibility = Visibility.Visible;
                        _coverContentSize = new Size(bitmap.PixelWidth, bitmap.PixelHeight);
                        ApplyCoverRounding();
                        RuntimeLog.Info($"封面加载：卡池={pool.UniqueName}，尺寸={bitmap.PixelWidth}x{bitmap.PixelHeight}，按显示宽度 {CoverDecodePixelWidth} 解码");
                    }
                    else
                    {
                        ShowDefaultCover(pool.Name);
                    }
                }
                catch { ShowDefaultCover(pool.Name); }
                return false;
            }
            ShowDefaultCover(pool.Name);
            return false;
        }

        /// <summary>
        /// 主界面大封面的圆角：按「图片 / 视频真正占的那块矩形」裁剪承载控件，
        /// Uniform 显示的上下（或左右）留白不参与圆角，否则圆角落在留白角落上，看着像没生效。
        /// </summary>
        /// <summary>把卡池的透明批注层叠到封面上（GIF / 视频也能带批注）。</summary>
        private void UpdateCoverOverlay(string poolDir)
        {
            try
            {
                if (CoverOverlayImage == null) return;

                string path = PoolCover.GetOverlayPath(poolDir);
                if (string.IsNullOrEmpty(path))
                {
                    _lastOverlayPath = "";
                    _lastOverlayTicks = -1;
                    CoverOverlayImage.Source = null;
                    CoverOverlayImage.Visibility = Visibility.Collapsed;
                    return;
                }

                long ticks = 0;
                try { ticks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }

                if (string.Equals(path, _lastOverlayPath, StringComparison.OrdinalIgnoreCase) && ticks == _lastOverlayTicks)
                {
                    CoverOverlayImage.Visibility = Visibility.Visible;
                    return;
                }

                var bmp = ImageHelper.LoadBitmapSized(path, CoverDecodePixelWidth);
                _lastOverlayPath = path;
                _lastOverlayTicks = ticks;
                CoverOverlayImage.Source = bmp;
                CoverOverlayImage.Visibility = bmp != null ? Visibility.Visible : Visibility.Collapsed;
                ApplyCoverRounding();
            }
            catch { }
        }

        private void ApplyCoverRounding()
        {
            try
            {
                if (CoverVisualHost != null) CoverVisualHost.Clip = null;

                var size = _coverContentSize;
                RoundCorner.SetEnabledForContent(DisplayImage, _coverRoundEnabled, size);
                if (_activeVideo != null) RoundCorner.SetEnabledForContent(_activeVideo, _coverRoundEnabled, size);
                if (_bufferVideo != null) RoundCorner.SetEnabledForContent(_bufferVideo, _coverRoundEnabled, size);
                if (CoverOverlayImage != null) RoundCorner.SetEnabledForContent(CoverOverlayImage, _coverRoundEnabled, size);
            }
            catch { }
        }

        private void ShowDefaultCover(string poolName)
        {
            var defaultBitmap = LoadEmbeddedImage("Assets/default_cover.jpg");
            if (defaultBitmap != null)
            {
                DisplayImage.Source = defaultBitmap;
                DisplayImage.Visibility = Visibility.Visible;
                _coverContentSize = new Size(defaultBitmap.PixelWidth, defaultBitmap.PixelHeight);
                ApplyCoverRounding();
            }
            else { DisplayImage.Source = null; DisplayImage.Visibility = Visibility.Collapsed; }

            // 3.48：编辑过默认封面后，卡池名已经画进批注位图，这里不再叠加文字，避免出现两遍
            bool nameHandledByOverlay = false;
            try
            {
                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                {
                    string dir = _allPools[_currentPoolIndex].Dir ?? "";
                    string overlay = PoolCover.GetOverlayPath(dir);
                    nameHandledByOverlay = PoolCover.HasDefaultCoverEdit(dir)
                        && !string.IsNullOrEmpty(overlay) && File.Exists(overlay);
                }
            }
            catch { }

            if (TxtDefaultCoverName != null)
            {
                if (!string.IsNullOrEmpty(poolName) && !nameHandledByOverlay)
                { TxtDefaultCoverName.Text = poolName; TxtDefaultCoverName.Visibility = Visibility.Visible; }
                else { TxtDefaultCoverName.Text = ""; TxtDefaultCoverName.Visibility = Visibility.Collapsed; }
            }

            try { _activeVideo.Stop(); } catch { }
            try { _activeVideo.Source = null; } catch { }
            try { _activeVideo.Visibility = Visibility.Collapsed; } catch { }
            try { _activeVideo.Opacity = 0; } catch { }
            try { _bufferVideo.Stop(); } catch { }
            try { _bufferVideo.Source = null; } catch { }
            try { _bufferVideo.Visibility = Visibility.Collapsed; } catch { }
            try { _bufferVideo.Opacity = 0; } catch { }

            // 默认封面（静态图）没有声音
            PublishCoverAudioState(false);
        }

        /// <summary>
        /// 把「当前封面视频是否带声音」告诉主窗口：带声音时按设置临时静音背景音乐，
        /// 避免视频原声和背景音乐叠在一起。只在状态变化时发一次，切卡池不会反复触发。
        /// </summary>
        private void PublishCoverAudioState(bool hasAudio)
        {
            if (_coverAudioState == hasAudio) return;
            _coverAudioState = hasAudio;
            try { MessageBus.Publish(new AppMessages.PoolCoverAudioChanged(hasAudio)); } catch { }
        }

        private void CancelPendingVideoPreload()
        {
            if (_videoPreloader != null) { try { _videoPreloader.Close(); } catch { } }
        }

        private void ShowVideoLoading(bool show)
        {
            try
            {
                if (show)
                {
                    VideoLoadingPanel.Visibility = Visibility.Visible;
                    if (_videoLoadingStoryboard == null) _videoLoadingStoryboard = (Storyboard)FindResource("VideoLoadingStoryboard");
                    try { _videoLoadingStoryboard.Stop(VideoLoadingPanel); } catch { }
                    try { _videoLoadingStoryboard.Begin(VideoLoadingPanel, true); } catch { }

                    if (_videoLoadTimeoutTimer == null)
                    {
                        _videoLoadTimeoutTimer = new DispatcherTimer { Interval = VideoLoadTimeout };
                        _videoLoadTimeoutTimer.Tick += (s, e) =>
                        {
                            _videoLoadTimeoutTimer.Stop();
                            ShowVideoLoading(false);
                            // 3.53 兜底：视频一直没出画面时，别让批注层一直藏着
                            try
                            {
                                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                                    UpdateCoverOverlay(_allPools[_currentPoolIndex].Dir);
                            }
                            catch { }
                        };
                    }
                    _videoLoadTimeoutTimer.Stop();
                    _videoLoadTimeoutTimer.Start();
                }
                else
                {
                    if (_videoLoadTimeoutTimer != null) { try { _videoLoadTimeoutTimer.Stop(); } catch { } }
                    try
                    {
                        if (_videoLoadingStoryboard == null) _videoLoadingStoryboard = (Storyboard)FindResource("VideoLoadingStoryboard");
                        _videoLoadingStoryboard.Stop(VideoLoadingPanel);
                        _videoLoadingStoryboard.Remove(VideoLoadingPanel);
                    }
                    catch { }
                    VideoLoadingPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void DisplayVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            var me = sender as MediaElement;
            if (me == null) return;
            if (me == _bufferVideo) { SwapVideoLayers(); return; }

            ShowVideoLoading(false);
            _firstVideoLoaded = true;
            try
            {
                if (me.NaturalVideoWidth > 0 && me.NaturalVideoHeight > 0)
                {
                    _coverContentSize = new Size(me.NaturalVideoWidth, me.NaturalVideoHeight);
                    ApplyCoverRounding();
                }
                DisplayImage.Visibility = Visibility.Collapsed;
                DisplayImage.Source = null;
                HideDefaultCoverName();
            }
            catch { }
            try { if (me.Visibility == Visibility.Visible) me.Play(); } catch { }

            // 3.59：直接读 MediaElement 的音轨信息，判断这个封面视频有没有声音
            PublishCoverAudioState(me.HasAudio);

            // 3.36：封面真的出画面了，把背景播放器对齐到同一时间点
            SyncRealtimeBlurToCover(me);

            // 3.52：视频第一帧到了，再把批注层叠上去（避免批注比画面先出现）
            try { UpdateCoverOverlay(_allPools[_currentPoolIndex].Dir); } catch { }
        }

        private void SwapVideoLayers()
        {
            try
            {
                _bufferVideo.Visibility = Visibility.Visible;
                _bufferVideo.Opacity = 1;
                _bufferVideo.Play();

                _activeVideo.Opacity = 0;
                try { _activeVideo.Stop(); } catch { }
                _activeVideo.Source = null;
                _activeVideo.Visibility = Visibility.Collapsed;

                var tmp = _activeVideo;
                _activeVideo = _bufferVideo;
                _bufferVideo = tmp;

                DisplayImage.Visibility = Visibility.Collapsed;
                DisplayImage.Source = null;
                HideDefaultCoverName();

                _firstVideoLoaded = true;
                ShowVideoLoading(false);

                // 3.36：换层播放的新视频从第 0 秒开始，背景也拉回同一个时间点
                SyncRealtimeBlurToCover(_activeVideo);

                // 3.59：新一层成为当前封面，音频状态跟着它
                PublishCoverAudioState(_activeVideo.HasAudio);

                // 3.52：新一层的画面就绪后再叠批注
                try { UpdateCoverOverlay(_allPools[_currentPoolIndex].Dir); } catch { }
            }
            catch { }
        }

        private void DisplayVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            var me = sender as MediaElement;
            if (me == null) return;
            var src = me.Source;
            if (src == null) return;
            try
            {
                me.Stop();
                me.Source = null;
                me.Source = src;
                if (me.Visibility == Visibility.Visible) me.Play();
            }
            catch { }
        }

        private void StartGifPlayback(string path)
        {
            StopGifPlayback();
            try
            {
                _gifDecoder = ImageHelper.LoadGifFromFile(path);
                if (_gifDecoder == null || _gifDecoder.Frames.Count == 0) return;

                // ★ 首帧先显示出来（不管页面当前激活与否），否则「页面未激活时回到主界面」会只剩空白；
                //   逐帧播放仍然只在激活时进行。
                // 3.37：同一个 GIF 从离开时的那一帧继续，返回主界面时不会跳回开头（看起来像闪一下）
                int start = 0;
                if (string.Equals(path, _gifCurrentPath, StringComparison.OrdinalIgnoreCase)
                    && _gifDecoder.Frames.Count > 0)
                {
                    start = Math.Max(0, _gifResumeIndex) % _gifDecoder.Frames.Count;
                }

                _gifCurrentPath = path;
                _gifFrameIndex = start;
                DisplayImage.Source = _gifDecoder.Frames[start];
                DisplayImage.Visibility = Visibility.Visible;
                _coverContentSize = new Size(_gifDecoder.Frames[start].PixelWidth, _gifDecoder.Frames[start].PixelHeight);
                ApplyCoverRounding();

                // 3.36：背景如果已经在放这个 GIF，立刻对齐到同一帧
                if (_bgBlurGif != null) AttachBackgroundToCoverGif();

                if (_gifDecoder.Frames.Count > 1)
                    StartGifTimer();
            }
            catch
            {
                var bitmap = ImageHelper.LoadBitmapFromFile(path);
                DisplayImage.Source = bitmap;
            }
        }

        /// <summary>启动逐帧计时器（GIF 帧数 > 1 且页面处于激活状态时才真正开始）。</summary>
        private void StartGifTimer()
        {
            if (_gifDecoder == null || _gifDecoder.Frames.Count <= 1) return;
            if (_gifTimer != null) return;

            _gifTimer = new DispatcherTimer { Interval = GetFrameDelay(_gifDecoder.Frames[_gifFrameIndex]) };
            _gifTimer.Tick += GifTimer_Tick;
            _gifTimer.Start();
        }

        private void StopGifPlayback()
        {
            // 3.37：记住停在第几帧，同一个 GIF 下次从这一帧继续
            if (_gifDecoder != null) _gifResumeIndex = _gifFrameIndex;

            if (_gifTimer != null) { _gifTimer.Stop(); _gifTimer.Tick -= GifTimer_Tick; _gifTimer = null; }
            _gifDecoder = null; _gifFrameIndex = 0;
        }

        private void GifTimer_Tick(object sender, EventArgs e)
        {
            if (_gifDecoder == null || _gifDecoder.Frames.Count == 0) return;
            _gifFrameIndex = (_gifFrameIndex + 1) % _gifDecoder.Frames.Count;
            DisplayImage.Source = _gifDecoder.Frames[_gifFrameIndex];
            if (_gifTimer != null) _gifTimer.Interval = GetFrameDelay(_gifDecoder.Frames[_gifFrameIndex]);

            // 3.36：背景跟着封面的帧走（同一个解码器、同一个帧号）
            if (_bgBlurGif != null || _bgBlurSharesCoverGif)
                AttachBackgroundToCoverGif();
        }

        private TimeSpan GetFrameDelay(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata meta)
                {
                    var delayObj = meta.GetQuery("/grctlext/Delay");
                    if (delayObj != null)
                    {
                        int delay = Convert.ToInt32(delayObj);
                        int ms = delay * 10; if (ms < 20) ms = 100;
                        return TimeSpan.FromMilliseconds(ms);
                    }
                }
            }
            catch { }
            return TimeSpan.FromMilliseconds(100);
        }

        private void UpdateDrawCountDisplay()
        {
            if (PanelStatsNormal == null) return;

            bool isAdvanced = false;
            if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                isAdvanced = Engine.HasActiveGachaStrategy(_allPools[_currentPoolIndex].Dir);

            _statsAdvancedActive = isAdvanced;
            if (isAdvanced)
            {
                PanelStatsNormal.Visibility = Visibility.Collapsed;
                PanelStatsAdvanced.Visibility = Visibility.Visible;

                var pool = _allPools[_currentPoolIndex];
                string gachaFile = Path.Combine(pool.Dir, "gacha_settings.ini");
                var gs = GachaSettings.LoadSettingsFromFile(gachaFile);
                var state = Engine.LoadGachaState(pool.Dir);

                if (gs != null && gs.PresetName == "蔚蓝档案（旧）")
                {
                    int totalDraws = 0, upThreeStarCount = 0, threeStarCount = 0;
                    var upSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (gs.BAPoolType == "限时招募") { if (!string.IsNullOrEmpty(gs.BAUp3Star)) upSet.Add(gs.BAUp3Star.Trim()); }
                    else if (gs.BAPoolType == "庆典招募")
                    {
                        foreach (var n in DrawEngine.ParseUpObjectsString(gs.BAMainUp3Stars)) upSet.Add(n);
                        foreach (var n in DrawEngine.ParseUpObjectsString(gs.BASubUp3Stars)) upSet.Add(n);
                    }

                    string historyFile = Path.Combine(pool.Dir, "history.txt");
                    if (File.Exists(historyFile))
                    {
                        try
                        {
                            foreach (var line in File.ReadAllLines(historyFile))
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var parts = line.Split('|');
                                if (parts.Length < 3) continue;
                                var fields = parts[2].Split('\t');
                                if (fields.Length < 2) continue;

                                totalDraws++;
                                string name = (fields[0] ?? "").Trim();
                                string cat = (fields[1] ?? "").Trim();
                                if (CategoryEquals(cat, gs.BA3StarCategory))
                                {
                                    threeStarCount++;
                                    if (upSet.Count > 0 && upSet.Contains(name)) upThreeStarCount++;
                                }
                            }
                        }
                        catch { }
                    }

                    int baPityLimit = gs.BA2StarPity > 0 ? gs.BA2StarPity : 10;
                    int baRemain = baPityLimit - state.Pity4;
                    if (baRemain < 0) baRemain = 0;

                    TxtDrawCountAdvanced.Text = $"招募点数：{totalDraws}";
                    TxtDrawCountAdvanced.Foreground = ThemeManager.GetBrush("Fg_000000");
                    TxtDrawCountAdvanced.Visibility = Visibility.Visible;

                    if (gs.BAPoolType == "常驻招募")
                    {
                        TxtPity5Info.Visibility = Visibility.Collapsed;
                        TxtPity4Info.Visibility = Visibility.Visible;
                        TxtPity4Info.Text = $"获得3星学生次数：{threeStarCount}";
                        TxtPity4Info.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    }
                    else
                    {
                        TxtPity5Info.Visibility = Visibility.Visible;
                        TxtPity5Info.Text = $"获得UP3星学生次数：{upThreeStarCount}";
                        TxtPity5Info.Foreground = ThemeManager.GetThemedBrushFromHex("#C026D3");

                        TxtPity4Info.Visibility = Visibility.Visible;
                        TxtPity4Info.Text = $"获得3星学生次数：{threeStarCount}";
                        TxtPity4Info.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    }

                    if (TxtPityExtraInfo != null)
                    {
                        TxtPityExtraInfo.Visibility = Visibility.Visible;
                        TxtPityExtraInfo.Text = $"{baRemain}抽内必得2星或以上学生";
                        TxtPityExtraInfo.Foreground = ThemeManager.GetThemedBrushFromHex("#666666");
                    }
                }
                else
                {
                    int count = 0;
                    string historyFile = Path.Combine(pool.Dir, "history.txt");
                    if (File.Exists(historyFile)) { try { count = File.ReadAllLines(historyFile).Length; } catch { count = 0; } }

                    TxtDrawCountAdvanced.Text = $"累计抽取次数：{count}";
                    TxtDrawCountAdvanced.Foreground = ThemeManager.GetBrush("Fg_000000");
                    TxtDrawCountAdvanced.Visibility = Visibility.Visible;

                    int fivePityLimit = (gs != null && gs.FiveStarPity > 0) ? gs.FiveStarPity : 90;
                    int fourPityLimit = (gs != null && gs.FourStarPity > 0) ? gs.FourStarPity : 10;

                    int remaining5 = fivePityLimit - state.Pity5;
                    if (remaining5 < 0) remaining5 = 0;

                    TxtPity5Info.Visibility = Visibility.Visible;
                    if (state.Guaranteed5UP)
                    { TxtPity5Info.Text = $"再抽 {remaining5} 抽必得当期5星角色"; TxtPity5Info.Foreground = ThemeManager.GetThemedBrushFromHex("#D32F2F"); }
                    else
                    { TxtPity5Info.Text = $"再抽 {remaining5} 抽必得5星角色"; TxtPity5Info.Foreground = ThemeManager.GetThemedBrushFromHex("#666666"); }

                    int remaining4 = fourPityLimit - state.Pity4;
                    if (remaining4 < 0) remaining4 = 0;

                    TxtPity4Info.Visibility = Visibility.Visible;
                    if (state.Guaranteed4UP)
                    { TxtPity4Info.Text = $"再抽 {remaining4} 抽必得当期4星角色"; TxtPity4Info.Foreground = ThemeManager.GetThemedBrushFromHex("#7B1FA2"); }
                    else
                    { TxtPity4Info.Text = $"再抽 {remaining4} 抽必得4星角色"; TxtPity4Info.Foreground = ThemeManager.GetThemedBrushFromHex("#666666"); }

                    if (TxtPityExtraInfo != null) TxtPityExtraInfo.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PanelStatsNormal.Visibility = Visibility.Visible;
                PanelStatsAdvanced.Visibility = Visibility.Collapsed;
                TxtDrawCountNormal.Foreground = ThemeManager.GetBrush("Fg_000000");

                if (ChkNoRepeat.IsChecked == true) TxtDrawCountNormal.Text = $"本次抽取次数：{Engine.DrawnCount}";
                else
                {
                    int count = 0;
                    if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                    {
                        string historyFile = Path.Combine(_allPools[_currentPoolIndex].Dir, "history.txt");
                        if (File.Exists(historyFile)) { try { count = File.ReadAllLines(historyFile).Length; } catch { count = 0; } }
                    }
                    TxtDrawCountNormal.Text = $"累计抽取次数：{count}";
                }
            }

            // 3.64：悬浮面板的数据 + 底部底衬跟着当前状态走（避免抽取后残留一条边）
            SyncDrawStatsPopup();
            UpdateStatusTextContrast();
            UpdatePopupActions();
            // 3.68.1：普通池 / 高级池切换后，「不重复抽取」与右列的显隐要立刻跟着变
            ApplyBottomChromeVisibility();
        }

        private static bool CategoryEquals(string a, string b) => (a ?? "").Trim() == (b ?? "").Trim();

        /// <summary>3.64：「抽取详情」悬浮面板的数据（和旧布局的状态文字保持同步）。</summary>
        private void SyncDrawStatsPopup()
        {
            try
            {
                if (TxtPopupCount == null) return;

                if (_statsAdvancedActive)
                {
                    TxtPopupCount.Text = string.IsNullOrWhiteSpace(TxtDrawCountAdvanced?.Text)
                        ? "累计抽取次数：0"
                        : TxtDrawCountAdvanced.Text;
                    CopyPopupLine(TxtPity5Info, TxtPopupPity5);
                    CopyPopupLine(TxtPity4Info, TxtPopupPity4);
                    CopyPopupLine(TxtPityExtraInfo, TxtPopupExtra);
                }
                else
                {
                    TxtPopupCount.Text = string.IsNullOrWhiteSpace(TxtDrawCountNormal?.Text)
                        ? "本次抽取次数：0"
                        : TxtDrawCountNormal.Text;
                    if (TxtPopupPity5 != null) TxtPopupPity5.Visibility = Visibility.Collapsed;
                    if (TxtPopupPity4 != null) TxtPopupPity4.Visibility = Visibility.Collapsed;
                    if (TxtPopupExtra != null) TxtPopupExtra.Visibility = Visibility.Collapsed;
                }

                // 3.75：详情行补上「已抽取 X 个对象」，和「本次抽取详情」对话框里的已抽列表同源
                if (TxtPopupDetailCount != null)
                    TxtPopupDetailCount.Text = $"已抽取 {Engine.DrawnCount} 个对象";
            }
            catch { }
        }

        private static void CopyPopupLine(TextBlock source, TextBlock target)
        {
            if (target == null) return;
            bool has = source != null
                       && source.Visibility == Visibility.Visible
                       && !string.IsNullOrWhiteSpace(source.Text);
            target.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            target.Text = source?.Text ?? "";
            // 3.65：把原来的特殊颜色一起带过来（UP3星紫、3星蓝、保底红…），不要统一成灰色
            if (has && source.Foreground != null) target.Foreground = source.Foreground;
        }

        // ================================================================
        // 「抽取详情」面板（3.65 悬浮 / 固定，3.68 面板内可直接操作）
        // ================================================================

        private System.Windows.Threading.DispatcherTimer _statsCloseTimer;
        private bool _statsPinned;
        private bool _statsWindowHooked;
        private Window _statsWindow;

        /// <summary>面板卡片宽（和 XAML 里 Border 的 Width 保持一致，用来水平居中）。</summary>
        private const double DrawStatsPanelWidth = 272;

        /// <summary>卡片外面给阴影留的空白（XAML 里外层 Grid 的 Margin）。</summary>
        private const double DrawStatsShadowMargin = 12;

        private void HookStatsWindow()
        {
            if (_statsWindowHooked) return;
            var window = Window.GetWindow(this);
            if (window == null) return;
            _statsWindowHooked = true;
            _statsWindow = window;
            try { window.PreviewMouseDown += StatsWindow_PreviewMouseDown; } catch { }
            try { window.Deactivated += StatsWindow_Deactivated; } catch { }
        }

        private void UnhookStatsWindow()
        {
            if (!_statsWindowHooked) return;
            _statsWindowHooked = false;
            try { if (_statsWindow != null) _statsWindow.PreviewMouseDown -= StatsWindow_PreviewMouseDown; } catch { }
            try { if (_statsWindow != null) _statsWindow.Deactivated -= StatsWindow_Deactivated; } catch { }
            _statsWindow = null;
        }

        /// <summary>3.72：切到别的应用时主窗口失去激活，面板（独立弹层）要跟着收起，
        /// 否则它会一直压在其他应用的窗口上面。</summary>
        private void StatsWindow_Deactivated(object sender, EventArgs e)
        {
            try { HideDrawStatsPanel(); } catch { }
        }

        /// <summary>点面板外面（页面空白 / 其它按钮）就收起；点「抽取详情」本身交给它的 Click 处理。</summary>
        private void StatsWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DrawStatsPopup == null || !DrawStatsPopup.IsOpen) return;
                if (BtnDrawStats != null && BtnDrawStats.IsMouseOver) return;
                HideDrawStatsPanel();
            }
            catch { }
        }

        private void ShowDrawStatsPanel(bool pin)
        {
            if (DrawStatsPopup == null || BtnDrawStats == null) return;

            SyncDrawStatsPopup();
            UpdatePopupActions();
            DrawStatsPopup.PlacementTarget = BtnDrawStats;
            DrawStatsPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            // 卡片中心对准按钮中心（弹层内容 = 卡片宽 + 两侧阴影留白）
            double outerWidth = DrawStatsPanelWidth + (DrawStatsShadowMargin * 2);
            DrawStatsPopup.HorizontalOffset = (BtnDrawStats.ActualWidth - outerWidth) / 2;
            // 3.73.0：弹层底边贴着按钮顶边，卡片下方那 12px 阴影留白正好就是卡片与按钮的间距。
            // 3.74.0：再往上抬 12——鼠标悬停在按钮的放大镜图标上时，弹层窗口不能压到按钮上，
            // 否则按钮会收到 MouseLeave，出现「开一下又收起来」的反复闪动。
            DrawStatsPopup.VerticalOffset = -12;

            if (pin) _statsPinned = true;
            DrawStatsPopup.IsOpen = true;
        }

        private void HideDrawStatsPanel()
        {
            _statsPinned = false;
            CancelStatsClose();
            if (DrawStatsPopup != null) DrawStatsPopup.IsOpen = false;
        }

        /// <summary>鼠标移出后等一小会儿再关：避免面板弹出瞬间的鼠标抖动导致开了又关、反复闪烁。</summary>
        private void StartStatsClose()
        {
            if (_statsCloseTimer == null)
            {
                _statsCloseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(180)
                };
                _statsCloseTimer.Tick += (s, e) =>
                {
                    try { _statsCloseTimer.Stop(); } catch { }
                    if (_statsPinned) return;
                    // 3.74.0：鼠标还在按钮（包括按钮里的放大镜图标）或面板上时不收——
                    // 弹层窗口一出现，按钮可能收到一次 MouseLeave，不加判断就会「开一下又关」。
                    if (BtnDrawStats != null && BtnDrawStats.IsMouseOver) return;
                    if (DrawStatsPopup?.Child is UIElement panelChild && panelChild.IsMouseOver) return;
                    if (DrawStatsPopup != null) DrawStatsPopup.IsOpen = false;
                };
            }
            _statsCloseTimer.Stop();
            _statsCloseTimer.Start();
        }

        private void CancelStatsClose()
        {
            try { _statsCloseTimer?.Stop(); } catch { }
        }

        /// <summary>3.74.1：鼠标进出「抽取详情」底衬时重新算一次底衬颜色——
        /// 背景模糊开启时，悬浮的加深效果要作用在整块半透明底衬上。</summary>
        private void CompactShell_MouseEnter(object sender, MouseEventArgs e) => RefreshChromeScrim();

        private void CompactShell_MouseLeave(object sender, MouseEventArgs e) => RefreshChromeScrim();

        private void RefreshChromeScrim()
        {
            try
            {
                Brush chrome = App.MainBackgroundBlur
                               && PoolBgImage != null
                               && PoolBgImage.Source != null
                               && PoolBgImage.Opacity > 0.01
                    ? ThemeManager.GetBrush("Ui.AcrylicFill")
                    : null;
                ApplyChromeScrim(chrome);
            }
            catch { }
        }

        /// <summary>鼠标悬浮「抽取详情」——预览面板（未固定）。</summary>
        private void BtnDrawStats_MouseEnter(object sender, MouseEventArgs e)
        {
            try
            {
                CancelStatsClose();
                if (DrawStatsPopup == null || DrawStatsPopup.IsOpen) return;
                ShowDrawStatsPanel(pin: false);
            }
            catch { }
        }

        private void BtnDrawStats_MouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_statsPinned) return;
                // 3.74.0：鼠标只是从图标挪到按钮里的文字，不算「离开」
                if (BtnDrawStats != null && BtnDrawStats.IsMouseOver) return;
                StartStatsClose();
            }
            catch { }
        }

        /// <summary>鼠标移到面板上：别让「移出按钮」的延时把它关掉，否则点不到面板里的按钮。</summary>
        private void DrawStatsPanel_MouseEnter(object sender, MouseEventArgs e)
        {
            CancelStatsClose();
        }

        private void DrawStatsPanel_MouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_statsPinned) return;
                StartStatsClose();
            }
            catch { }
        }

        /// <summary>点击「抽取详情」——固定面板（鼠标移开也不收起）；再点一次收起。</summary>
        private void BtnDrawStats_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CancelStatsClose();
                if (_statsPinned && DrawStatsPopup?.IsOpen == true) HideDrawStatsPanel();
                else ShowDrawStatsPanel(pin: true);
            }
            catch { }
        }

        private void StatsReset_Click(object sender, RoutedEventArgs e)
        {
            // 3.73.0：点「重置」不再收起面板——面板上的次数会立刻变回 0，
            // 顺便在窗口顶部给一条「已重置本次抽取数据。」的提示，让用户知道点到了。
            ResetDrawRecords();
            ToastNotification.ShowSuccess(Window.GetWindow(this), "已重置本次抽取数据。");
        }

        private void StatsDetail_Click(object sender, RoutedEventArgs e)
        {
            HideDrawStatsPanel();
            ShowDrawDetailDialog();
        }

        private void DrawStatsPopup_Closed(object sender, EventArgs e)
        {
            _statsPinned = false;
        }

        private void ChkDrawAnimation_Click(object sender, RoutedEventArgs e)
        {
            PoolUiState.SetDrawAnimation(CurrentPoolDir(), ChkDrawAnimation.IsChecked == true);
        }

        private string CurrentPoolDir()
        {
            try
            {
                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                    return _allPools[_currentPoolIndex].Dir;
            }
            catch { }
            return "";
        }

        /// <summary>按当前卡池恢复「不重复抽取 / 抽卡动画」的开关状态。</summary>
        private void ApplyPoolUiState()
        {
            try
            {
                string dir = CurrentPoolDir();

                bool noRepeat = PoolUiState.GetNoRepeat(dir, App.IsNoRepeat);
                if (ChkNoRepeat != null) ChkNoRepeat.IsChecked = noRepeat;
                // 3.68：简洁布局的开关和详细布局的状态区是两个控件，状态要一起同步
                if (ChkNoRepeatCompact != null) ChkNoRepeatCompact.IsChecked = noRepeat;
                if (ChkNoRepeat != null)
                {
                    if (TxtResetHyperlink != null)
                        TxtResetHyperlink.Visibility = noRepeat ? Visibility.Visible : Visibility.Collapsed;

                    if (TxtDrawDetailLink != null)
                        TxtDrawDetailLink.Visibility = noRepeat ? Visibility.Visible : Visibility.Collapsed;
                }

                if (ChkDrawAnimation != null)
                    ChkDrawAnimation.IsChecked = PoolUiState.GetDrawAnimation(dir, true);

                UpdateReportButton();
                UpdatePopupActions();
            }
            catch { }
        }

        private void ChkNoRepeat_Click(object sender, RoutedEventArgs e)
        {
            bool on = (sender as CheckBox)?.IsChecked == true;
            // 两个开关互相同步（代码赋值不会再触发 Click，不会递归）
            if (ChkNoRepeat != null && ChkNoRepeat.IsChecked != on) ChkNoRepeat.IsChecked = on;
            if (ChkNoRepeatCompact != null && ChkNoRepeatCompact.IsChecked != on) ChkNoRepeatCompact.IsChecked = on;

            App.IsNoRepeat = on;
            PoolUiState.SetNoRepeat(CurrentPoolDir(), on);
            App.SaveSettings();
            UpdateDrawCountDisplay();
            TxtResetHyperlink.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (TxtDrawDetailLink != null)
                TxtDrawDetailLink.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            UpdatePopupActions();
        }

        private async void PerformDraw(int count)
        {
            if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count) return;
            var pool = _allPools[_currentPoolIndex];
            if (pool.Items.Count == 0) return;

            var drawTimer = System.Diagnostics.Stopwatch.StartNew();
            RuntimeLog.Info($"开始抽取：卡池={pool.UniqueName}，次数={count}，不重复={ChkNoRepeat.IsChecked == true}");

            LoadPersonalizationForPool(pool);

            var session = DrawSession.Current ?? new DrawSession();
            session.Pool = pool;
            session.Pers = _currentPersonalization ?? new PersonalizationSettings();
            session.NoRepeat = ChkNoRepeat.IsChecked == true;
            session.Highlight = HighlightConfig.FromSettings(Engine.GetGachaSettings(pool.Dir));
            session.LastDrawCount = count;
            session.RefreshDisplayMode();

            session.UseAnimation = _shouldShowAnimationCheckbox
                                && ChkDrawAnimation?.IsChecked == true;

            DrawSession.Current = session;
            RuntimeLog.Info($"抽取参数：卡池={pool.UniqueName}，条目={pool.ItemCount}，次数={count}，不重复={session.NoRepeat}，动画={session.UseAnimation}，展示模式={session.DisplayMode}");

            // ★ 计算放后台线程，避免界面在抽取/统计期间进入「未响应」；
            //   但遮罩只在真正耗时（≥500 次）时才显示 —— 少量抽取显示遮罩会一闪而过。
            var win = Window.GetWindow(this) as MainWindow;
            bool heavyWork = count >= UiThresholds.BackgroundWorkMinCount;
            bool showBusy = count >= UiThresholds.LoadingOverlayMinCount;
            List<DrawResultData> results;
            try
            {
                if (heavyWork)
                {
                    if (showBusy) win?.ShowDrawBusy("正在抽取中，请稍候...");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    results = await Task.Run(() => session.PerformDraw(count));
                }
                else
                {
                    results = session.PerformDraw(count);
                }
            }
            catch (Exception ex)
            {
                // ★ 不在 async void 里 throw（会变成未处理异常直接炸掉进程），
                //   改为记录日志并提示用户。
                RuntimeLog.Error(ex, $"抽取失败：卡池={pool.UniqueName}，次数={count}");
                CustomMessageBox.Show(Window.GetWindow(this),
                    "抽取失败：\n\n" + ex.Message,
                    "抽取失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                if (showBusy) win?.HideDrawBusy();
            }

            RuntimeLog.Info($"抽取完成：卡池={pool.UniqueName}，请求={count}，结果={results.Count}，耗时={drawTimer.ElapsedMilliseconds}ms");

            if (results.Count == 0) { UpdateDrawCountDisplay(); return; }

            if (session.UseAnimation)
            {
                MessageBus.Publish(new AppMessages.OpenDrawAnimation(
                    results, pool.Dir, count, session.Pers, session.Highlight, session.DisplayMode, count == 1));
                MessageBus.Publish(new AppMessages.NavigateTo("DrawAnimation"));
            }
            else
            {
                MessageBus.Publish(new AppMessages.OpenDrawResultPage(
                    results, pool.Dir, count, session.Pers, session.Highlight, session.DisplayMode));
                MessageBus.Publish(new AppMessages.NavigateTo("DrawResultPage"));
            }

            UpdateDrawCountDisplay();
        }

        /// <summary>启用高级抽卡规则的卡池才显示「查看抽取报告」。</summary>
        private bool _reportRowHooked;
        private void UpdateReportButton()
        {
            try
            {
                if (BtnViewReport == null) return;

                _showReportButton = !string.IsNullOrEmpty(CurrentPoolDir()) && App.IsGachaReportVisible;

                // 3.68：详细布局右下角一个「查看抽取报告」，简洁布局的收进「抽取详情」面板
                ApplyBottomChromeVisibility();
                try { UpdateStatusTextContrast(); } catch { }
            }
            catch { }
        }

        private void BtnViewReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                HideDrawStatsPanel();
                string dir = CurrentPoolDir();
                if (string.IsNullOrEmpty(dir)) return;

                App.LastSelectedPoolDir = dir;
                App.GachaReportReturnPage = "Main";
                App.SaveSettings();

                MessageBus.Publish(new AppMessages.NavigateTo("GachaReport"));
            }
            catch { }
        }
        /// <summary>主界面右键封面：打开与卡池列表相同的菜单（含「调整封面」）。</summary>
        private void CenterPanelRoot_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count) return;

                var menu = BuildPoolContextMenu(_currentPoolIndex);
                menu.PlacementTarget = CenterPanelRoot;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                e.Handled = true;
            }
            catch { }
        }

        private void BtnOptions_Click(object sender, RoutedEventArgs e) => MessageBus.Publish(new AppMessages.NavigateTo("Options"));

        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            // ★ 完全没有卡池时也允许进入历史记录页，页面里会显示“当前未找到任何卡池。”
            if (_allPools == null || _allPools.Count == 0)
            {
                MessageBus.Publish(new AppMessages.NavigateTo("History"));
                return;
            }

            if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count || _allPools[_currentPoolIndex].ItemCount == 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择一个有内容的抽取池！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            MessageBus.Publish(new AppMessages.NavigateTo("History"));
        }

        private void BtnDraw1_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count) return;
            var pool = _allPools[_currentPoolIndex];
            if (pool.Items.Count == 0) return;

            var win = Window.GetWindow(this);
            int? adjusted = DrawFlowHelper.CheckAndAdjustCount(
                win, pool, Engine, ChkNoRepeat.IsChecked == true, 1);
            if (adjusted == null)
            {
                // ★ 修复：此处虽然是单抽，但为了防御状态不一致，取消时也确保恢复
                SetDrawButtonsVisible(true);
                return;
            }

            PerformDraw(adjusted.Value);
        }

        private async void BtnDrawMulti_Click(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this) as MainWindow;
            if (win == null) return;

            if (_currentPoolIndex < 0 || _currentPoolIndex >= _allPools.Count) return;
            var pool = _allPools[_currentPoolIndex];
            if (pool.Items.Count == 0) return;

            int? picked = await win.ShowDrawCountPanelAsync(10);
            if (picked == null)
            {
                // ★ 修复：如果在输入面板上直接点击了取消，面板在关闭时可能已经恢复过，这里防御性调用一次（由于状态一致会直接返回，不会重复播放动画）
                SetDrawButtonsVisible(true);
                return;
            }

            int count = picked.Value;

            int? adjusted = DrawFlowHelper.CheckAndAdjustCount(
                win, pool, Engine, ChkNoRepeat.IsChecked == true, count);
            if (adjusted == null)
            {
                // ★ 修复：在“超出数量”或“抽完”的弹窗上点击取消，通过标准动画方法平滑恢复按钮
                SetDrawButtonsVisible(true);
                return;
            }

            PerformDraw(adjusted.Value);
        }

        private void ResetText_Click(object sender, MouseButtonEventArgs e) => ResetDrawRecords();

        private void ResetDrawRecords()
        {
            Engine.ResetDrawnIndices();
            if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                Engine.ResetGachaState(_allPools[_currentPoolIndex].Dir);
            UpdateDrawCountDisplay();
        }

        private void BtnNewPoolFromMain_Click(object sender, RoutedEventArgs e)
        {
            MessageBus.Publish(new AppMessages.OpenPoolEditor(""));
        }

        private static (string question, string answer) ReadSecretQA(string poolDir)
        {
            try
            {
                string qaFile = Path.Combine(poolDir, "secret_qa.lock");
                if (!File.Exists(qaFile)) return ("", "");
                string content = CryptoUtils.DecryptString(File.ReadAllText(qaFile));
                if (string.IsNullOrEmpty(content)) return ("", "");
                int idx = content.IndexOf('\u0001');
                if (idx < 0) return ("", "");
                return (content.Substring(0, idx), content.Substring(idx + 1));
            }
            catch { return ("", ""); }
        }

        private static void WriteSecretQA(string poolDir, string question, string answer)
        {
            try
            {
                string qaFile = Path.Combine(poolDir, "secret_qa.lock");
                File.WriteAllText(qaFile, CryptoUtils.EncryptString(question + "\u0001" + answer));
            }
            catch { }
        }

        private bool VerifySecretQA(string poolDir)
        {
            var (question, answer) = ReadSecretQA(poolDir);
            if (string.IsNullOrEmpty(question))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "该卡池未设置密保问题。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            var win = new SecretQAWindow(question, answer) { Owner = Window.GetWindow(this) };
            return win.ShowDialog() == true;
        }

        private bool TryResetPasswordBySecretQA(string poolDir, string poolName)
        {
            if (!VerifySecretQA(poolDir)) return false;

            var newPwd = new PasswordDialog("请设置新密码", true) { Owner = Window.GetWindow(this) };
            if (newPwd.ShowDialog() != true)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "已取消密码重置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            try { CryptoUtils.SavePasswordHash(poolName, newPwd.Password); }
            catch (Exception ex)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "更新密码失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!string.IsNullOrEmpty(newPwd.SecretQuestion))
                WriteSecretQA(poolDir, newPwd.SecretQuestion, newPwd.SecretAnswer);

            CustomMessageBox.Show(Window.GetWindow(this), "密码已重置成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        private void BtnImportPoolFromMain_Click(object sender, RoutedEventArgs e)
        {
            string targetParent = App.SearchDirectories.FirstOrDefault(d => App.IsValidDirectoryPath(d));

            if (string.IsNullOrEmpty(targetParent) || !App.IsDirectoryWritable(targetParent))
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "没有可以写入的卡池目录。\n\n请先到「选项 → 系统」添加一个卡池列表目录，并确认它有写入权限。",
                    "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new ImportPoolsDialog(targetParent) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() == true)
            {
                MessageBus.Publish(new AppMessages.PoolListChanged());
            }
        }
    }
}
