using System.Windows;

namespace RandomDrawer
{
    public partial class InputDialog : FloatingDialogBase
    {
        public string InputText { get; private set; } = "";

        public InputDialog(string title, string message, string defaultValue = "")
        {
            InitializeComponent();
            this.Title = title;
            TxtMessage.Text = message;
            TxtInput.Text = defaultValue;
            Loaded += (s, e) =>
            {
                try { TxtInput.Focus(); TxtInput.SelectAll(); } catch { }
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            InputText = TxtInput.Text.Trim();
            this.DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => this.DialogResult = false;

        public static string Show(Window owner, string title, string message, string defaultValue = "")
        {
            var dialog = new InputDialog(title, message, defaultValue);
            dialog.Owner = owner;

            return dialog.ShowDialog() == true ? dialog.InputText : null;
        }
    }
}