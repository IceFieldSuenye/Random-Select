using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    /// <summary>
    /// 庆典招募 UP 选择弹窗：左侧主UP（单选），右侧次UP（多选），完成后发布 CelebrationUpFinished。
    /// </summary>
    public partial class CelebrationUpDialog : FloatingDialogBase, IHandleEscape
    {
        private readonly List<string> _allItems = new List<string>();
        private readonly List<string> _subUps = new List<string>();
        private string _mainUp;
        private string _searchText = "";
        private string _initialMainUp;
        private List<string> _initialSubUps = new List<string>();
        private bool _isLoading;
        private bool _initialized;
        private bool _discardConfirmed;

        private int MaxSub => Math.Max(0, (_allItems?.Count ?? 0) - 2);

        public CelebrationUpDialog(IEnumerable<string> items, string currentMainUp, IEnumerable<string> currentSubUps)
        {
            InitializeComponent();

            _allItems = (items ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct()
                .OrderBy(x => x, StringComparer.CurrentCulture)
                .ToList();

            _mainUp = string.IsNullOrWhiteSpace(currentMainUp) ? null : currentMainUp.Trim();
            if (_mainUp != null && !_allItems.Contains(_mainUp)) _mainUp = null;

            _subUps = (currentSubUps ?? Enumerable.Empty<string>())
                .Where(x => _allItems.Contains(x) && !string.Equals(x, _mainUp, StringComparison.Ordinal))
                .Distinct()
                .ToList();

            _initialMainUp = _mainUp;
            _initialSubUps = new List<string>(_subUps);

            TxtHint.Text = $"主UP只能选择 1 名学生；次UP可选 0 ~ {MaxSub} 名学生。主UP与次UP不能重复选择。";

            RefreshLists();
            _initialized = true;
            UpdateHeaders();
        }

        // ================================================================
        // 搜索 / 列表
        // ================================================================
        private void Search_SearchChanged(string text)
        {
            _searchText = text ?? "";
            RefreshLists();
            UpdateHeaders();
        }

        private void RefreshLists()
        {
            if (ListMainUp == null || ListSubUp == null) return;

            bool old = _isLoading;
            _isLoading = true;
            try
            {
                ListMainUp.Items.Clear();
                ListSubUp.Items.Clear();

                string q = (_searchText ?? "").Trim();
                bool Matches(string s) => q.Length == 0
                    || s.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;

                foreach (var item in _allItems)
                {
                    if (!_subUps.Contains(item) && Matches(item)) ListMainUp.Items.Add(item);
                    if (!string.Equals(item, _mainUp, StringComparison.Ordinal) && Matches(item))
                        ListSubUp.Items.Add(item);
                }

                if (!string.IsNullOrEmpty(_mainUp) && ListMainUp.Items.Contains(_mainUp))
                    ListMainUp.SelectedItem = _mainUp;

                foreach (var s in _subUps)
                    if (ListSubUp.Items.Contains(s)) ListSubUp.SelectedItems.Add(s);
            }
            finally { _isLoading = old; }
        }

        private void UpdateHeaders()
        {
            if (TxtMainUpHeader == null || TxtSubUpHeader == null) return;

            int mainCount = string.IsNullOrEmpty(_mainUp) ? 0 : 1;
            TxtMainUpHeader.Text = $"主UP（必选 1 个，已选 {mainCount} / 1）";
            TxtSubUpHeader.Text = $"次UP（可选 0 ~ {MaxSub} 个，已选 {_subUps.Count} 名）";
        }

        private void ListMainUp_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            string newMainUp = ListMainUp.SelectedItem as string;
            if (newMainUp == _mainUp) { UpdateHeaders(); return; }

            _mainUp = newMainUp;
            _subUps.RemoveAll(x => string.Equals(x, _mainUp, StringComparison.Ordinal));
            RefreshLists();
            UpdateHeaders();
        }

        private void ListSubUp_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            if (ListSubUp.SelectedItems.Count > MaxSub)
            {
                var last = ListSubUp.SelectedItems.Cast<string>().LastOrDefault();
                if (last != null) ListSubUp.SelectedItems.Remove(last);

                CustomMessageBox.Show(Window.GetWindow(this), $"次UP最多选择 {MaxSub} 名学生。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _subUps.Clear();
            foreach (var s in ListSubUp.SelectedItems.Cast<string>())
                if (!string.Equals(s, _mainUp, StringComparison.Ordinal)) _subUps.Add(s);

            RefreshLists();
            UpdateHeaders();
        }

        // ================================================================
        // 结果
        // ================================================================
        private void CloseWithResult(bool ok, string mainUp, List<string> subUps)
        {
            _discardConfirmed = true;
            MessageBus.Publish(new AppMessages.CelebrationUpFinished(ok, mainUp ?? "", subUps ?? new List<string>()));
            DialogResult = ok;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!_initialized) { CloseWithResult(false, "", new List<string>()); return; }

            if (string.IsNullOrEmpty(_mainUp))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请选择 1 名主UP3星学生。",
                    "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_subUps.Count > MaxSub)
            {
                CustomMessageBox.Show(Window.GetWindow(this), $"次UP最多选择 {MaxSub} 名学生。",
                    "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_subUps.Contains(_mainUp))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "主UP 与 次UP 不能选择同一名学生。",
                    "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CloseWithResult(true, _mainUp, new List<string>(_subUps));
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

            CloseWithResult(false, "", new List<string>());
        }

        private bool IsChanged()
        {
            if (!string.Equals(_mainUp ?? "", _initialMainUp ?? "", StringComparison.OrdinalIgnoreCase))
                return true;

            var cur = _subUps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var init = _initialSubUps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            if (cur.Count != init.Count) return true;
            for (int i = 0; i < cur.Count; i++)
                if (!string.Equals(cur[i], init[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool TryHandleEscape()
        {
            Cancel_Click(this, new RoutedEventArgs());
            return true;
        }
    }
}
