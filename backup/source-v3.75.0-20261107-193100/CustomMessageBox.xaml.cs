using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// "未保存修改" 场景下用户的选择。
    /// </summary>
    public enum UnsavedChoice
    {
        /// <summary>取消本次操作（不离开、不放弃）。</summary>
        Cancel,
        /// <summary>放弃修改并离开。</summary>
        Discard,
        /// <summary>保留修改，不离开。</summary>
        Keep
    }

    /// <summary>
    /// 「保存 / 不保存 / 取消」三选：有未保存修改时离开页面用。
    /// </summary>
    public enum SaveChoice
    {
        /// <summary>取消本次操作，留在当前页面。</summary>
        Cancel,
        /// <summary>不保存，直接离开。</summary>
        Discard,
        /// <summary>先保存，保存成功后再离开。</summary>
        Save
    }

    public partial class CustomMessageBox : FloatingDialogBase
    {
        public CustomMessageBox()
        {
            InitializeComponent();
        }

        private MessageBoxResult _result = MessageBoxResult.OK;

        /// <summary>去掉消息首尾空白、去掉空行，避免弹窗里出现多余的空行。</summary>
        private static string NormalizeMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            // 所有弹窗统一去掉空行，正文更紧凑
            while (text.Contains("\n\n")) text = text.Replace("\n\n", "\n");
            return text;
        }

        public static MessageBoxResult Show(Window owner, string message, string title = "提示",
            MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information,
            string yesText = null, string noText = null, string yesIcon = null, string noIcon = null)
        {
            var msgBox = new CustomMessageBox();
            msgBox.Title = title;
            msgBox.Owner = owner;

            msgBox.TxtMessage.Text = NormalizeMessage(message);

            if (image == MessageBoxImage.None)
            {
                msgBox.TxtIcon.Visibility = Visibility.Collapsed;
            }

            switch (image)
            {
                case MessageBoxImage.Error:
                    msgBox.TxtIcon.Text = "\uEA39";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#D32F2F");
                    break;
                case MessageBoxImage.Warning:
                    msgBox.TxtIcon.Text = "\uE7BA";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#F57C00");
                    break;
                case MessageBoxImage.Question:
                    msgBox.TxtIcon.Text = "\uE9CE";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    break;
                default:
                    msgBox.TxtIcon.Text = "\uE946";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    break;
            }

            // 核心修复：调换按钮位置，"否/取消"在左，"是/确定"在右
            switch (button)
            {
                case MessageBoxButton.OK:
                    msgBox.AddButton("确定", MessageBoxResult.OK, true, yesIcon);
                    break;
                case MessageBoxButton.OKCancel:
                    msgBox.AddButton("取消", MessageBoxResult.Cancel, false, noIcon);
                    msgBox.AddButton("确定", MessageBoxResult.OK, true, yesIcon);
                    break;
                case MessageBoxButton.YesNo:
                    msgBox.AddButton(string.IsNullOrWhiteSpace(noText) ? "取消" : noText, MessageBoxResult.No, false, noIcon);
                    msgBox.AddButton(string.IsNullOrWhiteSpace(yesText) ? "确定" : yesText, MessageBoxResult.Yes, true, yesIcon);
                    break;
                case MessageBoxButton.YesNoCancel:
                    msgBox.AddButton(string.IsNullOrWhiteSpace(noText) ? "取消" : noText, MessageBoxResult.No, false, noIcon);
                    msgBox.AddButton("取消", MessageBoxResult.Cancel, false, noIcon);
                    msgBox.AddButton(string.IsNullOrWhiteSpace(yesText) ? "确定" : yesText, MessageBoxResult.Yes, true, yesIcon);
                    break;
            }

            // ★ 右上角 ✕ 关闭时的返回值：只有「确定」这一种按钮等价于确认，
            //   其余（取消 / 是否 / 是-否-取消）一律按「未确认」处理，
            //   否则调用方会把「关掉弹窗」误当成「点了确定」。
            msgBox._result = button == MessageBoxButton.OK
                ? MessageBoxResult.OK
                : (button == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel);

            msgBox.ShowDialog();
            return msgBox._result;
        }

        /// <summary>带「不再提示」复选框的提示框。</summary>
        public static MessageBoxResult ShowWithCheckbox(Window owner, string message, string title,
            string checkboxText, out bool isChecked, MessageBoxImage image = MessageBoxImage.Information)
        {
            var msgBox = new CustomMessageBox();
            msgBox.Title = title;
            msgBox.Owner = owner;

            msgBox.TxtMessage.Text = NormalizeMessage(message);

            switch (image)
            {
                case MessageBoxImage.Error:
                    msgBox.TxtIcon.Text = "\uEA39";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#D32F2F");
                    break;
                case MessageBoxImage.Warning:
                    msgBox.TxtIcon.Text = "\uE7BA";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#F57C00");
                    break;
                case MessageBoxImage.Question:
                    msgBox.TxtIcon.Text = "\uE9CE";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    break;
                case MessageBoxImage.None:
                    msgBox.TxtIcon.Visibility = Visibility.Collapsed;
                    break;
                default:
                    msgBox.TxtIcon.Text = "\uE946";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    break;
            }

            msgBox.ChkDontAsk.Content = checkboxText;
            msgBox.ChkDontAsk.Visibility = Visibility.Visible;
            msgBox.ChkDontAsk.IsChecked = false;

            msgBox.AddButton("确定", MessageBoxResult.OK, true);
            msgBox.ShowDialog();
            isChecked = msgBox.ChkDontAsk.IsChecked == true;
            return msgBox._result;
        }

        /// <summary>
        /// 专用的"未保存修改"提示框。
        /// 按钮顺序：取消 → 放弃 → 保留（默认）。
        /// </summary>
        public static UnsavedChoice ShowUnsavedChanges(Window owner, string message)
        {
            var msgBox = new CustomMessageBox();
            msgBox.Title = "未保存的修改";
            msgBox.Owner = owner;

            msgBox.TxtMessage.Text = NormalizeMessage(message);

            // 警告图标（橙色）
            msgBox.TxtIcon.Text = "\uE7BA";
            msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#F57C00");

            UnsavedChoice result = UnsavedChoice.Cancel;

            // ★ 按钮顺序：取消、放弃、保留（默认 = 保留）
            msgBox.AddCustomButton("取消", "\uE711", () => result = UnsavedChoice.Cancel, false);
            msgBox.AddCustomButton("放弃", "\uE926", () => result = UnsavedChoice.Discard, false);
            msgBox.AddCustomButton("保留", "\uE74E", () => result = UnsavedChoice.Keep, true);

            msgBox.ShowDialog();
            return result;
        }

        /// <summary>
        /// 「保存 / 不保存 / 取消」三选提示框。
        /// 按钮顺序：取消 → 不保存 → 保存（默认）。
        /// </summary>
        public static SaveChoice ShowSaveChanges(Window owner, string message)
        {
            var msgBox = new CustomMessageBox();
            msgBox.Title = "未保存的修改";
            msgBox.Owner = owner;

            msgBox.TxtMessage.Text = NormalizeMessage(message);

            msgBox.TxtIcon.Text = "\uE7BA";
            msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#F57C00");

            SaveChoice result = SaveChoice.Cancel;

            msgBox.AddCustomButton("取消", "\uE711", () => result = SaveChoice.Cancel, false);
            msgBox.AddCustomButton("不保存", "\uE926", () => result = SaveChoice.Discard, false);
            msgBox.AddCustomButton("保存", "\uE74E", () => result = SaveChoice.Save, true);

            msgBox.ShowDialog();
            return result;
        }

        /// <summary>
        /// 自定义按钮文本和图标的消息框（用于“禁用动画”等特殊场景）。
        /// </summary>
        public static MessageBoxResult ShowCustom(Window owner, string message, string title,
            string yesText, string yesIcon, string noText, string noIcon,
            MessageBoxImage image = MessageBoxImage.Question, string cancelText = null)
        {
            var msgBox = new CustomMessageBox();
            msgBox.Title = title;
            msgBox.Owner = owner;

            msgBox.TxtMessage.Text = NormalizeMessage(message);

            switch (image)
            {
                case MessageBoxImage.Error:
                    msgBox.TxtIcon.Text = "\uEA39";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#D32F2F");
                    break;
                case MessageBoxImage.Warning:
                    msgBox.TxtIcon.Text = "\uE7BA";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#F57C00");
                    break;
                case MessageBoxImage.Question:
                    msgBox.TxtIcon.Text = "\uE9CE";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    break;
                default:
                    msgBox.TxtIcon.Text = "\uE946";
                    msgBox.TxtIcon.Foreground = ThemeManager.GetThemedBrushFromHex("#0078D7");
                    break;
            }

            MessageBoxResult result = MessageBoxResult.Cancel;

            // 左侧按钮（取消/否）
            if (!string.IsNullOrWhiteSpace(cancelText))
                msgBox.AddButton(cancelText, MessageBoxResult.Cancel, false, "\uE711");
            msgBox.AddCustomButton(noText, noIcon, () => result = MessageBoxResult.No, false);
            // 右侧按钮（继续/是），默认聚焦
            msgBox.AddCustomButton(yesText, yesIcon, () => result = MessageBoxResult.Yes, true);

            msgBox.ShowDialog();
            return result;
        }

        private void AddButton(string content, MessageBoxResult result, bool isDefault, string iconOverride = null)
        {
            var btn = new Button
            {
                Width = 90,
                Height = 32,
                Margin = new Thickness(10, 0, 0, 0),
                IsDefault = isDefault,
                MinWidth = 90
            };

            string icon = iconOverride ?? "";
            if (string.IsNullOrEmpty(icon))
            {
                if (result == MessageBoxResult.Yes || result == MessageBoxResult.OK)
                    icon = "\uE73E"; // 勾号
                else if (result == MessageBoxResult.No || result == MessageBoxResult.Cancel)
                    icon = "\uE711"; // 叉号
            }

            var stackPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (!string.IsNullOrEmpty(icon))
            {
                stackPanel.Children.Add(new TextBlock
                {
                    Text = icon,
                    FontFamily = App.IconFontFamily,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }

            stackPanel.Children.Add(new TextBlock { Text = content, VerticalAlignment = VerticalAlignment.Center });

            btn.Content = stackPanel;
            btn.Click += (s, e) =>
            {
                _result = result;
                this.Close();
            };

            ButtonPanel.Children.Add(btn);
        }

        /// <summary>
        /// 通用的自定义按钮：图标 + 文字，点击时执行回调（由调用方自行关闭对话框）。
        /// </summary>
        private void AddCustomButton(string text, string icon, Action onClick, bool isDefault)
        {
            var btn = new Button
            {
                Width = 90,
                Height = 32,
                Margin = new Thickness(10, 0, 0, 0),
                IsDefault = isDefault,
                MinWidth = 90
            };

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrEmpty(icon))
            {
                stackPanel.Children.Add(new TextBlock
                {
                    Text = icon,
                    FontFamily = App.IconFontFamily,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }

            stackPanel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

            btn.Content = stackPanel;
            btn.Click += (s, e) =>
            {
                onClick?.Invoke();
                this.Close();
            };

            ButtonPanel.Children.Add(btn);
        }
    }
}
