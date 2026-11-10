using System.Windows;

namespace RandomDrawer
{
    /// <summary>清理缓存选择：运行日志 / 抽取报告缓存 / 临时图片。</summary>
    public partial class ClearCacheDialog : FloatingDialogBase
    {
        public bool ClearLogs { get; private set; }
        public bool ClearReportCache { get; private set; }
        public bool ClearTempImages { get; private set; }

        public ClearCacheDialog()
        {
            InitializeComponent();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            bool logs = ChkLogs.IsChecked == true;
            bool reports = ChkReports.IsChecked == true;
            bool tempImages = ChkTempImages.IsChecked == true;

            if (!logs && !reports && !tempImages)
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "请至少选择一项要清理的内容。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ClearLogs = logs;
            ClearReportCache = reports;
            ClearTempImages = tempImages;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
