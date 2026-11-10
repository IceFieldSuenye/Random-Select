using System;
using System.Threading.Tasks;
using System.Windows;

namespace RandomDrawer
{
    /// <summary>
    /// 通用进度提示浮窗：后台执行耗时操作，界面上显示进度条与状态文字。
    /// 不能手动关闭，操作结束后自动关闭。
    /// </summary>
    public partial class ProgressDialog : FloatingDialogBase
    {
        public ProgressDialog(string title, string status)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(title)) Title = title;
            TxtStatus.Text = status ?? "正在处理…";
            CanClose = false;
        }

        /// <summary>更新进度（0~100）与状态文字。</summary>
        public void Report(int done, int total, string status)
        {
            try
            {
                if (total > 0)
                    Bar.Value = Math.Max(0, Math.Min(100, done * 100.0 / total));
                else
                    Bar.IsIndeterminate = true;

                if (!string.IsNullOrEmpty(status)) TxtStatus.Text = status;
                if (total > 0) TxtDetail.Text = $"{done} / {total}";
            }
            catch { }
        }

        public void Finish()
        {
            try { DialogResult = true; } catch { }
        }

        /// <summary>
        /// 执行一个耗时操作：work 在后台线程运行，通过 report 回报进度。
        /// 调用会阻塞到操作结束（在浮动窗的 DispatcherFrame 内）。
        /// </summary>
        public static void Run(Window owner, string title, string status, Action<Action<int, int, string>> work)
        {
            var dialog = new ProgressDialog(title, status) { Owner = owner };

            dialog.Loaded += (s, e) =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        work((done, total, text) =>
                        {
                            try { dialog.Dispatcher.BeginInvoke(new Action(() => dialog.Report(done, total, text))); }
                            catch { }
                        });
                    }
                    catch { }
                    finally
                    {
                        try { dialog.Dispatcher.BeginInvoke(new Action(dialog.Finish)); } catch { }
                    }
                });
            };

            try { dialog.ShowDialog(); } catch { }
        }
    }
}
