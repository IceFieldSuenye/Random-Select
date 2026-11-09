using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RandomDrawer
{
    /// <summary>
    /// 更新日志（窗口内浮动界面）。
    /// 版本号规则：3.x.y —— x 为内容更新，y 为问题修复，每次对应 +1（y 为 0 时可写作 3.x）。
    /// </summary>
    public partial class ChangelogDialog : FloatingDialogBase
    {
        /// <summary>展开历史版本时再执行：给最新版本条目补上展开 / 折叠箭头。</summary>
        private Action _pendingLatestToggle;

        public ChangelogDialog()
        {
            InitializeComponent();
            BuildLog();
        }

        public static void Show(Window owner)
        {
            var dialog = new ChangelogDialog { Owner = owner };
            dialog.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void BuildLog()
        {
            var versions = LoadVersionsFromResource();
            if (versions.Count == 0)
            {
                AddVersion("更新日志", "读取失败", "", new[] { "未能读取内置的更新日志文件（Readme\\更新日志.txt）。" }, true);
                return;
            }

            // ★ 先只构建当前版本：历史版本有两百多个，一次性排版会让打开更新日志时卡一下。
            //   用户点「展开历史版本」时再列出剩下的（每个版本的内容仍是展开时才填充）。
            //   3.61：初始只有当前版本时条目旁边不显示展开 / 折叠箭头（没别的可看，箭头多余）；
            //        点「展开历史版本」后当前版本照旧保留箭头，可以随时收起自己的内容。
            var latest = versions[0];
            AddVersion(latest.Version, latest.Tag, latest.Date, latest.Lines, true, showToggle: false);

            if (versions.Count <= 1) return;

            var moreText = new TextBlock
            {
                Text = "展开历史版本（" + (versions.Count - 1) + " 个）",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            moreText.SetResourceReference(TextBlock.ForegroundProperty, "Fg_0078D7");

            var moreButton = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 6, 0, 6),
                Child = moreText
            };
            moreButton.SetResourceReference(Border.BackgroundProperty, "Bg_CCE8FF");
            // 悬停时加深（深色主题下变亮一点，否则看不出变化）
            moreButton.MouseEnter += (s, e) =>
            {
                try
                {
                    if (TryFindResource("Bg_CCE8FF") is SolidColorBrush b)
                        moreButton.Background = HoverBrush(b, 22);
                }
                catch { }
            };
            moreButton.MouseLeave += (s, e) =>
                moreButton.SetResourceReference(Border.BackgroundProperty, "Bg_CCE8FF");
            moreButton.MouseLeftButtonUp += (s, e) =>
            {
                LogHost.Children.Remove(moreButton);

                // 3.61：展开历史版本后，最新版本条目也恢复展开 / 折叠箭头
                try
                {
                    _pendingLatestToggle?.Invoke();
                    _pendingLatestToggle = null;
                }
                catch { }

                // 当前版本与历史版本之间加一条分隔线
                var divider = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 10, 0, 2)
                };
                divider.SetResourceReference(Border.BorderBrushProperty, "Bd_E5E7EB");
                divider.SetResourceReference(Border.BackgroundProperty, "Bg_E5E7EB");
                LogHost.Children.Add(divider);

                for (int i = 1; i < versions.Count; i++)
                {
                    var v = versions[i];
                    AddVersion(v.Version, v.Tag, v.Date, v.Lines, false);
                }
                AnimateLogHeight();
            };
            LogHost.Children.Add(moreButton);
        }

        /// <summary>展开历史版本后，把滚动区高度从当前值快速过渡到新内容高度（对话框随之加高）。</summary>
        private void AnimateLogHeight()
        {
            try
            {
                if (LogScroll == null) return;
                double from = LogScroll.ActualHeight > 0 ? LogScroll.ActualHeight : 120;

                LogHost.UpdateLayout();
                LogScroll.UpdateLayout();
                double max = LogScroll.MaxHeight > 0 && !double.IsInfinity(LogScroll.MaxHeight) ? LogScroll.MaxHeight : 430;
                double to = Math.Min(LogScroll.ExtentHeight, max);
                if (to <= from + 1) return;

                LogScroll.Height = from;
                var anim = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    // HoldEnd：动画结束保持目标高度，再在同一帧交回 Auto，避免先跳回原值再恢复
                    FillBehavior = FillBehavior.HoldEnd
                };
                anim.Completed += (s2, e2) =>
                {
                    try
                    {
                        LogScroll.BeginAnimation(FrameworkElement.HeightProperty, null);
                        LogScroll.Height = double.NaN;
                    }
                    catch { }
                };
                LogScroll.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
            catch { }
        }

        /// <summary>悬停加深：浅色主题变暗、深色主题变亮，保证两种主题下都能看出变化。</summary>
        private static SolidColorBrush HoverBrush(SolidColorBrush baseBrush, int delta)
        {
            Color c = baseBrush?.Color ?? Color.FromRgb(0xCC, 0xE8, 0xFF);
            int d = ThemeManager.IsDark ? Math.Abs(delta) : -Math.Abs(delta);
            return new SolidColorBrush(Color.FromRgb(
                (byte)Math.Max(0, Math.Min(255, c.R + d)),
                (byte)Math.Max(0, Math.Min(255, c.G + d)),
                (byte)Math.Max(0, Math.Min(255, c.B + d))));
        }

        /// <summary>单个版本展开 / 收起：内容高度快速过渡（110ms 展开、90ms 收起）。</summary>
        private void AnimateVersionBody(StackPanel body, bool expand)
        {
            try
            {
                if (body == null) return;

                if (expand)
                {
                    body.Visibility = Visibility.Visible;
                    // 用父容器宽度测量，保证测量高度与最终布局一致（否则动画结束时高度会被重排「弹」一下）
                    double width = LogHost != null && LogHost.ActualWidth > 0
                        ? Math.Max(160, LogHost.ActualWidth - body.Margin.Left - body.Margin.Right)
                        : Math.Max(160, body.ActualWidth);
                    body.Measure(new Size(width, double.PositiveInfinity));
                    // DesiredSize 含 Margin，而 Height 只是内容高度：不减掉边距会多展开 10px 再缩回（看起来像弹跳）
                    double target = body.DesiredSize.Height - body.Margin.Top - body.Margin.Bottom;
                    if (target <= 0) return;

                    body.Height = 0;
                    var anim = new DoubleAnimation(0, target, new Duration(TimeSpan.FromMilliseconds(110)))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        // HoldEnd：动画结束保持目标高度，再在同一帧交回 Auto，避免「先跳回 0 再恢复」的弹跳
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    anim.Completed += (s, e) =>
                    {
                        try
                        {
                            body.BeginAnimation(FrameworkElement.HeightProperty, null);
                            body.Height = double.NaN;
                        }
                        catch { }
                    };
                    body.BeginAnimation(FrameworkElement.HeightProperty, anim);
                }
                else
                {
                    double from = body.ActualHeight;
                    if (from <= 0) { body.Visibility = Visibility.Collapsed; return; }
                    var anim = new DoubleAnimation(from, 0, new Duration(TimeSpan.FromMilliseconds(90)))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    anim.Completed += (s, e) =>
                    {
                        try
                        {
                            body.BeginAnimation(FrameworkElement.HeightProperty, null);
                            body.Height = double.NaN;
                            body.Visibility = Visibility.Collapsed;
                        }
                        catch { }
                    };
                    body.BeginAnimation(FrameworkElement.HeightProperty, anim);
                }
            }
            catch { }
        }

        /// <summary>箭头「>」转向下：0° → 90°（同样只花 110ms）。</summary>
        private static void AnimateChevron(RotateTransform rotate, bool expand)
        {
            try
            {
                if (rotate == null) return;
                var anim = new DoubleAnimation(rotate.Angle, expand ? 90 : 0, new Duration(TimeSpan.FromMilliseconds(110)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                rotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            catch { }
        }

        private sealed class ChangelogVersion
        {
            public string Version = "";
            public string Tag = "";
            public string Date = "";
            public string[] Lines = Array.Empty<string>();
        }

        /// <summary>
        /// 更新日志只有一个数据源：Readme\更新日志.txt（内嵌资源）。
        /// 以前是在本文件里维护一份 1300 行的版本数组，发版时必须两处同时改，容易漏。
        /// </summary>
        private static List<ChangelogVersion> LoadVersionsFromResource()
        {
            var list = new List<ChangelogVersion>();
            string text = null;

            try
            {
                var asm = typeof(ChangelogDialog).Assembly;
                string name = null;
                foreach (var n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("更新日志.txt", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                }
                if (name == null) return list;

                using (var stream = asm.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    text = reader.ReadToEnd();
                }
            }
            catch { return list; }

            if (string.IsNullOrEmpty(text)) return list;

            var versionRegex = new Regex(@"^(\d+(?:\.\d+)+)（(.+?)）\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
            var itemRegex = new Regex(@"^\d+\s*[\.、]\s*(.+)$", RegexOptions.Compiled);

            ChangelogVersion current = null;
            List<string> items = null;

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;

                var m = versionRegex.Match(line);
                if (m.Success)
                {
                    if (current != null) { current.Lines = items.ToArray(); list.Add(current); }

                    current = new ChangelogVersion
                    {
                        Version = m.Groups[1].Value,
                        Tag = m.Groups[2].Value.Trim(),
                        Date = m.Groups[3].Value
                    };
                    items = new List<string>();
                    continue;
                }

                if (current == null) continue;

                var im = itemRegex.Match(line);
                if (im.Success) items.Add(im.Groups[1].Value.Trim());
            }

            if (current != null) { current.Lines = items.ToArray(); list.Add(current); }
            return list;
        }
        private void AddVersion(string version, string tag, string date, string[] lines, bool latest, bool showToggle = true)
        {
            var section = new StackPanel
            {
                Margin = new Thickness(0, latest ? 0 : 8, 0, 0)
            };

            var body = new StackPanel
            {
                Margin = new Thickness(12, 2, 0, 8),
                Visibility = latest ? Visibility.Visible : Visibility.Collapsed
            };

            var chevronRotate = new RotateTransform(latest ? 90 : 0);
            var chevron = new TextBlock
            {
                Text = "\uE76C",
                FontFamily = App.IconFontFamily,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = chevronRotate
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "Fg_333333");

            var txtVersion = new TextBlock
            {
                Text = version,
                FontSize = latest ? 18 : 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            txtVersion.SetResourceReference(TextBlock.ForegroundProperty, latest ? "Fg_0078D7" : "Fg_333333");

            var txtTag = new TextBlock
            {
                Text = tag,
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            txtTag.SetResourceReference(TextBlock.ForegroundProperty, "Fg_888888");

            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            titlePanel.Children.Add(txtVersion);
            titlePanel.Children.Add(txtTag);

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(titlePanel);

            if (!string.IsNullOrEmpty(date))
            {
                var txtDate = new TextBlock
                {
                    Text = date,
                    FontSize = 12,
                    Margin = new Thickness(10, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                txtDate.SetResourceReference(TextBlock.ForegroundProperty, "Fg_999999");
                Grid.SetColumn(txtDate, 1);
                headerGrid.Children.Add(txtDate);
            }

            var header = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand,
                Child = headerGrid
            };
            header.SetResourceReference(Border.BackgroundProperty, "Bg_F5F5F5");
            header.MouseEnter += (s, e) => header.SetResourceReference(Border.BackgroundProperty, "Ui.HoverSoft");
            header.MouseLeave += (s, e) => header.SetResourceReference(Border.BackgroundProperty, "Bg_F5F5F5");
            // 内容按需加载：展开这个版本时才把条目加进 body
            bool populated = false;
            Action populate = () =>
            {
                if (populated) return;
                populated = true;
                int itemIndex = 0;
                foreach (var line in lines)
                {
                    itemIndex++;
                    var item = new TextBlock
                    {
                        Text = itemIndex + ".  " + line,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 21,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    item.SetResourceReference(TextBlock.ForegroundProperty, "Fg_333333");
                    body.Children.Add(item);
                }
            };

            Action toggle = () =>
            {
                bool expand = body.Visibility != Visibility.Visible;
                if (expand) populate();
                AnimateVersionBody(body, expand);
                AnimateChevron(chevronRotate, expand);
            };
            MouseButtonEventHandler toggleHandler = (s, e) => toggle();

            if (showToggle)
            {
                // 图标本来就「展开」：插到标题最前面，视觉上与其它版本一致
                titlePanel.Children.Insert(0, chevron);
                header.MouseLeftButtonDown += toggleHandler;
            }
            else
            {
                // 3.61：只有当前版本时没有箭头，也不能点击标题把自己收起来（收起就只剩一行标题）。
                // 展开历史版本时再把箭头和折叠能力补回来。
                _pendingLatestToggle = () =>
                {
                    if (!titlePanel.Children.Contains(chevron)) titlePanel.Children.Insert(0, chevron);
                    header.MouseLeftButtonDown -= toggleHandler;
                    header.MouseLeftButtonDown += toggleHandler;
                };
            }

            if (latest) populate();

            section.Children.Add(header);
            section.Children.Add(body);
            LogHost.Children.Add(section);
        }
    }
}
