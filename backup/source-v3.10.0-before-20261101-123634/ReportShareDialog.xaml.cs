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

        private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5));
        private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB));
        private static readonly Brush HoverFill = new SolidColorBrush(Color.FromRgb(0xE3, 0xEF, 0xFC));

        /// <summary>复制成功后的气泡文案（多张时会带上序号）。</summary>
        public string CopiedMessage { get; private set; } = "图片文件已复制到剪贴板";

        public ReportShareDialog(BitmapSource image, string savedPath)
            : this(new List<BitmapSource> { image }, new List<string> { savedPath })
        {
        }

        public ReportShareDialog(IList<BitmapSource> images, IList<string> savedPaths)
        {
            if (images != null)
                foreach (var img in images) if (img != null) _images.Add(img);
            if (savedPaths != null)
                foreach (var p in savedPaths) _paths.Add(p ?? "");

            InitializeComponent();

            try
            {
                BuildThumbnails();
                ShowIndex(0);
            }
            catch { }
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
            if (_images.Count > 1)
            {
                string name = string.IsNullOrEmpty(path) ? "未保存" : Path.GetFileName(path);
                TxtSavedPath.Text = $"第 {_index + 1}/{_images.Count} 张：{name}";
            }
            else
            {
                TxtSavedPath.Text = string.IsNullOrEmpty(path) ? "图片已生成" : "图片已保存：" + path;
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

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = _index < _paths.Count ? _paths[_index] : "";
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
