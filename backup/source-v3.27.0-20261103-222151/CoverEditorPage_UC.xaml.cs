using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RandomDrawer.Messaging;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池封面编辑页：导入图片 / GIF / 视频，在封面上加文字（颜色、字号、粗斜体、下划线、删除线、荧光笔）
    /// 或随手涂画（画笔 / 橡皮擦 / 笔粗细），保存成新的封面文件。
    /// </summary>
    public partial class CoverEditorPage_UC : UserControl, IPageAnimation
    {
        private enum ToolMode { Text, Pen, Eraser }

        private sealed class TextItem
        {
            public Border Container;
            public TextBox Box;
            public Thumb Grip;
        }

        private readonly List<TextItem> _texts = new List<TextItem>();
        private readonly List<HintBubble> _hints = new List<HintBubble>();

        private ToolMode _mode = ToolMode.Text;
        private Color _textColor = Color.FromRgb(0x33, 0x33, 0x33);
        private Color _highlightColor = Color.FromRgb(0xFF, 0xF7, 0xB2);
        private Color _penColor = Color.FromRgb(0xE5, 0x39, 0x35);
        private double _fontSize = 28;
        private bool _bold, _italic, _underline, _strike, _highlight;
        private TextItem _activeText;

        private string _coverPath = "";
        private bool _hasMedia;
        private bool _isVideo;
        private bool _draggingSelection;
        private Point _dragStart;
        private System.Windows.Shapes.Rectangle _selectionRect;

        private bool _isShowing;
        private Action<AppMessages.OpenCoverEditor> _onOpen;

        public CoverEditorPage_UC()
        {
            InitializeComponent();

            ComboFontSize.SelectedIndex = 3;   // 28
            ComboPenSize.SelectedIndex = 1;    // 中

            _onOpen = msg => Dispatcher.BeginInvoke(new Action(() => LoadFrom(msg)));
            MessageBus.Subscribe(_onOpen);

            StageHost.SizeChanged += (s, e) => LayoutStage();
            UpdateToolVisuals();

            foreach (var btn in new[] { BtnImport, BtnTextColor, BtnBold, BtnItalic, BtnUnderline,
                                        BtnStrike, BtnHighlight, BtnPen, BtnPenColor, BtnEraser })
                AttachHint(btn);
        }

        public void PlayEnterAnimation(Action onComplete) => SlideInAnimationHelper.PlaySlideIn(this, 60, 0, onComplete);
        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);
        public void PlayExitAnimation(Action onComplete)
        {
            _isShowing = false;
            ReleaseAllHints();
            SlideInAnimationHelper.PlaySlideOut(this, 60, 0, onComplete);
        }

        // ================================================================
        // 打开 / 载入
        // ================================================================

        private void LoadFrom(AppMessages.OpenCoverEditor msg)
        {
            if (msg == null) return;

            _isShowing = true;
            _coverPath = msg.CoverPath ?? "";
            ClearTexts();
            InkLayer.Strokes.Clear();

            if (!string.IsNullOrEmpty(_coverPath) && File.Exists(_coverPath))
                LoadMedia(_coverPath);
            else
                ShowEmpty();

            UpdateStatus();
        }

        private void ShowEmpty()
        {
            _hasMedia = false;
            _isVideo = false;
            BaseImage.Source = null;
            BaseImage.Visibility = Visibility.Collapsed;
            VideoPreview.Visibility = Visibility.Collapsed;
            try { VideoPreview.Stop(); VideoPreview.Close(); } catch { }
            EmptyHint.Visibility = Visibility.Visible;
            InkLayer.IsHitTestVisible = false;
        }

        private void LoadMedia(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

            _hasMedia = true;
            EmptyHint.Visibility = Visibility.Collapsed;

            if (videoExts.Contains(ext))
            {
                _isVideo = true;
                BaseImage.Source = null;
                BaseImage.Visibility = Visibility.Collapsed;
                VideoPreview.Visibility = Visibility.Visible;
                try
                {
                    VideoPreview.Source = new Uri(path, UriKind.Absolute);
                    VideoPreview.Play();
                }
                catch { }
            }
            else
            {
                _isVideo = false;
                try { VideoPreview.Stop(); VideoPreview.Close(); } catch { }
                VideoPreview.Visibility = Visibility.Collapsed;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                BaseImage.Source = bmp;
                BaseImage.Visibility = Visibility.Visible;
                _coverAspect = bmp.PixelWidth / (double)Math.Max(1, bmp.PixelHeight);
            }

            LayoutStage();
        }

        private double _coverAspect = 16.0 / 9.0;

        /// <summary>按封面比例把编辑区放进可用空间（居中）。</summary>
        private void LayoutStage()
        {
            if (!_hasMedia) return;
            if (StageHost.ActualWidth < 8 || StageHost.ActualHeight < 8) return;

            double availW = Math.Max(80, StageHost.ActualWidth - 24);
            double availH = Math.Max(80, StageHost.ActualHeight - 24);

            double w = availW, h = availW / _coverAspect;
            if (h > availH) { h = availH; w = availH * _coverAspect; }

            Stage.Width = Math.Round(w);
            Stage.Height = Math.Round(h);

            InkLayer.Width = Stage.Width;
            InkLayer.Height = Stage.Height;
            TextLayer.Width = Stage.Width;
            TextLayer.Height = Stage.Height;
            SelectionLayer.Width = Stage.Width;
            SelectionLayer.Height = Stage.Height;
        }

        private void UpdateStatus()
        {
            if (TxtStatus == null) return;
            TxtStatus.Text = string.IsNullOrEmpty(_coverPath) ? "未选择封面" : Path.GetFileName(_coverPath);
        }

        // ================================================================
        // 顶部按钮
        // ================================================================

        private void BtnBack_Click(object sender, RoutedEventArgs e) => CancelAndBack();

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => CancelAndBack();

        private void CancelAndBack()
        {
            MessageBus.Publish(new AppMessages.CoverEditorFinished("", false));
            MessageBus.Publish(new AppMessages.NavigateBack());
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择卡池封面",
                Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            _coverPath = dlg.FileName;
            ClearTexts();
            InkLayer.Strokes.Clear();
            LoadMedia(_coverPath);
            UpdateStatus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasMedia)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先导入封面图片、GIF 或视频。",
                    "还没有封面", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (_isVideo)
                {
                    // 视频无法把批注烧进画面，直接沿用原视频
                    MessageBus.Publish(new AppMessages.CoverEditorFinished(_coverPath, true));
                    MessageBus.Publish(new AppMessages.NavigateBack());
                    return;
                }

                ApplyFormattingToActiveText();
                string outPath = RenderComposition();
                if (string.IsNullOrEmpty(outPath)) return;

                MessageBus.Publish(new AppMessages.CoverEditorFinished(outPath, true, ChkBlurBackground?.IsChecked == true));
                MessageBus.Publish(new AppMessages.NavigateBack());
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "保存封面失败");
                CustomMessageBox.Show(Window.GetWindow(this), "保存封面失败：" + ex.Message,
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>把预览区（底图 + 涂画 + 文字）渲染成 PNG，返回临时文件路径。</summary>
        private string RenderComposition()
        {
            var selection = SelectionLayer;   // 选区框不参与渲染
            if (selection != null) selection.Visibility = Visibility.Collapsed;

            try
            {
                Stage.UpdateLayout();

                int w = Math.Max(16, (int)Math.Round(Stage.ActualWidth));
                int h = Math.Max(16, (int)Math.Round(Stage.ActualHeight));

                // ★ 只渲染批注层（文字 + 笔迹），透明底；原封面文件保持不变
                var baseVis = BaseImage.Visibility;
                var vidVis = VideoPreview.Visibility;
                BaseImage.Visibility = Visibility.Collapsed;
                VideoPreview.Visibility = Visibility.Collapsed;

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                try { rtb.Render(Stage); }
                finally
                {
                    BaseImage.Visibility = baseVis;
                    VideoPreview.Visibility = vidVis;
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                string name = "cover-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";

                // 依次尝试：封面所在目录 → 程序数据目录 → 系统临时目录
                var dirs = new List<string>();
                try { if (!string.IsNullOrEmpty(_coverPath)) dirs.Add(Path.GetDirectoryName(_coverPath)); } catch { }
                try { dirs.Add(Path.Combine(App.AppDataDir, "cover-edits")); } catch { }
                try { dirs.Add(Path.Combine(Path.GetTempPath(), "RadomSelectCover")); } catch { }

                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, name);
                        using (var fs = File.Create(file)) encoder.Save(fs);
                        return file;
                    }
                    catch { }
                }

                return "";
            }
            finally
            {
                if (selection != null) selection.Visibility = Visibility.Visible;
            }
        }

        // ================================================================
        // 工具栏：文字
        // ================================================================

        private void BtnTextColor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ColorPickerDialog("选择文字颜色", Hex(_textColor)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            if (dlg.Cleared) return;

            try { _textColor = (Color)ColorConverter.ConvertFromString(dlg.SelectedHex); } catch { return; }
            TextColorSwatch.Background = new SolidColorBrush(_textColor);
            ApplyFormattingToActiveText();
        }

        private void BtnHighlight_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ColorPickerDialog("选择荧光笔颜色", Hex(_highlightColor)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Cleared) return;

            try { _highlightColor = (Color)ColorConverter.ConvertFromString(dlg.SelectedHex); } catch { return; }
            HighlightSwatch.Background = new SolidColorBrush(_highlightColor);
            _highlight = true;
            ApplyFormattingToActiveText();
        }

        private void ComboFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboFontSize.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Content as string, out double size))
            {
                _fontSize = size;
                ApplyFormattingToActiveText();
            }
        }

        private void BtnBold_Click(object sender, RoutedEventArgs e) { _bold = !_bold; UpdateToolVisuals(); ApplyFormattingToActiveText(); }
        private void BtnItalic_Click(object sender, RoutedEventArgs e) { _italic = !_italic; UpdateToolVisuals(); ApplyFormattingToActiveText(); }
        private void BtnUnderline_Click(object sender, RoutedEventArgs e) { _underline = !_underline; UpdateToolVisuals(); ApplyFormattingToActiveText(); }
        private void BtnStrike_Click(object sender, RoutedEventArgs e) { _strike = !_strike; UpdateToolVisuals(); ApplyFormattingToActiveText(); }

        private void UpdateToolVisuals()
        {
            SetToggle(BtnBold, _bold);
            SetToggle(BtnItalic, _italic);
            SetToggle(BtnUnderline, _underline);
            SetToggle(BtnStrike, _strike);
            SetToggle(BtnPen, _mode == ToolMode.Pen);
            SetToggle(BtnEraser, _mode == ToolMode.Eraser);
        }

        /// <summary>开关型按钮的选中态：用强调色描边 + 浅底。</summary>
        private static void SetToggle(Button button, bool on)
        {
            if (button == null) return;
            if (on)
            {
                button.SetResourceReference(Control.BackgroundProperty, "Bg_E5F3FF");
                button.SetResourceReference(Control.BorderBrushProperty, "Fg_0078D7");
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
            }
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // ================================================================
        // 工具栏：画笔
        // ================================================================

        private void BtnPen_Click(object sender, RoutedEventArgs e) { _mode = _mode == ToolMode.Pen ? ToolMode.Text : ToolMode.Pen; ApplyMode(); }

        private void BtnEraser_Click(object sender, RoutedEventArgs e) { _mode = _mode == ToolMode.Eraser ? ToolMode.Text : ToolMode.Eraser; ApplyMode(); }

        private void BtnPenColor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ColorPickerDialog("选择画笔颜色", Hex(_penColor)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Cleared) return;

            try { _penColor = (Color)ColorConverter.ConvertFromString(dlg.SelectedHex); } catch { return; }
            PenColorSwatch.Background = new SolidColorBrush(_penColor);
            InkLayer.DefaultDrawingAttributes.Color = _penColor;
        }

        private void ComboPenSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            double width = ComboPenSize.SelectedIndex == 2 ? 12 : ComboPenSize.SelectedIndex == 1 ? 6 : 2;
            InkLayer.DefaultDrawingAttributes.Width = width;
            InkLayer.DefaultDrawingAttributes.Height = width;
        }

        private void ApplyMode()
        {
            UpdateToolVisuals();

            bool draw = _mode != ToolMode.Text;
            InkLayer.IsHitTestVisible = draw;
            InkLayer.EditingMode = _mode == ToolMode.Eraser ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
            Stage.Cursor = draw ? Cursors.Pen : Cursors.Cross;

            // 画图时文本框不抢鼠标
            TextLayer.IsHitTestVisible = !draw;
        }

        // ================================================================
        // 拖出文本框
        // ================================================================

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_hasMedia || _mode != ToolMode.Text) return;
            if (e.OriginalSource is TextBox) return;

            _draggingSelection = true;
            _dragStart = e.GetPosition(Stage);
            Stage.CaptureMouse();

            _selectionRect = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new double[] { 4, 3 }),
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD7))
            };
            SelectionLayer.Children.Clear();
            SelectionLayer.Children.Add(_selectionRect);
            Canvas.SetLeft(_selectionRect, _dragStart.X);
            Canvas.SetTop(_selectionRect, _dragStart.Y);
            e.Handled = true;
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingSelection || _selectionRect == null) return;

            Point p = e.GetPosition(Stage);
            double x = Math.Min(p.X, _dragStart.X);
            double y = Math.Min(p.Y, _dragStart.Y);
            double w = Math.Abs(p.X - _dragStart.X);
            double h = Math.Abs(p.Y - _dragStart.Y);

            Canvas.SetLeft(_selectionRect, x);
            Canvas.SetTop(_selectionRect, y);
            _selectionRect.Width = w;
            _selectionRect.Height = h;
        }

        private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_draggingSelection) return;

            _draggingSelection = false;
            try { Stage.ReleaseMouseCapture(); } catch { }

            double w = _selectionRect?.Width ?? 0;
            double h = _selectionRect?.Height ?? 0;
            double x = Canvas.GetLeft(_selectionRect);
            double y = Canvas.GetTop(_selectionRect);
            SelectionLayer.Children.Clear();
            _selectionRect = null;

            if (w < 20 || h < 16) return;   // 太小当作误触
            CreateTextBox(x, y, w, h);
        }

        private void CreateTextBox(double x, double y, double w, double h)
        {
            var box = new TextBox
            {
                Text = "双击编辑文字",
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(_textColor),
                FontSize = _fontSize,
                FontWeight = _bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = _italic ? FontStyles.Italic : FontStyles.Normal,
                Padding = new Thickness(2),
                VerticalContentAlignment = VerticalAlignment.Top
            };
            ApplyDecorations(box);

            var grip = new Thumb
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                Cursor = Cursors.SizeNWSE
            };

            var container = new Border
            {
                Width = w,
                Height = h,
                Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x78, 0xD7)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3),
                Child = new Grid
                {
                    Children = { box, grip }
                }
            };

            var item = new TextItem { Container = container, Box = box, Grip = grip };
            box.GotKeyboardFocus += (s, e) => { _activeText = item; };
            box.TextChanged += (s, e) => { };
            box.PreviewMouseLeftButtonDown += (s, e) => { _activeText = item; };

            // 拖动文本框：按住边框移动
            bool moving = false;
            Point moveStart = default;
            double startX = 0, startY = 0;
            container.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is TextBox) return;
                moving = true;
                moveStart = e.GetPosition(TextLayer);
                startX = Canvas.GetLeft(container);
                startY = Canvas.GetTop(container);
                container.CaptureMouse();
                e.Handled = true;
            };
            container.MouseMove += (s, e) =>
            {
                if (!moving) return;
                Point p = e.GetPosition(TextLayer);
                Canvas.SetLeft(container, Math.Max(0, startX + (p.X - moveStart.X)));
                Canvas.SetTop(container, Math.Max(0, startY + (p.Y - moveStart.Y)));
            };
            container.MouseLeftButtonUp += (s, e) =>
            {
                moving = false;
                try { container.ReleaseMouseCapture(); } catch { }
            };

            // 拖动右下角缩放
            double startW = w, startH = h;
            grip.DragStarted += (s, e) => { startW = container.Width; startH = container.Height; };
            grip.DragDelta += (s, e) =>
            {
                container.Width = Math.Max(40, startW + e.HorizontalChange);
                container.Height = Math.Max(24, startH + e.VerticalChange);
            };

            Canvas.SetLeft(container, x);
            Canvas.SetTop(container, y);
            TextLayer.Children.Add(container);
            _texts.Add(item);
            _activeText = item;

            box.Focus();
            box.SelectAll();
        }

        private void ApplyDecorations(TextBox box)
        {
            var decorations = new TextDecorationCollection();
            if (_underline) decorations.Add(TextDecorations.Underline[0]);
            if (_strike) decorations.Add(TextDecorations.Strikethrough[0]);
            box.TextDecorations = decorations.Count == 0 ? null : decorations;

            box.Background = _highlight
                ? new SolidColorBrush(Color.FromArgb(0x99, _highlightColor.R, _highlightColor.G, _highlightColor.B))
                : Brushes.Transparent;
        }

        private void ApplyFormattingToActiveText()
        {
            if (_activeText == null) return;

            var box = _activeText.Box;
            box.Foreground = new SolidColorBrush(_textColor);
            box.FontSize = _fontSize;
            box.FontWeight = _bold ? FontWeights.Bold : FontWeights.Normal;
            box.FontStyle = _italic ? FontStyles.Italic : FontStyles.Normal;
            ApplyDecorations(box);
        }

        private void ClearTexts()
        {
            TextLayer.Children.Clear();
            _texts.Clear();
            _activeText = null;
        }

        // ================================================================
        // 悬浮提示：跟随鼠标
        // ================================================================

        private void AttachHint(Button button)
        {
            if (button == null) return;
            string text = button.Tag as string;
            if (string.IsNullOrEmpty(text)) return;

            var hint = new HintBubble(button, text);
            _hints.Add(hint);

            button.MouseEnter += (s, e) => hint.Show(Mouse.GetPosition(button));
            button.MouseMove += (s, e) => hint.Show(Mouse.GetPosition(button));
            button.MouseLeave += (s, e) => hint.Hide();
        }

        private void ReleaseAllHints()
        {
            foreach (var h in _hints) { try { h.Hide(); } catch { } }
        }

        private HintBubble _blurHint;

        private void ChkBlurBackground_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_blurHint == null) _blurHint = new HintBubble(ChkBlurBackground, "开启后将把封面原图模糊后作为主界面背景");
            _blurHint.Show(Mouse.GetPosition(ChkBlurBackground));
        }

        private void ChkBlurBackground_MouseMove(object sender, MouseEventArgs e)
        {
            if (_blurHint != null) _blurHint.Show(Mouse.GetPosition(ChkBlurBackground));
        }

        private void ChkBlurBackground_MouseLeave(object sender, MouseEventArgs e)
        {
            try { _blurHint?.Hide(); } catch { }
        }
        private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
        {
            try { VideoPreview.Position = TimeSpan.Zero; VideoPreview.Play(); } catch { }
        }
    }
}
