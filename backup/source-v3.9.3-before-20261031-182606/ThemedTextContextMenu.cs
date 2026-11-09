using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RandomDrawer
{
    /// <summary>文本框 / 密码框的统一右键菜单，按设置切换现代或经典主题。</summary>
    public sealed class ThemedTextContextMenu : ContextMenu
    {
        public ThemedTextContextMenu()
        {
            Items.Add(CreateItem("剪切", ApplicationCommands.Cut, "Ctrl+X"));
            Items.Add(CreateItem("复制", ApplicationCommands.Copy, "Ctrl+C"));
            Items.Add(CreateItem("粘贴", ApplicationCommands.Paste, "Ctrl+V"));
            Items.Add(new Separator());
            Items.Add(CreateItem("全选", ApplicationCommands.SelectAll, "Ctrl+A"));

            Opened += (s, e) => ApplyTheme();
        }

        private static MenuItem CreateItem(string header, ICommand command, string gesture)
        {
            return new MenuItem
            {
                Header = header,
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
