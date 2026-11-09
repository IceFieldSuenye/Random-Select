using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, UserControl> _pageCache = new Dictionary<string, UserControl>();
        private readonly Stack<string> _navStack = new Stack<string>();
        private readonly Dictionary<string, string> _parentPageCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> _transientPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Personalization", "DrawResultPage", "DrawAnimation" };

        private static readonly HashSet<string> _drawFlowPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "DrawAnimation", "DrawResultPage" };

        private bool _isGoingBack = false;
        private bool _isSwitching = false;
        private bool _skipLeaveCheck = false;
        private DispatcherTimer _switchWatchdog;

        private string _pendingNavKey = null;
        private bool _pendingIsBack = false;

        private string _currentPageKey = "Main";
        private UserControl _currentPage = null;

        private static readonly TimeSpan TopBarDuration = TimeSpan.FromMilliseconds(240);
        private const double TopBarFullHeight = 48;

        private readonly MediaPlayer _musicPlayer = new MediaPlayer();
        private string _loadedMusicFullPath = "";
        private string _currentMusicPath = "";
        private bool _musicErrorShown = false;
        private bool _musicShouldPlay;
        private System.Windows.Threading.DispatcherTimer _musicDebounce;
        // ★ 防抖计时器只创建一次，必须读取“当前”待应用参数，不能捕获第一次调用时的值
        private string _musicPendingPath = "";
        private bool _musicPendingEnabled;
        private double _musicPendingVolume = 0.5;
        private bool _musicPendingMuted;
        private bool _isAppActive = true;
        private bool _startupErrorShown;
        private bool _iconCompatNoticeShown;

        private readonly List<Key> _cheatCode = new List<Key>
        { Key.Up, Key.Up, Key.Down, Key.Down, Key.Left, Key.Right, Key.Left, Key.Right };
        private int _cheatIndex = 0;

        // ================================================================
        // ★ 抽取次数浮动面板
        // ================================================================
        private TaskCompletionSource<int?> _drawCountTcs;
        private int _drawCountMax = 9999;

        // ★ 记录"因面板打开而被隐藏按钮"的宿主
        private IDrawButtonsHost _hiddenButtonHost = null;

        // ================================================================
        // ★ CompositionTarget.Rendering 动画引擎
        // ================================================================
        private enum PanelAnimState { Idle, Showing, Hiding }
        private PanelAnimState _panelAnimState = PanelAnimState.Idle;
        private Stopwatch _panelAnimClock;
        private double _panelAnimStartY;
        private double _panelAnimTargetY;
        private double _panelAnimStartOpacity;
        private double _panelAnimTargetOpacity;
        private double _panelAnimDurationMs;
        private Action _panelAnimCompleted;
        private bool _panelAnimSubscribed = false;

        // 面板的动画常量
        private const double PanelShowFromY = 40;
        private const double PanelHideToY = 40;
        private const double PanelShowDurationMs = 240;
        private const double PanelHideDurationMs = 180;

        public MainWindow()
        {
            InitializeComponent();

            ApplyScreenAdaptation();

            try { _musicPlayer.Volume = App.MusicVolume; } catch { }
            try { _musicPlayer.IsMuted = App.IsMuted; } catch { }

            MessageBus.Subscribe<AppMessages.NavigateTo>(OnNavigateRequested);
            MessageBus.Subscribe<AppMessages.NavigateBack>(OnNavigateBackRequested);
            MessageBus.Subscribe<AppMessages.MusicSettingsChanged>(OnMusicSettingsChanged);
            MessageBus.Subscribe<AppMessages.DrawResultPageContinueDraw>(OnDrawResultPageContinueDraw);
            // 窗口标题里的 {pool} / {page} 会随卡池与页面变化
            MessageBus.Subscribe<AppMessages.CurrentPoolChanged>(OnCurrentPoolChangedForTitle);
            MessageBus.Subscribe<AppMessages.PoolListChanged>(OnPoolListChangedForTitle);
            MessageBus.Subscribe<AppMessages.UiOptionsChanged>(OnUiOptionsChangedForTitle);

            _musicPlayer.MediaEnded += MusicPlayer_MediaEnded;
            _musicPlayer.MediaFailed += MusicPlayer_MediaFailed;
            _musicPlayer.MediaOpened += MusicPlayer_MediaOpened;

            if (Application.Current != null)
            {
                Application.Current.Activated += App_Activated;
                Application.Current.Deactivated += App_Deactivated;
            }

            this.StateChanged += MainWindow_StateChanged;

            _pageCache["Main"] = new MainPage_UC();
            _pageCache["Pool"] = new NewPoolPage_UC();
            _pageCache["History"] = new HistoryPage_UC();
            _pageCache["GachaReport"] = new GachaReportPage_UC();
            _pageCache["Options"] = new OptionsPage_UC();
            _pageCache["AdvancedGacha"] = new AdvancedGachaPage_UC();
            _pageCache["Personalization"] = new PersonalizationPage_UC();
            _pageCache["DrawResultPage"] = new DrawResultPage_UC();
            _pageCache["DrawAnimation"] = new DrawAnimationPage_UC();

            UpdateWindowTitle();

            SourceInitialized += (s, e) =>
            {
                SingleInstance.Hook(this);
                try { ThemeManager.ApplyWindowChrome(this); } catch { }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ThemeManager.ApplyWindowChrome(this); } catch { }
                }), DispatcherPriority.ApplicationIdle);
            };

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private void ApplyScreenAdaptation()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double screenW = wa.Width;
                double screenH = wa.Height;

                if (screenW < 320 || screenH < 240)
                {
                    screenW = SystemParameters.PrimaryScreenWidth;
                    screenH = SystemParameters.PrimaryScreenHeight;
                }

                double desiredW = 1000;
                double desiredH = 700;
                double hardMinW = 900;
                double hardMinH = 620;

                double maxW = Math.Max(hardMinW, screenW * 0.92);
                double maxH = Math.Max(hardMinH, screenH * 0.92);

                double initW = Math.Min(desiredW, maxW);
                double initH = Math.Min(desiredH, maxH);

                double minW = Math.Min(initW, hardMinW);
                double minH = Math.Min(initH, hardMinH);

                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Width = initW;
                this.Height = initH;
                this.MinWidth = minW;
                this.MinHeight = minH;

                this.Left = wa.Left + Math.Max(0, (screenW - initW) / 2);
                this.Top = wa.Top + Math.Max(0, (screenH - initH) / 2);
            }
            catch
            {
                this.Width = 1000;
                this.Height = 700;
                this.MinWidth = 900;
                this.MinHeight = 620;
            }
        }

        private void ShowIconCompatNotice()
        {
            try
            {
                var lines = new List<string>
                {
                    $"检测到当前系统为 {App.WindowsDisplayName}，系统图标字体无法完整显示本软件的界面图标。"
                };

                if (App.IsLegacyWindows)
                    lines.Add("· 当前系统版本较低，部分界面效果可能无法正常显示。");

                lines.Add("· 已自动启用「图标兼容模式」：无法显示的字体会替换为相近的旧版图标，或改用文字显示。");
                lines.Add("· 请注意：即使启用兼容模式，仍然可能存在部分问题，例如个别图标缺失、图标样式与正常系统不同、排版出现细微差异。");
                lines.Add("· 建议将系统升级到 Windows 10 1809 或更高版本（推荐 Windows 11），以获得完整的界面显示效果。");

                CustomMessageBox.ShowWithCheckbox(this, string.Join("\n", lines), "图标显示异常",
                    "不再提示", out bool dontAskAgain, MessageBoxImage.Warning);

                if (dontAskAgain && !App.SuppressIconCompatNotice)
                {
                    App.SuppressIconCompatNotice = true;
                    App.SaveSettings();
                    RuntimeLog.Info("用户勾选不再提示图标兼容模式");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "显示图标兼容模式提示失败");
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            App.LoadSettings();

            // 启动时读取卡池/资源目录失败：统一提示一次（不自动创建目录）
            var startupErrors = App.StartupErrors.ToList();
            if (startupErrors.Count > 0 && !_startupErrorShown)
            {
                _startupErrorShown = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        CustomMessageBox.Show(this,
                            "读取卡池或资源目录时遇到问题：\n\n" + string.Join("\n\n", startupErrors),
                            "读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    catch { }
                }), DispatcherPriority.ApplicationIdle);
            }

            if (App.IconCompatActive && !App.SuppressIconCompatNotice && !_iconCompatNoticeShown)
            {
                _iconCompatNoticeShown = true;
                Dispatcher.BeginInvoke(new Action(ShowIconCompatNotice), DispatcherPriority.ApplicationIdle);
            }

            try { IconSupport.StartAutoSweep(this); } catch { }

            try { ThemeManager.ApplyWindowChrome(this); } catch { }

            try { _musicPlayer.Volume = App.MusicVolume; } catch { }

            Navigate("Main", force: true);
            UpdateMusicMuteState();

            try { UpdateBackgroundMusic(App.LastMusicPath, App.IsMusicEnabled, App.MusicVolume, App.IsMuted); } catch { }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { UpdateBackgroundMusic(App.LastMusicPath, App.IsMusicEnabled, App.MusicVolume, App.IsMuted); }
                catch { }
            }), DispatcherPriority.ApplicationIdle);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { AdaptDrawCountPanelToWindow(); } catch { }
            }), DispatcherPriority.Loaded);
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            try { FloatingOverlay.CloseAll(); } catch { }
            UnsubscribePanelAnim();

            MessageBus.Unsubscribe<AppMessages.NavigateTo>(OnNavigateRequested);
            MessageBus.Unsubscribe<AppMessages.NavigateBack>(OnNavigateBackRequested);
            MessageBus.Unsubscribe<AppMessages.MusicSettingsChanged>(OnMusicSettingsChanged);
            MessageBus.Unsubscribe<AppMessages.DrawResultPageContinueDraw>(OnDrawResultPageContinueDraw);

            if (Application.Current != null)
            {
                Application.Current.Activated -= App_Activated;
                Application.Current.Deactivated -= App_Deactivated;
            }

            foreach (var page in _pageCache.Values)
            {
                if (page is IDisposable d) { try { d.Dispose(); } catch { } }
            }

            try { _musicPlayer.Stop(); } catch { }
            try { _musicPlayer.Close(); } catch { }
            try { App.SaveSettings(); } catch { }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // ★ 抽取 / 渲染结果 / 生成长图期间禁止关闭程序：
            //   这些操作在 UI 线程上分块跑，中途关窗会留下半成品文件或让渲染卡死。
            try
            {
                if (AppBusy.IsBusy)
                {
                    e.Cancel = true;
                    RuntimeLog.Info("正在处理中，已拦截关闭窗口：" + AppBusy.CurrentReasons);
                    try
                    {
                        ToastNotification.ShowWarning(this, "有任务进行中，请稍后再试。");
                    }
                    catch { }
                    return;
                }
            }
            catch { }

            if (_drawCountTcs != null)
            {
                UnsubscribePanelAnim();
                var tcs = _drawCountTcs;
                _drawCountTcs = null;
                DrawCountOverlay.Visibility = Visibility.Collapsed;
                SetDrawCountBusy(false);
                RestoreHiddenButtonHost();
                tcs.TrySetResult(null);
            }

            var currentPage = _currentPage;
            if (currentPage is IConfirmLeave confirm)
            {
                if (confirm.CanLeave()) { base.OnClosing(e); return; }
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        private void App_Activated(object sender, EventArgs e)
        {
            _isAppActive = true;
            UpdateMusicMuteState();
            if (_pageCache.TryGetValue("Main", out var mp) && mp is MainPage_UC mainPage)
            { try { mainPage.SetActive(true); } catch { } }
        }

        private void App_Deactivated(object sender, EventArgs e)
        {
            _isAppActive = false;
            UpdateMusicMuteState();
            if (_pageCache.TryGetValue("Main", out var mp) && mp is MainPage_UC mainPage)
            { try { mainPage.SetActive(false); } catch { } }
        }

        // ================================================================
        // ★ CompositionTarget.Rendering 动画引擎
        // ================================================================

        private void SubscribePanelAnim()
        {
            if (_panelAnimSubscribed) return;
            _panelAnimSubscribed = true;
            CompositionTarget.Rendering += OnPanelRendering;
        }

        private void UnsubscribePanelAnim()
        {
            if (!_panelAnimSubscribed) return;
            _panelAnimSubscribed = false;
            CompositionTarget.Rendering -= OnPanelRendering;
            _panelAnimClock?.Stop();
        }

        private void OnPanelRendering(object sender, EventArgs e)
        {
            if (_panelAnimState == PanelAnimState.Idle) return;
            if (_panelAnimClock == null) return;

            double elapsedMs = _panelAnimClock.Elapsed.TotalMilliseconds;
            double t = Math.Min(1.0, elapsedMs / _panelAnimDurationMs);

            // 手写缓动曲线，避免每次 new EasingFunction 的对象开销
            double eased = _panelAnimState == PanelAnimState.Showing
                ? EaseOutCubic(t)
                : EaseInCubic(t);

            // 直接写渲染层属性：不触发 Measure/Arrange
            DrawCountPanelTransform.Y = _panelAnimStartY + (_panelAnimTargetY - _panelAnimStartY) * eased;
            DrawCountPanelAnimator.Opacity = _panelAnimStartOpacity + (_panelAnimTargetOpacity - _panelAnimStartOpacity) * eased;

            if (t >= 1.0)
            {
                var completed = _panelAnimCompleted;
                _panelAnimCompleted = null;
                _panelAnimState = PanelAnimState.Idle;
                _panelAnimClock.Stop();
                UnsubscribePanelAnim();

                DrawCountPanelTransform.Y = _panelAnimTargetY;
                DrawCountPanelAnimator.Opacity = _panelAnimTargetOpacity;

                completed?.Invoke();
            }
        }

        private static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3.0);
        private static double EaseInCubic(double t) => t * t * t;

        private void StartPanelAnimation(
            double fromY, double toY,
            double fromOpacity, double toOpacity,
            double durationMs,
            PanelAnimState state,
            Action onCompleted)
        {
            // 中断旧动画
            UnsubscribePanelAnim();

            _panelAnimState = state;
            _panelAnimStartY = fromY;
            _panelAnimTargetY = toY;
            _panelAnimStartOpacity = fromOpacity;
            _panelAnimTargetOpacity = toOpacity;
            _panelAnimDurationMs = Math.Max(1, durationMs);
            _panelAnimCompleted = onCompleted;

            DrawCountPanelTransform.Y = fromY;
            DrawCountPanelAnimator.Opacity = fromOpacity;

            _panelAnimClock = Stopwatch.StartNew();
            SubscribePanelAnim();
        }

        // ================================================================
        // ★ 抽取次数浮动面板：显示 / 隐藏
        // ================================================================

        public Task<int?> ShowDrawCountPanelAsync(int defaultCount = 10, int maxCount = 9999)
        {
            SetDrawCountBusy(false);
            if (_drawCountTcs != null) return _drawCountTcs.Task;

            _drawCountMax = maxCount > 0 ? maxCount : 9999;
            _drawCountTcs = new TaskCompletionSource<int?>();

            if (defaultCount <= 0) defaultCount = 1;
            if (defaultCount > _drawCountMax) defaultCount = _drawCountMax;
            TxtDrawCountInput.Text = defaultCount.ToString();

            AdaptDrawCountPanelToWindow();
            HideCurrentPageDrawButtons();

            DrawCountOverlay.Visibility = Visibility.Visible;


            // 先把面板排版好再启动动画，避免首帧掉帧
            DrawCountOverlay.UpdateLayout();

            if (App.IsAnimationDisabled)
            {
                DrawCountPanelTransform.Y = 0;
                DrawCountPanelAnimator.Opacity = 1;
            }
            else
            {
                StartPanelAnimation(
                    PanelShowFromY, 0,
                    0, 1,
                    PanelShowDurationMs,
                    PanelAnimState.Showing,
                    null);
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    TxtDrawCountInput.Focus();
                    TxtDrawCountInput.CaretIndex = TxtDrawCountInput.Text.Length;
                }
                catch { }
            }), DispatcherPriority.Input);

            return _drawCountTcs.Task;
        }

        /// <summary>
        /// 面板尺寸自适应屏幕工作区：
        ///   · 宽度 = min(720, max(400, workAreaW * 0.40))
        ///   · 高度 = min(560, max(360, workAreaH * 0.55))
        /// 由于 Viewbox 是 DownOnly，实际尺寸只会小于等于设计稿 420，不会放大。
        /// </summary>
        private void AdaptDrawCountPanelToWindow()
        {
            try
            {
                var wa = SystemParameters.WorkArea;
                double screenW = wa.Width;
                double screenH = wa.Height;

                if (screenW < 320) screenW = SystemParameters.PrimaryScreenWidth;
                if (screenH < 240) screenH = SystemParameters.PrimaryScreenHeight;

                double maxW = Math.Max(400, Math.Min(720, screenW * 0.40));
                double maxH = Math.Max(360, Math.Min(560, screenH * 0.55));

                DrawCountPanelAnimator.MaxWidth = maxW;
                DrawCountPanelAnimator.MaxHeight = maxH;
            }
            catch { }
        }

        private void CloseDrawCountPanel(int? result, bool restoreButtons)
        {
            if (_drawCountTcs == null) return;

            var tcs = _drawCountTcs;
            _drawCountTcs = null;

            Action afterHide = () =>
            {
                DrawCountOverlay.Visibility = Visibility.Collapsed;
                DrawCountPanelTransform.Y = PanelHideToY;
                DrawCountPanelAnimator.Opacity = 0;

                if (restoreButtons)
                {
                    RestoreHiddenButtonHost();
                }
                else
                {
                    _hiddenButtonHost = null;
                }

                tcs.TrySetResult(result);
            };

            if (App.IsAnimationDisabled)
            {
                afterHide();
                return;
            }

            // 取消时立刻恢复按钮（与面板淡出并行），避免面板关完才出现按钮的停顿
            if (restoreButtons)
            {
                RestoreHiddenButtonHost();
            }
            else
            {
                _hiddenButtonHost = null;
            }

            double startY = DrawCountPanelTransform.Y;
            double startOpacity = DrawCountPanelAnimator.Opacity;
            if (startOpacity <= 0) startOpacity = 1;

            StartPanelAnimation(
                startY, PanelHideToY,
                startOpacity, 0,
                PanelHideDurationMs,
                PanelAnimState.Hiding,
                afterHide);
        }

        // ================================================================
        // ★ 按钮显隐
        // ================================================================
        private void HideCurrentPageDrawButtons()
        {
            try
            {
                if (_currentPage is IDrawButtonsHost host)
                {
                    host.SetDrawButtonsVisibleInstant(false);
                    _hiddenButtonHost = host;
                }
            }
            catch { }
        }

        private void RestoreHiddenButtonHost()
        {
            try
            {
                if (_hiddenButtonHost != null)
                {
                    _hiddenButtonHost.SetDrawButtonsVisible(true);
                    _hiddenButtonHost = null;
                }
            }
            catch { }
        }

        private void DrawCountNumBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                TxtDrawCountInput.Text = btn.Tag.ToString();
                TxtDrawCountInput.Focus();
                TxtDrawCountInput.CaretIndex = TxtDrawCountInput.Text.Length;
            }
        }

        // ================================================================
        // 「开始抽取」按钮：点阵加载动画 + 禁用按钮
        // ================================================================
        private void SetDrawCountBusy(bool busy)
        {
            try
            {
                if (BtnDrawCountConfirm != null) BtnDrawCountConfirm.IsEnabled = !busy;
                if (BtnDrawCountCancel != null) BtnDrawCountCancel.IsEnabled = !busy;

                if (TxtStartIcon != null) TxtStartIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;

                if (StartSpinner != null)
                {
                    StartSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                    StartSpinner.SetColor(ThemeManager.GetBrush("Fg_333333"));

                    // 状态类动画：即使「禁用动画」开启也要转，否则看起来像卡住了
                    if (busy) StartSpinner.Start();
                    else StartSpinner.Stop();
                }
            }
            catch { }
        }

        private void DrawCountCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawCountPanel(null, restoreButtons: true);
        }

        /// <summary>显示「正在抽取」遮罩（大量抽取时避免界面像未响应）。</summary>
        public void ShowDrawBusy(string text = "正在抽取中，请稍候...")
        {
            try
            {
                AppBusy.Enter("draw");
                if (TxtDrawBusy != null) TxtDrawBusy.Text = text;
                if (DrawBusyOverlay != null) DrawBusyOverlay.Visibility = Visibility.Visible;
                if (DrawBusySpinner != null)
                {
                    DrawBusySpinner.SetColor(ThemeManager.GetBrush("Fg_333333"));
                    // 状态类动画：禁用动画时也照常旋转
                    DrawBusySpinner.Start();
                }
            }
            catch { }
        }

        /// <summary>隐藏「正在抽取」遮罩。</summary>
        public void HideDrawBusy()
        {
            try
            {
                AppBusy.Exit("draw");
                if (DrawBusySpinner != null) DrawBusySpinner.Stop();
                if (DrawBusyOverlay != null) DrawBusyOverlay.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // ================================================================
        // 「生成长图」进度遮罩
        // ★ 必须挂在窗口根 Grid（跨两行）：放在页面里会被 PageHost 裁掉顶部导航栏，
        //   卡片也只会相对内容区居中，看起来偏下。
        // ================================================================
        /// <summary>显示「正在生成长图」遮罩（覆盖整个窗口，卡片居中）。</summary>
        public void ShowShareBusy(string text = "正在生成长图…")
        {
            try
            {
                if (TxtShareBusy != null) TxtShareBusy.Text = text;
                if (ShareBusyBar != null) ShareBusyBar.Value = 0;
                if (TxtShareBusyPercent != null) TxtShareBusyPercent.Text = "0%";

                if (ShareBusyOverlay == null) return;

                AppBusy.Enter("share");
                ShareBusyOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                ShareBusyOverlay.Opacity = App.IsAnimationDisabled ? 1 : 0;
                ShareBusyOverlay.Visibility = Visibility.Visible;

                if (!App.IsAnimationDisabled)
                {
                    ShareBusyOverlay.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                        });
                }
            }
            catch { }
        }

        /// <summary>更新「正在生成长图」的进度（0~100）。</summary>
        public void UpdateShareBusy(double percent)
        {
            try
            {
                double value = Math.Max(0, Math.Min(100, percent));
                if (ShareBusyBar != null) ShareBusyBar.Value = value;
                if (TxtShareBusyPercent != null) TxtShareBusyPercent.Text = $"{value:0}%";
            }
            catch { }
        }

        /// <summary>隐藏「正在生成长图」遮罩。</summary>
        public void HideShareBusy()
        {
            try
            {
                if (ShareBusyOverlay == null) return;
                AppBusy.Exit("share");
                ShareBusyOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                ShareBusyOverlay.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void DrawCountConfirm_Click(object sender, RoutedEventArgs e)
        {
            string input = (TxtDrawCountInput.Text ?? "").Trim();
            if (int.TryParse(input, out int count) && count > 0 && count <= _drawCountMax)
            {
                SetDrawCountBusy(true);
                CloseDrawCountPanel(count, restoreButtons: false);
            }
            else
            {
                CustomMessageBox.Show(this,
                    $"请输入有效的抽取次数（1~{_drawCountMax} 之间的整数）！",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtDrawCountInput.Focus();
                TxtDrawCountInput.CaretIndex = TxtDrawCountInput.Text.Length;
            }
        }

        // ================================================================
        // 抽取继续
        // ================================================================
        private async void OnDrawResultPageContinueDraw(AppMessages.DrawResultPageContinueDraw msg)
        {
            if (msg == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnDrawResultPageContinueDraw(msg)));
                return;
            }

            var session = DrawSession.Current;
            if (session == null || session.Pool == null || session.Pool.Items.Count == 0)
            {
                BtnHome_Click(this, new RoutedEventArgs());
                return;
            }

            int count = msg.DrawCount <= 0 ? 1 : msg.DrawCount;
            var drawTimer = System.Diagnostics.Stopwatch.StartNew();
            RuntimeLog.Info($"继续抽取开始：卡池={session.Pool.UniqueName}，条目={session.Pool.ItemCount}，次数={count}，不重复={session.NoRepeat}，动画={session.UseAnimation}，展示模式={session.DisplayMode}");
            // 计算放后台；遮罩只在 ≥500 次时显示，避免少量抽取时闪一下
            bool heavyWork = count >= UiThresholds.BackgroundWorkMinCount;
            bool showBusy = count >= UiThresholds.LoadingOverlayMinCount;
            List<DrawResultData> results;
            try
            {
                if (heavyWork)
                {
                    if (showBusy) ShowDrawBusy("正在抽取中，请稍候...");
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
                // ★ async void 里 throw 会变成未处理异常，改为提示后返回
                RuntimeLog.Error(ex, $"继续抽取失败：卡池={session.Pool.UniqueName}，次数={count}");
                CustomMessageBox.Show(this,
                    "继续抽取失败：\n\n" + ex.Message,
                    "抽取失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                if (showBusy) HideDrawBusy();
            }

            RuntimeLog.Info($"继续抽取完成：结果={results.Count}，耗时={drawTimer.ElapsedMilliseconds}ms");
            if (results.Count == 0) return;

            session.LastDrawCount = count;
            session.RefreshDisplayMode();

            bool isOnAnimation = string.Equals(_currentPageKey, "DrawAnimation", StringComparison.OrdinalIgnoreCase);
            bool isOnResult = string.Equals(_currentPageKey, "DrawResultPage", StringComparison.OrdinalIgnoreCase);

            if (session.UseAnimation)
            {
                MessageBus.Publish(new AppMessages.OpenDrawAnimation(
                    results, session.Pool.Dir, count, session.Pers,
                    session.Highlight, session.DisplayMode, count == 1));
                if (!isOnAnimation) Navigate("DrawAnimation");
            }
            else
            {
                MessageBus.Publish(new AppMessages.OpenDrawResultPage(
                    results, session.Pool.Dir, count, session.Pers,
                    session.Highlight, session.DisplayMode));
                if (!isOnResult) Navigate("DrawResultPage");
            }
        }

        // ================================================================
        // 键盘
        // ================================================================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ★ 抽取 / 渲染 / 生成长图期间，键盘快捷键一律不处理（尤其 Esc 不能退回主页）
            if (AppBusy.IsBusy)
            {
                e.Handled = true;
                return;
            }

            // ★ 浮动界面打开时，按键交给 FloatingOverlay 处理，避免穿透到页面逻辑
            if (FloatingOverlay.IsOpen) return;

            if (DrawCountOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape)
                {
                    CloseDrawCountPanel(null, restoreButtons: true);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Enter)
                {
                    DrawCountConfirm_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
                return;
            }

            if (e.Key == Key.F12)
            {
                // ★ 任意界面都可以用 F12 临时显示 / 隐藏已隐藏的功能
                HandleF12();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (IsEditingInDataGrid()) return;
                if (_currentPage is IHandleEscape handler)
                {
                    if (handler.TryHandleEscape()) { e.Handled = true; return; }
                }
                HandleEscapeKey();
                e.Handled = true;
                return;
            }

            if (_currentPageKey == "Main")
            {
                if (_currentPage is MainPage_UC mainPage)
                {
                    if (e.Key == Key.Up) { mainPage.SelectPrevPool(); e.Handled = true; return; }
                    if (e.Key == Key.Down) { mainPage.SelectNextPool(); e.Handled = true; return; }
                }
            }

            if (_currentPageKey == "History")
            {
                if (_currentPage is HistoryPage_UC historyPage)
                {
                    if (e.Key == Key.Left) { historyPage.GoToPrevPage(); e.Handled = true; return; }
                    if (e.Key == Key.Right) { historyPage.GoToNextPage(); e.Handled = true; return; }
                }
            }

            if (_currentPageKey != "Options") { _cheatIndex = 0; return; }

            var optionsPage = _currentPage as OptionsPage_UC;
            if (optionsPage == null || !optionsPage.IsAboutVisible) { _cheatIndex = 0; return; }

            if (e.Key == _cheatCode[_cheatIndex])
            {
                _cheatIndex++;
                if (_cheatIndex == _cheatCode.Count)
                {
                    _cheatIndex = 0;
                    optionsPage.TriggerEasterEggExternal();
                }
            }
            else
            {
                _cheatIndex = 0;
                if (e.Key == _cheatCode[0]) _cheatIndex = 1;
            }
        }

        private void HandleF12()
        {
            if (!App.HasAnyHiddenOption) return;

            App.IsHiddenRevealed = !App.IsHiddenRevealed;

            // 先刷新界面，再给出提示，这样点掉提示时已经能看到变化
            MessageBus.Publish(new AppMessages.UiOptionsChanged());

            try
            {
                CustomMessageBox.Show(this,
                    App.IsHiddenRevealed
                        ? "已暂时显示所有已隐藏的功能，再次按 F12 可恢复隐藏（重启后自动恢复）。"
                        : "已恢复隐藏的功能。",
                    "功能隐藏",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private static bool IsEditingInDataGrid()
        {
            try
            {
                var focused = Keyboard.FocusedElement as DependencyObject;
                while (focused != null)
                {
                    if (focused is DataGridCell cell) return cell.IsEditing;
                    if (focused is DataGrid) return false;
                    focused = (focused is Visual || focused is System.Windows.Media.Media3D.Visual3D)
                        ? VisualTreeHelper.GetParent(focused)
                        : LogicalTreeHelper.GetParent(focused);
                }
            }
            catch { }
            return false;
        }

        private void HandleEscapeKey()
        {
            if (_currentPageKey == "Main")
            {
                var r = CustomMessageBox.ShowCustom(this, "确定要退出应用吗？", "退出应用",
                    "退出", "\uF3B1", "取消", "\uE711", MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) this.Close();
                return;
            }
            BtnBack_Click(this, new RoutedEventArgs());
        }

        // ================================================================
        // 导航
        // ================================================================
        private void OnNavigateRequested(AppMessages.NavigateTo msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.PageKey)) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Navigate(msg.PageKey)));
                return;
            }
            Navigate(msg.PageKey);
        }

        private void OnNavigateBackRequested(AppMessages.NavigateBack msg)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => BtnBack_Click(this, new RoutedEventArgs())));
                return;
            }
            BtnBack_Click(this, new RoutedEventArgs());
        }

        private static PageEnterSource GetEnterSource(string fromKey)
        {
            if (fromKey == "Main") return PageEnterSource.Main;
            if (fromKey == "Options") return PageEnterSource.Options;
            return PageEnterSource.Other;
        }

        private void Navigate(string key, bool force = false)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_pageCache.TryGetValue(key, out var page)) return;

            if (!string.Equals(_currentPageKey, key, StringComparison.Ordinal))
                RuntimeLog.Info($"页面导航：{_currentPageKey} -> {key}");

            if (_isSwitching)
            {
                _pendingNavKey = key;
                _pendingIsBack = _isGoingBack;
                return;
            }

            var oldPage = _currentPage;
            string fromKey = _currentPageKey;

            if (!force && oldPage == page)
            {
                MessageBus.Publish(new AppMessages.PageActivated(key));
                return;
            }

            if (!_skipLeaveCheck && oldPage != null && oldPage is IConfirmLeave confirm)
            {
                if (!confirm.CanLeave()) return;
            }

            bool isFromTransient = _transientPages.Contains(_currentPageKey);
            bool isToTransient = _transientPages.Contains(key);

            if (isToTransient && oldPage != null && _currentPageKey != key)
            {
                if (_drawFlowPages.Contains(key)) _parentPageCache[key] = "Main";
                else _parentPageCache[key] = _currentPageKey;
            }

            bool isBackward = _isGoingBack;

            if (!_isGoingBack && !isFromTransient && !isToTransient && oldPage != null && _currentPageKey != key)
            {
                if (_navStack.Count > 0 && _navStack.Peek() == key)
                {
                    _navStack.Pop();
                    isBackward = true;
                }
                else if (_navStack.Count == 0 || _navStack.Peek() != _currentPageKey)
                {
                    _navStack.Push(_currentPageKey);
                }
            }

            NavigationContext.CurrentSource = GetEnterSource(fromKey);
            _currentPageKey = key;
            UpdateWindowTitle();

            bool isFullscreenPage = (key == "Main") || (key == "DrawResultPage") || (key == "DrawAnimation");
            bool needShowTopBar = !isFullscreenPage && (TopBar.Visibility != Visibility.Visible);
            bool needHideTopBar = isFullscreenPage && (TopBar.Visibility == Visibility.Visible);

            if (key != "Main") TxtPageTitle.Text = GetPageTitle(key);

            SwitchPage(page, needShowTopBar, needHideTopBar, isBackward, () =>
            {
                if (key == "Main")
                {
                    RestoreMainPageDrawButtons();
                }
                MessageBus.Publish(new AppMessages.PageActivated(key));
            });

            if (App.IconCompatActive)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { IconSupport.NormalizeTree(this); } catch { }
                    try { IconSupport.RequestSweep(this); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void RestoreMainPageDrawButtons()
        {
            try
            {
                if (_pageCache.TryGetValue("Main", out var page) && page is MainPage_UC mp)
                {
                    mp.SetDrawButtonsVisible(true);
                }
            }
            catch { }
        }

        private void SwitchPage(UserControl newPage, bool needShowTopBar, bool needHideTopBar,
                                bool isBackward, Action onComplete)
        {
            var oldPage = _currentPage;

            if (oldPage == null || !PageHost.Children.Contains(oldPage))
            {
                PageHost.Children.Clear();
                PageHost.Children.Add(newPage);
                newPage.Visibility = Visibility.Visible;
                newPage.Opacity = 1;

                if (needShowTopBar) PlayTopBarShowAnimation();
                if (needHideTopBar) PlayTopBarHideAnimation();

                if (newPage is IPageAnimation a) { try { a.PlayEnterAnimation(() => { }); } catch { } }

                _currentPage = newPage;
                onComplete?.Invoke();
                return;
            }

            if (App.IsAnimationDisabled)
            {
                try { PageHost.Children.Remove(oldPage); } catch { }
                if (!PageHost.Children.Contains(newPage)) PageHost.Children.Add(newPage);
                newPage.Visibility = Visibility.Visible;
                newPage.Opacity = 1;
                newPage.RenderTransform = Transform.Identity;

                if (newPage is IPageAnimation newAnimSkip) { try { newAnimSkip.PlayEnterAnimation(() => { }); } catch { } }

                if (needShowTopBar) { TopBar.Visibility = Visibility.Visible; TopBar.Height = TopBarFullHeight; TopBar.Opacity = 1; }
                if (needHideTopBar) { TopBar.Visibility = Visibility.Collapsed; TopBar.Height = 0; TopBar.Opacity = 0; TxtPageTitle.Text = ""; }

                _currentPage = newPage;
                onComplete?.Invoke();
                return;
            }

            _isSwitching = true;

            if (!PageHost.Children.Contains(newPage)) PageHost.Children.Add(newPage);
            newPage.Visibility = Visibility.Visible;
            newPage.Opacity = 1;

            try { Panel.SetZIndex(oldPage, 0); } catch { }
            try { Panel.SetZIndex(newPage, 1); } catch { }

            if (needShowTopBar) PlayTopBarShowAnimation();
            if (needHideTopBar) PlayTopBarHideAnimation();

            int remaining = 2;
            bool cleaned = false;
            Action onBothDone = () =>
            {
                if (--remaining > 0) return;
                if (cleaned) return;
                cleaned = true;
                _switchWatchdog?.Stop();

                try { PageHost.Children.Remove(oldPage); } catch { }

                // 清掉旧页面残留的透明度/位移，避免下次复用时带着上一次的动画状态
                try
                {
                    oldPage.Opacity = 1;
                    oldPage.RenderTransform = Transform.Identity;
                    oldPage.Visibility = Visibility.Visible;
                }
                catch { }

                // 子页面里可能还开着浮层（例如选择弹窗），离开时统一收掉
                try { if (FloatingOverlay.IsOpen) FloatingOverlay.CloseAll(); } catch { }

                _currentPage = newPage;
                _isSwitching = false;
                PageHost.IsHitTestVisible = true;

                if (!string.IsNullOrEmpty(_pendingNavKey))
                {
                    string next = _pendingNavKey;
                    bool wasBack = _pendingIsBack;
                    _pendingNavKey = null;
                    _pendingIsBack = false;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _isGoingBack = wasBack;
                        try { Navigate(next); }
                        finally { _isGoingBack = false; }
                    }), DispatcherPriority.Background);
                    return;
                }

                onComplete?.Invoke();
            };

            // ★ 切换动画兜底：页面动画回调若因异常/控件卸载而丢失，remaining 永远凑不满，
            //   之后所有导航只会写进 _pendingNavKey，界面表现为「点哪都没反应」。
            _switchWatchdog?.Stop();
            _switchWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _switchWatchdog.Tick += (s, e) =>
            {
                _switchWatchdog?.Stop();
                if (cleaned) return;
                RuntimeLog.Warn("页面切换超时，强制结束切换动画");
                onBothDone();
                onBothDone();
            };
            _switchWatchdog.Start();

            if (oldPage is IPageAnimation oldAnim) { try { oldAnim.PlayExitAnimation(onBothDone); } catch { onBothDone(); } }
            else onBothDone();

            if (newPage is IPageAnimation newAnim)
            {
                if (isBackward)
                {
                    try { newAnim.PlayEnterAsReverseAnimation(onBothDone); }
                    catch
                    {
                        try { newAnim.PlayEnterAnimation(onBothDone); }
                        catch { onBothDone(); }
                    }
                }
                else
                {
                    try { newAnim.PlayEnterAnimation(onBothDone); }
                    catch { onBothDone(); }
                }
            }
            else onBothDone();
        }

        private void PlayTopBarShowAnimation()
        {
            TopBar.Visibility = Visibility.Visible;
            TopBar.BeginAnimation(FrameworkElement.HeightProperty, null);
            TopBar.BeginAnimation(UIElement.OpacityProperty, null);
            TopBar.Height = 0;
            TopBar.Opacity = 0;

            if (App.IsAnimationDisabled) { TopBar.Height = TopBarFullHeight; TopBar.Opacity = 1; return; }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var hAnim = new DoubleAnimation(0, TopBarFullHeight, TopBarDuration) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(0, 1, TopBarDuration) { EasingFunction = ease };

            TopBar.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
            TopBar.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private void PlayTopBarHideAnimation(Action onComplete = null)
        {
            if (App.IsAnimationDisabled)
            {
                TopBar.Visibility = Visibility.Collapsed;
                TopBar.Height = 0;
                TopBar.Opacity = 0;
                TxtPageTitle.Text = "";
                onComplete?.Invoke();
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            double startH = TopBar.ActualHeight;
            double startOpa = TopBar.Opacity;

            var hAnim = new DoubleAnimation(startH, 0, TopBarDuration) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(startOpa, 0, TopBarDuration) { EasingFunction = ease };

            opacityAnim.Completed += (s, e) =>
            {
                try
                {
                    TopBar.BeginAnimation(FrameworkElement.HeightProperty, null);
                    TopBar.BeginAnimation(UIElement.OpacityProperty, null);
                    TopBar.Height = 0;
                    TopBar.Opacity = 0;
                    TopBar.Visibility = Visibility.Collapsed;
                }
                catch { }
                onComplete?.Invoke();
            };

            TopBar.BeginAnimation(FrameworkElement.HeightProperty, hAnim);
            TopBar.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }

        private string GetPageTitle(string key)
        {
            switch (key)
            {
                case "Main": return "主页";
                case "Pool":
                    if (_pageCache.TryGetValue("Pool", out var poolPage) && poolPage is NewPoolPage_UC newPoolPage)
                        return newPoolPage.CurrentTitle;
                    return "卡池管理";
                case "History": return "历史记录";
                case "GachaReport": return "查看抽取报告";
                case "Options": return "选项";
                case "AdvancedGacha": return "高级抽卡规则";
                case "MultiSelect": return "选择角色";
                case "CelebrationUpSelector": return "选择庆典招募UP学生";
                case "Personalization": return "个性化抽取对象";
                case "DrawResultPage": return "抽取结果";
                case "DrawAnimation": return "抽取动画";
                default: return "";
            }
        }

        // ================================================================
        // 窗口标题（选项 → 常规 → 外观 → 窗口标题）
        // ================================================================
        private DispatcherTimer _windowTitleTimer;

        /// <summary>按「窗口标题」模板刷新标题栏文本；模板为空时用默认标题。</summary>
        public void UpdateWindowTitle()
        {
            try
            {
                string format = App.WindowTitleFormat ?? "";
                string title = BuildWindowTitle(format);
                if (!string.Equals(Title, title, StringComparison.Ordinal)) Title = title;
                UpdateWindowTitleClock(format);
            }
            catch { }
        }

        private string BuildWindowTitle(string format)
        {
            bool isMainPage = string.Equals(_currentPageKey, "Main", StringComparison.Ordinal);
            string pageTitle = GetPageTitle(_currentPageKey);
            string poolName = isMainPage ? (App.LastSelectedPoolName ?? "") : "";
            return WindowTitleFormatter.Format(format, WindowTitleFormatter.GetAppVersionText(),
                pageTitle, poolName, isMainPage, DateTime.Now);
        }

        /// <summary>模板里含 {time} 时每秒刷新一次标题（不含就停掉计时器）。</summary>
        private void UpdateWindowTitleClock(string format)
        {
            bool needsClock = WindowTitleFormatter.NeedsClock(format);

            if (!needsClock)
            {
                if (_windowTitleTimer != null)
                {
                    _windowTitleTimer.Stop();
                    _windowTitleTimer = null;
                }
                return;
            }

            if (_windowTitleTimer != null) return;

            _windowTitleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _windowTitleTimer.Tick += (s, e) =>
            {
                try
                {
                    string title = BuildWindowTitle(App.WindowTitleFormat ?? "");
                    if (!string.Equals(Title, title, StringComparison.Ordinal)) Title = title;
                }
                catch { }
            };
            _windowTitleTimer.Start();
        }

        private void OnCurrentPoolChangedForTitle(AppMessages.CurrentPoolChanged msg) => UpdateWindowTitle();

        private void OnPoolListChangedForTitle(AppMessages.PoolListChanged msg) => UpdateWindowTitle();

        private void OnUiOptionsChangedForTitle(AppMessages.UiOptionsChanged msg) => UpdateWindowTitle();

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            // 抽取 / 渲染 / 生成长图期间，用户主动切页一律拦下（程序内部的流程导航不受影响）
            if (AppBusy.IsBusy) return;

            if (_transientPages.Contains(_currentPageKey))
            {
                if (_parentPageCache.TryGetValue(_currentPageKey, out var parent)
                    && !string.IsNullOrEmpty(parent)
                    && _pageCache.ContainsKey(parent))
                {
                    if (_currentPage is IConfirmLeave confirmT && !confirmT.CanLeave()) return;

                    _isGoingBack = true;
                    _skipLeaveCheck = true;
                    try { Navigate(parent); }
                    finally { _skipLeaveCheck = false; _isGoingBack = false; }
                    return;
                }
            }

            if (_currentPage is IConfirmLeave confirm) { if (!confirm.CanLeave()) return; }

            _isGoingBack = true;
            _skipLeaveCheck = true;
            try
            {
                string target = (_navStack.Count > 0) ? _navStack.Pop() : "Main";
                Navigate(target);
            }
            finally
            {
                _skipLeaveCheck = false;
                _isGoingBack = false;
            }
        }

        private void BtnHome_Click(object sender, RoutedEventArgs e)
        {
            if (AppBusy.IsBusy) return;

            var keysToCheck = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_currentPageKey != "Main" && seen.Add(_currentPageKey)) keysToCheck.Add(_currentPageKey);
            foreach (var k in _navStack) if (k != "Main" && seen.Add(k)) keysToCheck.Add(k);

            var unsaved = new List<string>();
            foreach (var k in keysToCheck)
            {
                if (_pageCache.TryGetValue(k, out var p) && p is IConfirmLeave c)
                {
                    if (c.HasUnsavedChanges())
                    {
                        string label = string.IsNullOrEmpty(c.UnsavedLabel) ? k : c.UnsavedLabel;
                        if (!unsaved.Contains(label)) unsaved.Add(label);
                    }
                }
            }

            if (unsaved.Count > 0)
            {
                string list = string.Join("\n", unsaved.Select(x => "  • " + x));
                var choice = CustomMessageBox.ShowUnsavedChanges(this,
                    "以下页面有未保存的修改：\n\n" + list +
                    "\n\n是否放弃所有未保存的修改并返回主页？");
                if (choice != UnsavedChoice.Discard) return;
            }

            _navStack.Clear();
            _parentPageCache.Clear();
            _isGoingBack = true;
            _skipLeaveCheck = true;
            try { Navigate("Main"); }
            finally { _skipLeaveCheck = false; _isGoingBack = false; }
        }

        // ================================================================
        // 音乐
        // ================================================================
        private void OnMusicSettingsChanged(AppMessages.MusicSettingsChanged msg)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                UpdateBackgroundMusic(App.LastMusicPath, App.IsMusicEnabled, App.MusicVolume, App.IsMuted);
                UpdateMusicMuteState();
            }));
        }

        private void UpdateMusicMuteState()
        {
            bool autoMute = App.MuteWhenUnfocused && (!_isAppActive || this.WindowState == WindowState.Minimized);
            try { _musicPlayer.IsMuted = App.IsMuted || autoMute; } catch { }
        }

        public void UpdateBackgroundMusic(string path, bool isEnabled, double volume, bool isMuted)
        {
            try { _musicPlayer.Volume = volume; } catch { }

            _musicPendingPath = path ?? "";
            _musicPendingEnabled = isEnabled;
            _musicPendingVolume = volume;
            _musicPendingMuted = isMuted;

            // 快速开关时避免多次 Open / Play 竞争：关闭时立即停止，开启做 120ms 防抖
            _musicShouldPlay = isEnabled && !string.IsNullOrEmpty(_musicPendingPath);

            if (!_musicShouldPlay)
            {
                // ★ 立即取消尚未执行的防抖播放，并静音兜底；
                //   这样刚开始缓冲/播放时点击关闭也能立刻停下来。
                try { _musicDebounce?.Stop(); } catch { }
                try
                {
                    _musicPlayer.Stop();
                    _musicPlayer.Close();
                    _loadedMusicFullPath = "";
                    _musicPlayer.IsMuted = true;
                }
                catch { }
                return;
            }

            if (_musicDebounce == null)
            {
                _musicDebounce = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(120)
                };

                _musicDebounce.Tick += (s, e) =>
                {
                    _musicDebounce.Stop();
                    // ★ 使用当前待应用参数，而不是创建计时器时捕获的旧值
                    ApplyBackgroundMusic(_musicPendingPath, _musicPendingEnabled,
                        _musicPendingVolume, _musicPendingMuted);
                };
            }

            _musicDebounce.Stop();
            _musicDebounce.Start();
        }

        private void ApplyBackgroundMusic(string path, bool isEnabled, double volume, bool isMuted)
        {
            if (!_musicShouldPlay)
            {
                try { _musicPlayer.Stop(); } catch { }
                return;
            }

            try { _musicPlayer.Volume = volume; } catch { }

            string newPath = path ?? "";
            if (!string.Equals(_currentMusicPath, newPath, StringComparison.OrdinalIgnoreCase)) _musicErrorShown = false;
            _currentMusicPath = newPath;

            string fullPath = Path.Combine(App.ResourcesDir, newPath);
            if (!File.Exists(fullPath))
            {
                try { _musicPlayer.Stop(); } catch { }
                ShowMusicError($"背景音乐文件不存在：\n\n曲目：{newPath}\n路径：{fullPath}\n\n请在 选项 → 常规 中重新选择音频文件。");
                return;
            }

            bool needResetSource = string.IsNullOrEmpty(_loadedMusicFullPath)
                || !string.Equals(_loadedMusicFullPath, fullPath, StringComparison.OrdinalIgnoreCase);

            if (needResetSource)
            {
                try
                {
                    _musicPlayer.Close();
                    _musicPlayer.Open(new Uri(fullPath, UriKind.Absolute));
                    _loadedMusicFullPath = fullPath;
                }
                catch (Exception ex)
                {
                    ShowMusicError($"无法加载音频文件。\n\n曲目：{newPath}\n错误：{ex.Message}");
                    return;
                }
            }

            UpdateMusicMuteState();

            // ★ Open 是异步的：如果用户在打开过程中关闭了音乐，MediaOpened 不能再自动播放
            if (!_musicShouldPlay || !App.IsMusicEnabled) return;

            try { _musicPlayer.Play(); }
            catch (Exception ex) { ShowMusicError($"无法播放音频。\n\n曲目：{newPath}\n错误：{ex.Message}"); }
        }


        private void MusicPlayer_MediaOpened(object sender, EventArgs e)
        {
            try { _musicPlayer.Volume = App.MusicVolume; } catch { }

            // ★ 防止关闭音乐后，缓冲完成的 MediaOpened 又把声音播出来
            if (!_musicShouldPlay || !App.IsMusicEnabled) return;

            UpdateMusicMuteState();
            try { _musicPlayer.Play(); } catch { }
        }

        private void MusicPlayer_MediaFailed(object sender, ExceptionEventArgs e)
        {
            string reason = e?.ErrorException?.Message ?? "未知错误";
            ShowMusicError($"背景音乐播放失败。\n\n曲目：{_currentMusicPath}\n原因：{reason}\n\n请在 选项 → 常规 中重新选择音频文件。");
        }

        private void MusicPlayer_MediaEnded(object sender, EventArgs e)
        {
            if (!_musicShouldPlay || !App.IsMusicEnabled) return;
            if (string.IsNullOrEmpty(_loadedMusicFullPath)) return;

            try
            {
                _musicPlayer.Stop();
                _musicPlayer.Position = TimeSpan.Zero;
                _musicPlayer.Play();
                UpdateMusicMuteState();
            }
            catch { }
        }

        private void ShowMusicError(string message)
        {
            if (_musicErrorShown) return;
            _musicErrorShown = true;
            RuntimeLog.Error(message);

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CustomMessageBox.Show(this, message, "背景音乐错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    App.IsMusicEnabled = false;
                    try { _musicPlayer.Stop(); } catch { }
                    App.SaveSettings();
                    MessageBus.Publish(new AppMessages.NavigateTo("Options"));
                }), DispatcherPriority.Background);
            }
            catch { }
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            UpdateMusicMuteState();
            if (_pageCache.TryGetValue("Main", out var mp) && mp is MainPage_UC mainPage)
            {
                bool active = _isAppActive && this.WindowState != WindowState.Minimized;
                try { mainPage.SetActive(active); } catch { }
            }
        }
    }
}
