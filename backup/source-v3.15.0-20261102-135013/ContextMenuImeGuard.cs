using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace RandomDrawer
{
    /// <summary>
    /// 右键菜单弹出期间临时切断中文输入法。
    ///
    /// 菜单会抢走键盘焦点，输入法如果还挂在菜单上，关掉菜单后就会出现「卡输入法」：
    /// 打不出字、快捷键被输入法吃掉。这里在菜单打开时把窗口的输入法上下文解绑，
    /// 关闭后再挂回去；菜单本身也禁用输入法。
    /// </summary>
    internal static class ContextMenuImeGuard
    {
        [DllImport("imm32.dll")]
        private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        public static void Attach(ContextMenu menu)
        {
            if (menu == null) return;

            IntPtr hwnd = IntPtr.Zero;
            IntPtr savedImc = IntPtr.Zero;

            menu.Opened += (s, e) =>
            {
                try
                {
                    InputMethod.SetIsInputMethodEnabled(menu, false);

                    var target = menu.PlacementTarget as DependencyObject;
                    var window = (target != null ? Window.GetWindow(target) : null)
                                 ?? Application.Current?.MainWindow;
                    if (window == null) return;

                    hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero) return;

                    // 解绑输入法上下文 = 这一步菜单期间中文输入法彻底不参与
                    savedImc = ImmAssociateContext(hwnd, IntPtr.Zero);
                }
                catch { }
            };

            menu.Closed += (s, e) =>
            {
                try
                {
                    if (hwnd != IntPtr.Zero && savedImc != IntPtr.Zero)
                        ImmAssociateContext(hwnd, savedImc);
                }
                catch { }
                finally
                {
                    hwnd = IntPtr.Zero;
                    savedImc = IntPtr.Zero;
                    try { InputMethod.SetIsInputMethodEnabled(menu, true); } catch { }
                }
            };
        }
    }
}
