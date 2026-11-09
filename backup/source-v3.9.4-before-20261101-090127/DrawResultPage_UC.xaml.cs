using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace RandomDrawer
{
    public partial class DrawResultPage_UC : UserControl, IPageAnimation, IHandleEscape, IDrawButtonsHost
    {
        private List<DrawResultData> _items = new List<DrawResultData>();
        private string _poolDir = "";
        private int _lastDrawCount = 1;
        private DrawResultDisplayMode _displayMode = DrawResultDisplayMode.CardGrid;
        private HighlightConfig _highlight;
        private PersonalizationSettings _settings = new PersonalizationSettings();

        private readonly MediaPlayer _soundPlayer = new MediaPlayer();

        private Action<AppMessages.OpenDrawResultPage> _onOpen;

        private bool _drawButtonsVisible = true;

        private static readonly TimeSpan ShowYDuration = TimeSpan.FromMilliseconds(280);
        private static readonly TimeSpan ShowOpacityDuration = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(200);
        private const double HiddenY = 40;

        public DrawResultPage_UC()
        {
            InitializeComponent();
            _onOpen = OnOpen;
            MessageBus.Subscribe(_onOpen);

            // 窗口尺寸变化时重新生成毛玻璃（按钮位置会变）
            Loaded += (s, e) =>
            {
                try
                {
                    ResultRoot.SizeChanged += ResultRoot_SizeChanged;
                    ResultRoot.PreviewMouseWheel += ResultRoot_PreviewMouseWheel;
                }
                catch { }
            };

            if (PageScrollBar != null) PageScrollBar.ValueChanged += PageScrollBar_ValueChanged;

            // 跟随主题切换刷新按钮样式
            ThemeManager.ThemeChanged += (s, e) =>
            {
                try { if (IsVisible) ApplyBackground(); } catch { }
            };
        }

        public void SetDrawButtonsVisible(bool visible)
        {
            if (_drawButtonsVisible == visible) return;
            _drawButtonsVisible = visible;

            if (ButtonBar == null) return;

            var t = ButtonBar.RenderTransform as TranslateTransform;
            if (t == null)
            {
                t = new TranslateTransform();
                ButtonBar.RenderTransform = t;
            }

            ButtonBar.BeginAnimation(UIElement.OpacityProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);

            if (App.IsAnimationDisabled)
            {
                t.Y = 0;
                ButtonBar.Opacity = 1;
                ButtonBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            if (visible)
            {
                ButtonBar.Visibility = Visibility.Visible;

                ButtonBar.Opacity = 0;
                t.Y = HiddenY;

                var yAnim = new DoubleAnimation(HiddenY, 0, ShowYDuration)
                { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };
                var opAnim = new DoubleAnimation(0, 1, ShowOpacityDuration)
                { EasingFunction = easeOut, FillBehavior = FillBehavior.HoldEnd };

                t.BeginAnimation(TranslateTransform.YProperty, yAnim);
                ButtonBar.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
            else
            {
                double startY = t.Y;
                double startOpacity = ButtonBar.Opacity;
                if (startOpacity <= 0) startOpacity = 1;

                var yAnim = new DoubleAnimation(startY, HiddenY, HideDuration)
                { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd };
                var opAnim = new DoubleAnimation(startOpacity, 0, HideDuration)
                { EasingFunction = easeIn, FillBehavior = FillBehavior.HoldEnd };

                bool fired = false;
                Action finish = () =>
                {
                    if (fired) return;
                    fired = true;
                    try
                    {
                        ButtonBar.BeginAnimation(UIElement.OpacityProperty, null);
                        t.BeginAnimation(TranslateTransform.YProperty, null);
                        ButtonBar.Opacity = 1;
                        t.Y = 0;
                        ButtonBar.Visibility = Visibility.Collapsed;
                    }
                    catch { }
                };

                opAnim.Completed += (s, e) => finish();

                t.BeginAnimation(TranslateTransform.YProperty, yAnim);
                ButtonBar.BeginAnimation(UIElement.OpacityProperty, opAnim);
            }
        }

        public void SetDrawButtonsVisibleInstant(bool visible) => SetDrawButtonsVisible(visible);

        public void PlayEnterAnimation(Action onComplete)
        {
            this.BeginAnimation(UIElement.OpacityProperty, null);
            this.Opacity = 1;

            CardsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CardsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            // 禁用动画时直接到位，避免结果页残留缩放/淡入动画
            if (App.IsAnimationDisabled)
            {
                CardsScale.ScaleX = 1;
                CardsScale.ScaleY = 1;
                onComplete?.Invoke();
                return;
            }

            CardsScale.ScaleX = 0.94;
            CardsScale.ScaleY = 0.94;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var sx = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            sx.Completed += (s, e) => onComplete?.Invoke();

            var sy = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            CardsScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            CardsScale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        public void PlayExitAnimation(Action onComplete)
        {
            // 用户可能在大结果生成过程中就离开：让后台的逐行生成尽快停下
            _renderCancelled = true;
            try { DrawResultRenderer.StopAllAnimations(); } catch { }
            try { _soundPlayer.Stop(); } catch { }

            var fade = new DoubleAnimation(this.Opacity, 0, TimeSpan.FromMilliseconds(180))
            {
                FillBehavior = FillBehavior.HoldEnd
            };
            fade.Completed += (s, e) =>
            {
                try
                {
                    this.BeginAnimation(UIElement.OpacityProperty, null);
                    this.Opacity = 1;
                }
                catch { }
                ReleaseResultVisuals();
                onComplete?.Invoke();
            };
            this.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void ReleaseResultVisuals()
        {
            try { DrawResultRenderer.StopAllAnimations(); } catch { }
            try
            {
                if (CardsHost != null)
                {
                    CardsHost.Children.Clear();
                    CardsHost.ColumnDefinitions.Clear();
                    CardsHost.RowDefinitions.Clear();
                    CardsHost.Tag = null;
                }
            }
            catch { }

            _items = new List<DrawResultData>();
            _glassBackdrop = null;
        }

        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);

        public bool TryHandleEscape()
        {
            BtnOk_Click(this, new RoutedEventArgs());
            return true;
        }

        private async void OnOpen(AppMessages.OpenDrawResultPage msg)
        {
            if (msg == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnOpen(msg)));
                return;
            }

            // 先释放上一轮结果，避免大量抽取时新旧两套视觉树同时驻留
            ReleaseResultVisuals();

            _items = msg.Items ?? new List<DrawResultData>();
            _poolDir = msg.PoolDir ?? "";
            _lastDrawCount = msg.LastDrawCount <= 0 ? 1 : msg.LastDrawCount;
            _displayMode = msg.DisplayMode;
            _highlight = msg.Highlight;
            _settings = msg.Settings ?? new PersonalizationSettings();

            var renderTimer = System.Diagnostics.Stopwatch.StartNew();
            RuntimeLog.Info($"开始渲染抽取结果：条目={_items.Count}，模式={_displayMode}，卡池目录={_poolDir}，上次次数={_lastDrawCount}");
            ApplyBackground(); // ★ 调用背景应用

            // ★ 先显示加载遮罩，再分批生成结果，避免大量结果时界面未响应
            _renderCancelled = false;
            ShowRenderLoading();
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                await PopulateResultsAsync();
                RuntimeLog.Info($"抽取结果渲染完成：条目={_items.Count}，耗时={renderTimer.ElapsedMilliseconds}ms，工作集={System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024}MB");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, $"抽取结果渲染失败：条目={_items.Count}，模式={_displayMode}");
                HideRenderLoading();
                _drawButtonsVisible = false;
                SetDrawButtonsVisible(true);
            }

            // 布局完成后再应用一次：确保按钮样式与背景/主题完全一致
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { ApplyBackground(); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // 渲染过程中用户离开页面时置为 true，后台的逐行生成会尽快停下
        private bool _renderCancelled;

        private void UpdateRenderProgress(int built, int total)
        {
            try
            {
                double percent = total > 0 ? Math.Min(100, built * 100.0 / total) : 0;
                if (RenderProgressBar != null) RenderProgressBar.Value = percent;
                if (RenderProgressText != null) RenderProgressText.Text = $"{percent:0}%";
            }
            catch { }
        }

        private void ShowRenderLoading()
        {
            try
            {
                if (RenderLoadingPanel != null) RenderLoadingPanel.Visibility = Visibility.Visible;
                if (RenderSpinner != null)
                {
                    RenderSpinner.SetDotRadius(0.9);
                    RenderSpinner.SetColor(ThemeManager.GetBrush("Fg_333333"));
                    if (App.IsAnimationDisabled) RenderSpinner.Stop();
                    else RenderSpinner.Start();
                }
                if (RenderProgressBar != null) RenderProgressBar.Value = 0;
                if (RenderProgressText != null) RenderProgressText.Text = "0%";
                if (ButtonBar != null) ButtonBar.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void HideRenderLoading()
        {
            try
            {
                if (RenderSpinner != null) RenderSpinner.Stop();
                if (RenderLoadingPanel != null) RenderLoadingPanel.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>整页滚轮：无论指针在哪，都滚动结果内容；同时支持触摸上下滑动。</summary>
        private void ResultRoot_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            try
            {
                if (CardsHost?.Tag is ScrollViewer listScroll)
                {
                    if (listScroll.ScrollableHeight > 1)
                    {
                        listScroll.ScrollToVerticalOffset(listScroll.VerticalOffset - e.Delta);
                        e.Handled = true;
                    }
                    return;
                }

                if (ResultScrollViewer != null && ResultScrollViewer.ScrollableHeight > 1)
                {
                    ResultScrollViewer.ScrollToVerticalOffset(ResultScrollViewer.VerticalOffset - e.Delta);
                    e.Handled = true;
                }
            }
            catch { }
        }
        private ScrollViewer _listScroll;

        /// <summary>把页面右侧整高滚动条与列表滚动区绑定。</summary>
        private void SyncPageScrollBar()
        {
            try
            {
                if (PageScrollBar == null) return;

                var listScroll = CardsHost?.Tag as ScrollViewer;

                if (!ReferenceEquals(_listScroll, listScroll))
                {
                    if (_listScroll != null)
                    {
                        _listScroll.ScrollChanged -= ListScroll_ScrollChanged;
                    }

                    _listScroll = listScroll;

                    if (_listScroll != null)
                    {
                        _listScroll.ScrollChanged += ListScroll_ScrollChanged;
                    }
                }

                UpdatePageScrollBar();
            }
            catch { }
        }

        private void ListScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdatePageScrollBar();

        private void UpdatePageScrollBar()
        {
            try
            {
                if (PageScrollBar == null) return;

                if (_listScroll == null)
                {
                    PageScrollBar.Visibility = Visibility.Collapsed;
                    return;
                }

                double max = Math.Max(0, _listScroll.ExtentHeight - _listScroll.ViewportHeight);
                PageScrollBar.Maximum = max;
                PageScrollBar.ViewportSize = Math.Max(1, _listScroll.ViewportHeight);
                PageScrollBar.LargeChange = Math.Max(1, _listScroll.ViewportHeight);
                PageScrollBar.SmallChange = 32;

                if (Math.Abs(PageScrollBar.Value - _listScroll.VerticalOffset) > 0.5)
                    PageScrollBar.Value = _listScroll.VerticalOffset;

                PageScrollBar.Visibility = max > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void PageScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (_listScroll == null) return;
                if (Math.Abs(_listScroll.VerticalOffset - PageScrollBar.Value) > 0.5)
                    _listScroll.ScrollToVerticalOffset(PageScrollBar.Value);
            }
            catch { }
        }
        private void ResultRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                if (_settings?.EffectiveBackgroundMode != BackgroundMode.Image) return;
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1
                    && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ApplyBackground(); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        private ImageSource _glassBackdrop;
        private const double GlassPad = 24;
        private bool _pendingBgApply;

        /// <summary>把整页背景图渲染成一张模糊位图，供按钮做「毛玻璃」底。</summary>
        private void BuildGlassBackdrop()
        {
            try
            {
                _glassBackdrop = null;
                if (ResultRoot == null || _settings == null) return;

                ResultRoot.UpdateLayout();

                double w = ResultRoot.ActualWidth;
                double h = ResultRoot.ActualHeight;
                if (w < 8 || h < 8) return;

                string bgPath = ResolvePath(_settings.BackgroundImage);
                if (!File.Exists(bgPath)) return;

                var bmp = LoadBitmapSafe(bgPath);
                if (bmp == null) return;

                int pw = (int)Math.Ceiling(w + GlassPad * 2);
                int ph = (int)Math.Ceiling(h + GlassPad * 2);
                if (pw <= 0 || ph <= 0) return;

                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = pw,
                    Height = ph,
                    Fill = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
                    Effect = new BlurEffect { Radius = 18, KernelType = KernelType.Gaussian }
                };

                var canvas = new Grid { Width = pw, Height = ph };
                canvas.Children.Add(rect);
                canvas.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = pw,
                    Height = ph,
                    Fill = new SolidColorBrush(Color.FromArgb(0x3A, 0x00, 0x00, 0x00))
                });
                canvas.Measure(new Size(pw, ph));
                canvas.Arrange(new Rect(0, 0, pw, ph));
                canvas.UpdateLayout();

                var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(canvas);
                rtb.Freeze();
                _glassBackdrop = rtb;
            }
            catch { _glassBackdrop = null; }
        }
        // ★ 解析并应用个性化背景（默认 / 纯色 / 图片），并让按钮自动适配
        private void ApplyBackground()
        {
            if (ResultRoot == null) return;

            // 图片背景的毛玻璃需要先完成布局，拿不到尺寸时延后一帧再处理
            if (_settings?.EffectiveBackgroundMode == BackgroundMode.Image
                && (ResultRoot.ActualWidth < 8 || ResultRoot.ActualHeight < 8))
            {
                if (!_pendingBgApply)
                {
                    _pendingBgApply = true;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _pendingBgApply = false;
                        try { ApplyBackground(); } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                return;
            }

            var mode = _settings?.EffectiveBackgroundMode ?? BackgroundMode.Default;
            bool hasImage = false;

            if (mode == BackgroundMode.Image)
            {
                string bgPath = ResolvePath(_settings.BackgroundImage);
                if (File.Exists(bgPath))
                {
                    var bmp = LoadBitmapSafe(bgPath);
                    if (bmp != null)
                    {
                        ResultRoot.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                        hasImage = true;
                        BuildGlassBackdrop();
                    }
                }
            }

            Color solid = Color.FromRgb(0xF0, 0xF0, 0xF0);
            if (!hasImage)
            {
                if (mode == BackgroundMode.Color)
                {
                    try { solid = (Color)ColorConverter.ConvertFromString(_settings.BackgroundColor); } catch { }
                }
                else
                {
                    // 默认：浅色灰色 / 深色深色，跟随主题
                    if (ThemeManager.GetBrush("Bg_F0F0F0") is SolidColorBrush themeBrush)
                        solid = themeBrush.Color;
                }
                ResultRoot.Background = new SolidColorBrush(solid);
            }

            ApplyResultButtons(mode, hasImage, solid);
        }

        /// <summary>
        /// 图片背景 → 半透明「毛玻璃」按钮；纯色/默认背景 → 高对比强调按钮。
        /// </summary>
        private void ApplyResultButtons(BackgroundMode mode, bool hasImage, Color background)
        {
            if (BtnContinueDraw == null || BtnOk == null) return;

            // 默认背景：保持原版按钮样式
            if (mode == BackgroundMode.Default)
            {
                foreach (var btn in new[] { BtnContinueDraw, BtnOk })
                {
                    btn.ClearValue(Control.BackgroundProperty);
                    btn.ClearValue(Control.BorderBrushProperty);
                    btn.ClearValue(Control.ForegroundProperty);
                }
                if (TxtCountHint != null) TxtCountHint.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            if (hasImage)
            {
                var glassBorder = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
                var fallback = new SolidColorBrush(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF));

                if (_glassBackdrop == null) BuildGlassBackdrop();

                foreach (var btn in new[] { BtnContinueDraw, BtnOk })
                {
                    bool applied = false;

                    if (_glassBackdrop is BitmapSource backdrop)
                    {
                        try
                        {
                            btn.UpdateLayout();
                            Point pt = btn.TransformToVisual(ResultRoot).Transform(new Point(0, 0));
                            double bw = btn.ActualWidth > 2 ? btn.ActualWidth : btn.Width;
                            double bh = btn.ActualHeight > 2 ? btn.ActualHeight : btn.Height;

                            if (bw > 2 && bh > 2)
                            {
                                btn.Background = new ImageBrush(backdrop)
                                {
                                    Stretch = Stretch.Fill,
                                    ViewboxUnits = BrushMappingMode.Absolute,
                                    Viewbox = new Rect(pt.X + GlassPad, pt.Y + GlassPad, bw, bh)
                                };
                                applied = true;
                            }
                        }
                        catch { }
                    }

                    if (!applied) btn.Background = fallback;

                    btn.BorderBrush = glassBorder;
                    btn.Foreground = Brushes.White;
                }
                // 标题颜色跟随主题
                if (TxtCountHint != null) TxtCountHint.ClearValue(TextBlock.ForegroundProperty);
                return;
            }

            bool lightBackground = IsLightColor(background);

            // 纯色背景：按钮使用同色系「更亮」的颜色，并按对比度自动选择深/浅文字
            Color buttonColor = ScaleColor(background, lightBackground ? 1.22 : 1.60);
            Color borderColor = ScaleColor(background, lightBackground ? 0.60 : 0.80);
            Brush fill = new SolidColorBrush(buttonColor);
            Brush text = IsLightColor(buttonColor)
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
                : Brushes.White;

            foreach (var btn in new[] { BtnContinueDraw, BtnOk })
            {
                btn.Background = fill;
                btn.BorderBrush = new SolidColorBrush(borderColor);
                btn.Foreground = text;
            }

            // 标题颜色跟随主题
            if (TxtCountHint != null) TxtCountHint.ClearValue(TextBlock.ForegroundProperty);
        }

        /// <summary>按比例调整颜色明度（大于 1 变亮，小于 1 变暗）。</summary>
        private static Color ScaleColor(Color c, double factor)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, Math.Round(c.R * factor)),
                (byte)Math.Min(255, Math.Round(c.G * factor)),
                (byte)Math.Min(255, Math.Round(c.B * factor)));
        }

        private static bool IsLightColor(Color c)
        {
            double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return luminance >= 0.55;
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

        private async Task PopulateResultsAsync()
        {
            int count = _items.Count;

            if (_lastDrawCount == 1)
                TxtCountHint.Text = "单次抽取结果";
            else
                TxtCountHint.Text = count > 0 ? $"本次抽取 {count} 项" : "";

            if (_displayMode == DrawResultDisplayMode.List)
            {
                // 列表模式：垂直滚动条由页面右侧单独提供，滚动区贴到窗口边缘
                ResultScrollViewer.Margin = new Thickness(20, 70, 20, 90);
                CardsHost.Margin = new Thickness(0);
                ResultScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ResultScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

                var binding = new Binding("ViewportHeight")
                {
                    Source = ResultScrollViewer,
                    Mode = BindingMode.OneWay
                };
                CardsHost.SetBinding(FrameworkElement.MaxHeightProperty, binding);
            }
            else
            {
                ResultScrollViewer.Margin = new Thickness(20, 70, 16, 90);
                CardsHost.Margin = new Thickness(0, 0, 8, 0);
                ResultScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ResultScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                CardsHost.ClearValue(FrameworkElement.MaxHeightProperty);
            }

            // ★ 传入 _settings 和 _poolDir，以便根据背景设置处理局部毛玻璃效果
            await DrawResultRenderer.RenderAsync(CardsHost, _items, _displayMode, _highlight, _settings, _poolDir,
                _glassBackdrop as BitmapSource, ResultRoot,
                (built, total) => UpdateRenderProgress(built, total),
                () => _renderCancelled);

            // 等一次布局完成后再收起遮罩，避免大结果集的首帧布局期间露出空白
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            // 列表的滚动条由表格自身提供，页面级整高滚动条不再启用
            if (PageScrollBar != null) PageScrollBar.Visibility = Visibility.Collapsed;

            // ★ 结果生成完毕后再显示按钮，避免加载过程中被点击
            HideRenderLoading();
            _drawButtonsVisible = false;
            SetDrawButtonsVisible(true);
            PlayResultSounds();
        }

        private void PlayResultSounds()
        {
            try
            {
                if (App.IsSoundEffectMuted || _settings == null || _settings.SoundMode == SoundMode.None) return;

                // ★ 仅在单抽时（_items.Count == 1）播放音效，多抽不播
                if (_items.Count != 1) return;

                if (_settings.SoundMode == SoundMode.Global)
                {
                    PlaySoundFile(_settings.GlobalSoundFile);
                }
                else if (_settings.SoundMode == SoundMode.PerItem)
                {
                    var item = _items[0];
                    var pi = _settings.Items?.FirstOrDefault(x => x.ObjectName == item.ObjectName);
                    if (pi != null) PlaySoundFile(pi.SoundFile);
                }
            }
            catch { }
        }

        private void PlaySoundFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string fullPath = path;
                if (!Path.IsPathRooted(fullPath) && !string.IsNullOrEmpty(_poolDir))
                {
                    fullPath = Path.Combine(_poolDir, path);
                }
                if (!File.Exists(fullPath)) return;

                _soundPlayer.Open(new Uri(fullPath, UriKind.Absolute));
                _soundPlayer.Volume = Math.Max(0, Math.Min(1, App.SoundEffectVolume));
                _soundPlayer.Play();
            }
            catch { }
        }

        private async void BtnContinueDraw_Click(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this) as MainWindow;
            if (win == null) return;

            var session = DrawSession.Current;
            if (session == null || session.Pool == null || session.Pool.Items.Count == 0)
                return;

            int count;

            if (_lastDrawCount <= 1)
            {
                count = 1;
            }
            else
            {
                int? picked = await win.ShowDrawCountPanelAsync(_lastDrawCount);
                if (picked == null)
                {
                    // ★ 修复：输入面板取消时，平滑恢复按钮
                    SetDrawButtonsVisible(true);
                    return;
                }
                count = picked.Value;
            }

            int? adjusted = DrawFlowHelper.CheckAndAdjustCount(
                win, session.Pool, session.Engine, session.NoRepeat, count);
            if (adjusted == null)
            {
                // ★ 修复：数量超出/抽完提示框取消时，平滑恢复按钮
                SetDrawButtonsVisible(true);
                return;
            }

            MessageBus.Publish(new AppMessages.DrawResultPageContinueDraw(adjusted.Value));
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            try { _soundPlayer.Stop(); } catch { }
            MessageBus.Publish(new AppMessages.DrawResultPageClosed());
            MessageBus.Publish(new AppMessages.NavigateBack());
        }
    }
}
