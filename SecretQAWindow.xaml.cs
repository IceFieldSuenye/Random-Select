using System;
using System.Windows;

namespace RandomDrawer
{
    public partial class SecretQAWindow : FloatingDialogBase
    {
        private readonly string _correctAnswer;

        public SecretQAWindow(string question, string correctAnswer)
        {
            InitializeComponent();

            TxtQuestion.Text = string.IsNullOrWhiteSpace(question) ? "（未设置密保问题）" : question;
            _correctAnswer = (correctAnswer ?? "").Trim();

            Loaded += (s, e) => TxtAnswer.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string userAnswer = (TxtAnswer.Text ?? "").Trim();

            if (string.IsNullOrEmpty(userAnswer))
            {
                CustomMessageBox.Show(OwnerWindow, "请输入答案。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtAnswer.Focus();
                return;
            }

            if (string.Equals(userAnswer, _correctAnswer, StringComparison.OrdinalIgnoreCase))
            {
                DialogResult = true;
            }
            else
            {
                CustomMessageBox.Show(OwnerWindow, "答案错误，请重试。", "密保验证失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtAnswer.SelectAll();
                TxtAnswer.Focus();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}