using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RandomDrawer
{
    public partial class PasswordDialog : FloatingDialogBase
    {
        public string Password { get; private set; }

        // ★ 新增：密保问题属性（可选）
        public string SecretQuestion { get; private set; } = "";
        public string SecretAnswer { get; private set; } = "";

        // ★ 新增：用户是否点击了"忘记密码"
        public bool ForgotPasswordRequested { get; private set; } = false;

        public PasswordDialog(string title, bool isConfirm = false)
        {
            InitializeComponent();

            if (title.Contains("设置锁定密码"))
            {
                this.Title = "设置锁定密码";
                TipText.Text = "当删除或修改此卡池时，需要验证密码才能继续。";
            }
            else
            {
                this.Title = "验证密码";
                TipText.Text = title;
            }

            if (isConfirm)
            {
                ConfirmPanel.Visibility = Visibility.Visible;

                // 只有"设置密码"时才显示密保问题区域
                if (SecretQAPanel != null)
                    SecretQAPanel.Visibility = Visibility.Visible;

                // 设置密码场景下不需要"忘记密码"按钮
                if (BtnForgot != null)
                    BtnForgot.Visibility = Visibility.Collapsed;
            }
        }

        // ================= 眼睛图标：按住显示明文 =================
        private void IcoEye_MouseDown(object sender, MouseButtonEventArgs e)
        {
            TxtVisiblePwd.Text = PwdBox.Password;
            PwdBox.Visibility = Visibility.Collapsed;
            TxtVisiblePwd.Visibility = Visibility.Visible;
            IcoEye.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
        }

        private void IcoEye_MouseUp(object sender, MouseButtonEventArgs e)
        {
            PwdBox.Password = TxtVisiblePwd.Text;
            TxtVisiblePwd.Visibility = Visibility.Collapsed;
            PwdBox.Visibility = Visibility.Visible;
            IcoEye.Foreground = ThemeManager.GetBrush("Fg_808080");
        }

        private void IcoEyeConfirm_MouseDown(object sender, MouseButtonEventArgs e)
        {
            TxtVisiblePwdConfirm.Text = PwdBoxConfirm.Password;
            PwdBoxConfirm.Visibility = Visibility.Collapsed;
            TxtVisiblePwdConfirm.Visibility = Visibility.Visible;
            IcoEyeConfirm.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
        }

        private void IcoEyeConfirm_MouseUp(object sender, MouseButtonEventArgs e)
        {
            PwdBoxConfirm.Password = TxtVisiblePwdConfirm.Text;
            TxtVisiblePwdConfirm.Visibility = Visibility.Collapsed;
            PwdBoxConfirm.Visibility = Visibility.Visible;
            IcoEyeConfirm.Foreground = ThemeManager.GetBrush("Fg_808080");
        }
        // ==========================================================

        // 勾选/取消勾选"添加密保问题"
        private void ChkEnableSecretQA_Click(object sender, RoutedEventArgs e)
        {
            if (SecretQABody == null) return;

            bool enabled = ChkEnableSecretQA.IsChecked == true;

            // 平滑展开/收起，窗口高度自然过渡
            PanelAnimator.Animate(SecretQABody, enabled, 300, () =>
            {
                if (enabled) return;
                try
                {
                    TxtSecretQuestion.Text = "";
                    TxtSecretAnswer.Text = "";
                }
                catch { }
            });
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PwdBox.Password))
            {
                CustomMessageBox.Show(OwnerWindow, "密码不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ConfirmPanel.Visibility == Visibility.Visible && PwdBox.Password != PwdBoxConfirm.Password)
            {
                CustomMessageBox.Show(OwnerWindow, "两次输入的密码不一致！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 校验密保
            if (SecretQAPanel != null && SecretQAPanel.Visibility == Visibility.Visible &&
                ChkEnableSecretQA.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(TxtSecretQuestion.Text))
                {
                    CustomMessageBox.Show(OwnerWindow, "密保问题不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtSecretQuestion.Focus();
                    return;
                }
                if (string.IsNullOrWhiteSpace(TxtSecretAnswer.Text))
                {
                    CustomMessageBox.Show(OwnerWindow, "密保答案不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtSecretAnswer.Focus();
                    return;
                }

                SecretQuestion = TxtSecretQuestion.Text.Trim();
                SecretAnswer = TxtSecretAnswer.Text.Trim();
            }

            Password = PwdBox.Password;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ★ 忘记密码
        private void Forgot_Click(object sender, RoutedEventArgs e)
        {
            ForgotPasswordRequested = true;
            DialogResult = false;   // 关闭本窗口，交由调用方处理
        }
    }
}