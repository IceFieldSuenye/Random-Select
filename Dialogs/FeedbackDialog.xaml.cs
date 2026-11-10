using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace RandomDrawer
{
    /// <summary>
    /// 3.76：反馈与联系（窗口内浮动界面）。
    /// 把原来「关于」页上的「反馈邮箱」和「问题反馈」两行合并到这里，排版与关于页保持一致。
    /// </summary>
    public partial class FeedbackDialog : FloatingDialogBase
    {
        private const string MailAddress = "ssssssss888777@163.com";
        private const string IssuesUrl = "https://github.com/IceFieldSuenye/Random-Select/issues";

        public FeedbackDialog()
        {
            InitializeComponent();
        }

        public static void Show(Window owner)
        {
            var dialog = new FeedbackDialog { Owner = owner };
            dialog.ShowDialog();
        }

        private void CopyMail_Click(object sender, RoutedEventArgs e)
        {
            if (TrySetClipboardText(MailAddress))
            {
                ToastNotification.ShowSuccess(Window.GetWindow(this), "已成功复制");
            }
            else
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "复制失败，剪贴板可能被其他程序占用，请稍后重试。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
            }
        }

        private void OpenIssues_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = IssuesUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "打开链接失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private static bool TrySetClipboardText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, copy: true);
                    return true;
                }
                catch
                {
                    Thread.Sleep(50);
                }
            }
            return false;
        }
    }
}
