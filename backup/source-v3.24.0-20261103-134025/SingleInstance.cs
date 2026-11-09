using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RandomDrawer
{
    /// <summary>
    /// 单实例支持：再次启动程序时，把已有窗口带到前台，而不是弹“请勿重复打开”。
    /// </summary>
    internal static class SingleInstance
    {
        private const string ActivateMessageName = "RandomDrawer_Activate_v1";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        /// <summary>向已运行的实例广播“激活”消息。</summary>
        public static void BroadcastActivate()
        {
            try
            {
                uint msg = RegisterWindowMessage(ActivateMessageName);
                if (msg != 0) PostMessage(HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>在主窗口上挂接“激活”消息。</summary>
        public static void Hook(Window window)
        {
            if (window == null) return;
            try
            {
                var helper = new WindowInteropHelper(window);
                if (helper.Handle == IntPtr.Zero) return;

                uint target = RegisterWindowMessage(ActivateMessageName);
                var source = HwndSource.FromHwnd(helper.Handle);
                if (source == null) return;

                source.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (target != 0 && m == target)
                    {
                        handled = true;
                        window.Dispatcher.BeginInvoke(new Action(() => BringToFront(window)));
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
        }

        /// <summary>还原并聚焦窗口。</summary>
        public static void BringToFront(Window window)
        {
            if (window == null) return;
            try
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Show();
                window.Activate();

                // 置顶一次再取消，绕过前台窗口限制
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            }
            catch { }
        }
    }
}
