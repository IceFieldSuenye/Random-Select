using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RandomDrawer
{
    public partial class DrawAnimationPage_UC : UserControl, IPageAnimation, IHandleEscape
    {
        private const double StartScale = 6.0;
        private const double EndScale = 1.0;   // ★ 从 1.3 改为 1.0：完整显示，不超出容器
        private static readonly TimeSpan DrawDuration = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan PauseAfterDraw = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(450);

        private List<DrawResultData> _items = new List<DrawResultData>();
        private PersonalizationSettings _pers = new PersonalizationSettings();
        private HighlightConfig _highlight;
        private string _poolDir = "";
        private int _count = 1;
        private DrawResultDisplayMode _displayMode = DrawResultDisplayMode.CardGrid;
        private bool _isSingleDraw;

        private bool _skipAnimationRequested = false;
        private bool _interruptCurrentItem = false;
        private TaskCompletionSource<bool> _clickContinueTcs;
        private int _animationToken = 0;

        private readonly MediaPlayer _soundPlayer = new MediaPlayer();
        private bool _windowHooksAttached = false;

        // ★ GIF 立绘播放
        private DispatcherTimer _illustGifTimer;
        private GifBitmapDecoder _illustGifDecoder;
        private int _illustGifFrameIndex = 0;

        private Action<AppMessages.OpenDrawAnimation> _onOpen;

        public DrawAnimationPage_UC()
        {
            InitializeComponent();
            _onOpen = OnOpen;
            MessageBus.Subscribe(_onOpen);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e) { AttachWindowHooks(); }
        private void OnUnloaded(object sender, RoutedEventArgs e) { StopSound(); StopIllustGifPlayback(); DetachWindowHooks(); }

        private void AttachWindowHooks()
        {
            if (_windowHooksAttached) return;
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Activated += OnAppActivated;
                    Application.Current.Deactivated += OnAppDeactivated;
                }
                if (Application.Current?.MainWindow != null)
                    Application.Current.MainWindow.StateChanged += OnWindowStateChanged;
                _windowHooksAttached = true;
            }
            catch { }
        }

        private void DetachWindowHooks()
        {
            if (!_windowHooksAttached) return;
            try
            {
                if (Application.Current != null)
                {
                    Application.Current.Activated -= OnAppActivated;
                    Application.Current.Deactivated -= OnAppDeactivated;
                }
                if (Application.Current?.MainWindow != null)
                    Application.Current.MainWindow.StateChanged -= OnWindowStateChanged;
            }
            catch { }
            _windowHooksAttached = false;
        }

        private void OnAppActivated(object sender, EventArgs e) => UpdateSoundMuteState();
        private void OnAppDeactivated(object sender, EventArgs e) => UpdateSoundMuteState();
        private void OnWindowStateChanged(object sender, EventArgs e) => UpdateSoundMuteState();

        private void UpdateSoundMuteState()
        {
            try
            {
                var win = Application.Current?.MainWindow;
                bool minimized = win != null && win.WindowState == WindowState.Minimized;
                bool active = win == null || win.IsActive;
                _soundPlayer.IsMuted = minimized || !active;
            }
            catch { }
        }

        private void StopSound() { try { _soundPlayer.Stop(); } catch { } }

        public void PlayEnterAnimation(Action onComplete)
        {
            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = 1;
            onComplete?.Invoke();
        }
        public void PlayExitAnimation(Action onComplete) { StopSound(); StopIllustGifPlayback(); ReleaseVisuals(); onComplete?.Invoke(); }
        public void PlayEnterAsReverseAnimation(Action onComplete) { StopSound(); StopIllustGifPlayback(); ReleaseVisuals(); onComplete?.Invoke(); }

        private void ReleaseVisuals()
        {
            try { DrawAnimationImage.Source = null; } catch { }
            try { DrawAnimationSilhouetteMask.ImageSource = null; } catch { }
            _items = new List<DrawResultData>();
            _pers = null;
            _highlight = null;
        }

        public bool TryHandleEscape()
        {
            if (_clickContinueTcs != null && !_clickContinueTcs.Task.IsCompleted)
            {
                _clickContinueTcs.TrySetResult(true);
                return true;
            }
            if (DrawAnimationButtonBar.Visibility == Visibility.Visible)
            {
                BtnDrawAnimOk_Click(this, new RoutedEventArgs());
                return true;
            }
            if (!_isSingleDraw)
            {
                BtnSkipAnimation_Click(this, new RoutedEventArgs());
                return true;
            }
            return false;
        }

        private void OnOpen(AppMessages.OpenDrawAnimation msg)
        {
            if (msg == null) return;
            Dispatcher.Invoke(new Action(() => LoadFrom(msg)));
        }

        private void LoadFrom(AppMessages.OpenDrawAnimation msg)
        {
            StopSound();
            StopIllustGifPlayback();

            _items = msg.Items ?? new List<DrawResultData>();
            _pers = msg.Settings ?? new PersonalizationSettings();
            _highlight = msg.Highlight;
            _poolDir = msg.PoolDir ?? "";
            _count = msg.Count;
            _displayMode = msg.DisplayMode;
            _isSingleDraw = msg.IsSingleDraw;

            _skipAnimationRequested = false;
            _interruptCurrentItem = false;

            if (_clickContinueTcs != null && !_clickContinueTcs.Task.IsCompleted)
                _clickContinueTcs.TrySetResult(true);

            _animationToken++;
            int myToken = _animationToken;
            if (_isSingleDraw) _ = PlaySingleDrawAsync(myToken);
            else _ = PlayMultiDrawAsync(myToken);
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                if (Path.IsPathRooted(path)) return path;
                if (!string.IsNullOrEmpty(_poolDir))
                {
                    string combined = Path.Combine(_poolDir, path);
                    if (File.Exists(combined)) return combined;
                }
                return path;
            }
            catch { return path; }
        }

        /// <summary>未设置颜色时使用主题正文色（深色模式自动变亮）。</summary>
        private static Brush DefaultItemBrush =>
            ThemeManager.IsDark ? ThemeManager.GetBrush("Fg_333333") : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

        private Brush GetItemBrush(DrawResultData item)
        {
            if (item == null) return DefaultItemBrush;

            if (!string.IsNullOrEmpty(item.ColorHex))
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(item.ColorHex);
                    return new SolidColorBrush(c);
                }
                catch { }
            }

            if (_highlight != null && _highlight.Enabled)
            {
                var b = _highlight.GetBrushByCategory(item.Category);
                if (b != null) return b;
            }

            return DefaultItemBrush;
        }

        private void TryPlaySoundForItem(DrawResultData item)
        {
            try
            {
                StopSound();
                string soundPath = null;
                if (_pers != null)
                {
                    if (_pers.SoundMode == SoundMode.Global) soundPath = _pers.GlobalSoundFile;
                    else if (_pers.SoundMode == SoundMode.PerItem && item != null && _pers.Items != null)
                    {
                        var pi = _pers.Items.FirstOrDefault(x => string.Equals(x.ObjectName, item.ObjectName, StringComparison.Ordinal));
                        soundPath = pi?.SoundFile;
                    }
                }
                soundPath = ResolvePath(soundPath);
                if (string.IsNullOrEmpty(soundPath)) return;
                if (!File.Exists(soundPath)) return;

                _soundPlayer.Open(new Uri(soundPath, UriKind.Absolute));
                if (App.IsSoundEffectMuted) _soundPlayer.Volume = 0.0;
                else
                {
                    double v = App.SoundEffectVolume;
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    _soundPlayer.Volume = v;
                }
                UpdateSoundMuteState();
                _soundPlayer.Play();
            }
            catch { }
        }

        private async Task PlaySingleDrawAsync(int token)
        {
            ShowOverlay();
            await Task.Delay(280);
            if (_animationToken != token) return;
            if (_items.Count > 0) await PlayDrawAnimationForItemAsync(_items[0], token);
            if (_animationToken != token) return;
            PlaySingleResultEndAnimations();
        }

        private async Task PlayMultiDrawAsync(int token)
        {
            ShowOverlay();
            await Task.Delay(280);
            if (_animationToken != token) return;

            for (int i = 0; i < _items.Count; i++)
            {
                if (_animationToken != token) return;
                if (_skipAnimationRequested) break;
                _interruptCurrentItem = false;

                await PlayDrawAnimationForItemAsync(_items[i], token);
                if (_animationToken != token) return;
                if (_skipAnimationRequested) break;

                bool wasInterrupted = _interruptCurrentItem;
                _interruptCurrentItem = false;

                // ★ FIX（问题2）：最后一个动画也必须等待用户点击。
                //
                // 原逻辑：
                //     if (i < _items.Count - 1 && !wasInterrupted)
                //         await WaitForClickContinueAsync();   ← 只在"非最后一张"时进入
                //
                // 导致：最后一张播完后直接 break 出循环，用户还没看清结果就跳到结果页。
                //
                // 新逻辑：无论是否最后一张，只要没有被"跳过"或"点击中断"，
                // 就停下来等用户点击，然后进入结果页。
                // 提示文案统一为"点击继续"（已按用户要求恢复原样）。
                if (!wasInterrupted)
                {
                    bool isLast = (i == _items.Count - 1);
                    await WaitForClickContinueAsync(isLast);
                    if (_animationToken != token) return;
                    if (_skipAnimationRequested) break;

                    // 只有"非最后一张"才需要进度间隔的轻微停顿，
                    // 最后一张点击后立即跳结果页，避免多余的 120ms 空等。
                    if (!isLast)
                    {
                        await Task.Delay(120);
                        if (_animationToken != token) return;
                    }
                }
            }

            _skipAnimationRequested = false;
            _interruptCurrentItem = false;
            if (_animationToken != token) return;

            StopSound();
            StopIllustGifPlayback();
            MessageBus.Publish(new AppMessages.OpenDrawResultPage(
                _items, _poolDir, _count, _pers, _highlight, _displayMode));
            MessageBus.Publish(new AppMessages.NavigateTo("DrawResultPage"));
        }

        private void ShowOverlay()
        {
            ApplyBackground(_pers);

            // 布局完成后再应用一次，保证毛玻璃/纯色按钮样式正确
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { ApplyBackground(_pers); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
            AnimationRoot.BeginAnimation(UIElement.OpacityProperty, null);
            AnimationRoot.Opacity = 1;

            BtnSkipAnimation.Visibility = _isSingleDraw ? Visibility.Collapsed : Visibility.Visible;
            DrawAnimationButtonBar.Visibility = Visibility.Collapsed;

            DrawAnimationResultHint.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationResultHint.Visibility = Visibility.Collapsed;
            DrawAnimationResultHint.Opacity = 0;
            DrawAnimationResultHintTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DrawAnimationResultHintTranslate.Y = -40;

            DrawAnimationButtonBar.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationButtonBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DrawAnimationButtonBar.Opacity = 1;
            DrawAnimationButtonBarTranslate.Y = 0;

            DrawAnimationClickHint.Visibility = Visibility.Collapsed;
            DrawAnimationSilhouette.Visibility = Visibility.Collapsed;
            DrawAnimationImage.Source = null;
            DrawAnimationImage.Opacity = 0;
            DrawAnimationImage.Visibility = Visibility.Visible;
            DrawAnimationImageTranslate.X = 0;
            DrawAnimationImageScale.ScaleX = 1;
            DrawAnimationImageScale.ScaleY = 1;
            DrawAnimationFallbackIcon.Visibility = Visibility.Collapsed;
            DrawAnimationTextLayer.Visibility = Visibility.Collapsed;
            DrawAnimationTextPanel.Opacity = 0;

            StopIllustGifPlayback();
        }

        private ImageSource _glassBackdrop;

        private void ApplyBackground(PersonalizationSettings pers)
        {
            var mode = pers?.EffectiveBackgroundMode ?? BackgroundMode.Default;
            bool hasImage = false;
            Color solid = Color.FromRgb(0xF0, 0xF0, 0xF0);

            if (mode == BackgroundMode.Image && !string.IsNullOrEmpty(pers?.BackgroundImage))
            {
                string bgPath = ResolvePath(pers.BackgroundImage);
                if (File.Exists(bgPath))
                {
                    var bmp = LoadBitmapSafe(bgPath);
                    if (bmp != null)
                    {
                        DrawAnimationBackground.Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                        hasImage = true;
                    }
                }
            }

            if (!hasImage)
            {
                if (mode == BackgroundMode.Color)
                {
                    try { solid = (Color)ColorConverter.ConvertFromString(pers.BackgroundColor); } catch { }
                }
                else if (ThemeManager.GetBrush("Bg_F0F0F0") is SolidColorBrush themeBrush)
                {
                    solid = themeBrush.Color;
                }

                DrawAnimationBackground.Fill = new SolidColorBrush(solid);
            }

            ApplyBarButtonStyles(mode, hasImage, solid, pers);
        }

        /// <summary>底部按钮跟随主题 / 纯色 / 图片背景变化。</summary>
        private void ApplyBarButtonStyles(BackgroundMode mode, bool hasImage, Color solid, PersonalizationSettings pers)
        {
            var buttons = new[] { BtnDrawAnimContinue, BtnDrawAnimOk };

            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                btn.ClearValue(Control.BackgroundProperty);
                btn.ClearValue(Control.BorderBrushProperty);
                btn.ClearValue(Control.ForegroundProperty);
            }

            if (mode == BackgroundMode.Default) return;

            if (hasImage)
            {
                string imgPath = pers != null ? ResolvePath(pers.BackgroundImage) : "";
                _glassBackdrop = GlassButtonStyler.BuildBackdrop(this, imgPath);

                var glassBorder = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                var fallback = new SolidColorBrush(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF));

                foreach (var btn in buttons)
                {
                    if (btn == null) continue;
                    if (!GlassButtonStyler.Apply(this, btn, _glassBackdrop)) btn.Background = fallback;
                    btn.BorderBrush = glassBorder;
                    btn.Foreground = Brushes.White;
                }
                return;
            }

            bool light = GlassButtonStyler.IsLight(solid);
            Color fill = GlassButtonStyler.Scale(solid, light ? 1.22 : 1.60);
            Color border = GlassButtonStyler.Scale(solid, light ? 0.60 : 0.80);
            Brush text = GlassButtonStyler.IsLight(fill)
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
                : Brushes.White;

            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                btn.Background = new SolidColorBrush(fill);
                btn.BorderBrush = new SolidColorBrush(border);
                btn.Foreground = text;
            }
        }
        private void PrepareTextContent(DrawResultData item)
        {
            Brush fg = GetItemBrush(item);
            DrawAnimationBigName.Text = item.ObjectName ?? "";
            DrawAnimationBigName.Foreground = fg;
            DrawAnimationOtherFields.Items.Clear();

            for (int i = 0; i < item.OtherFields.Count && i < item.OtherHeaders.Count; i++)
            {
                string header = item.OtherHeaders[i] ?? "";
                string value = item.OtherFields[i] ?? "";
                if (string.IsNullOrWhiteSpace(header) && string.IsNullOrWhiteSpace(value)) continue;

                var tb = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(header) ? value : $"{header}：{value}",
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    FontSize = 22,
                    FontWeight = FontWeights.Normal,
                    Foreground = fg,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 640,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                DrawAnimationOtherFields.Items.Add(tb);
            }

            DrawAnimationTextLayer.Visibility = Visibility.Collapsed;
            DrawAnimationTextPanel.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationTextPanel.Opacity = 0;
            DrawAnimationTextTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            DrawAnimationTextTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DrawAnimationTextTranslate.X = Math.Max(40, this.ActualWidth * 0.25);
            DrawAnimationTextTranslate.Y = Math.Max(40, this.ActualHeight * 0.28);
        }

        private void ShowText()
        {
            DrawAnimationTextLayer.Visibility = Visibility.Visible;
            DrawAnimationTextPanel.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationTextTranslate.BeginAnimation(TranslateTransform.XProperty, null);

            double startX = Math.Max(40, this.ActualWidth * 0.25);
            double endX = Math.Max(40, this.ActualWidth * 0.15);
            DrawAnimationTextTranslate.X = startX;
            DrawAnimationTextPanel.Opacity = 0;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
            DrawAnimationTextPanel.BeginAnimation(UIElement.OpacityProperty, fade);

            var slide = new DoubleAnimation(startX, endX, TimeSpan.FromMilliseconds(450)) { EasingFunction = easeInOut, FillBehavior = FillBehavior.HoldEnd };
            DrawAnimationTextTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        // ============================================================
        // ★ GIF 感知的立绘加载
        // ============================================================
        private void ApplyIllustrationSource(string path)
        {
            StopIllustGifPlayback();
            DrawAnimationImage.Source = null;
            DrawAnimationSilhouetteMask.ImageSource = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gif")
            {
                try
                {
                    _illustGifDecoder = new GifBitmapDecoder(
                        new Uri(path, UriKind.Absolute),
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (_illustGifDecoder.Frames.Count > 0)
                    {
                        _illustGifFrameIndex = 0;
                        var first = _illustGifDecoder.Frames[0];
                        DrawAnimationImage.Source = first;
                        DrawAnimationSilhouetteMask.ImageSource = first;

                        if (_illustGifDecoder.Frames.Count > 1)
                        {
                            _illustGifTimer = new DispatcherTimer { Interval = GetGifFrameDelay(first) };
                            _illustGifTimer.Tick += IllustGifTimer_Tick;
                            _illustGifTimer.Start();
                        }
                        return;
                    }
                }
                catch { }
            }

            var bmp = LoadBitmapSafe(path);
            DrawAnimationImage.Source = bmp;
            DrawAnimationSilhouetteMask.ImageSource = bmp;
        }

        private void IllustGifTimer_Tick(object sender, EventArgs e)
        {
            if (_illustGifDecoder == null || _illustGifDecoder.Frames.Count == 0) return;

            _illustGifFrameIndex = (_illustGifFrameIndex + 1) % _illustGifDecoder.Frames.Count;
            var frame = _illustGifDecoder.Frames[_illustGifFrameIndex];

            try
            {
                DrawAnimationImage.Source = frame;
                DrawAnimationSilhouetteMask.ImageSource = frame;
            }
            catch { }

            if (_illustGifTimer != null)
                _illustGifTimer.Interval = GetGifFrameDelay(frame);
        }

        private void StopIllustGifPlayback()
        {
            if (_illustGifTimer != null)
            {
                _illustGifTimer.Stop();
                _illustGifTimer.Tick -= IllustGifTimer_Tick;
                _illustGifTimer = null;
            }
            _illustGifDecoder = null;
            _illustGifFrameIndex = 0;
        }

        private static TimeSpan GetGifFrameDelay(BitmapFrame frame)
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

        private async Task PlayDrawAnimationForItemAsync(DrawResultData item, int token)
        {
            DrawAnimationSilhouetteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            DrawAnimationSilhouetteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            DrawAnimationImageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            DrawAnimationSilhouette.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationImage.BeginAnimation(UIElement.OpacityProperty, null);

            // ★ 默认 Scale = 1.0（EndScale）
            DrawAnimationSilhouetteScale.ScaleX = EndScale;
            DrawAnimationSilhouetteScale.ScaleY = EndScale;
            DrawAnimationImageTranslate.X = 0;
            DrawAnimationSilhouette.Opacity = 1;
            DrawAnimationSilhouette.Visibility = Visibility.Collapsed;
            DrawAnimationImage.Source = null;
            DrawAnimationImage.Opacity = 0;
            DrawAnimationImage.Visibility = Visibility.Visible;
            DrawAnimationFallbackIcon.Visibility = Visibility.Collapsed;

            PrepareTextContent(item);
            TryPlaySoundForItem(item);

            string illustrationPath = ResolvePath(item.IllustrationPath ?? "");
            bool hasIllustration = !string.IsNullOrEmpty(illustrationPath) && File.Exists(illustrationPath);

            int animMode = _pers != null ? (int)_pers.AnimationMode : 2;

            if (!hasIllustration)
            {
                StopIllustGifPlayback();
                DrawAnimationImage.Visibility = Visibility.Collapsed;
                DrawAnimationFallbackIcon.Visibility = Visibility.Visible;
                ShowText();
                await Task.Delay(700);
                return;
            }

            DrawAnimationFallbackIcon.Visibility = Visibility.Collapsed;
            ApplyIllustrationSource(illustrationPath);
            DrawAnimationImage.Visibility = Visibility.Visible;

            if (animMode == 0)
            {
                DrawAnimationImage.Opacity = 1;
                ShowText();
                await Task.Delay(600);
                return;
            }

            if (animMode == 2)
            {
                DrawAnimationSilhouette.Visibility = Visibility.Visible;
                DrawAnimationSilhouetteScale.ScaleX = StartScale;
                DrawAnimationSilhouetteScale.ScaleY = StartScale;

                var tcs = new TaskCompletionSource<bool>();
                var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

                var sx = new DoubleAnimation(StartScale, EndScale, DrawDuration) { EasingFunction = ease };
                var sy = new DoubleAnimation(StartScale, EndScale, DrawDuration) { EasingFunction = ease };
                sx.Completed += (s, e) => tcs.TrySetResult(true);

                DrawAnimationSilhouetteScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
                DrawAnimationSilhouetteScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);

                await tcs.Task;
                if (_animationToken != token) return;
                if (_skipAnimationRequested || _interruptCurrentItem) return;

                await Task.Delay(PauseAfterDraw);
                if (_animationToken != token) return;
                if (_skipAnimationRequested || _interruptCurrentItem) return;

                ShowText();

                double slideDist = this.ActualWidth * 0.05;
                var morphEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

                var slide = new DoubleAnimation(0, slideDist, MorphDuration) { EasingFunction = morphEase };
                DrawAnimationImageTranslate.BeginAnimation(TranslateTransform.XProperty, slide);

                var fadeOut = new DoubleAnimation(1, 0, MorphDuration) { EasingFunction = morphEase };
                DrawAnimationSilhouette.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                var fadeIn = new DoubleAnimation(0, 1, MorphDuration) { EasingFunction = morphEase };
                DrawAnimationImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                await Task.Delay(520);
            }
            else
            {
                double startX = -this.ActualWidth * 0.35;
                DrawAnimationImageTranslate.X = startX;
                DrawAnimationImage.Opacity = 0;
                ShowText();

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var slide = new DoubleAnimation(startX, 0, MorphDuration) { EasingFunction = ease };
                DrawAnimationImageTranslate.BeginAnimation(TranslateTransform.XProperty, slide);

                var fadeIn = new DoubleAnimation(0, 1, MorphDuration) { EasingFunction = ease };
                DrawAnimationImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                await Task.Delay(520);
            }
        }

        /// <summary>
        /// 等待用户点击继续。
        /// ★ 提示文案统一为"点击继续"（已按用户要求恢复原样）。
        ///   isLast 参数保留（后续如需按"最后一张"做其它差异化处理时可复用，当前不参与提示文案）。
        /// </summary>
        private async Task WaitForClickContinueAsync(bool isLast)
        {
            _clickContinueTcs = new TaskCompletionSource<bool>();

            // 提示语：统一为"点击继续"
            DrawAnimationClickHint.Text = "点击继续";

            DrawAnimationClickHint.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationClickHint.Visibility = Visibility.Visible;
            DrawAnimationClickHint.Opacity = 0;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            DrawAnimationClickHint.BeginAnimation(UIElement.OpacityProperty, fade);

            await _clickContinueTcs.Task;
            StopSound();
            DrawAnimationClickHint.Visibility = Visibility.Collapsed;
            _clickContinueTcs = null;
        }

        private void AnimationRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_clickContinueTcs != null && !_clickContinueTcs.Task.IsCompleted)
            {
                _clickContinueTcs.TrySetResult(true);
                e.Handled = true;
                return;
            }

            if (!_isSingleDraw)
            {
                _interruptCurrentItem = true;

            // 用户点击打断后，右上角「跳过」保持常显，便于继续跳过
            if (!_isSingleDraw && BtnSkipAnimation != null)
                BtnSkipAnimation.Visibility = Visibility.Visible;
                StopSound();
                e.Handled = true;
            }
        }

        private void PlaySingleResultEndAnimations()
        {
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            DrawAnimationResultHint.Visibility = Visibility.Visible;
            DrawAnimationResultHint.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationResultHintTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DrawAnimationResultHint.Opacity = 0;
            DrawAnimationResultHintTranslate.Y = -40;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
            DrawAnimationResultHint.BeginAnimation(UIElement.OpacityProperty, fade);

            var slide = new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
            DrawAnimationResultHintTranslate.BeginAnimation(TranslateTransform.YProperty, slide);

            DrawAnimationButtonBar.Visibility = Visibility.Visible;
            DrawAnimationButtonBar.BeginAnimation(UIElement.OpacityProperty, null);
            DrawAnimationButtonBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DrawAnimationButtonBar.Opacity = 0;
            DrawAnimationButtonBarTranslate.Y = 40;

            var barFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
            DrawAnimationButtonBar.BeginAnimation(UIElement.OpacityProperty, barFade);

            var barSlide = new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
            DrawAnimationButtonBarTranslate.BeginAnimation(TranslateTransform.YProperty, barSlide);
        }

        private void BtnDrawAnimContinue_Click(object sender, RoutedEventArgs e)
        {
            StopSound();
            if (!_isSingleDraw) { BtnDrawAnimOk_Click(sender, e); return; }
            MessageBus.Publish(new AppMessages.DrawResultPageContinueDraw(1));
        }

        private void BtnDrawAnimOk_Click(object sender, RoutedEventArgs e)
        {
            StopSound();
            if (_isSingleDraw)
            {
                MessageBus.Publish(new AppMessages.NavigateBack());
                return;
            }
            MessageBus.Publish(new AppMessages.OpenDrawResultPage(
                _items, _poolDir, _count, _pers, _highlight, _displayMode));
            MessageBus.Publish(new AppMessages.NavigateTo("DrawResultPage"));
        }

        private void BtnSkipAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (_isSingleDraw) return;
            _skipAnimationRequested = true;
            StopSound();
            if (_clickContinueTcs != null && !_clickContinueTcs.Task.IsCompleted)
                _clickContinueTcs.TrySetResult(true);
            DrawAnimationClickHint.Visibility = Visibility.Collapsed;
        }

        private static BitmapImage LoadBitmapSafe(string path)
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
                return bmp;
            }
            catch { }

            return null;
        }
    }
}
