using System.Windows;

namespace RandomDrawer
{
    public partial class ResetPromptDialog : FloatingDialogBase
    {
        public bool IsReset { get; private set; } = false;
        public ResetPromptDialog(string poolName)
        {
            InitializeComponent();
            MsgText.Text = $"卡池“{poolName}”已抽完！";
        }
        private void Reset_Click(object sender, RoutedEventArgs e) { IsReset = true; DialogResult = true; }
        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}