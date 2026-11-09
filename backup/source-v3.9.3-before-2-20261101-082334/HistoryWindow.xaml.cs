using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RandomDrawer
{
    public partial class HistoryWindow : Window
    {
        private class PoolEntry
        {
            public string DisplayName { get; set; }
            public string Dir { get; set; }
        }
        private List<PoolEntry> _allPoolEntries = new List<PoolEntry>();

        private int _currentPage = 1;
        private int _totalPages = 1;
        private const int PageSize = 10;
        private string _currentPoolDir = "";
        private List<string> _poolHeaders = new List<string>();
        private int _probIndex = -1;
        private int _catIndex = -1;

        private bool _enableStarColor = false;
        private HighlightConfig _highlight = null;

        // 隐藏列名：分类值直接放在 DataTable 中，随行排序
        private const string CategoryColumnName = "_Category";

        public HistoryWindow(string defaultPoolName, string poolDir, HighlightConfig highlight = null)
        {
            InitializeComponent();
            _currentPoolDir = poolDir;

            // ★ 订阅 AutoGeneratingColumn：隐藏 _Category 列
            HistoryGrid.AutoGeneratingColumn += HistoryGrid_AutoGeneratingColumn;

            LoadPoolComboBox();

            int selectedIndex = -1;
            if (!string.IsNullOrEmpty(poolDir))
                selectedIndex = _allPoolEntries.FindIndex(p => p.Dir == poolDir);
            if (selectedIndex < 0 && !string.IsNullOrEmpty(defaultPoolName))
                selectedIndex = _allPoolEntries.FindIndex(p => p.DisplayName == defaultPoolName
                                                              || Path.GetFileName(p.Dir) == defaultPoolName);

            if (selectedIndex >= 0) ComboPools.SelectedIndex = selectedIndex;
            else if (ComboPools.Items.Count > 0) ComboPools.SelectedIndex = 0;
        }

        // ★ 隐藏 _Category 列
        private void HistoryGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == CategoryColumnName)
            {
                e.Cancel = true;
            }
        }

        private void LoadPoolComboBox()
        {
            ComboPools.Items.Clear();
            _allPoolEntries.Clear();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var baseDir in App.SearchDirectories)
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var subDir in Directory.GetDirectories(baseDir).OrderBy(d => d))
                {
                    string poolName = Path.GetFileName(subDir);
                    string uniqueName = poolName;
                    int suffix = 2;
                    while (usedNames.Contains(uniqueName))
                    {
                        uniqueName = $"{poolName} [{suffix}]";
                        suffix++;
                    }
                    usedNames.Add(uniqueName);

                    _allPoolEntries.Add(new PoolEntry { DisplayName = uniqueName, Dir = subDir });
                    ComboPools.Items.Add(uniqueName);
                }
            }
        }

        // ================================================================
        // 切换卡池：完全重置并重新读取当前卡池的高亮配置
        // ================================================================
        private void ComboPools_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboPools.SelectedIndex < 0 || ComboPools.SelectedIndex >= _allPoolEntries.Count) return;

            _currentPoolDir = _allPoolEntries[ComboPools.SelectedIndex].Dir;

            _highlight = null;
            _enableStarColor = false;

            try
            {
                string gachaFile = Path.Combine(_currentPoolDir, "gacha_settings.ini");
                if (File.Exists(gachaFile))
                {
                    var gs = GachaSettings.LoadSettingsFromFile(gachaFile);
                    if (gs != null)
                    {
                        _enableStarColor = gs.EnableStarColor;
                        _highlight = HighlightConfig.FromSettings(gs);
                    }
                }
            }
            catch
            {
                _highlight = null;
                _enableStarColor = false;
            }

            _currentPage = 1;
            LoadPage();
        }

        private void LoadRealHeaders()
        {
            _poolHeaders.Clear();
            _probIndex = -1;
            _catIndex = -1;

            string lockFile = Path.Combine(_currentPoolDir, "data.lock");
            string txtFile = Path.Combine(_currentPoolDir, "data.txt");
            string content = "";

            try
            {
                if (File.Exists(lockFile)) content = CryptoUtils.DecryptString(File.ReadAllText(lockFile));
                else if (File.Exists(txtFile)) content = CryptoUtils.ReadAllTextWithEncoding(txtFile);

                if (!string.IsNullOrEmpty(content))
                {
                    var firstLine = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (firstLine != null)
                    {
                        _poolHeaders = firstLine.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        _probIndex = _poolHeaders.IndexOf("概率");
                        _catIndex = _poolHeaders.IndexOf("分类");
                    }
                }
            }
            catch { }

            if (_poolHeaders.Count == 0)
            {
                _poolHeaders.Add("字段1");
                _poolHeaders.Add("字段2");
            }
        }

        // ================================================================
        // ★ 空状态切换
        // ================================================================
        private void SetEmptyStateVisible(bool visible)
        {
            if (EmptyHistoryPanel == null || HistoryGrid == null) return;
            EmptyHistoryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            HistoryGrid.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowEmptyPage()
        {
            HistoryGrid.ItemsSource = null;
            TxtPage.Text = "第 1 页 / 共 1 页";
            _totalPages = 1;
            _currentPage = 1;
            SetEmptyStateVisible(true);
        }

        private void LoadPage()
        {
            LoadRealHeaders();

            string historyFile = Path.Combine(_currentPoolDir, "history.txt");
            if (!File.Exists(historyFile))
            {
                ShowEmptyPage();
                return;
            }

            List<string> allLines;
            try
            {
                allLines = File.ReadAllLines(historyFile).Reverse().ToList();
            }
            catch
            {
                ShowEmptyPage();
                return;
            }

            if (allLines.Count == 0)
            {
                ShowEmptyPage();
                return;
            }

            // 有记录：显示表格
            SetEmptyStateVisible(false);

            _totalPages = (int)Math.Ceiling((double)allLines.Count / PageSize);
            if (_totalPages == 0) _totalPages = 1;
            if (_currentPage > _totalPages) _currentPage = _totalPages;
            if (_currentPage < 1) _currentPage = 1;

            var pageLines = allLines.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();

            var parsedRows = new List<Tuple<string, string, List<string>, string>>();
            int maxDataCols = 0;

            foreach (var line in pageLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string time = "";
                string type = "";
                var fieldParts = new List<string>();

                if (line.Contains("|"))
                {
                    var pipeParts = line.Split('|').ToList();

                    if (pipeParts.Count > 0 && DateTime.TryParse(pipeParts[0], out _))
                    {
                        time = pipeParts[0];
                        pipeParts.RemoveAt(0);
                    }

                    if (pipeParts.Count > 0 && pipeParts[0].Contains("抽取"))
                    {
                        type = pipeParts[0];
                        pipeParts.RemoveAt(0);
                    }

                    foreach (var p in pipeParts)
                        fieldParts.AddRange(p.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries));
                }
                else
                {
                    fieldParts.AddRange(line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries));
                }

                // 在去概率列之前提取分类值
                string category = "";
                if (_catIndex >= 0 && _catIndex < fieldParts.Count)
                    category = (fieldParts[_catIndex] ?? "").Trim();

                if (_probIndex >= 0 && _probIndex < fieldParts.Count)
                    fieldParts.RemoveAt(_probIndex);

                maxDataCols = Math.Max(maxDataCols, fieldParts.Count);
                parsedRows.Add(Tuple.Create(time, type, fieldParts, category));
            }

            DataTable dt = new DataTable();
            dt.Columns.Add("抽取时间", typeof(string));
            dt.Columns.Add("抽取类型", typeof(string));

            for (int i = 0; i < _poolHeaders.Count; i++)
            {
                if (i == _probIndex) continue;
                string colName = _poolHeaders[i];
                if (dt.Columns.Contains(colName)) colName = $"{colName}_{i}";
                dt.Columns.Add(colName, typeof(string));
            }

            int displayedHeaderCount = _poolHeaders.Count - (_probIndex >= 0 ? 1 : 0);
            for (int i = displayedHeaderCount; i < maxDataCols; i++)
            {
                dt.Columns.Add($"列{i + 1}", typeof(string));
            }

            // ★ 隐藏列：分类值
            dt.Columns.Add(CategoryColumnName, typeof(string));

            foreach (var t in parsedRows)
            {
                DataRow dr = dt.NewRow();
                dr[0] = t.Item1;
                dr[1] = t.Item2;

                var fields = t.Item3;
                for (int i = 0; i < fields.Count; i++)
                {
                    int colIndex = 2 + i;
                    if (colIndex < dt.Columns.Count - 1)  // 减 1 是因为最后一列是隐藏列
                        dr[colIndex] = fields[i];
                }

                // ★ 分类值直接放在行数据里，排序后仍能正确对应
                dr[CategoryColumnName] = t.Item4 ?? "";

                dt.Rows.Add(dr);
            }

            HistoryGrid.ItemsSource = dt.DefaultView;
            TxtPage.Text = $"第 {_currentPage} 页 / 共 {_totalPages} 页";
        }

        // ================================================================
        // ★ 行着色：从 DataRowView 读取分类值（排序后仍正确）
        // ================================================================
        private void HistoryGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            try
            {
                var rowView = e.Row.Item as DataRowView;
                string category = "";
                if (rowView != null && rowView.DataView.Table.Columns.Contains(CategoryColumnName))
                    category = rowView[CategoryColumnName]?.ToString() ?? "";

                // 1. 高亮（按分类列）
                if (_highlight != null && _highlight.Enabled)
                {
                    var brush = _highlight.GetBrushByCategory(category);
                    if (brush != null)
                        e.Row.Foreground = brush;
                    else
                        try { e.Row.ClearValue(DataGridRow.ForegroundProperty); } catch { }
                }
                else
                {
                    try { e.Row.ClearValue(DataGridRow.ForegroundProperty); } catch { }
                }

                // 2. 兼容旧的"星卡背景色"
                if (_enableStarColor && !string.IsNullOrEmpty(category))
                {
                    int starCount = 0;
                    foreach (char c in category) if (c == '★' || c == '⭐') starCount++;

                    if (starCount == 5)
                        e.Row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF3B0"));
                    else if (starCount == 4)
                        e.Row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8D5F5"));
                    else
                        e.Row.Background = null;
                }
                else
                {
                    e.Row.Background = null;
                }
            }
            catch
            {
                e.Row.Background = null;
                try { e.Row.ClearValue(DataGridRow.ForegroundProperty); } catch { }
            }
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1) { _currentPage--; LoadPage(); }
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages) { _currentPage++; LoadPage(); }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPoolDir)) return;

            if (CustomMessageBox.Show(this, "确定要清空当前卡池的历史记录和保底状态吗？",
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string historyFile = Path.Combine(_currentPoolDir, "history.txt");
            if (File.Exists(historyFile)) File.Delete(historyFile);

            string gachaStateFile = Path.Combine(_currentPoolDir, "gacha_state.ini");
            if (File.Exists(gachaStateFile)) File.Delete(gachaStateFile);

            _currentPage = 1;
            LoadPage();
        }
    }
}