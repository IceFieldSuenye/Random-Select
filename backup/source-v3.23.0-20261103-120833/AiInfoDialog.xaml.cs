using System.Windows;

namespace RandomDrawer
{
    public partial class AiInfoDialog : FloatingDialogBase
    {
        public AiInfoDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
