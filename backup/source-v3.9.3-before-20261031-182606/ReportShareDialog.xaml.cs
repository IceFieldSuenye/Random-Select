using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>抽取报告分享预览：展示生成的长图，并提供「打开文件夹」「复制到剪贴板」「确定」。</summary>
    public partial class ReportShareDialog : FloatingDialogBase
    {
        private readonly BitmapSource _image;
        private readonly string _savedPath;

        public ReportShareDialog(BitmapSource image, string savedPath)
        {
            _image = image;
            _savedPath = savedPath ?? "";

            InitializeComponent();

            try
            {
                PreviewImage.Source = image;
                TxtSavedPath.Text = string.IsNullOrEmpty(_savedPath)
                    ? "图片已生成"
                    : "图片已保存：" + _savedPath;
            }
            catch { }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_savedPath) && File.Exists(_savedPath))
                {
                    // 打开资源管理器并选中已保存的图片
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"")
                    {
                        UseShellExecute = true
                    });
                    return;
                }

                string dir = !string.IsNullOrEmpty(_savedPath)
                    ? Path.GetDirectoryName(_savedPath)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Saved Pictures");

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private void Share_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ★ 复制的是已保存的 .jpg 文件本身，而不是位图对象；
                //   粘贴到文件夹、聊天窗口时会得到一个图片文件。
                if (!string.IsNullOrEmpty(_savedPath) && File.Exists(_savedPath))
                {
                    var files = new System.Collections.Specialized.StringCollection { _savedPath };
                    Clipboard.SetFileDropList(files);
                }
                else if (_image != null)
                {
                    // 保存失败时退回复制图片数据，至少还能粘贴图片
                    Clipboard.SetImage(_image);
                }
            }
            catch { }

            DialogResult = true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
