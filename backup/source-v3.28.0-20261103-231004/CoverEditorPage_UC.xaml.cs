using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RandomDrawer.Messaging;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池封面编辑页：导入图片 / GIF / 视频，在封面上加文字（颜色、字号、粗斜体、下划线、删除线、荧光笔）
    /// 或随手涂画（画笔 / 荧光笔 / 橡皮擦），保存成带透明通道的批注层（原封面文件不动）。
    /// </summary>
    public partial class CoverEditorPage_UC : UserControl, IPageAnimation
    {
        private enum ToolMode { Text, Pen, Highlight, Eraser }

        private sealed class TextItem
        {
            public RichTextBox Box;
            public Thumb Grip;
        }

        private readonly List<TextItem> _texts = new List<TextItem>();
        private readonly Stack<Action> _undo = new Stack<Action>();
        private readonly Stack<Action> _redo = new Stack<Action>();

        private ToolMode _mode = ToolMode.Text;
        private Color _textColor = Color.FromRgb(0x33, 0x33, 0x33);
        private Color _highlightColor = Color.FromRgb(0xFF, 0xF7, 0xB2);
        private Color _penColor = Color.FromRgb(0xE5, 0x39, 0x35);
        private double _fontSize = 32;
        private bool _bold, _italic;
        private double _penWidth = 6;
        private TextItem _activeText;

        private string _coverPath = "";
        private bool _hasMedia;
        private bool _isVideo;
        private double _coverAspect = 16.0 / 9.0;

        private bool _draggingSelection;
        private Point _dragStart;
        private System.Windows.Shapes.Rectangle _selectionRect;

        private Action<AppMessages.OpenCoverEditor> _onOpen;

        public CoverEditorPage_UC()
        {
            InitializeComponent();

            BuildFontSizeItems();
            ComboPenSize.SelectedIndex = 1;

            _onOpen = msg => Dispatcher.BeginInvoke(new Action(() => LoadFrom(msg)));
            MessageBus.Subscribe(_onOpen);

            StageHost.SizeChanged += (s, e) => LayoutStage();
            ApplyMode();
        }

        public void PlayEnterAnimation(Action onComplete) => SlideInAnimationHelper.PlaySlideIn(this, 60, 0, onComplete);
        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);
        public void PlayExitAnimation(Action onComplete) => SlideInAnimationHelper.PlaySlideOut(this, 60, 0, onComplete);

        // ================================================================
        // 载入
        // ================================================================

        private void LoadFrom(AppMessages.OpenCoverEditor msg)
        {
            if (msg == null) return;

            _coverPath = msg.CoverPath ?? "";
            ClearTexts();
            InkLayer.Strokes.Clear();
            _undo.Clear();
            _redo.Clear();

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

            foreach (var layer in new FrameworkElement[] { InkLayer, TextLayer, SelectionLayer })
            {
                layer.Width = Stage.Width;
                layer.Height = Stage.Height;
            }
        }

        private void UpdateStatus()
        {
            if (TxtStatus == null) return;
            TxtStatus.Text = string.IsNullOrEmpty(_coverPath) ? "未选择封面" : Path.GetFileName(_coverPath);
        }

        // ================================================================
        // 按钮
        // ================================================================

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
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
            _undo.Clear();
            _redo.Clear();
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
                CommitActiveText();

                // 视频无法把批注烧进画面，直接沿用原视频
                if (_isVideo)
                {
                    MessageBus.Publish(new AppMessages.CoverEditorFinished(_coverPath, true,
                        ChkBlurBackground?.IsChecked == true, ChkRoundCover?.IsChecked == true));
                    MessageBus.Publish(new AppMessages.NavigateBack());
                    return;
                }

                string outPath = RenderOverlay();
                if (string.IsNullOrEmpty(outPath))
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "保存批注图层失败。",
                        "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MessageBus.Publish(new AppMessages.CoverEditorFinished(outPath, true,
                    ChkBlurBackground?.IsChecked == true, ChkRoundCover?.IsChecked == true));
                MessageBus.Publish(new AppMessages.NavigateBack());
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "保存封面批注失败");
                CustomMessageBox.Show(Window.GetWindow(this), "保存封面失败：" + ex.Message,
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>只渲染批注层（透明 PNG），原封面文件不动。</summary>
        private string RenderOverlay()
        {
            CommitActiveText();
            SelectionLayer.Visibility = Visibility.Collapsed;

            var baseVis = BaseImage.Visibility;
            var vidVis = VideoPreview.Visibility;
            BaseImage.Visibility = Visibility.Collapsed;
            VideoPreview.Visibility = Visibility.Collapsed;

            try
            {
                Stage.UpdateLayout();

                int w = Math.Max(16, (int)Math.Round(Stage.ActualWidth));
                int h = Math.Max(16, (int)Math.Round(Stage.ActualHeight));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(Stage);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                string name = "cover-overlay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
                foreach (string dir in CandidateDirs())
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
                BaseImage.Visibility = baseVis;
                VideoPreview.Visibility = vidVis;
                SelectionLayer.Visibility = Visibility.Visible;
            }
        }

        private List<string> CandidateDirs()
        {
            var dirs = new List<string>();
            try { if (!string.IsNullOrEmpty(_coverPath)) dirs.Add(Path.GetDirectoryName(_coverPath)); } catch { }
            try { dirs.Add(Path.Combine(App.AppDataDir, "cover-edits")); } catch { }
            try { dirs.Add(Path.Combine(Path.GetTempPath(), "RadomSelectCover")); } catch { }
            return dirs;
        }

        // ================================================================
        // 工具切换
        // ================================================================

        private void BtnTextTool_Click(object sender, RoutedEventArgs e)
        {
            _mode = ToolMode.Text;
            ApplyMode();
        }

        private void BtnPen_Click(object sender, RoutedEventArgs e)
        {
            _mode = _mode == ToolMode.Pen ? ToolMode.Text : ToolMode.Pen;
            ApplyMode();
        }

        private void BtnEraser_Click(object sender, RoutedEventArgs e)
        {
            _mode = _mode == ToolMode.Eraser ? ToolMode.Text : ToolMode.Eraser;
            ApplyMode();
        }

        private void BtnHighlight_Click(object sender, RoutedEventArgs e)
        {
            // 有选中文字 → 给选中文字加荧光；否则进入荧光笔绘制模式
            if (_activeText != null && HasTextSelection(_activeText.Box))
            {
                ApplyToSelection(TextElement.BackgroundProperty,
                    new SolidColorBrush(Color.FromArgb(0x99, _highlightColor.R, _highlightColor.G, _highlightColor.B)));
                return;
            }

            _mode = _mode == ToolMode.Highlight ? ToolMode.Text : ToolMode.Highlight;
            ApplyMode();
        }

        private void BtnPenColor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ColorPickerDialog("选择画笔颜色", Hex(_penColor)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Cleared) return;

            try { _penColor = (Color)ColorConverter.ConvertFromString(dlg.SelectedHex); } catch { return; }
            PenColorSwatch.Background = new SolidColorBrush(_penColor);
            ApplyDrawingAttributes();
        }

        private void ComboPenSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _penWidth = ComboPenSize.SelectedIndex switch
            {
                3 => 24,
                2 => 14,
                1 => 6,
                _ => 2
            };
            ApplyDrawingAttributes();
        }

        private void ApplyDrawingAttributes()
        {
            var da = InkLayer.DefaultDrawingAttributes;
            bool highlight = _mode == ToolMode.Highlight;
            da.Color = highlight
                ? Color.FromArgb(0x80, _highlightColor.R, _highlightColor.G, _highlightColor.B)
                : _penColor;
            da.Width = highlight ? Math.Max(14, _penWidth * 2.4) : _penWidth;
            da.Height = da.Width;
            da.IsHighlighter = false;
        }

        private void ApplyMode()
        {
            UpdateToolVisuals();

            InkLayer.IsHitTestVisible = _mode != ToolMode.Text;
            InkLayer.EditingMode = _mode == ToolMode.Eraser
                ? InkCanvasEditingMode.EraseByPoint
                : InkCanvasEditingMode.Ink;
            InkLayer.EraserShape = new System.Windows.Ink.EllipseStylusShape(Math.Max(4, _penWidth * 2), Math.Max(4, _penWidth * 2));
            ApplyDrawingAttributes();

            Stage.Cursor = _mode == ToolMode.Text
                ? Cursors.IBeam
                : _mode == ToolMode.Eraser ? Cursors.No : Cursors.Pen;

            TextLayer.IsHitTestVisible = _mode == ToolMode.Text;
            if (_mode != ToolMode.Text) CommitActiveText();
        }

        private void UpdateToolVisuals()
        {
            SetToggle(BtnTextTool, _mode == ToolMode.Text);
            SetToggle(BtnPen, _mode == ToolMode.Pen);
            SetToggle(BtnEraser, _mode == ToolMode.Eraser);
            SetToggle(BtnHighlight, _mode == ToolMode.Highlight);
        }

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
        // 字号
        // ================================================================

        private void BuildFontSizeItems()
        {
            ComboFontSize.Items.Clear();
            for (int size = 12; size <= 200; size += 4)
                ComboFontSize.Items.Add(size.ToString());
            ComboFontSize.Text = ((int)_fontSize).ToString();
        }

        private void ComboFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboFontSize.SelectedItem is string s && double.TryParse(s, out double size))
            {
                _fontSize = Math.Max(6, Math.Min(400, size));
                _activeText = _activeText;
                ApplyFontSizeToSelection();
            }
        }

        private void ComboFontSize_LostFocus(object sender, RoutedEventArgs e) => CommitFontSizeText();

        private void ComboFontSize_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitFontSizeText(); e.Handled = true; }
        }

        private void CommitFontSizeText()
        {
            if (double.TryParse((ComboFontSize.Text ?? "").Trim(), out double size))
            {
                _fontSize = Math.Max(6, Math.Min(400, size));
                ApplyFontSizeToSelection();
            }
            else
            {
                ComboFontSize.Text = ((int)_fontSize).ToString();
            }
        }

        private void ApplyFontSizeToSelection()
        {
            if (_activeText == null) return;
            ApplyToSelection(TextElement.FontSizeProperty, _fontSize);
        }

        // ================================================================
        // 文本样式（只作用于选中的文字）
        // ================================================================

        private void BtnTextColor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ColorPickerDialog("选择文字颜色", Hex(_textColor)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Cleared) return;

            try { _textColor = (Color)ColorConverter.ConvertFromString(dlg.SelectedHex); } catch { return; }
            TextColorSwatch.Background = new SolidColorBrush(_textColor);
            ApplyToSelection(TextElement.ForegroundProperty, new SolidColorBrush(_textColor));
        }

        private void BtnBold_Click(object sender, RoutedEventArgs e) => ToggleTextStyle(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);
        private void BtnItalic_Click(object sender, RoutedEventArgs e) => ToggleTextStyle(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);

        private void BtnUnderline_Click(object sender, RoutedEventArgs e)
        {
            if (_activeText == null) return;
            bool on = Equals(GetSelectionValue(Inline.TextDecorationsProperty), TextDecorations.Underline);
            ApplyToSelection(Inline.TextDecorationsProperty, on ? new TextDecorationCollection() : TextDecorations.Underline);
        }

        private void BtnStrike_Click(object sender, RoutedEventArgs e)
        {
            if (_activeText == null) return;
            bool on = Equals(GetSelectionValue(Inline.TextDecorationsProperty), TextDecorations.Strikethrough);
            ApplyToSelection(Inline.TextDecorationsProperty, on ? new TextDecorationCollection() : TextDecorations.Strikethrough);
        }

        private void ToggleTextStyle(DependencyProperty property, object on, object off)
        {
            if (_activeText == null) return;
            bool currently = Equals(GetSelectionValue(property), on);
            ApplyToSelection(property, currently ? off : on);
        }

        private object GetSelectionValue(DependencyProperty property)
        {
            try
            {
                if (_activeText == null) return null;
                var box = _activeText.Box;
                var range = new TextRange(box.Selection.Start, box.Selection.End);
                return range.GetPropertyValue(property);
            }
            catch { return null; }
        }

        private static bool HasTextSelection(RichTextBox box)
        {
            try { return box != null && !box.Selection.IsEmpty; } catch { return false; }
        }

        private void ApplyToSelection(DependencyProperty property, object value)
        {
            if (_activeText == null) return;

            try
            {
                var box = _activeText.Box;
                TextRange range = box.Selection.IsEmpty
                    ? new TextRange(box.Document.ContentStart, box.Document.ContentEnd)
                    : new TextRange(box.Selection.Start, box.Selection.End);

                range.ApplyPropertyValue(property, value);
            }
            catch { }
        }

        // ================================================================
        // 拖出文本框
        // ================================================================

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_hasMedia) return;

            if (_mode == ToolMode.Eraser)
            {
                EraseAt(e.GetPosition(Stage));
                return;
            }

            if (_mode != ToolMode.Text) return;
            if (e.OriginalSource is RichTextBox) return;

            // 点空白处：先固定当前文本框
            CommitActiveText();

            _dragStart = e.GetPosition(Stage);
            _draggingSelection = true;
            Stage.CaptureMouse();
            e.Handled = true;
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingSelection) return;

            Point p = e.GetPosition(Stage);
            double dx = Math.Abs(p.X - _dragStart.X);
            double dy = Math.Abs(p.Y - _dragStart.Y);
            if (dx < 6 && dy < 6) return;   // 还没拖动，先不显示选区

            if (_selectionRect == null)
            {
                _selectionRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection(new double[] { 4, 3 }),
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD7))
                };
                SelectionLayer.Children.Clear();
                SelectionLayer.Children.Add(_selectionRect);
            }

            double x = Math.Min(p.X, _dragStart.X);
            double y = Math.Min(p.Y, _dragStart.Y);
            Canvas.SetLeft(_selectionRect, x);
            Canvas.SetTop(_selectionRect, y);
            _selectionRect.Width = dx;
            _selectionRect.Height = dy;
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

            if (w < 24 || h < 18) return;
            CreateTextBox(x, y, w, h);
        }

        private void EraseAt(Point p)
        {
            for (int i = _texts.Count - 1; i >= 0; i--)
            {
                var item = _texts[i];
                double left = Canvas.GetLeft(item.Box), top = Canvas.GetTop(item.Box);
                if (p.X >= left && p.X <= left + item.Box.Width && p.Y >= top && p.Y <= top + item.Box.Height)
                {
                    RemoveTextItem(item);
                    return;
                }
            }

            try
            {
                var hit = InkLayer.Strokes.Where(s => s.HitTest(p, Math.Max(4, _penWidth * 1.5))).ToList();
                foreach (var s in hit)
                {
                    InkLayer.Strokes.Remove(s);
                    var stroke = s;
                    _undo.Push(() => InkLayer.Strokes.Add(stroke));
                    _redo.Clear();
                }
            }
            catch { }
        }

        private void CreateTextBox(double x, double y, double w, double h)
        {
            var box = new RichTextBox
            {
                Width = w,
                Height = h,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                Padding = new Thickness(3),
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                FontSize = _fontSize,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Foreground = new SolidColorBrush(_textColor),
                FontWeight = _bold ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = _italic ? FontStyles.Italic : FontStyles.Normal
            };
            box.Document.Blocks.Clear();
            box.Document.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });

            var grip = new Thumb
            {
                Width = 12,
                Height = 12,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                Cursor = Cursors.SizeNWSE
            };

            var item = new TextItem { Box = box, Grip = grip };

            box.GotKeyboardFocus += (s, e) => SetActiveText(item);
            box.PreviewMouseLeftButtonDown += (s, e) => SetActiveText(item);

            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            Canvas.SetLeft(grip, x + w - 6);
            Canvas.SetTop(grip, y + h - 6);
            Canvas.SetZIndex(box, 1);
            Canvas.SetZIndex(grip, 2);
            TextLayer.Children.Add(box);
            TextLayer.Children.Add(grip);
            _texts.Add(item);

            SetActiveText(item);

            grip.DragDelta += (s, e) =>
            {
                double nw = Math.Max(40, box.Width + e.HorizontalChange);
                double nh = Math.Max(24, box.Height + e.VerticalChange);
                box.Width = nw;
                box.Height = nh;
                Canvas.SetLeft(grip, Canvas.GetLeft(box) + nw - 6);
                Canvas.SetTop(grip, Canvas.GetTop(box) + nh - 6);
            };

            _undo.Push(() => RemoveTextItem(item));
            _redo.Clear();

            box.Focus();
        }

        private void SetActiveText(TextItem item)
        {
            if (!ReferenceEquals(_activeText, item)) CommitActiveText();
            _activeText = item;
            if (item != null)
            {
                item.Box.BorderThickness = new Thickness(1);
                item.Grip.Visibility = Visibility.Visible;
                Canvas.SetLeft(item.Grip, Canvas.GetLeft(item.Box) + item.Box.Width - 6);
                Canvas.SetTop(item.Grip, Canvas.GetTop(item.Box) + item.Box.Height - 6);
            }
        }

        /// <summary>固定当前文本框：去掉边框与手柄，文字保留。</summary>
        private void CommitActiveText()
        {
            if (_activeText == null) return;

            try
            {
                _activeText.Box.BorderThickness = new Thickness(0);
                _activeText.Grip.Visibility = Visibility.Collapsed;
            }
            catch { }

            _activeText = null;
        }

        private void RemoveTextItem(TextItem item)
        {
            try
            {
                TextLayer.Children.Remove(item.Box);
                TextLayer.Children.Remove(item.Grip);
            }
            catch { }
            _texts.Remove(item);
            if (ReferenceEquals(_activeText, item)) _activeText = null;
        }

        private void ClearTexts()
        {
            TextLayer.Children.Clear();
            _texts.Clear();
            _activeText = null;
        }

        // ================================================================
        // 撤销 / 恢复
        // ================================================================

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_undo.Count == 0) return;
            var action = _undo.Pop();
            try { action(); } catch { }
            _redo.Push(action);
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0) return;
            var action = _redo.Pop();
            try { action(); } catch { }
            _undo.Push(action);
        }

        // ================================================================
        // 悬浮提示（可显示到窗口外，但不出屏幕）
        // ================================================================

        private HintBubble _roundHint;
        private HintBubble _blurHint;

        private void ChkRoundCover_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_roundHint == null) _roundHint = new HintBubble(ChkRoundCover, "开启后封面预览与主界面封面使用圆角");
            _roundHint.Show(Mouse.GetPosition(ChkRoundCover));
        }

        private void ChkRoundCover_MouseMove(object sender, MouseEventArgs e) => _roundHint?.Show(Mouse.GetPosition(ChkRoundCover));

        private void ChkBlurBackground_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_blurHint == null) _blurHint = new HintBubble(ChkBlurBackground, "开启后将把封面原图模糊后作为主界面背景");
            _blurHint.Show(Mouse.GetPosition(ChkBlurBackground));
        }

        private void ChkBlurBackground_MouseMove(object sender, MouseEventArgs e) => _blurHint?.Show(Mouse.GetPosition(ChkBlurBackground));

        private void ChkHint_MouseLeave(object sender, MouseEventArgs e)
        {
            try { _roundHint?.Hide(); _blurHint?.Hide(); } catch { }
        }

        private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
        {
            try { VideoPreview.Position = TimeSpan.Zero; VideoPreview.Play(); } catch { }
        }
    }
}
