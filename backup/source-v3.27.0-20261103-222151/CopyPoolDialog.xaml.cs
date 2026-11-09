using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>
    /// 复制卡池：输入新名称并选择复制到的位置。
    /// </summary>
    public partial class CopyPoolDialog : FloatingDialogBase
    {
        public string PoolName { get; private set; } = "";
        public string TargetDir { get; private set; } = "";

        public CopyPoolDialog(string suggestedName, string defaultDir)
        {
            InitializeComponent();

            TxtName.Text = suggestedName ?? "";

            try
            {
                foreach (var dir in App.SearchDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    if (!App.IsValidDirectoryPath(dir)) continue;
                    ComboTarget.Items.Add(dir);
                }
            }
            catch { }

            ComboTarget.Text = defaultDir ?? "";

            Loaded += (s, e) =>
            {
                try
                {
                    TxtName.Focus();
                    TxtName.SelectAll();
                }
                catch { }
            };
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择卡池复制位置",
                Multiselect = false
            };

            string current = (ComboTarget.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(OwnerWindow) == true)
                ComboTarget.Text = dialog.FolderName;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string name = (TxtName.Text ?? "").Trim();
            string dir = (ComboTarget.Text ?? "").Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(name))
            {
                CustomMessageBox.Show(OwnerWindow, "请输入卡池名称。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                CustomMessageBox.Show(OwnerWindow,
                    "名称包含非法符号，请修改后再继续。\n\n以下符号不能使用：\n" + PoolTextRules.InvalidFileNameCharsText,
                    "名称不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(dir))
            {
                CustomMessageBox.Show(OwnerWindow, "请选择复制位置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try { dir = Path.GetFullPath(dir); }
            catch
            {
                CustomMessageBox.Show(OwnerWindow, "复制位置路径无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!App.IsDirectoryWritable(dir))
            {
                CustomMessageBox.Show(OwnerWindow, "该位置不可写入，请换一个目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PoolName = name;
            TargetDir = dir;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
