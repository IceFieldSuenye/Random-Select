using System.Windows;

namespace RandomDrawer
{
    /// <summary>
    /// 开源许可（窗口内浮动界面）：展示 MIT 许可与第三方组件声明。
    /// 内容与仓库根目录的 LICENSE、THIRD-PARTY-NOTICES.md 保持一致。
    /// </summary>
    public partial class LicensesDialog : FloatingDialogBase
    {
        public LicensesDialog()
        {
            InitializeComponent();
            Loaded += (s, e) => Dispatcher.BeginInvoke(new System.Action(() =>
            {
                try { LicensesScroll.ScrollToTop(); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public static void Show(Window owner)
        {
            var dialog = new LicensesDialog { Owner = owner };
            dialog.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
