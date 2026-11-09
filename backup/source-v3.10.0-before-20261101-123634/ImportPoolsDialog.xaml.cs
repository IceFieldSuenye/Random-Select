using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>
    /// 导入卡池：拖入 / 选择 .zip，列出其中的卡池，确认后再真正解压导入。
    /// </summary>
    public partial class ImportPoolsDialog : FloatingDialogBase
    {
        private sealed class ItemRow
        {
            public PoolTransfer.ImportItem Item;
            public CheckBox Check;
        }

        private readonly List<ItemRow> _rows = new List<ItemRow>();
        private readonly List<string> _unrecognized = new List<string>();
        private bool _busy;

        public int ImportedCount { get; private set; }
        public string FirstImportedDir { get; private set; }

        public ImportPoolsDialog(string targetDir)
        {
            InitializeComponent();

            // 目录下拉：与「修改卡池」界面一致，可从已保存的卡池目录中选择，也可手动输入
            try
            {
                foreach (var dir in App.SearchDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    if (!App.IsValidDirectoryPath(dir)) continue;
                    TargetDirBox.Items.Add(dir);
                }
            }
            catch { }

            TargetDirBox.Text = targetDir ?? "";

            // 打开时不要让目录框里的文字呈现全选高亮
            Loaded += (s, e) =>
            {
                try { System.Windows.Input.Keyboard.ClearFocus(); } catch { }
            };

            UpdateUi();
        }

        // ============================================================
        // 目录选择
        // ============================================================

        private string GetTargetDir()
        {
            string dir = (TargetDirBox.Text ?? "").Trim().Trim('"');
            return dir;
        }

        private bool ValidateTargetDir(out string error)
        {
            error = null;
            string dir = GetTargetDir();

            if (string.IsNullOrWhiteSpace(dir))
            {
                error = "请选择或输入导入目录。";
                return false;
            }

            try { Path.GetFullPath(dir); }
            catch { error = "目录路径无效。"; return false; }

            if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                error = "目录路径包含非法符号，请修改后再继续。以下符号不能使用：" + PoolTextRules.InvalidFileNameCharsText;
                return false;
            }

            return true;
        }

        private void BrowseTargetDir_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var dialog = new OpenFolderDialog
            {
                Title = "选择卡池导入目录",
                Multiselect = false
            };

            string current = GetTargetDir();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(OwnerWindow) == true)
            {
                TargetDirBox.Text = dialog.FolderName;
                UpdateUi();
            }
        }

        // ============================================================
        // 拖放
        // ============================================================

        private static bool HasFiles(DragEventArgs e)
        {
            return e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void DropArea_DragEnter(object sender, DragEventArgs e) => HandleDrag(e);

        private void DropArea_DragOver(object sender, DragEventArgs e) => HandleDrag(e);

        private void HandleDrag(DragEventArgs e)
        {
            if (_busy)
            {
                e.Effects = DragDropEffects.None;
                HideInvalidTip();
                e.Handled = true;
                return;
            }

            if (DragContainsZip(e))
            {
                e.Effects = DragDropEffects.Copy;
                SetDropHighlight(true);
                HideInvalidTip();
            }
            else
            {
                // 不是 .zip：光标变成禁止，并在光标右上角提示
                e.Effects = DragDropEffects.None;
                SetDropHighlight(false);
                if (HasFiles(e)) ShowInvalidTip(e);
                else HideInvalidTip();
            }

            e.Handled = true;
        }

        private static bool DragContainsZip(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;

            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return false;

            return paths.Any(p => !string.IsNullOrWhiteSpace(p)
                                  && p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        private Popup _invalidPopup;

        private void ShowInvalidTip(DragEventArgs e)
        {
            try
            {
                if (_invalidPopup == null)
                {
                    var text = new TextBlock
                    {
                        Text = "不是正确的卡池压缩包文件",
                        FontSize = 12
                    };
                    text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.TitleText");

                    var border = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 6, 10, 6),
                        Child = text,
                        IsHitTestVisible = false
                    };
                    border.SetResourceReference(Border.BackgroundProperty, "Ui.CardBg");
                    border.SetResourceReference(Border.BorderBrushProperty, "Ui.CardBorder");

                    _invalidPopup = new Popup
                    {
                        Child = border,
                        AllowsTransparency = true,
                        StaysOpen = true,
                        IsHitTestVisible = false,
                        Focusable = false,
                        Placement = PlacementMode.Relative,
                        PlacementTarget = DropArea
                    };
                }

                // 每次拖动事件都更新位置，保证提示紧跟鼠标（ToolTip 在拖放循环里不会跟随）
                if (e != null)
                {
                    // 用相对 DropArea 的 DIP 坐标（PositionToScreen 返回物理像素，会被 DPI 放大导致偏移过大）
                    Point pos = e.GetPosition(DropArea);
                    _invalidPopup.HorizontalOffset = pos.X + 16;
                    _invalidPopup.VerticalOffset = pos.Y - 40;
                }

                if (!_invalidPopup.IsOpen) _invalidPopup.IsOpen = true;
            }
            catch { }
        }
        private void HideInvalidTip()
        {
            try { if (_invalidPopup != null && _invalidPopup.IsOpen) _invalidPopup.IsOpen = false; } catch { }
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropArea();
            HideInvalidTip();
        }

        private bool? _dropHighlight;

        private void SetDropHighlight(bool highlight)
        {
            if (_dropHighlight == highlight) return;
            _dropHighlight = highlight;
            try
            {
                DropArea.SetResourceReference(Border.BorderBrushProperty, highlight ? "Bd_0078D7" : "Bd_CCCCCC");
                DropArea.SetResourceReference(Border.BackgroundProperty, highlight ? "Bg_E5F3FF" : "Bg_F9FAFB");
            }
            catch { }
        }

        private void ResetDropArea()
        {
            SetDropHighlight(false);
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            ResetDropArea();
            HideInvalidTip();
            if (_busy) return;

            if (HasFiles(e))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null) AddFiles(paths);
            }
            e.Handled = true;
        }

        private void PickFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var dialog = new OpenFileDialog
            {
                Title = "选择卡池压缩包",
                Filter = "卡池压缩包 (*.zip)|*.zip",
                Multiselect = true
            };

            if (dialog.ShowDialog(OwnerWindow) == true)
                AddFiles(dialog.FileNames);
        }

        // ============================================================
        // 解析文件
        // ============================================================

        private void AddFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (Directory.Exists(path))
                {
                    _unrecognized.Add($"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}：暂不支持直接导入文件夹，请先压缩为 .zip");
                    continue;
                }

                if (!File.Exists(path)) continue;

                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    _unrecognized.Add($"{Path.GetFileName(path)}：不是 .zip 卡池压缩包");
                    continue;
                }

                if (_rows.Any(r => string.Equals(r.Item.ZipPath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var items = PoolTransfer.InspectZip(path, out string error);
                if (!string.IsNullOrEmpty(error))
                {
                    _unrecognized.Add($"{Path.GetFileName(path)}：{error}");
                    RuntimeLog.Error($"导入卡池识别失败：{path}，{error}");
                    continue;
                }

                RuntimeLog.Info($"导入卡池识别：{path}，可导入={items.Count}");
                if (items.Count == 0)
                {
                    _unrecognized.Add($"{Path.GetFileName(path)}：压缩包里没有可导入的内容");
                    RuntimeLog.Error($"导入卡池为空：{path}");
                    continue;
                }

                foreach (var item in items) AddRow(item);
            }

            UpdateUi();
        }

        private void AddRow(PoolTransfer.ImportItem item)
        {
            var nameText = new TextBlock
            {
                Text = item.Name,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            string infoText = item.IsValid
                ? (item.ItemCount >= 0 ? $"{item.ItemCount} 个对象" : "加密卡池")
                : "无法识别：" + item.Reason;

            var info = new TextBlock
            {
                Text = infoText,
                FontSize = 12,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = item.IsValid
                    ? ThemeManager.GetBrush("Fg_888888")
                    : ThemeManager.GetThemedBrushFromHex("#D32F2F"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(nameText);
            content.Children.Add(info);

            var check = new CheckBox
            {
                Content = content,
                IsChecked = item.IsValid,
                IsEnabled = item.IsValid,
                Margin = new Thickness(0, 6, 0, 6)
            };
            check.Checked += (s, e) => UpdateUi();
            check.Unchecked += (s, e) => UpdateUi();

            ItemHost.Children.Add(check);
            _rows.Add(new ItemRow { Item = item, Check = check });
        }

        // ============================================================
        // 状态
        // ============================================================

        private List<PoolTransfer.ImportItem> GetCheckedItems()
        {
            return _rows.Where(r => r.Check.IsChecked == true && r.Item.IsValid)
                        .Select(r => r.Item)
                        .ToList();
        }

        private void UpdateUi()
        {
            int checkedCount = GetCheckedItems().Count;

            TxtListTitle.Text = _rows.Count == 0
                ? "待导入的卡池："
                : $"待导入的卡池：已选 {checkedCount} / 共 {_rows.Count} 个";

            if (!_busy)
                BtnImport.IsEnabled = checkedCount > 0 && !string.IsNullOrWhiteSpace(GetTargetDir());
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnImport.IsEnabled = !busy && GetCheckedItems().Count > 0;
            BtnCancel.IsEnabled = !busy;
            DropArea.IsEnabled = !busy;
            TargetDirBox.IsEnabled = !busy;

            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TxtStatus.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Progress.Value = 0;
            TxtStatus.Text = busy ? "准备中…" : "";

            if (!busy) UpdateUi();
        }

        // ============================================================
        // 导入
        // ============================================================

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var items = GetCheckedItems();
            if (items.Count == 0)
            {
                CustomMessageBox.Show(OwnerWindow, "请先添加并勾选要导入的卡池。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ValidateTargetDir(out string targetError))
            {
                CustomMessageBox.Show(OwnerWindow, targetError, "导入目录无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetDir = GetTargetDir();

            SetBusy(true);

            var progress = new Progress<(int done, int total, string name)>(p =>
            {
                Progress.Value = p.total <= 0 ? 0 : p.done * 100.0 / p.total;
                TxtStatus.Text = $"正在导入（{p.done}/{p.total}）：{p.name}";
            });

            PoolTransfer.ImportResult result = null;
            try
            {
                result = await Task.Run(() => PoolTransfer.Import(
                    items,
                    targetDir,
                    (done, total, name) => ((IProgress<(int, int, string)>)progress).Report((done, total, name))));
            }
            catch (Exception ex)
            {
                result = new PoolTransfer.ImportResult();
                result.Failed.Add(ex.Message);
            }

            SetBusy(false);

            ImportedCount = result?.Imported ?? 0;
            FirstImportedDir = result?.ImportedDirs?.FirstOrDefault();

            // ---- 全部操作结束后统一汇总 ----
            var message = new StringBuilder();
            message.AppendLine($"成功导入 {ImportedCount} 个卡池。");
            message.AppendLine("导入到：" + targetDir);
            RuntimeLog.Info($"导入卡池完成：成功={ImportedCount}，失败={result?.Failed?.Count ?? 0}，目标={targetDir}");

            var failed = result?.Failed ?? new List<string>();
            if (failed.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("以下卡池导入失败：");
                foreach (var line in failed.Take(12)) message.AppendLine("· " + line);
                if (failed.Count > 12) message.AppendLine($"· …… 另有 {failed.Count - 12} 条");
            }

            if (_unrecognized.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("以下文件无法识别，已跳过：");
                foreach (var line in _unrecognized.Take(12)) message.AppendLine("· " + line);
                if (_unrecognized.Count > 12) message.AppendLine($"· …… 另有 {_unrecognized.Count - 12} 条");
            }

            bool hasProblem = failed.Count > 0 || _unrecognized.Count > 0;
            CustomMessageBox.Show(
                OwnerWindow,
                message.ToString().TrimEnd(),
                ImportedCount > 0 ? "导入完成" : "导入失败",
                MessageBoxButton.OK,
                hasProblem ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (ImportedCount > 0) DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            DialogResult = false;
        }
    }
}
