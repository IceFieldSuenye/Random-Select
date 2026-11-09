using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>文本框 / 密码框的统一右键菜单，按设置切换现代或经典主题。</summary>
    public sealed class ThemedTextContextMenu : ContextMenu
    {
        public ThemedTextContextMenu()
        {
            Items.Add(CreateItem("剪切", "\uE8C6", ApplicationCommands.Cut, "Ctrl+X"));
            Items.Add(CreateItem("复制", "\uE8C8", ApplicationCommands.Copy, "Ctrl+C"));
            Items.Add(CreateItem("粘贴", "\uE77F", ApplicationCommands.Paste, "Ctrl+V"));
            Items.Add(new Separator());
            Items.Add(CreateItem("全选", "\uE8B3", ApplicationCommands.SelectAll, "Ctrl+A"));

            Opened += (s, e) => ApplyTheme();
        }

        private static MenuItem CreateItem(string header, string iconGlyph, ICommand command, string gesture)
        {
            return new MenuItem
            {
                Header = header,
                Icon = new TextBlock
                {
                    Text = iconGlyph,
                    FontFamily = (FontFamily)Application.Current.TryFindResource("AppIconFont"),
                    FontSize = 14,
                    Foreground = ThemeManager.GetBrush("Fg_666666"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Command = command,
                InputGestureText = gesture
            };
        }

        private void ApplyTheme()
        {
            bool modern = App.UseModernContextMenu;
            Style = TryFindResource(modern ? "Win11ContextMenuStyle" : "LegacyContextMenuStyle") as Style;

            var itemStyle = TryFindResource(modern ? "Win11MenuItemStyle" : "LegacyMenuItemStyle") as Style;
            var separatorStyle = TryFindResource(modern ? "Win11SeparatorStyle" : "LegacySeparatorStyle") as Style;

            foreach (object item in Items)
            {
                if (item is MenuItem menuItem)
                {
                    if (itemStyle != null) menuItem.Style = itemStyle;
                    menuItem.CommandTarget = PlacementTarget;
                }
                else if (item is Separator separator && separatorStyle != null)
                {
                    separator.Style = separatorStyle;
                }
            }
        }
    }
}
