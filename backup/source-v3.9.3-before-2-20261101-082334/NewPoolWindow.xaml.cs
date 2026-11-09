using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RandomDrawer
{
    public partial class NewPoolWindow : Window
    {
        private string _selectedCoverPath = "";
        private string _editPoolName = null;
        private string _poolAbsoluteDir = "";    // 卡池完整路径
        private bool _isSyncing = false;
        private bool _isCoverChanged = false;

        private static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg" };

        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".jfif" };

        // ================================================================
        // 构造函数：新建
        // ================================================================
        public NewPoolWindow()
        {
            InitializeComponent();

            // ★ 填充下拉列表（已保存的卡池目录）
            PopulateSaveLocationCombo();

            // 选择默认父目录（优先第一个扫描目录，且可写）
            string defaultParent = App.SearchDirectories.FirstOrDefault() ?? Path.Combine(App.BaseDir, "SelectBox");
            if (!App.IsDirectoryWritable(defaultParent))
                defaultParent = App.GetFallbackPoolDir();

            SaveLocationBox.Text = defaultParent;

            InputBox.Text = "抽取对象 分类\n示例1 1\n示例2 2";
        }

        // ================================================================
        // 构造函数：编辑已有卡池
        // ================================================================
        public NewPoolWindow(string poolName, string poolDir)
        {
            InitializeComponent();

            // ★ 填充下拉列表
            PopulateSaveLocationCombo();

            _editPoolName = poolName;
            _poolAbsoluteDir = poolDir;

            this.Title = "修改卡池";
            PoolNameBox.Text = poolName;
            SaveLocationBox.Text = Path.GetDirectoryName(poolDir);

            string lockFile = Path.Combine(_poolAbsoluteDir, "data.lock");
            string txtFile = Path.Combine(_poolAbsoluteDir, "data.txt");

            if (File.Exists(lockFile))
            {
                ChkLock.IsChecked = true;
                try { InputBox.Text = CryptoUtils.DecryptString(File.ReadAllText(lockFile)); } catch { }
            }
            else if (File.Exists(txtFile))
            {
                ChkLock.IsChecked = false;
                try { InputBox.Text = CryptoUtils.ReadAllTextWithEncoding(txtFile); } catch { }
            }

            bool hasStrategy = HasActiveGachaStrategy();
            bool hasProbColumn = HasProbabilityColumn();

            if (hasStrategy || hasProbColumn)
            {
                _isSyncing = true;
                ChkProbability.IsChecked = true;
                _isSyncing = false;

                BtnAdvanced.Visibility = Visibility.Visible;

                if (hasStrategy) UpdateActiveStrategyDisplay();
            }

            InputBox_TextChanged(null, null);

            try
            {
                var covers = Directory.GetFiles(_poolAbsoluteDir, "cover.*");
                if (covers.Length > 0)
                {
                    _selectedCoverPath = covers[0];
                    TxtCoverPath.Text = Path.GetFileName(_selectedCoverPath);
                }
            }
            catch { }
        }

        // ================================================================
        // ★ 填充保存位置下拉列表：来自 App.SearchDirectories
        // ================================================================
        private void PopulateSaveLocationCombo()
        {
            try
            {
                SaveLocationBox.Items.Clear();
                foreach (var dir in App.SearchDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    SaveLocationBox.Items.Add(dir);
                }
            }
            catch { }
        }

        // ================================================================
        // ★ 路径工具
        // ================================================================
        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            try
            {
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static bool PathEquals(string a, string b)
        {
            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 确保路径以 "SelectBox" 结尾：若末级目录不是 SelectBox，则自动追加一级子目录。
        /// 例如：D:\MyPools → D:\MyPools\SelectBox
        ///       D:\MyPools\SelectBox → D:\MyPools\SelectBox（不重复追加）
        /// </summary>
        private static string EnsureSelectBoxSuffix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            try
            {
                string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // 处理盘符根路径（如 "C:" → "C:\"）
                if (trimmed.Length == 2 && trimmed[1] == ':')
                    trimmed += Path.DirectorySeparatorChar;

                if (string.IsNullOrEmpty(trimmed)) return path;

                string leaf = Path.GetFileName(trimmed);
                if (string.Equals(leaf, "SelectBox", StringComparison.OrdinalIgnoreCase))
                    return trimmed;

                return Path.Combine(trimmed, "SelectBox");
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// 如果指定目录尚未在 App.SearchDirectories 中，则添加并保存设置。
        /// </summary>
        private static void EnsureSearchDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;

            try
            {
                string normalized = NormalizePath(dir);

                foreach (var existing in App.SearchDirectories)
                {
                    if (PathEquals(existing, normalized)) return;
                }

                App.SearchDirectories.Add(dir);
                App.SaveSettings();
            }
            catch { }
        }

        // ================================================================
        // ★ 浏览保存位置
        // ================================================================
        private void BrowseSaveLocation_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择卡池的保存位置",
                Multiselect = false
            };

            string cur = (SaveLocationBox.Text ?? "").Trim();
            if (Directory.Exists(cur))
            {
                try { dlg.InitialDirectory = cur; } catch { }
            }

            if (dlg.ShowDialog() != true) return;

            string newDir = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(newDir)) return;

            // 自动在用户选择的目录后追加 SelectBox 子目录
            newDir = EnsureSelectBoxSuffix(newDir);

            // 写入前先检测目录是否可写
            if (!App.IsDirectoryWritable(newDir))
            {
                CustomMessageBox.Show(this,
                    "所选目录不可写，请重新选择。\n\n" + newDir + "\n\n" +
                    "可能的原因：\n" +
                    "  • 该目录属于系统保护目录（如 Program Files）\n" +
                    "  • 当前用户没有该目录的写权限\n\n" +
                    "建议选择：文档、用户目录或 D 盘下的自定义目录。",
                    "目录不可写", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveLocationBox.Text = newDir;
        }

        // ================================================================
        // ★ 目录移动：跨卷自动降级为"复制 + 删除"
        // ================================================================
        private static bool TryMoveDirectory(string sourceDir, string destDir, out string errorMsg)
        {
            errorMsg = null;

            // 1. 优先尝试 Directory.Move（同卷时最快）
            try
            {
                Directory.Move(sourceDir, destDir);
                return true;
            }
            catch (Exception)
            {
                // 忽略，继续尝试跨卷方案
            }

            // 2. 跨卷（或 Move 失败）：递归复制 + 删除源目录
            bool copyOk = false;
            try
            {
                CopyDirectoryRecursive(sourceDir, destDir);
                copyOk = true;
            }
            catch (Exception ex)
            {
                // 复制失败 → 清理目标目录残留，避免误删
                try { if (Directory.Exists(destDir)) Directory.Delete(destDir, true); } catch { }
                errorMsg = "移动卡池失败，请检查磁盘空间或目录权限。\n\n详细信息：" + ex.Message;
                return false;
            }

            if (copyOk)
            {
                try { Directory.Delete(sourceDir, true); }
                catch
                {
                    // 源目录删除失败：不影响卡池已移动，只是残留旧文件夹
                    // 静默处理，用户可以稍后手动删除
                }
            }
            return true;
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectoryRecursive(subDir, destSubDir);
            }
        }

        // ================================================================
        // 高级规则辅助
        // ================================================================
        private bool HasActiveGachaStrategy()
        {
            try
            {
                string file = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                if (!File.Exists(file)) return false;
                var s = GachaSettings.LoadSettingsFromFile(file);
                return s != null && s.Enabled;
            }
            catch { return false; }
        }

        private void UpdateActiveStrategyDisplay()
        {
            try
            {
                string file = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                if (File.Exists(file))
                {
                    var s = GachaSettings.LoadSettingsFromFile(file);
                    if (s != null && s.Enabled)
                    {
                        TxtActiveStrategy.Text = $"已启用抽卡规则：{s.PresetName}";
                        TxtActiveStrategy.Visibility = Visibility.Visible;
                        return;
                    }
                }
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
            }
            catch { TxtActiveStrategy.Visibility = Visibility.Collapsed; }
        }

        private bool HasProbabilityColumn()
        {
            var lines = InputBox.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;
            var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
            return firstLineParts.Contains("概率");
        }

        private Dictionary<string, List<string>> BuildCategoryMap()
        {
            var map = new Dictionary<string, List<string>>();
            var dtView = PreviewGrid.ItemsSource as DataView;
            if (dtView == null) return map;

            DataTable dt = dtView.Table;
            if (!dt.Columns.Contains("分类")) return map;

            string objectColumn = dt.Columns[0].ColumnName;

            foreach (DataRow row in dt.Rows)
            {
                string category = row["分类"]?.ToString() ?? "";
                string obj = row[objectColumn]?.ToString() ?? "";
                if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(obj)) continue;

                if (!map.ContainsKey(category))
                    map[category] = new List<string>();
                if (!map[category].Contains(obj))
                    map[category].Add(obj);
            }
            return map;
        }

        private string LoadOriginalPoolContent()
        {
            try
            {
                string lockFile = Path.Combine(_poolAbsoluteDir, "data.lock");
                string txtFile = Path.Combine(_poolAbsoluteDir, "data.txt");
                if (File.Exists(lockFile))
                    return CryptoUtils.DecryptString(File.ReadAllText(lockFile));
                else if (File.Exists(txtFile))
                    return CryptoUtils.ReadAllTextWithEncoding(txtFile);
            }
            catch { }
            return null;
        }

        private string NormalizeContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        private bool HasPoolContentChanged()
        {
            string original = LoadOriginalPoolContent();
            if (original == null) return false;
            return NormalizeContent(original) != NormalizeContent(InputBox.Text);
        }

        // ================================================================
        // 校验方法
        // ================================================================
        private bool ValidateDuplicateObjects(DataTable dt)
        {
            if (dt == null || dt.Columns.Count == 0) return true;

            string objectColumn = dt.Columns[0].ColumnName;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();

            foreach (DataRow row in dt.Rows)
            {
                string obj = (row[objectColumn]?.ToString() ?? "").Trim();
                if (string.IsNullOrEmpty(obj)) continue;

                if (!seen.Add(obj))
                {
                    if (!duplicates.Contains(obj)) duplicates.Add(obj);
                }
            }

            if (duplicates.Count == 0) return true;

            int show = Math.Min(duplicates.Count, 20);
            string listText = string.Join("\n", duplicates.Take(show).Select(x => "  • " + x));
            if (duplicates.Count > show) listText += $"\n  ...（共 {duplicates.Count} 个）";

            CustomMessageBox.Show(this,
                $"第一列“{objectColumn}”中存在重复的对象，请修改后再保存：\n\n" + listText +
                "\n\n提示：每一行的对象必须唯一（不区分大小写）。",
                "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);

            return false;
        }

        private bool ValidateCategoryColumn(DataTable dt)
        {
            if (dt == null || !dt.Columns.Contains("分类")) return true;

            string objectColumn = dt.Columns[0].ColumnName;
            var missing = new List<string>();

            foreach (DataRow row in dt.Rows)
            {
                string cat = (row["分类"]?.ToString() ?? "").Trim();
                if (!string.IsNullOrEmpty(cat)) continue;

                string obj = (row[objectColumn]?.ToString() ?? "").Trim();
                if (string.IsNullOrEmpty(obj)) continue;

                missing.Add(obj);
            }

            if (missing.Count == 0) return true;

            int show = Math.Min(missing.Count, 20);
            string listText = string.Join("\n", missing.Take(show).Select(x => "  • " + x));
            if (missing.Count > show) listText += $"\n  ...（共 {missing.Count} 个）";

            var r = CustomMessageBox.Show(this,
                "以下对象未填写“分类”列，启用高级规则时将被舍弃：\n\n" + listText +
                "\n\n是否仍要继续保存？",
                "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            return r == MessageBoxResult.Yes;
        }

        private bool ValidateAdvancedSettingsAfterEdit(Dictionary<string, List<string>> categoryMap)
        {
            string gachaFile = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
            if (!File.Exists(gachaFile)) return true;

            var settings = GachaSettings.LoadSettingsFromFile(gachaFile);
            if (settings == null || !settings.Enabled) return true;

            if (!HasPoolContentChanged()) return true;

            string reason = GetAdvancedSettingsInvalidReason(settings, categoryMap);

            if (reason != null)
            {
                var r = CustomMessageBox.Show(this,
                    "卡池内容已修改，现有高级规则不再适用：\n\n  " + reason +
                    "\n\n是否重置高级规则？\n（重置后需重新点击“高级”按钮配置后才能继续使用）",
                    "高级规则失效", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (r != MessageBoxResult.Yes) return false;

                try { File.Delete(gachaFile); } catch { }
                try
                {
                    string stateFile = Path.Combine(_poolAbsoluteDir, "gacha_state.ini");
                    if (File.Exists(stateFile)) File.Delete(stateFile);
                }
                catch { }

                TxtActiveStrategy.Visibility = Visibility.Collapsed;
                BtnAdvanced.Visibility = Visibility.Collapsed;
                _isSyncing = true;
                ChkProbability.IsChecked = false;
                _isSyncing = false;

                return true;
            }

            var r2 = CustomMessageBox.Show(this,
                "卡池内容已被修改。由于当前卡池已启用高级规则，原有规则配置可能与新数据不匹配。\n\n" +
                "建议先点击“高级”按钮重新配置规则后再保存。\n\n" +
                "是否仍要保存当前修改？",
                "高级规则提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            return r2 == MessageBoxResult.Yes;
        }

        private string GetAdvancedSettingsInvalidReason(GachaSettings settings,
            Dictionary<string, List<string>> categoryMap)
        {
            if (string.IsNullOrEmpty(settings.FiveStarCategory) || !categoryMap.ContainsKey(settings.FiveStarCategory))
                return $"5星角色分类“{settings.FiveStarCategory}”不存在或已被清空。";

            if (categoryMap[settings.FiveStarCategory].Count < 2)
                return $"5星角色分类“{settings.FiveStarCategory}”下的对象少于 2 个。";

            if (!string.IsNullOrEmpty(settings.FiveStarUpObject) &&
                !categoryMap[settings.FiveStarCategory].Contains(settings.FiveStarUpObject))
                return $"UP5星角色“{settings.FiveStarUpObject}”已不存在。";

            if (string.IsNullOrEmpty(settings.FourStarCategory) || !categoryMap.ContainsKey(settings.FourStarCategory))
                return $"4星角色分类“{settings.FourStarCategory}”不存在或已被清空。";

            if (categoryMap[settings.FourStarCategory].Count < 2)
                return $"4星角色分类“{settings.FourStarCategory}”下的对象少于 2 个。";

            var up4 = (settings.FourStarUpObjects ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            foreach (var up in up4)
            {
                if (!categoryMap[settings.FourStarCategory].Contains(up))
                    return $"UP4星角色“{up}”已不存在。";
            }
            if (up4.Count > categoryMap[settings.FourStarCategory].Count - 1)
                return $"UP4星角色数量超过了当前4星分类下的对象总数 - 1。";

            bool has3Star = categoryMap.Any(kvp =>
                kvp.Key != settings.FiveStarCategory &&
                kvp.Key != settings.FourStarCategory &&
                kvp.Value.Count > 0);
            if (!has3Star)
                return "卡池中不存在可用的“3星”分类。";

            return null;
        }

        // ================================================================
        // 高级按钮
        // ================================================================
        private void BtnAdvanced_Click(object sender, RoutedEventArgs e)
        {
            var map = BuildCategoryMap();
            if (map.Count == 0)
            {
                CustomMessageBox.Show(this, "必须先创建“分类”列并填写数据，才能使用高级设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string settingsFile = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
            var initial = File.Exists(settingsFile) ? GachaSettings.LoadSettingsFromFile(settingsFile) : null;

            var win = new AdvancedGachaWindow(initial, map, _poolAbsoluteDir) { Owner = this };
            if (win.ShowDialog() == true)
            {
                var afterSave = GachaSettings.LoadSettingsFromFile(settingsFile);
                if (afterSave != null && afterSave.Enabled)
                {
                    RemoveProbabilityColumnSilently();
                    UpdateActiveStrategyDisplay();

                    _isSyncing = true;
                    ChkProbability.IsChecked = true;
                    _isSyncing = false;
                    BtnAdvanced.Visibility = Visibility.Visible;
                }
                else
                {
                    TxtActiveStrategy.Visibility = Visibility.Collapsed;
                    BtnAdvanced.Visibility = Visibility.Collapsed;

                    _isSyncing = true;
                    ChkProbability.IsChecked = false;
                    _isSyncing = false;
                }
            }
        }

        private void RemoveProbabilityColumnSilently()
        {
            try
            {
                var lines = InputBox.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (lines.Count == 0) return;

                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                if (!firstLineParts.Contains("概率")) return;

                int probIndex = Array.IndexOf(firstLineParts, "概率");
                var newLines = new List<string>();
                for (int i = 0; i < lines.Count; i++)
                {
                    var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (probIndex >= 0 && probIndex < parts.Count) parts.RemoveAt(probIndex);
                    newLines.Add(string.Join(" ", parts));
                }

                _isSyncing = true;
                InputBox.Text = string.Join(Environment.NewLine, newLines);
                _isSyncing = false;

                InputBox_TextChanged(null, null);
            }
            catch { }
        }

        // ================================================================
        // 封面选择
        // ================================================================
        private void SelectCover_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm" +
                         "|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" +
                         "|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm" +
                         "|所有文件|*.*",
                FilterIndex = 1
            };

            if (ofd.ShowDialog() == true)
            {
                string ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
                if (!VideoExts.Contains(ext) && !ImageExts.Contains(ext))
                {
                    CustomMessageBox.Show(this,
                        $"不支持的文件类型：{ext}\n\n" +
                        "支持的图片格式：png / jpg / jpeg / gif / bmp / webp\n" +
                        "支持的视频格式：mp4 / avi / mkv / mov / wmv / m4v / webm\n\n" +
                        "封面未被修改。",
                        "不支持的格式", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _selectedCoverPath = ofd.FileName;
                TxtCoverPath.Text = Path.GetFileName(_selectedCoverPath);
                _isCoverChanged = true;
            }
        }

        private void SelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "文本文件|*.txt|所有文件|*.*" };
            if (ofd.ShowDialog() == true)
            {
                try { InputBox.Text = CryptoUtils.ReadAllTextWithEncoding(ofd.FileName); }
                catch (Exception ex) { CustomMessageBox.Show(this, "读取文件失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
        }

        // ================================================================
        // 概率勾选
        // ================================================================
        private void ChkProbability_Checked(object sender, RoutedEventArgs e)
        {
            BtnAdvanced.Visibility = Visibility.Visible;

            if (_isSyncing) return;
            RebuildTextWithProbability(false);
        }

        private void ChkProbability_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isSyncing) return;

            if (HasActiveGachaStrategy())
            {
                _isSyncing = true;
                ChkProbability.IsChecked = true;
                _isSyncing = false;

                CustomMessageBox.Show(this,
                    "当前卡池已启用高级抽卡规则。\n\n" +
                    "如需禁用，请点击“高级”按钮进入高级设置界面，取消勾选“启用”后点击确定。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!HasProbabilityColumn())
            {
                BtnAdvanced.Visibility = Visibility.Collapsed;
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
                return;
            }

            var result = CustomMessageBox.Show(this, "是否同时删除‘概率’列？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                RebuildTextWithProbability(true);
                BtnAdvanced.Visibility = Visibility.Collapsed;
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
            }
            else
            {
                _isSyncing = true;
                ChkProbability.IsChecked = true;
                _isSyncing = false;
                BtnAdvanced.Visibility = Visibility.Visible;
            }
        }

        private void RebuildTextWithProbability(bool removeProbColumn)
        {
            if (_isSyncing) return;
            var lines = InputBox.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count == 0) return;

            bool textChanged = false;

            if (ChkProbability.IsChecked == true)
            {
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                if (!firstLineParts.Contains("概率"))
                {
                    lines[0] = lines[0].TrimEnd() + " 概率";
                    textChanged = true;
                }
            }
            else if (removeProbColumn)
            {
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                if (firstLineParts.Contains("概率"))
                {
                    var newLines = new List<string>();
                    int probIndex = Array.IndexOf(firstLineParts, "概率");
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        if (probIndex >= 0 && probIndex < parts.Count) parts.RemoveAt(probIndex);
                        newLines.Add(string.Join(" ", parts));
                    }
                    lines = newLines;
                    textChanged = true;
                }
            }

            if (textChanged)
            {
                _isSyncing = true;
                InputBox.Text = string.Join(Environment.NewLine, lines);
                _isSyncing = false;

                InputBox_TextChanged(null, null);
            }
        }

        // ================================================================
        // 预览网格同步
        // ================================================================
        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncing) return;
            _isSyncing = true;
            try
            {
                string raw = InputBox.Text;
                var lines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                DataTable dt = new DataTable();
                if (lines.Count == 0) { PreviewGrid.ItemsSource = null; return; }

                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                int columnCount = firstLineParts.Length;
                if (columnCount == 0) { PreviewGrid.ItemsSource = null; return; }

                for (int i = 0; i < columnCount; i++)
                {
                    string header = firstLineParts[i];
                    if (string.IsNullOrEmpty(header)) header = $"列{i + 1}";
                    if (dt.Columns.Contains(header)) header = $"{header}_{i}";
                    dt.Columns.Add(header, typeof(string));
                }

                for (int i = 1; i < lines.Count; i++)
                {
                    var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    DataRow row = dt.NewRow();
                    for (int j = 0; j < columnCount; j++) row[j] = j < parts.Length ? parts[j] : "";
                    dt.Rows.Add(row);
                }
                PreviewGrid.ItemsSource = dt.DefaultView;
            }
            catch { }
            finally { _isSyncing = false; }
        }

        private void PreviewGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_isSyncing) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isSyncing = true;
                try
                {
                    var dtView = PreviewGrid.ItemsSource as DataView;
                    if (dtView == null) return;
                    DataTable dt = dtView.Table;
                    StringBuilder sb = new StringBuilder();

                    sb.AppendLine(string.Join(" ", dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName)));
                    foreach (DataRow row in dt.Rows)
                        sb.AppendLine(string.Join(" ", row.ItemArray.Select(i => i?.ToString() ?? "")));
                    InputBox.Text = sb.ToString().Trim();
                }
                finally { _isSyncing = false; }
            }), DispatcherPriority.Background);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private bool TryParseProbabilityValue(string raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) { value = 0; return true; }

            string s = raw.Trim();
            bool hasPercent = s.EndsWith("%");
            if (hasPercent) s = s.TrimEnd('%').Trim();

            if (!double.TryParse(s, out double v)) return false;

            if (hasPercent) v = v / 100.0;
            value = v;
            return true;
        }

        // ================================================================
        // ★ Create_Click：保存卡池
        // ================================================================
        private void Create_Click(object sender, RoutedEventArgs e)
        {
            // ---------- 1. 基础校验 ----------
            string poolName = (PoolNameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(poolName))
            {
                CustomMessageBox.Show(this, "请输入卡池名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (poolName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                CustomMessageBox.Show(this, "名称包含非法字符！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dtView = PreviewGrid.ItemsSource as DataView;
            if (dtView == null || dtView.Table.Rows.Count == 0)
            {
                CustomMessageBox.Show(this, "抽取对象不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ---------- 2. 校验保存位置 ----------
            string saveLocation = (SaveLocationBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(saveLocation))
            {
                CustomMessageBox.Show(this, "请选择保存位置！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 手动输入的路径同样应用 SelectBox 后缀，保持与浏览按钮行为一致
            saveLocation = EnsureSelectBoxSuffix(saveLocation);

            if (!App.IsDirectoryWritable(saveLocation))
            {
                CustomMessageBox.Show(this,
                    "保存位置不可写：\n\n" + saveLocation + "\n\n" +
                    "可能的原因：\n" +
                    "  • 该目录属于系统保护目录（如 Program Files）\n" +
                    "  • 当前用户没有该目录的写权限\n\n" +
                    "请点击“浏览”选择一个用户可写的位置（如 文档 或 D 盘）。",
                    "保存位置不可写", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ---------- 3. 数据校验 ----------
            if (!ValidateDuplicateObjects(dtView.Table)) return;

            if (ChkProbability.IsChecked == true
                && !HasActiveGachaStrategy()
                && HasProbabilityColumn()
                && dtView.Table.Columns.Contains("概率"))
            {
                DataTable dt = dtView.Table;
                double sum = 0;
                foreach (DataRow row in dt.Rows)
                {
                    string probStr = row["概率"]?.ToString() ?? "";
                    if (!TryParseProbabilityValue(probStr, out double prob))
                    {
                        CustomMessageBox.Show(this,
                            $"概率值“{probStr}”不是有效的数字！\n\n" +
                            "请填写以下任一种格式：\n" +
                            "  • 小数：0.5\n" +
                            "  • 百分数：50%",
                            "校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    sum += prob;
                }
                if (Math.Abs(sum - 1.0) > 0.001)
                {
                    CustomMessageBox.Show(this, $"概率之和必须等于 1（或 100%），当前概率之和为：{sum}！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            if (!ValidateCategoryColumn(dtView.Table)) return;

            if (_editPoolName != null)
            {
                var categoryMap = BuildCategoryMap();
                if (!ValidateAdvancedSettingsAfterEdit(categoryMap)) return;
            }

            bool isLocked = ChkLock.IsChecked == true;
            string rawText = InputBox.Text.Trim();

            // ---------- 4. 计算最终卡池路径 ----------
            string finalPoolDir = Path.Combine(saveLocation, poolName);

            // ---------- 5. 若"重命名 + 锁定"则提前验证原密码（修复密码迁移 Bug） ----------
            string renamedPassword = null;
            if (_editPoolName != null && isLocked && !PathEquals(_poolAbsoluteDir, finalPoolDir))
            {
                string originalLockFile = Path.Combine(_poolAbsoluteDir, "data.lock");
                if (File.Exists(originalLockFile))
                {
                    var pwd = new PasswordDialog($"卡池已重命名为“{poolName}”，请输入原密码以保留锁定状态") { Owner = this };
                    if (pwd.ShowDialog() != true) return;
                    if (!CryptoUtils.VerifyPassword(_editPoolName, pwd.Password))
                    {
                        CustomMessageBox.Show(this, "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    renamedPassword = pwd.Password;
                }
            }

            // ---------- 6. 创建目录 / 移动卡池 ----------
            if (_editPoolName == null)
            {
                // 新建
                if (Directory.Exists(finalPoolDir))
                {
                    CustomMessageBox.Show(this, "该位置已存在相同名称的卡池！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                try
                {
                    Directory.CreateDirectory(finalPoolDir);
                    _poolAbsoluteDir = finalPoolDir;
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(this, "创建卡池目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                // 编辑：可能重命名，也可能移动位置
                if (!PathEquals(_poolAbsoluteDir, finalPoolDir))
                {
                    if (Directory.Exists(finalPoolDir))
                    {
                        CustomMessageBox.Show(this, "目标位置已存在相同名称的卡池！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    string moveError;
                    if (!TryMoveDirectory(_poolAbsoluteDir, finalPoolDir, out moveError))
                    {
                        CustomMessageBox.Show(this,
                            "移动卡池失败：\n\n" + moveError,
                            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    _poolAbsoluteDir = finalPoolDir;
                }
            }

            bool wasLocked = File.Exists(Path.Combine(_poolAbsoluteDir, "data.lock"));

            // ---------- 7. 写入数据 ----------
            try
            {
                // 封面处理
                if (_isCoverChanged && !string.IsNullOrEmpty(_selectedCoverPath) && File.Exists(_selectedCoverPath))
                {
                    string ext = Path.GetExtension(_selectedCoverPath).ToLowerInvariant();
                    if (VideoExts.Contains(ext) || ImageExts.Contains(ext))
                    {
                        var oldCovers = Directory.GetFiles(_poolAbsoluteDir, "cover.*");
                        foreach (var oldCover in oldCovers) File.Delete(oldCover);
                        File.Copy(_selectedCoverPath, Path.Combine(_poolAbsoluteDir, $"cover{ext}"), true);
                    }
                }

                string dataFile = isLocked ? "data.lock" : "data.txt";
                string dataPath = Path.Combine(_poolAbsoluteDir, dataFile);

                if (isLocked)
                {
                    if (!wasLocked)
                    {
                        // 新建锁定卡池：提示设置密码
                        var pwdDialog = new PasswordDialog("设置锁定密码", true) { Owner = this };
                        if (pwdDialog.ShowDialog() == true)
                        {
                            File.WriteAllText(dataPath, CryptoUtils.EncryptString(rawText));
                            CryptoUtils.SavePasswordHash(poolName, pwdDialog.Password);

                            if (!string.IsNullOrEmpty(pwdDialog.SecretQuestion))
                            {
                                try
                                {
                                    string qaContent = pwdDialog.SecretQuestion + "\u0001" + pwdDialog.SecretAnswer;
                                    File.WriteAllText(
                                        Path.Combine(_poolAbsoluteDir, "secret_qa.lock"),
                                        CryptoUtils.EncryptString(qaContent));
                                }
                                catch { }
                            }
                        }
                        else return;
                    }
                    else
                    {
                        // 已锁定：原地更新内容
                        File.WriteAllText(dataPath, CryptoUtils.EncryptString(rawText));

                        // ★ 修复：重命名锁定的卡池 → 迁移密码哈希到新名称
                        if (!string.IsNullOrEmpty(renamedPassword))
                        {
                            try
                            {
                                CryptoUtils.SavePasswordHash(poolName, renamedPassword);
                                CryptoUtils.DeletePasswordHash(_editPoolName);
                            }
                            catch { }
                        }
                    }

                    if (File.Exists(Path.Combine(_poolAbsoluteDir, "data.txt")))
                        File.Delete(Path.Combine(_poolAbsoluteDir, "data.txt"));
                }
                else
                {
                    File.WriteAllText(dataPath, rawText);

                    if (wasLocked)
                    {
                        // 取消锁定：删除 data.lock、密码哈希、密保文件
                        if (File.Exists(Path.Combine(_poolAbsoluteDir, "data.lock")))
                            File.Delete(Path.Combine(_poolAbsoluteDir, "data.lock"));

                        try { CryptoUtils.DeletePasswordHash(poolName); } catch { }
                        if (_editPoolName != null && !PathEquals(_editPoolName, poolName))
                        {
                            try { CryptoUtils.DeletePasswordHash(_editPoolName); } catch { }
                        }

                        try
                        {
                            string qaFile = Path.Combine(_poolAbsoluteDir, "secret_qa.lock");
                            if (File.Exists(qaFile)) File.Delete(qaFile);
                        }
                        catch { }
                    }
                }

                if (ChkProbability.IsChecked != true && !HasActiveGachaStrategy())
                {
                    string gachaFile = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                    if (File.Exists(gachaFile)) File.Delete(gachaFile);
                }

                // ---------- 8. 把保存位置加入卡池扫描列表 ----------
                EnsureSearchDirectory(saveLocation);

                CustomMessageBox.Show(this, _editPoolName == null ? $"卡池“{poolName}”创建成功！" : "修改成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this,
                    "保存卡池失败：\n\n" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}