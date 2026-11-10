using System.Windows;

namespace RandomDrawer
{
    public partial class ResetConfirmDialog : FloatingDialogBase
    {
        public ResetConfirmDialog() => InitializeComponent();
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}