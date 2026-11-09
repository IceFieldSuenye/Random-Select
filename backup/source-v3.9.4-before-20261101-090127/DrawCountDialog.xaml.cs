using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RandomDrawer
{
    public partial class DrawCountDialog : Window
    {
        public int DrawCount { get; private set; } = 10;

        public DrawCountDialog()
        {
            InitializeComponent();
            // 聚焦到输入框，并把光标置于文本末尾
            TxtCustomCount.Focus();
            TxtCustomCount.CaretIndex = TxtCustomCount.Text.Length;
        }

        private void NumberBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                TxtCustomCount.Text = btn.Tag.ToString();
                // 光标移到末尾
                TxtCustomCount.Focus();
                TxtCustomCount.CaretIndex = TxtCustomCount.Text.Length;
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            string input = TxtCustomCount.Text.Trim();
            if (int.TryParse(input, out int count) && count > 0 && count <= 9999)
            {
                DrawCount = count;
                DialogResult = true;
            }
            else
            {
                CustomMessageBox.Show(this, "请输入有效的抽取次数（1~9999 之间的整数）！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtCustomCount.Focus();
                TxtCustomCount.CaretIndex = TxtCustomCount.Text.Length;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}