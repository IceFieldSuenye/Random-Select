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
        private bool _pageActive = true;

        private DispatcherTimer _gifTimer;
        private GifBitmapDecoder _gifDecoder;
        private int _gifFrameIndex;

        private Storyboard _syncStoryboard;
        private Storyboard _videoLoadingStoryboard;
        private DispatcherTimer _videoLoadTimeoutTimer;
        private static readonly TimeSpan VideoLoadTimeout = TimeSpan.FromSeconds(8);

        private bool _firstVideoLoaded = false;
        private MediaElement _activeVideo;
        private MediaElement _bufferVideo;
        private MediaPlayer _videoPreloader;


        private string _lastLoadedCoverPath = "";

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
        private const int CoverDecodePixelWidth = 1600;

        private Action<AppMessages.PageActivated> _onPageActivated;
        private Action<AppMessages.PoolListChanged> _onPoolListChanged;
        private Action<AppMessages.PersonalizationFinished> _onPersonalizationFinished;
        private Action<AppMessages.UiOptionsChanged> _onUiOptionsChanged;

        private bool _subscribed = false;

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

            Loaded += MainPage_UC_Loaded;
            Unloaded += MainPage_UC_Unloaded;
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

            var targets = new List<FrameworkElement>();
            if (BtnDraw1 != null) targets.Add(BtnDraw1);
            if (BtnDrawMulti != null) targets.Add(BtnDrawMulti);
            if (ChkDrawAnimation != null && _shouldShowAnimationCheckbox) targets.Add(ChkDrawAnimation);
            if (BtnViewReport != null && _showReportButton) targets.Add(BtnViewReport);
            if (PanelStatsNormal != null && !_statsAdvancedActive) targets.Add(PanelStatsNormal);
            if (PanelStatsAdvanced != null && _statsAdvancedActive) targets.Add(PanelStatsAdvanced);

            foreach (var el in targets)
            {
                StopDrawButtonAnimation(el);
                if (el.RenderTransform is TranslateTransform t) t.Y = 0;
                el.Opacity = 1;
                el.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }
        }
        private void AnimateDrawButtons(bool visible)
        {
            var targets = new List<FrameworkElement>();
            if (BtnDraw1 != null) targets.Add(BtnDraw1);
            if (BtnDrawMulti != null) targets.Add(BtnDrawMulti);
            if (ChkDrawAnimation != null && _shouldShowAnimationCheckbox) targets.Add(ChkDrawAnimation);
            if (BtnViewReport != null && _showReportButton) targets.Add(BtnViewReport);
            if (PanelStatsNormal != null && !_statsAdvancedActive) targets.Add(PanelStatsNormal);
            if (PanelStatsAdvanced != null && _statsAdvancedActive) targets.Add(PanelStatsAdvanced);

            if (targets.Count == 0) return;

            // 复选框在显示时先恢复可见，避免取消抽取后残留 Hidden 状态。
            if (visible && ChkDrawAnimation != null && _shouldShowAnimationCheckbox)
                ChkDrawAnimation.Visibility = Visibility.Visible;

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
                        el.Visibility = Visibility.Collapsed;
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
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            MessageBus.Unsubscribe(_onPageActivated);
            MessageBus.Unsubscribe(_onPoolListChanged);
            MessageBus.Unsubscribe(_onPersonalizationFinished);
            MessageBus.Unsubscribe(_onUiOptionsChanged);
        }

        private async void MainPage_UC_Loaded(object sender, RoutedEventArgs e)
        {
            Subscribe();

            StartPoolWatcher();

            var hostWindow = Window.GetWindow(this);
            if (hostWindow != null && _reportRowHooked == false)
            {
                _reportRowHooked = true;
            }

            if (_initialized) return;
            _initialized = true;

            App.LoadSettings();

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

            StopPoolWatcher();
            try { StopGifPlayback(); } catch { }
            try { ShowVideoLoading(false); } catch { }
            try { StopAllVideos(); } catch { }
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

        public void SetActive(bool active)
        {
            _pageActive = active;
            if (active)
            {
                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                {
                    var pool = _allPools[_currentPoolIndex];
                    if (!string.IsNullOrEmpty(pool.CoverPath) && File.Exists(pool.CoverPath))
                    {
                        string ext = Path.GetExtension(pool.CoverPath).ToLowerInvariant();
                        if (ext == ".gif") { try { StartGifPlayback(pool.CoverPath); } catch { } }
                    }
                }
                try
                {
                    if (_activeVideo?.Source != null && _activeVideo.Visibility == Visibility.Visible)
                        _activeVideo.Play();
                }
                catch { }
            }
            else { StopGifPlayback(); try { _activeVideo?.Pause(); } catch { } }
        }

        public bool TryHandleEscape() { return false; }

        public void PlayEnterAnimation(Action onComplete)
        {
            _isAnimating = false;
            _uiHidden = true;

            // 进入页面时先按即将展示的卡池刷新「抽卡动画」复选框，
            // 避免从上一页（其它卡池）残留的显示状态闪一下。
            RefreshAnimationCheckboxForSelectedPool();

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
                if (_currentPoolIndex >= 0 && _currentPoolIndex < _allPools.Count)
                {
                    var pool = _allPools[_currentPoolIndex];
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (DisplayImage.Visibility == Visibility.Visible
                                || (_activeVideo != null && _activeVideo.Visibility == Visibility.Visible))
                            {
                                if (!string.IsNullOrEmpty(pool.CoverPath) && File.Exists(pool.CoverPath))
                                {
                                    string ext = Path.GetExtension(pool.CoverPath).ToLowerInvariant();
                                    if (ext == ".gif") StartGifPlayback(pool.CoverPath);
                                }
                                try
                                {
                                    if (_activeVideo?.Source != null && _activeVideo.Visibility == Visibility.Visible && _pageActive)
                                        _activeVideo.Play();
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }), DispatcherPriority.Background);
                }
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
                var durLeft = TimeSpan.FromMilliseconds(300);
                var durCenter = TimeSpan.FromMilliseconds(280);
                var durBottom = TimeSpan.FromMilliseconds(320);
                var delayCenter = TimeSpan.FromMilliseconds(70);
                var delayBottom = TimeSpan.FromMilliseconds(140);

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
                    UpdateDrawCountDisplay();
                }
            }), DispatcherPriority.Background);
        }

        private void OnUiOptionsChanged(AppMessages.UiOptionsChanged msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateDrawCountDisplay();
                UpdateReportButton();
                RefreshPoolContextMenus();
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

                var covers = PoolCover.GetCoverFiles(dir);
                if (covers.Length > 0) pool.CoverPath = covers[0];

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
                    Style = (Style)FindResource("PoolButtonStyle"),
                    Tag = i
                };
                btn.Click += BtnPool_Click;
                AttachTouchContextMenu(btn);
                btn.ContextMenu = BuildPoolContextMenu(i);
                PoolButtonsPanel.Children.Add(btn);
            }
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

        private void DrawDetail_Click(object sender, MouseButtonEventArgs e)
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
            menu.Items.Add(edit);
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
                    case Key.E: action = () => EditPoolAt(index); break;
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

        private void PickCoverAt(int index)
        {
            if (index < 0 || index >= _allPools.Count) return;

            var pool = _allPools[index];
            var window = Window.GetWindow(this);

            // 换封面不属于"修改名单"，按产品要求不校验密码
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择封面文件",
                Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|所有文件|*.*",
                FilterIndex = 1
            };

            if (ofd.ShowDialog() != true) return;

            // 先预览（图片 / GIF / 视频），确认后再写入
            var preview = new CoverPreviewDialog(ofd.FileName) { Owner = window };
            if (preview.ShowDialog() != true) return;

            try
            {
                if (!PoolCover.SaveCover(pool.Dir, ofd.FileName))
                {
                    CustomMessageBox.Show(window, "封面保存失败。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                pool.CoverPath = PoolCover.GetCoverPath(pool.Dir) ?? "";

                if (index == _currentPoolIndex)
                {
                    try { LoadCoverForPool(pool); } catch { }
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(window, "调整封面失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

            App.LastSelectedPoolName = pool.UniqueName;
            App.LastSelectedPoolDir = pool.Dir;
            App.SaveSettings();

            MessageBus.Publish(new AppMessages.CurrentPoolChanged(pool.Dir));

            Dispatcher.BeginInvoke(new Action(() => { try { LoadCoverForPool(pool); } catch { } }), DispatcherPriority.Background);
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

                if (ChkDrawAnimation != null)
                    ChkDrawAnimation.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
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
                if (ChkDrawAnimation.RenderTransform is TranslateTransform t)
                    t.BeginAnimation(TranslateTransform.YProperty, null);
            }
            catch { }
            ChkDrawAnimation.Opacity = 1;
            if (ChkDrawAnimation.RenderTransform is TranslateTransform tt) tt.Y = 0;

            bool shouldShow = _shouldShowAnimationCheckbox && _drawButtonsVisible;
            ChkDrawAnimation.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideDefaultCoverName()
        {
            if (TxtDefaultCoverName != null) TxtDefaultCoverName.Visibility = Visibility.Collapsed;
        }

        private static BitmapImage LoadEmbeddedImage(string resourcePath)
        {
            return EmbeddedImageLoader.Load(resourcePath);
        }

        private void LoadCoverForPool(PoolInfo pool)
        {
            if (pool == null) return;
            string coverPath = pool.CoverPath ?? "";

            if (!string.IsNullOrEmpty(coverPath)
                && string.Equals(coverPath, _lastLoadedCoverPath, StringComparison.OrdinalIgnoreCase))
            {
                if (DisplayImage.Visibility == Visibility.Visible && DisplayImage.Source != null)
                    return;
                if (_activeVideo != null && _activeVideo.Visibility == Visibility.Visible && _activeVideo.Source != null)
                    return;
                if (_bufferVideo != null && _bufferVideo.Source is Uri bufUri
                    && string.Equals(bufUri.LocalPath, coverPath, StringComparison.OrdinalIgnoreCase))
                    return;
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

                if (alreadyPlaying) return;
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
                    catch { ShowVideoLoading(false); ShowDefaultCover(pool.Name); }
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
                    catch { ShowDefaultCover(pool.Name); }
                }
                return;
            }

            CancelPendingVideoPreload();
            ShowVideoLoading(false);
            StopGifPlayback();

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
                return;
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
                    }
                    else
                    {
                        ShowDefaultCover(pool.Name);
                    }
                }
                catch { ShowDefaultCover(pool.Name); }
                return;
            }
            ShowDefaultCover(pool.Name);
        }

        private void ShowDefaultCover(string poolName)
        {
            var defaultBitmap = LoadEmbeddedImage("Assets/default_cover.jpg");
            if (defaultBitmap != null)
            { DisplayImage.Source = defaultBitmap; DisplayImage.Visibility = Visibility.Visible; }
            else { DisplayImage.Source = null; DisplayImage.Visibility = Visibility.Collapsed; }

            if (TxtDefaultCoverName != null)
            {
                if (!string.IsNullOrEmpty(poolName))
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
                        _videoLoadTimeoutTimer.Tick += (s, e) => { _videoLoadTimeoutTimer.Stop(); ShowVideoLoading(false); };
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
                DisplayImage.Visibility = Visibility.Collapsed;
                DisplayImage.Source = null;
                HideDefaultCoverName();
            }
            catch { }
            try { if (_pageActive && me.Visibility == Visibility.Visible) me.Play(); } catch { }
        }

        private void SwapVideoLayers()
        {
            try
            {
                _bufferVideo.Visibility = Visibility.Visible;
                _bufferVideo.Opacity = 1;
                if (_pageActive) _bufferVideo.Play();

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
                if (me.Visibility == Visibility.Visible && _pageActive) me.Play();
            }
            catch { }
        }

        private void StartGifPlayback(string path)
        {
            StopGifPlayback();
            if (!_pageActive) return;
            try
            {
                _gifDecoder = ImageHelper.LoadGifFromFile(path);
                if (_gifDecoder == null || _gifDecoder.Frames.Count == 0) return;
                _gifFrameIndex = 0;
                DisplayImage.Source = _gifDecoder.Frames[0];
                if (_gifDecoder.Frames.Count > 1)
                {
                    _gifTimer = new DispatcherTimer { Interval = GetFrameDelay(_gifDecoder.Frames[0]) };
                    _gifTimer.Tick += GifTimer_Tick;
                    _gifTimer.Start();
                }
            }
            catch
            {
                var bitmap = ImageHelper.LoadBitmapFromFile(path);
                DisplayImage.Source = bitmap;
            }
        }

        private void StopGifPlayback()
        {
            if (_gifTimer != null) { _gifTimer.Stop(); _gifTimer.Tick -= GifTimer_Tick; _gifTimer = null; }
            _gifDecoder = null; _gifFrameIndex = 0;
        }

        private void GifTimer_Tick(object sender, EventArgs e)
        {
            if (_gifDecoder == null || _gifDecoder.Frames.Count == 0) return;
            _gifFrameIndex = (_gifFrameIndex + 1) % _gifDecoder.Frames.Count;
            DisplayImage.Source = _gifDecoder.Frames[_gifFrameIndex];
            if (_gifTimer != null) _gifTimer.Interval = GetFrameDelay(_gifDecoder.Frames[_gifFrameIndex]);
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
        }

        private static bool CategoryEquals(string a, string b) => (a ?? "").Trim() == (b ?? "").Trim();

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

        /// <summary>按当前卡池恢复「不重复抽取 / 抽卡动画」的勾选状态。</summary>
        private void ApplyPoolUiState()
        {
            try
            {
                string dir = CurrentPoolDir();

                bool noRepeat = PoolUiState.GetNoRepeat(dir, true);
                if (ChkNoRepeat != null)
                {
                    ChkNoRepeat.IsChecked = noRepeat;
                    if (TxtResetHyperlink != null)
                        TxtResetHyperlink.Visibility = noRepeat ? Visibility.Visible : Visibility.Collapsed;

                    if (TxtDrawDetailLink != null)
                        TxtDrawDetailLink.Visibility = noRepeat ? Visibility.Visible : Visibility.Collapsed;
                }

                if (ChkDrawAnimation != null)
                    ChkDrawAnimation.IsChecked = PoolUiState.GetDrawAnimation(dir, true);

                UpdateReportButton();
            }
            catch { }
        }

        private void ChkNoRepeat_Click(object sender, RoutedEventArgs e)
        {
            App.IsNoRepeat = ChkNoRepeat.IsChecked == true;
            PoolUiState.SetNoRepeat(CurrentPoolDir(), App.IsNoRepeat);
            App.SaveSettings();
            UpdateDrawCountDisplay();
            TxtResetHyperlink.Visibility = ChkNoRepeat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (TxtDrawDetailLink != null)
                TxtDrawDetailLink.Visibility = ChkNoRepeat.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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

                bool visible = _showReportButton && _drawButtonsVisible;
                BtnViewReport.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            }
            catch { }
        }

        private void BtnViewReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
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

        private void ResetText_Click(object sender, MouseButtonEventArgs e)
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
