using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    /// <summary>
    /// 多选角色弹窗（UP4星角色等）：搜索 + 勾选列表 + 取消 / 确定。
    /// 完成后发布 MultiSelectFinished，调用方沿用原来的消息处理即可。
    /// </summary>
    public partial class MultiSelectDialog : FloatingDialogBase, IHandleEscape
    {
        private readonly List<string> _allItems = new List<string>();
        private readonly List<string> _selected = new List<string>();
        private List<string> _initialSelection = new List<string>();
        private string _searchText = "";
        private int _maxSelection = int.MaxValue;
        private bool _isFiltering;
        private bool _initialized;
        private bool _discardConfirmed;

        public MultiSelectDialog(IEnumerable<string> items, IEnumerable<string> preSelected,
                                 string title, int maxSelection)
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "选择角色" : title;

            _allItems = (items ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct()
                .OrderBy(x => x, StringComparer.CurrentCulture)
                .ToList();

            _maxSelection = maxSelection > 0 ? maxSelection : Math.Max(1, _allItems.Count - 1);
            TxtHint.Text = $"可勾选 1 ~ {_maxSelection} 个（最多为总数 - 1）";

            _initialSelection = (preSelected ?? Enumerable.Empty<string>())
                .Where(x => _allItems.Contains(x))
                .ToList();
            _selected.AddRange(_initialSelection);

            ApplyFilter();
            _initialized = true;
        }

        // ================================================================
        // 搜索 / 列表
        // ================================================================
        private void Search_SearchChanged(string text)
        {
            _searchText = text ?? "";
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (ListItems == null) return;

            _isFiltering = true;
            try
            {
                string q = (_searchText ?? "").Trim();
                var visible = _allItems
                    .Where(x => q.Length == 0
                                || x.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    .ToList();

                ListItems.ItemsSource = null;
                ListItems.ItemsSource = visible;

                foreach (var s in _selected)
                    if (visible.Contains(s)) ListItems.SelectedItems.Add(s);
            }
            finally { _isFiltering = false; }
        }

        private void ListItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isFiltering) return;

            var visible = ListItems.Items.Cast<string>().ToList();
            _selected.RemoveAll(s => visible.Contains(s));

            foreach (var s in ListItems.SelectedItems.Cast<string>())
                if (!_selected.Contains(s)) _selected.Add(s);
        }

        // ================================================================
        // 结果
        // ================================================================
        private void CloseWithResult(bool ok, List<string> selected)
        {
            _discardConfirmed = true;
            MessageBus.Publish(new AppMessages.MultiSelectFinished(selected ?? new List<string>(), ok));
            DialogResult = ok;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized) { CloseWithResult(false, null); return; }

            var selected = new List<string>(_selected);
            if (selected.Count < 1)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "至少选择 1 个 UP 角色。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (selected.Count > _maxSelection)
            {
                CustomMessageBox.Show(Window.GetWindow(this), $"最多只能选择 {_maxSelection} 个 UP 角色。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CloseWithResult(true, selected);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_discardConfirmed && IsChanged())
            {
                var r = CustomMessageBox.Show(Window.GetWindow(this),
                    "您已修改了选择，确定要放弃并返回吗？",
                    "确认放弃", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    yesText: "放弃", noText: "取消", yesIcon: "\uE926");
                if (r != MessageBoxResult.Yes) return;
            }

            CloseWithResult(false, null);
        }

        private bool IsChanged()
        {
            var current = _selected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var initial = _initialSelection.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            if (current.Count != initial.Count) return true;
            for (int i = 0; i < current.Count; i++)
                if (!string.Equals(current[i], initial[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public bool TryHandleEscape()
        {
            Cancel_Click(this, new RoutedEventArgs());
            return true;
        }
    }
}
