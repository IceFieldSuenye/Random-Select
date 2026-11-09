using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>
    /// 导出卡池：勾选若干卡池，打包成一个 zip。
    /// </summary>
    public partial class ExportPoolsDialog : FloatingDialogBase
    {
        private readonly List<(string Name, string Dir)> _pools;
        private readonly List<CheckBox> _checks = new List<CheckBox>();
        private bool _busy;

        public int ExportedCount { get; private set; }

        public ExportPoolsDialog(IEnumerable<(string Name, string Dir)> pools, string currentPoolDir)
        {
            InitializeComponent();

            _pools = pools?.ToList() ?? new List<(string, string)>();

            foreach (var pool in _pools)
            {
                var check = new CheckBox
                {
                    Content = pool.Name,
                    Margin = new Thickness(0, 6, 0, 6),
                    IsChecked = !string.IsNullOrEmpty(currentPoolDir)
                                && string.Equals(pool.Dir, currentPoolDir, StringComparison.OrdinalIgnoreCase)
                };
                check.Checked += SelectionChanged;
                check.Unchecked += SelectionChanged;
                _checks.Add(check);
                PoolListHost.Children.Add(check);
            }

            // 没有当前卡池时，默认全选
            if (!_checks.Any(c => c.IsChecked == true))
            {
                foreach (var c in _checks) c.IsChecked = true;
            }

            UpdateCount();
        }

        private void SelectionChanged(object sender, RoutedEventArgs e) => UpdateCount();

        private void UpdateCount()
        {
            int selected = _checks.Count(c => c.IsChecked == true);
            TxtCount.Text = $"已选择 {selected} / {_checks.Count} 个";
            if (!_busy) BtnExport.IsEnabled = selected > 0;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            foreach (var c in _checks) c.IsChecked = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            foreach (var c in _checks) c.IsChecked = false;
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var selected = _checks.Where(c => c.IsChecked == true)
                                  .Select(c => _pools[_checks.IndexOf(c)])
                                  .ToList();

            if (selected.Count == 0)
            {
                CustomMessageBox.Show(OwnerWindow, "请至少选择一个卡池。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "保存卡池压缩包",
                Filter = "卡池压缩包 (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                FileName = (selected.Count == 1
                    ? PoolTransfer.SanitizeFolderName(selected[0].Name)
                    : $"卡池导出_{selected.Count}个") + ".zip",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(OwnerWindow) != true) return;

            SetBusy(true, selected.Count);

            var progress = new Progress<(int done, int total, string name)>(p =>
            {
                Progress.Value = p.total <= 0 ? 0 : p.done * 100.0 / p.total;
                TxtStatus.Text = $"正在导出（{p.done}/{p.total}）：{p.name}";
            });

            PoolTransfer.ExportResult result = null;
            try
            {
                result = await Task.Run(() => PoolTransfer.Export(
                    selected,
                    dialog.FileName,
                    (done, total, name) => ((IProgress<(int, int, string)>)progress).Report((done, total, name))));
            }
            catch (Exception ex)
            {
                result = new PoolTransfer.ExportResult();
                result.Errors.Add(ex.Message);
            }

            SetBusy(false, selected.Count);
            ExportedCount = result?.Exported ?? 0;

            var message = new StringBuilder();
            message.AppendLine($"成功导出 {ExportedCount} 个卡池。");
            message.AppendLine();
            message.AppendLine("保存位置：");
            message.AppendLine(dialog.FileName);

            if (result != null && result.Errors.Count > 0)
            {
                message.AppendLine();
                message.AppendLine("以下内容导出失败：");
                foreach (var error in result.Errors.Take(12))
                    message.AppendLine("· " + error);
                if (result.Errors.Count > 12)
                    message.AppendLine($"· …… 另有 {result.Errors.Count - 12} 条");
            }

            CustomMessageBox.Show(OwnerWindow, message.ToString().TrimEnd(),
                ExportedCount > 0 ? "导出完成" : "导出失败",
                MessageBoxButton.OK,
                ExportedCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ExportedCount > 0) DialogResult = true;
        }

        private void SetBusy(bool busy, int total)
        {
            _busy = busy;
            BtnExport.IsEnabled = !busy;
            BtnCancel.IsEnabled = !busy;
            BtnSelectAll.IsEnabled = !busy;
            BtnSelectNone.IsEnabled = !busy;

            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            TxtStatus.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Progress.Value = busy ? 0 : 0;
            TxtStatus.Text = busy ? "准备中…" : "";

            if (!busy) UpdateCount();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            DialogResult = false;
        }
    }
}
