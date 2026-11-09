using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
                AddVersion("更新日志", "读取失败", "", new[] { "未能读取内置的更新日志文件（Release\\更新日志.txt）。" }, true);
                return;
            }

            // 文件里最新的在最前面，第一条默认展开
            for (int i = 0; i < versions.Count; i++)
            {
                var v = versions[i];
                AddVersion(v.Version, v.Tag, v.Date, v.Lines, i == 0);
            }
        }

        private sealed class ChangelogVersion
        {
            public string Version = "";
            public string Tag = "";
            public string Date = "";
            public string[] Lines = Array.Empty<string>();
        }

        /// <summary>
        /// 更新日志只有一个数据源：Release\更新日志.txt（内嵌资源）。
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
        private void AddVersion(string version, string tag, string date, string[] lines, bool latest)
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

            var chevron = new TextBlock
            {
                Text = latest ? "\uE70D" : "\uE76C",
                FontFamily = App.IconFontFamily,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
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
            titlePanel.Children.Add(chevron);
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
            header.MouseLeftButtonDown += (s, e) =>
            {
                bool expand = body.Visibility != Visibility.Visible;
                body.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = expand ? "\uE70D" : "\uE76C";
            };

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

            section.Children.Add(header);
            section.Children.Add(body);
            LogHost.Children.Add(section);

            if (latest)
            {
                var divider = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                divider.SetResourceReference(Border.BorderBrushProperty, "Bd_E5E7EB");
                divider.SetResourceReference(Border.BackgroundProperty, "Bg_E5E7EB");
                LogHost.Children.Add(divider);
            }
        }
    }
}
