using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 抽取报告分享预览：展示生成的长图（一张或多张），并提供「打开文件夹」「复制到剪贴板」「确定」。
    /// 多张时下方出现方形缩略图 + 左右切换按钮，复制只针对当前显示的那张。
    /// </summary>
    public partial class ReportShareDialog : FloatingDialogBase
    {
        private readonly List<BitmapSource> _images = new List<BitmapSource>();
        private readonly List<string> _paths = new List<string>();
        private readonly List<Border> _thumbs = new List<Border>();
        private int _index;

        /// <summary>PDF 模式下的文件路径（非空表示这次预览的是生成好的 PDF）。</summary>
        private readonly string _pdfPath;

        // 缩略图边框 / 悬停底色都跟主题走：深色模式用亮色边框会变成刺眼的白框
        private static Brush ThemedBrush(string key, Color fallback)
        {
            try
            {
                var brush = ThemeManager.GetBrush(key);
                if (brush != null) return brush;
            }
            catch { }
            return new SolidColorBrush(fallback);
        }

        private Brush SelectedBorder => ThemedBrush("Ui.AccentFill", Color.FromRgb(0x1E, 0x88, 0xE5));
        private Brush NormalBorder => ThemedBrush("Bd_E5E7EB", Color.FromRgb(0xE5, 0xE7, 0xEB));
        private Brush HoverFill => ThemedBrush("Ui.HoverSoft", Color.FromRgb(0xE3, 0xEF, 0xFC));

        /// <summary>复制成功后的气泡文案（多张时会带上序号）。</summary>
        public string CopiedMessage { get; private set; } = "图片文件已复制到剪贴板";

        /// <summary>用户这次是否真的点过复制（关掉弹窗不算，避免误弹「已复制」提示）。</summary>
        public bool Copied { get; private set; }

        public ReportShareDialog(BitmapSource image, string savedPath, string pdfPath = null)
            : this(new List<BitmapSource> { image }, new List<string> { savedPath }, pdfPath)
        {
        }

        public ReportShareDialog(IList<BitmapSource> images, IList<string> savedPaths, string pdfPath = null)
        {
            _pdfPath = pdfPath ?? "";

            if (images != null)
                foreach (var img in images) if (img != null) _images.Add(img);
            if (savedPaths != null)
                foreach (var p in savedPaths) _paths.Add(p ?? "");

            InitializeComponent();

            try
            {
                if (IsPdfMode) Title = "预览抽取报告 PDF";
                BuildPreviewContextMenu();
                BuildThumbnails();
                ShowIndex(0);
            }
            catch { }
        }

        private bool IsPdfMode => !string.IsNullOrEmpty(_pdfPath);

        // ================================================================
        // 预览图右键菜单：复制（C）/ 使用系统图片查看器打开（O）
        // ================================================================
        private void BuildPreviewContextMenu()
        {
            try
            {
                if (PreviewImage == null) return;

                var copyItem = new MenuItem
                {
                    Header = "复制（C）",
                    Icon = MenuIcon("\uE8C8")
                };
                copyItem.Click += (s, e) => CopyCurrentImage();

                var openItem = new MenuItem
                {
                    Header = IsPdfMode ? "使用系统查看器打开（O）" : "使用系统图片查看器打开（O）",
                    Icon = MenuIcon("\uEB9F")
                };
                openItem.Click += (s, e) => OpenCurrentWithSystemViewer();

                var menu = new ContextMenu();
                menu.Items.Add(copyItem);
                menu.Items.Add(openItem);

                // 与全局右键菜单同一套主题（深色 / 浅色都跟着变）
                menu.Opened += (s, e) => ApplyMenuTheme(menu);
                menu.PreviewKeyDown += (s, e) =>
                {
                    try
                    {
                        if (e.Key == Key.C) { copyItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); e.Handled = true; }
                        else if (e.Key == Key.O) { openItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); e.Handled = true; }
                    }
                    catch { }
                };

                _previewMenu = menu;
                AttachPreviewMenu(PreviewImage);
                AttachPreviewMenu(PreviewBorder);
            }
            catch { }
        }

        private ContextMenu _previewMenu;

        /// <summary>给元素挂上预览图右键菜单（缩略图也用同一个菜单，右键时先切到那张）。</summary>
        private void AttachPreviewMenu(FrameworkElement element, int? thumbIndex = null)
        {
            try
            {
                if (element == null || _previewMenu == null) return;

                element.ContextMenu = _previewMenu;
                if (thumbIndex.HasValue)
                {
                    int index = thumbIndex.Value;
                    element.ContextMenuOpening += (s, e) => ShowIndex(index);
                }
            }
            catch { }
        }

        /// <summary>右键菜单图标（跟随主题色）。</summary>
        private static TextBlock MenuIcon(string glyph)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = App.IconFontFamily,
                FontSize = 14,
                Foreground = ThemeManager.GetBrush("Fg_666666"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static void ApplyMenuTheme(ContextMenu menu)
        {
            try
            {
                bool modern = App.UseModernContextMenu;
                menu.Style = menu.TryFindResource(modern ? "Win11ContextMenuStyle" : "LegacyContextMenuStyle") as Style;

                var itemStyle = menu.TryFindResource(modern ? "Win11MenuItemStyle" : "LegacyMenuItemStyle") as Style;
                if (itemStyle == null) return;

                foreach (object item in menu.Items)
                {
                    if (item is MenuItem mi)
                    {
                        mi.Style = itemStyle;
                        mi.CommandTarget = menu.PlacementTarget;
                    }
                }
            }
            catch { }
        }

        /// <summary>复制当前显示的这张图片（右键菜单）；PDF 模式下复制的是 PDF 文件本身。</summary>
        private void CopyCurrentImage()
        {
            try
            {
                if (IsPdfMode && File.Exists(_pdfPath))
                {
                    Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _pdfPath });
                    Copied = true;
                    CopiedMessage = "PDF 已复制到剪贴板";
                    ToastNotification.ShowSuccess(Window.GetWindow(this), CopiedMessage);
                    return;
                }

                string path = _index < _paths.Count ? _paths[_index] : "";
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var files = new System.Collections.Specialized.StringCollection { path };
                    Clipboard.SetFileDropList(files);
                }
                else if (_images.Count > 0)
                {
                    Clipboard.SetImage(_images[Math.Min(_index, _images.Count - 1)]);
                }

                Copied = true;
                CopiedMessage = _images.Count > 1 ? $"已复制第 {_index + 1} 张图片" : "图片已复制到剪贴板";
                ToastNotification.ShowSuccess(Window.GetWindow(this), CopiedMessage);
            }
            catch
            {
                try { ToastNotification.ShowWarning(Window.GetWindow(this), "复制失败，请稍后再试"); } catch { }
            }
        }

        /// <summary>用系统默认查看器打开当前内容（图片 / PDF）。</summary>
        private void OpenCurrentWithSystemViewer()
        {
            try
            {
                if (IsPdfMode && File.Exists(_pdfPath))
                {
                    Process.Start(new ProcessStartInfo(_pdfPath) { UseShellExecute = true });
                    return;
                }

                string path = _index < _paths.Count ? _paths[_index] : "";

                // 图片已保存过就直接打开；万一保存失败（例如目录不可写），先落一份临时文件再打开
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    if (_images.Count == 0) return;
                    path = Path.Combine(Path.GetTempPath(),
                        $"抽取报告_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                    var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                    var source = _images[Math.Min(_index, _images.Count - 1)];
                    encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        encoder.Save(fs);
                    }
                }

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                try { ToastNotification.ShowWarning(Window.GetWindow(this), "无法用系统图片查看器打开"); } catch { }
            }
        }

        private void BuildThumbnails()
        {
            if (_images.Count <= 1)
            {
                MultiPanel.Visibility = Visibility.Collapsed;
                return;
            }

            MultiPanel.Visibility = Visibility.Visible;

            for (int i = 0; i < _images.Count; i++)
            {
                int index = i;

                var img = new Image
                {
                    Source = _images[i],
                    Stretch = Stretch.UniformToFill,
                    Width = 52,
                    Height = 52,
                    // ★ 圆角裁剪：Border 的圆角不会裁到子元素，这里给图片自己加圆角几何
                    Clip = new RectangleGeometry(new Rect(0, 0, 52, 52), 3, 3)
                };
                var border = new Border
                {
                    Width = 56,
                    Height = 56,
                    Margin = new Thickness(0, 0, 8, 0),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(2),
                    BorderBrush = NormalBorder,
                    Background = Brushes.Transparent,
                    ClipToBounds = true,
                    Cursor = Cursors.Hand,
                    ToolTip = $"第 {i + 1} 张",
                    Child = img
                };

                border.MouseLeftButtonUp += (s, e) => { ShowIndex(index); };
                AttachPreviewMenu(border, index);
                border.MouseEnter += (s, e) =>
                {
                    if (index != _index)
                    {
                        border.BorderBrush = SelectedBorder;
                        border.Background = HoverFill;
                    }
                };
                border.MouseLeave += (s, e) =>
                {
                    if (index != _index)
                    {
                        border.BorderBrush = NormalBorder;
                        border.Background = Brushes.Transparent;
                    }
                };

                _thumbs.Add(border);
                ThumbPanel.Children.Add(border);
            }
        }

        private void ShowIndex(int index)
        {
            if (_images.Count == 0) return;

            _index = Math.Max(0, Math.Min(_images.Count - 1, index));

            PreviewImage.Source = _images[_index];

            string path = _index < _paths.Count ? _paths[_index] : "";
            // 保存位置（左）+ 第X张/共N张（右）；PDF 模式用「第X页/共N页」
            if (TxtPageIndicator != null)
            {
                if (_images.Count > 1)
                    TxtPageIndicator.Text = IsPdfMode
                        ? $"第{_index + 1}页/共{_images.Count}页"
                        : $"第{_index + 1}张/共{_images.Count}张";
                else
                    TxtPageIndicator.Text = "";
            }

            if (IsPdfMode)
            {
                TxtSavedPath.Text = "PDF 已保存到：" + _pdfPath;
                TxtSavedPath.ToolTip = _pdfPath;
            }
            else if (string.IsNullOrEmpty(path))
            {
                TxtSavedPath.Text = "当前图片尚未保存到磁盘";
            }
            else
            {
                TxtSavedPath.Text = "当前图片已保存到：" + path;
                TxtSavedPath.ToolTip = path;
            }

            for (int i = 0; i < _thumbs.Count; i++)
            {
                bool selected = i == _index;
                _thumbs[i].BorderBrush = selected ? SelectedBorder : NormalBorder;
                _thumbs[i].Background = selected ? HoverFill : Brushes.Transparent;
            }

            if (BtnPrev != null) BtnPrev.IsEnabled = _index > 0;
            if (BtnNext != null) BtnNext.IsEnabled = _index < _images.Count - 1;

            ScrollThumbIntoView();
        }

        /// <summary>把当前选中的缩略图平滑滚动到可视区中间（点箭头切换时让滚动条跟随）。</summary>
        private void ScrollThumbIntoView()
        {
            try
            {
                if (_thumbs.Count == 0 || _thumbs.Count <= _index) return;
                var thumb = _thumbs[_index];

                var sv = FindScrollViewer(ThumbPanel);
                if (sv == null || sv.ViewportWidth <= 0) return;

                var pos = thumb.TransformToAncestor(sv).Transform(new Point(0, 0));
                double target = sv.HorizontalOffset + pos.X - (sv.ViewportWidth - thumb.ActualWidth) / 2;
                target = Math.Max(0, Math.Min(target, sv.ScrollableWidth));

                double from = sv.HorizontalOffset;
                if (Math.Abs(target - from) < 1) return;

                // 用一个短计时器分步滚动，做出平滑移动效果
                int step = 0;
                const int steps = 10;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                timer.Tick += (s, e) =>
                {
                    step++;
                    double k = step / (double)steps;
                    double eased = 1 - Math.Pow(1 - k, 3);
                    try { sv.ScrollToHorizontalOffset(from + (target - from) * eased); } catch { }
                    if (step >= steps) timer.Stop();
                };
                timer.Start();
            }
            catch { }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject node)
        {
            try
            {
                var current = node;
                while (current != null)
                {
                    if (current is ScrollViewer sv) return sv;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch { }
            return null;
        }
        private void Prev_Click(object sender, RoutedEventArgs e) => ShowIndex(_index - 1);

        private void Next_Click(object sender, RoutedEventArgs e) => ShowIndex(_index + 1);

        /// <summary>
        /// 缩略图条支持鼠标滚轮横向滚动（WPF 的 ScrollViewer 默认把滚轮当纵向，这里改横向）。
        /// </summary>
        private void ThumbScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                if (!(sender is ScrollViewer sv)) return;
                if (sv.ScrollableWidth <= 0) return;

                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
            catch { }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = IsPdfMode ? _pdfPath : (_index < _paths.Count ? _paths[_index] : "");
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                    return;
                }

                string dir = !string.IsNullOrEmpty(path)
                    ? Path.GetDirectoryName(path)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Saved Pictures");

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void Share_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // PDF 模式：复制的就是这个 PDF 文件
                if (IsPdfMode && File.Exists(_pdfPath))
                {
                    Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _pdfPath });
                    Copied = true;
                    CopiedMessage = "PDF 已复制到剪贴板";
                    ToastNotification.ShowSuccess(Window.GetWindow(this), CopiedMessage);
                    return;
                }

                // ★ 复制全部图片：多张时把每个已保存的文件都放进剪贴板
                var existing = new System.Collections.Specialized.StringCollection();
                foreach (var p in _paths)
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) existing.Add(p);

                if (existing.Count > 0)
                {
                    Clipboard.SetFileDropList(existing);
                }
                else if (_images.Count > 0)
                {
                    // 保存失败时退回复制图片数据，至少还能粘贴图片
                    Clipboard.SetImage(_images[Math.Min(_index, _images.Count - 1)]);
                }

                Copied = true;
                CopiedMessage = _images.Count > 1
                    ? $"已复制全部 {existing.Count} 张图片"
                    : "图片文件已复制到剪贴板";

                // 复制后弹窗保持打开，方便继续预览/切换
                ToastNotification.ShowSuccess(Window.GetWindow(this), CopiedMessage);
            }
            catch { }
        }


        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
