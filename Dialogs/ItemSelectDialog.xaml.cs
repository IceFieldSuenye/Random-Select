using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RandomDrawer
{
    /// <summary>单对象选择界面（带搜索），用于高级抽卡规则中需要从列表选择角色/学生的场景。</summary>
    public partial class ItemSelectDialog : FloatingDialogBase
    {
        private readonly List<string> _allItems;
        private string _selected;
        private string _searchText = "";

        public string SelectedItem => _selected;
        public bool ClearRequested { get; private set; }

        public ItemSelectDialog(string title, IEnumerable<string> items, string initialSelection = "",
            bool allowClear = false, string hint = "请选择一个对象。")
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "选择对象" : title;
            TxtHint.Text = hint ?? "";
            _allItems = (items ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct()
                .OrderBy(x => x, StringComparer.CurrentCulture)
                .ToList();

            _selected = (initialSelection ?? "").Trim();
            BtnClear.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;

            ApplyFilter();
            if (!string.IsNullOrEmpty(_selected) && ListItems.Items.Contains(_selected))
                ListItems.SelectedItem = _selected;
        }

        private void ApplyFilter()
        {
            string q = (_searchText ?? "").Trim();
            var filtered = _allItems
                .Where(x => q.Length == 0
                    || x.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || string.Equals(x, _selected, StringComparison.Ordinal))
                .ToList();

            ListItems.ItemsSource = null;
            ListItems.ItemsSource = filtered;

            if (!string.IsNullOrEmpty(_selected) && filtered.Contains(_selected))
                ListItems.SelectedItem = _selected;
        }

        private void Search_SearchChanged(string text)
        {
            _searchText = text ?? "";
            ApplyFilter();
        }

        private void ListItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListItems.SelectedItem is string s) _selected = s;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _selected = "";
            ClearRequested = true;
            DialogResult = true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selected))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择一个对象。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
