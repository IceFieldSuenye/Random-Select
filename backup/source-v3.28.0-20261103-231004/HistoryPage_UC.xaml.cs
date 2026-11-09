using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    public partial class HistoryPage_UC : UserControl, IPageAnimation
    {
        private class PoolEntry
        {
            public string DisplayName { get; set; }
            public string Dir { get; set; }
        }
        private List<PoolEntry> _allPoolEntries = new List<PoolEntry>();

        private bool _showAll;
        private int _currentPage = 1;

        public void PlayEnterAsReverseAnimation(Action onComplete)
        {
            PlayEnterAnimation(onComplete);
        }
        private int _totalPages = 1;
        private const int PageSize = 10;
        private string _currentPoolDir = "";
        private List<string> _poolHeaders = new List<string>();
        private int _probIndex = -1;
        private int _catIndex = -1;
        private DataView _historyView;
        private DataTable _historyTable;
        private string _historySearchText = "";
        // 点击表头排序后，关键词只在这一列里搜索；空表示搜索全部列
        private string _searchColumn = null;
        /// <summary>搜索框上的提示气泡（与选项页窗口标题共用 <see cref="HintBubble"/>）。</summary>
        private HintBubble _searchHint;

        private bool _enableStarColor = false;
        private HighlightConfig _highlight = null;

        private const string CategoryColumnName = "_Category";

        private double _lastEnterFromX = 0;
        private double _lastEnterFromY = 0;

        private Action<AppMessages.PageActivated> _onPageActivated;
        private Action<AppMessages.PoolListChanged> _onPoolListChanged;

        public HistoryPage_UC()
        {
            InitializeComponent();

            try { _showAll = IniHelper.ReadValue("UI", "HistoryShowAll", "0", App.SettingsFile) == "1"; } catch { }
            Loaded += (s, e) =>
            {
                UpdateViewModeButton();
                UpdateReportButtonVisibility();
            };

            HistoryGrid.AutoGeneratingColumn += HistoryGrid_AutoGeneratingColumn;

            // 行虚拟化后布局仍会变化，靠 LayoutUpdated 保证滚动条状态与范围始终最新
            HistoryGrid.LayoutUpdated += (s, e) =>
            {
                try
                {
                    if (_gridScroll == null) AttachGridScrollBar();
                    else UpdateGridScrollBar();
                }
                catch { }
            };

            _onPageActivated = OnPageActivated;
            _onPoolListChanged = OnPoolListChanged;
            MessageBus.Subscribe(_onPageActivated);
            MessageBus.Subscribe(_onPoolListChanged);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            // 搜索框里按 Esc：取消「仅搜索某一列」，恢复搜索全部列
            SearchHistory.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _searchColumn != null)
                {
                    ClearSearchColumnScope();
                    e.Handled = true;
                }
            };
        }

        // 进场动画期间先不做渲染，等动画播完再填数据（优先保证动画流畅）
        private bool _enterAnimating;
        private (List<Tuple<string, string, List<string>, string>> Rows, int MaxCols, bool Heavy)? _pendingRender;
        private string _historyColumnSignature;

        public void PlayEnterAnimation(Action onComplete)
        {
            double fromX = 0, fromY = 0;
            var src = NavigationContext.CurrentSource;

            if (src == PageEnterSource.Main) fromY = 60;
            else fromX = 80;

            _lastEnterFromX = fromX;
            _lastEnterFromY = fromY;

            _enterAnimating = true;
            Action done = () =>
            {
                _enterAnimating = false;
                var pending = _pendingRender;
                _pendingRender = null;
                if (pending.HasValue) RenderHistoryPage(pending.Value.Rows, pending.Value.MaxCols, pending.Value.Heavy);
                onComplete?.Invoke();
            };

            if (src == PageEnterSource.Main)
                SlideInAnimationHelper.PlaySlideIn(this, fromX, fromY, TimeSpan.FromMilliseconds(420), done);
            else
                SlideInAnimationHelper.PlaySlideIn(this, fromX, fromY, done);
        }

        public void PlayExitAnimation(Action onComplete)
        {
            SlideInAnimationHelper.PlaySlideOut(this, _lastEnterFromX, _lastEnterFromY, onComplete);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadPoolComboBox();
            UpdateNoPoolState();
            RestoreLastSelection();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            MessageBus.Unsubscribe(_onPageActivated);
            MessageBus.Unsubscribe(_onPoolListChanged);
            HideSearchHint();

            // 页面缓存会长期保留实例：离开时释放大表格，避免历史记录多时占用持续增长
            try
            {
                if (HistoryGrid != null) HistoryGrid.ItemsSource = null;
                _historyTable?.Clear();
                _historyTable = null;
                _historyView = null;
            }
            catch { }
        }

        private void OnPageActivated(AppMessages.PageActivated msg)
        {
            if (msg == null || msg.PageKey != "History") return;
            LoadPoolComboBox();
            UpdateNoPoolState();
            RestoreLastSelection();
        }

        private void OnPoolListChanged(AppMessages.PoolListChanged msg)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                LoadPoolComboBox();
                UpdateNoPoolState();
                RestoreLastSelection();
            }));
        }

        // 历史记录行缓存：同一个文件（时间戳/大小未变）不重复读取与反转
        private List<string> _cachedLines;
        private string _cachedLinesKey;

        // 重建卡池下拉框时屏蔽选择事件，避免反复触发整页解析
        private bool _suppressPoolSelection;

        private void RestoreLastSelection()
        {
            int idx = -1;
            if (!string.IsNullOrEmpty(App.LastSelectedPoolDir))
                idx = _allPoolEntries.FindIndex(p => p.Dir == App.LastSelectedPoolDir);
            if (idx < 0 && !string.IsNullOrEmpty(App.LastSelectedPoolName))
                idx = _allPoolEntries.FindIndex(p => p.DisplayName == App.LastSelectedPoolName
                                                  || Path.GetFileName(p.Dir) == App.LastSelectedPoolName);

            if (idx >= 0)
            {
                // 已经停在这个卡池且表格还在，就不必再解析一次（进入页面时会被多个入口重复调用）
                if (ComboPools.SelectedIndex == idx && _historyTable != null
                    && string.Equals(_currentPoolDir, _allPoolEntries[idx].Dir, StringComparison.OrdinalIgnoreCase))
                    return;

                ComboPools.SelectedIndex = idx;
            }
            else if (ComboPools.Items.Count > 0)
            {
                if (ComboPools.SelectedIndex == 0 && _historyTable != null) return;
                ComboPools.SelectedIndex = 0;
            }
        }

        private void HistoryGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == CategoryColumnName) e.Cancel = true;
        }

        private void LoadPoolComboBox()
        {
            bool restore = _suppressPoolSelection;
            _suppressPoolSelection = true;
            try
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
            finally
            {
                _suppressPoolSelection = restore;
            }
        }

        /// <summary>没有任何卡池时，整页显示居中的空状态。</summary>
        private void UpdateNoPoolState()
        {
            if (NoPoolPanel == null) return;
            bool hasPools = _allPoolEntries != null && _allPoolEntries.Count > 0;
            NoPoolPanel.Visibility = hasPools ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ComboPools_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPoolSelection) return;
            if (ComboPools.SelectedIndex < 0 || ComboPools.SelectedIndex >= _allPoolEntries.Count) return;

            _currentPoolDir = _allPoolEntries[ComboPools.SelectedIndex].Dir;

            MessageBus.Publish(new AppMessages.CurrentPoolChanged(_currentPoolDir));

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

        private void SetEmptyStateVisible(bool visible)
        {
            if (EmptyHistoryPanel == null || HistoryGridContainer == null) return;
            EmptyHistoryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            HistoryGridContainer.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowEmptyPage()
        {
            HistoryGrid.ItemsSource = null;
            _historyTable = null;
            _historyView = null;
            if (SearchEmptyPanel != null) SearchEmptyPanel.Visibility = Visibility.Collapsed;
            TxtPage.Text = "第 1 页 / 共 1 页";
            _totalPages = 1;
            _currentPage = 1;
            SetEmptyStateVisible(true);

            // ★ 禁用重置按钮
            if (BtnReset != null) BtnReset.IsEnabled = false;
        }

        private static readonly Brush FiveStarRowBrush =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF3B0"));

        private static readonly Brush FourStarRowBrush =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8D5F5"));

        private void LoadPage()
        {
            LoadRealHeaders();

            string historyFile = Path.Combine(_currentPoolDir, "history.txt");
            if (!File.Exists(historyFile)) { ShowEmptyPage(); return; }

            List<string> allLines;
            string cacheKey = "";
            try
            {
                var info = new FileInfo(historyFile);
                cacheKey = _currentPoolDir + "|" + info.LastWriteTimeUtc.Ticks + ":" + info.Length;
            }
            catch { }

            bool fromCache = _cachedLines != null && !string.IsNullOrEmpty(cacheKey) && _cachedLinesKey == cacheKey;
            if (fromCache)
            {
                allLines = _cachedLines;
            }
            else
            {
                try { allLines = File.ReadAllLines(historyFile).Reverse().ToList(); }
                catch (Exception ex)
                {
                    RuntimeLog.Error(ex, $"读取历史记录失败：{historyFile}");
                    ShowEmptyPage();
                    return;
                }
                _cachedLines = allLines;
                _cachedLinesKey = cacheKey;
            }
            RuntimeLog.Info($"读取历史记录：卡池={_currentPoolDir}，总行数={allLines.Count}，页码={_currentPage}，搜索={_historySearchText}");

            if (allLines.Count == 0) { ShowEmptyPage(); return; }

            SetEmptyStateVisible(false);

            // ★ 启用重置按钮
            if (BtnReset != null) BtnReset.IsEnabled = true;

            int pageSize = _showAll ? Math.Max(1, allLines.Count) : PageSize;
            _totalPages = _showAll ? 1 : (int)Math.Ceiling((double)allLines.Count / PageSize);
            if (_totalPages == 0) _totalPages = 1;
            if (_currentPage > _totalPages) _currentPage = _totalPages;
            if (_currentPage < 1) _currentPage = 1;

            var pageLines = _showAll
                ? allLines.ToList()
                : allLines.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();

            // 只有「第一次真正读文件」且数据量大时才显示加载遮罩；
            // 翻页走缓存，再闪一次遮罩会很难看。
            // 全部显示模式下要一次性解析所有行，即使命中缓存也很重，需要遮罩
            bool heavy = (!fromCache && allLines.Count > 2000)
                         || (_showAll && allLines.Count > 500);
            if (heavy) ShowLoadingOverlay(true);

            var linesSnapshot = pageLines;
            System.Threading.Tasks.Task.Run(() => ParseRows(linesSnapshot))
                .ContinueWith(task =>
                {
                    var parsed = task.IsFaulted
                        ? (Rows: new List<Tuple<string, string, List<string>, string>>(), MaxCols: 0)
                        : task.Result;

                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { RenderHistoryPage(parsed.Rows, parsed.MaxCols, heavy); }
                            catch { if (heavy) ShowLoadingOverlay(false); }
                        }));
                    }
                    catch { }
                });
        }

        /// <summary>把后台解析好的行渲染成表格（必须在 UI 线程执行）。</summary>
        private void RenderHistoryPage(List<Tuple<string, string, List<string>, string>> parsedRows, int maxDataCols, bool heavy)
        {
            // 进场动画还没播完：先缓存结果（遮罩保持显示），动画结束后再一次性填表
            if (_enterAnimating)
            {
                _pendingRender = (parsedRows, maxDataCols, heavy);
                return;
            }

            try
            {
                RenderHistoryPageCore(parsedRows, maxDataCols);
            }
            finally
            {
                if (heavy) ShowLoadingOverlay(false);
            }
        }

        private void RenderHistoryPageCore(List<Tuple<string, string, List<string>, string>> parsedRows, int maxDataCols)
        {
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
                dt.Columns.Add($"列{i + 1}", typeof(string));

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
                    if (colIndex < dt.Columns.Count - 1)
                        dr[colIndex] = fields[i];
                }

                dr[CategoryColumnName] = t.Item4 ?? "";
                dt.Rows.Add(dr);
            }

            BuildHistoryColumns(dt);
            HistoryGrid.ItemsSource = dt.DefaultView;
            _historyTable = dt;
            _historyView = dt.DefaultView;
            // 重新建表后排序状态会丢失，搜索范围也回到「全部列」
            _searchColumn = null;
            ApplyHistorySearch();
            TxtPage.Text = $"第 {_currentPage} 页 / 共 {_totalPages} 页";
        }

        /// <summary>显示/隐藏 Win10 转圈加载层。</summary>
        private void ShowLoadingOverlay(bool show)
        {
            try
            {
                if (LoadingOverlay != null)
                    LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                if (HistorySpinner == null) return;
                if (show)
                {
                    // 圆点按控件尺寸放大（原来是 1px 半径，放在 64px 框里几乎看不见）

                    // 颜色跟主题走，不再固定蓝色

                    HistorySpinner.Start();
                }
                else
                {
                    HistorySpinner.Stop();
                }
            }
            catch { }
        }

        /// <summary>把历史文本行解析成表格行（纯计算，可安全放到后台线程）。</summary>
        private (List<Tuple<string, string, List<string>, string>> Rows, int MaxCols) ParseRows(List<string> lines)
        {
            var rows = new List<Tuple<string, string, List<string>, string>>();
            int maxCols = 0;

            foreach (var line in lines)
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

                string category = "";
                if (_catIndex >= 0 && _catIndex < fieldParts.Count)
                    category = (fieldParts[_catIndex] ?? "").Trim();

                if (_probIndex >= 0 && _probIndex < fieldParts.Count)
                    fieldParts.RemoveAt(_probIndex);

                maxCols = Math.Max(maxCols, fieldParts.Count);
                rows.Add(Tuple.Create(time, type, fieldParts, category));
            }

            return (rows, maxCols);
        }

        private void BuildHistoryColumns(DataTable dt)
        {
            try
            {
                // 列名集合没变时直接复用现有列，翻页就不会因重建列而闪一下
                string signature = string.Join("|", dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
                if (signature == _historyColumnSignature && HistoryGrid.Columns.Count > 0) return;
                _historyColumnSignature = signature;

                HistoryGrid.Columns.Clear();
                foreach (DataColumn col in dt.Columns)
                {
                    // 内部用于高亮/搜索的分类列不单独显示
                    if (string.Equals(col.ColumnName, CategoryColumnName, StringComparison.Ordinal)) continue;

                    HistoryGrid.Columns.Add(new DataGridTextColumn
                    {
                        Header = col.ColumnName,
                        Binding = new System.Windows.Data.Binding("[" + col.ColumnName + "]"),
                        IsReadOnly = true,
                        SortMemberPath = col.ColumnName
                    });
                }
            }
            catch { }
        }

        private void SearchHistory_SearchChanged(string text)
        {
            _historySearchText = text ?? "";
            ApplyHistorySearch();
        }

        /// <summary>
        /// 点击表头排序：记录当前列，关键词只在这一列里搜索（再次点击同一列表头仍保持该列）。
        /// </summary>
        private void HistoryGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            try
            {
                string column = e.Column?.SortMemberPath;
                if (string.IsNullOrWhiteSpace(column) && e.Column?.Header is string header) column = header;
                if (string.IsNullOrWhiteSpace(column)) return;

                _searchColumn = column;
                // 排序由 DataGrid 默认处理，等它排序完再按新范围过滤
                Dispatcher.BeginInvoke(new Action(ApplyHistorySearch),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        /// <summary>取消「仅搜索某一列」并清掉排序箭头。</summary>
        private void ClearSearchColumnScope()
        {
            _searchColumn = null;

            // DataView 的排序是字符串 Sort，置空即取消排序
            try { if (_historyView != null) _historyView.Sort = ""; } catch { }
            try
            {
                if (HistoryGrid?.Columns != null)
                {
                    foreach (var column in HistoryGrid.Columns) column.SortDirection = null;
                }
            }
            catch { }

            ApplyHistorySearch();
        }

        private void SearchHistory_MouseEnter(object sender, MouseEventArgs e) => ShowSearchHint(e.GetPosition(this));

        private void SearchHistory_MouseMove(object sender, MouseEventArgs e) => ShowSearchHint(e.GetPosition(this));

        private void SearchHistory_MouseLeave(object sender, MouseEventArgs e) => HideSearchHint();

        private void EnsureSearchHint()
        {
            if (_searchHint != null) return;
            _searchHint = new HintBubble(this, "将默认搜索列表中全部关键词，点击任意列的表头可设置仅在此列搜索。");
        }

        /// <summary>提示气泡始终跟随鼠标，并保证不越出页面边界。</summary>
        private void ShowSearchHint(Point pos)
        {
            try
            {
                if (IsVisible == false) return;
                EnsureSearchHint();
                _searchHint?.Show(pos);
            }
            catch { }
        }

        private void HideSearchHint()
        {
            try { _searchHint?.Hide(); } catch { }
        }

        private void ApplyHistorySearch()
        {
            if (SearchEmptyPanel == null) return;
            if (_historyView == null)
            {
                SearchEmptyPanel.Visibility = Visibility.Collapsed;
                return;
            }

            string query = (_historySearchText ?? "").Trim();
            if (query.Length == 0)
            {
                try { _historyView.RowFilter = ""; } catch { }
                SearchEmptyPanel.Visibility = Visibility.Collapsed;
                return;
            }

            string escaped = query.Replace("'", "''")
                                  .Replace("[", "[[]")
                                  .Replace("%", "[%]")
                                  .Replace("_", "[_]");
            var stringColumns = _historyTable == null
                ? new List<DataColumn>()
                : _historyTable.Columns.Cast<DataColumn>()
                    .Where(c => c.DataType == typeof(string)).ToList();

            if (stringColumns.Count == 0)
            {
                SearchEmptyPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // 点击过表头后只在该列搜索；该列不存在时退回搜索全部列
            List<DataColumn> targetColumns = stringColumns;
            if (!string.IsNullOrEmpty(_searchColumn))
            {
                var only = stringColumns.FirstOrDefault(c =>
                    string.Equals(c.ColumnName, _searchColumn, StringComparison.Ordinal));
                if (only != null) targetColumns = new List<DataColumn> { only };
            }

            string filter = string.Join(" OR ", targetColumns.Select(c =>
            {
                string name = c.ColumnName.Replace("]", "]]");
                return $"[{name}] LIKE '%{escaped}%'";
            }));

            try
            {
                _historyView.RowFilter = filter;
                SearchEmptyPanel.Visibility = _historyView.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                try { _historyView.RowFilter = ""; } catch { }
                SearchEmptyPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void HistoryGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            try
            {
                var rowView = e.Row.Item as DataRowView;
                string category = "";
                if (rowView != null && rowView.DataView.Table.Columns.Contains(CategoryColumnName))
                    category = rowView[CategoryColumnName]?.ToString() ?? "";

                if (_highlight != null && _highlight.Enabled)
                {
                    var brush = _highlight.GetBrushByCategory(category);
                    if (brush != null) e.Row.Foreground = brush;
                    else try { e.Row.ClearValue(DataGridRow.ForegroundProperty); } catch { }
                }
                else
                {
                    try { e.Row.ClearValue(DataGridRow.ForegroundProperty); } catch { }
                }

                if (_enableStarColor && !string.IsNullOrEmpty(category))
                {
                    int starCount = 0;
                    foreach (char c in category) if (c == '★' || c == '⭐') starCount++;

                    if (starCount == 5)
                        e.Row.Background = FiveStarRowBrush;
                    else if (starCount == 4)
                        e.Row.Background = FourStarRowBrush;
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

        public void GoToNextPage()
        {
            if (_currentPage < _totalPages) { _currentPage++; LoadPage(); }
        }

        public void GoToPrevPage()
        {
            if (_currentPage > 1) { _currentPage--; LoadPage(); }
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1) { _currentPage--; LoadPage(); }
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages) { _currentPage++; LoadPage(); }
        }

        private bool HasActiveGachaStrategy()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentPoolDir)) return false;
                string file = Path.Combine(_currentPoolDir, "gacha_settings.ini");
                if (!File.Exists(file)) return false;
                var s = GachaSettings.LoadSettingsFromFile(file);
                return s != null && s.Enabled;
            }
            catch { return false; }
        }

        /// <summary>按页查看 / 全部显示 切换（状态保存在 settings.ini）。</summary>
        private void BtnViewMode_Click(object sender, RoutedEventArgs e)
        {
            _showAll = !_showAll;
            try { IniHelper.WriteValue("UI", "HistoryShowAll", _showAll ? "1" : "0", App.SettingsFile); } catch { }
            UpdateViewModeButton();
            _currentPage = 1;
            LoadPage();
        }

        private void UpdateReportButtonVisibility()
        {
            try
            {
                if (BtnHistoryReport != null)
                    BtnHistoryReport.Visibility = App.IsGachaReportVisible ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }
        private void UpdateViewModeButton()
        {
            try
            {
                // 按钮显示的是「点一下会切换到的模式」，而不是当前模式
                if (TxtViewMode != null) TxtViewMode.Text = _showAll ? "按页查看" : "全部显示";
                if (TxtViewModeIcon != null) TxtViewModeIcon.Text = _showAll ? "\uE7C3" : "\uE8FD";

                // 全部显示模式：隐藏下方翻页按钮与页码
                if (PagerPanel != null) PagerPanel.Visibility = _showAll ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }

        private void BtnHistoryReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_currentPoolDir)) return;

                App.LastSelectedPoolDir = _currentPoolDir;
                App.GachaReportReturnPage = "History";
                App.SaveSettings();

                MessageBus.Publish(new AppMessages.NavigateTo("GachaReport"));
            }
            catch { }
        }
        private ScrollViewer _gridScroll;
        private bool _syncingScrollBar;

        private static void CollectScrollViewers(DependencyObject root, List<ScrollViewer> list)
        {
            if (root == null) return;
            if (root is ScrollViewer sv) list.Add(sv);

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
                CollectScrollViewers(VisualTreeHelper.GetChild(root, i), list);
        }

        private static DataGridColumnHeadersPresenter FindHeadersPresenter(DependencyObject root)
        {
            if (root == null) return null;
            if (root is DataGridColumnHeadersPresenter p) return p;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindHeadersPresenter(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }
        /// <summary>把外层滚动条与 DataGrid 内部滚动区同步（轨道贯穿表头）。</summary>
        private void AttachGridScrollBar()
        {
            try
            {
                var all = new List<ScrollViewer>();
                CollectScrollViewers(HistoryGrid, all);

                var sv = all.OrderByDescending(x => x.ExtentHeight).FirstOrDefault();
                if (sv == null && all.Count > 0) sv = all[0];
                if (ReferenceEquals(sv, _gridScroll)) { UpdateGridScrollBar(); return; }

                if (_gridScroll != null) _gridScroll.ScrollChanged -= GridScroll_ScrollChanged;

                _gridScroll = sv;
                if (_gridScroll != null) _gridScroll.ScrollChanged += GridScroll_ScrollChanged;

                UpdateGridScrollBar();
            }
            catch { }
        }

        private void GridScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateGridScrollBar();

        private void UpdateGridScrollBar()
        {
            try
            {
                if (HistoryScrollBar == null) return;

                if (_gridScroll == null)
                {
                    HistoryScrollBar.Visibility = Visibility.Collapsed;
                    return;
                }

                double max = Math.Max(0, _gridScroll.ScrollableHeight);
                if (max <= 0) max = Math.Max(0, _gridScroll.ExtentHeight - _gridScroll.ViewportHeight);
                HistoryScrollBar.Minimum = 0;
                HistoryScrollBar.Maximum = max;
                HistoryScrollBar.ViewportSize = Math.Max(1, _gridScroll.ViewportHeight);
                HistoryScrollBar.LargeChange = Math.Max(1, _gridScroll.ViewportHeight * 0.9);
                HistoryScrollBar.SmallChange = 32;

                if (Math.Abs(HistoryScrollBar.Value - _gridScroll.VerticalOffset) > 0.5)
                    HistoryScrollBar.Value = _gridScroll.VerticalOffset;

                HistoryScrollBar.Visibility = max > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void HistoryScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            try
            {
                if (_gridScroll == null || _syncingScrollBar) return;
                _syncingScrollBar = true;
                _gridScroll.ScrollToVerticalOffset(HistoryScrollBar.Value);
            }
            catch { }
            finally { _syncingScrollBar = false; }
        }
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPoolDir)) return;

            bool isAdvanced = HasActiveGachaStrategy();

            string message = isAdvanced
                ? "确定要清空当前卡池的历史记录和保底状态吗？"
                : "确定要清空当前卡池的历史记录吗？";

            if (CustomMessageBox.Show(Window.GetWindow(this), message,
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "清空", yesIcon: "\uE74D", noIcon: "\uE711") != MessageBoxResult.Yes)
                return;

            string historyFile = Path.Combine(_currentPoolDir, "history.txt");
            if (File.Exists(historyFile)) File.Delete(historyFile);

            if (isAdvanced)
            {
                string gachaStateFile = Path.Combine(_currentPoolDir, "gacha_state.ini");
                if (File.Exists(gachaStateFile)) File.Delete(gachaStateFile);
            }

            RuntimeLog.Info($"已清空历史记录：卡池={_currentPoolDir}，高级规则={isAdvanced}");
            _currentPage = 1;
            LoadPage();
        }
    }
}
