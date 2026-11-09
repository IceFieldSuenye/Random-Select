using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>外观主题。</summary>
    public enum ThemeMode
    {
        /// <summary>跟随系统（默认）。</summary>
        System = 0,
        /// <summary>浅色。</summary>
        Light = 1,
        /// <summary>深色。</summary>
        Dark = 2
    }

    /// <summary>
    /// 全局主题管理：
    ///   · 所有 XAML 中的颜色都已改为 {DynamicResource Xxx_######} 引用；
    ///   · 浅色值 = 原始颜色（保证浅色模式与旧版完全一致）；
    ///   · 深色值 = Dark 表中的对应颜色；
    ///   · 同时覆盖 WPF 系统画刷（SystemColors.*BrushKey），
    ///     让默认模板的 TextBox / ListBox / Menu / 滚动条等控件一起变深。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>主题变化通知（动态资源会自动更新，此事件用于需要自绘的代码）。</summary>
        public static event EventHandler ThemeChanged;

        public static ThemeMode Mode { get; private set; } = ThemeMode.System;
        public static bool IsDark { get; private set; }

        private static ResourceDictionary _theme;
        private static DispatcherTimer _timer;
        private static bool? _lastSystemLight;

        // 由 obj/_tools/theme-transform.ps1 生成的键集合（XAML 中引用的全部 DynamicResource 键）
        private static readonly string[] Keys =
        {
            "Bd_0078D7", "Bd_808080", "Bd_A91D1D", "Bd_ABADB3", "Bd_ADADAD", "Bd_C8C8C8",
            "Bd_CCCCCC", "Bd_D0D0D0", "Bd_DDDDDD", "Bd_E5E7EB", "Bd_EAECEF", "Bd_F1F3F5",
            "Bg_000000", "Bg_0078D7", "Bg_B71C1C", "Bg_C0C0C0", "Bg_C8C8C8", "Bg_CCE8FF",
            "Bg_D1D5DB", "Bg_D32F2F", "Bg_D6D6D6", "Bg_E0E0E0", "Bg_E1E1E1", "Bg_E53935",
            "Bg_E5E7EB", "Bg_E5F3FF", "Bg_ECECEC", "Bg_EEEEEE", "Bg_EFF6FF", "Bg_F0F0F0",
            "Bg_F3F4F6", "Bg_F5F5F5", "Bg_F9FAFB", "Bg_FBFCFD", "Bg_FFFFFF",
            "Fg_000000", "Fg_0078D4", "Fg_0078D7", "Fg_1F1F1F", "Fg_1F2937", "Fg_333333",
            "Fg_666666", "Fg_6B7280", "Fg_808080", "Fg_888888", "Fg_999999", "Fg_9A9A9A",
            "Fg_9CA3AF", "Fg_BBBBBB", "Fg_D1D5DB", "Fg_D32F2F", "Fg_F57C00", "Fg_FFFFFF",
            "Fl_000000", "Fl_005A9E", "Fl_0078D7", "Fl_707070", "Fl_D3D3D3", "Fl_F0F0F0", "Fl_FFFFFF",

            // 默认控件 / 浮动面板使用的语义色
            "Ui.ButtonFace", "Ui.ButtonBorder", "Ui.ButtonText", "Ui.ButtonHover", "Ui.ButtonPressed",
            "Ui.ScrollThumb", "Ui.ScrollThumbHover", "Ui.ScrollTrack", "Ui.FocusRing",
            "Ui.Backdrop", "Ui.CardBg", "Ui.CardBorder", "Ui.TitleText", "Ui.Separator",
            "Ui.InputBg", "Ui.SliderThumb", "Ui.AccentFill", "Ui.CheckMark", "Ui.ScrollArrow", "Ui.HoverSoft"
        };

        private static readonly Dictionary<string, string> DarkMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 背景
            ["Bg_000000"] = "#000000",
            ["Bg_0078D7"] = "#2F6FB0",
            ["Bg_B71C1C"] = "#7F1D1D",
            ["Bg_C0C0C0"] = "#4A4A4C",
            ["Bg_C8C8C8"] = "#4A4A4C",
            ["Bg_CCE8FF"] = "#24507C",
            ["Bg_D1D5DB"] = "#4A4A4C",
            ["Bg_D32F2F"] = "#B3261E",
            ["Bg_D6D6D6"] = "#444446",
            ["Bg_E0E0E0"] = "#3A3A3C",
            ["Bg_E1E1E1"] = "#3A3A3C",
            ["Bg_E53935"] = "#C62828",
            ["Bg_E5E7EB"] = "#333335",
            ["Bg_E5F3FF"] = "#1C3A5C",
            ["Bg_ECECEC"] = "#303032",
            ["Bg_EEEEEE"] = "#2A2A2C",
            ["Bg_EFF6FF"] = "#1C3A5C",
            ["Bg_F0F0F0"] = "#242425",
            ["Bg_F3F4F6"] = "#2C2C2E",
            ["Bg_F5F5F5"] = "#242425",
            ["Bg_F9FAFB"] = "#232324",
            ["Bg_FBFCFD"] = "#232324",
            ["Bg_FFFFFF"] = "#1E1E1E",

            // 边框
            ["Bd_0078D7"] = "#4FA3F7",
            ["Bd_808080"] = "#5A5A5E",
            ["Bd_A91D1D"] = "#7F1D1D",
            ["Bd_ABADB3"] = "#4A4A4E",
            ["Bd_ADADAD"] = "#4A4A4E",
            ["Bd_C8C8C8"] = "#4A4A4C",
            ["Bd_CCCCCC"] = "#3C3C3E",
            ["Bd_D0D0D0"] = "#3C3C3E",
            ["Bd_DDDDDD"] = "#3C3C3E",
            ["Bd_E5E7EB"] = "#3C3C3E",
            ["Bd_EAECEF"] = "#3C3C3E",
            ["Bd_F1F3F5"] = "#2E2E30",

            // 文字
            ["Fg_000000"] = "#E8E8E8",
            ["Fg_0078D4"] = "#4FA3F7",
            ["Fg_0078D7"] = "#4FA3F7",
            ["Fg_1F1F1F"] = "#E8E8E8",
            ["Fg_1F2937"] = "#E8E8E8",
            ["Fg_333333"] = "#E8E8E8",
            ["Fg_666666"] = "#ACACAC",
            ["Fg_6B7280"] = "#ACACAC",
            ["Fg_808080"] = "#9E9E9E",
            ["Fg_888888"] = "#9E9E9E",
            ["Fg_999999"] = "#9E9E9E",
            ["Fg_9A9A9A"] = "#9E9E9E",
            ["Fg_9CA3AF"] = "#9E9E9E",
            ["Fg_7B1FA2"] = "#C792EA",
            ["Fg_BBBBBB"] = "#8A8A8A",
            ["Fg_D1D5DB"] = "#6E6E6E",
            ["Fg_D32F2F"] = "#FF7B72",
            ["Fg_F57C00"] = "#FFA657",
            ["Fg_FFFFFF"] = "#FFFFFF",

            // 图形填充
            ["Fl_000000"] = "#E8E8E8",
            ["Fl_005A9E"] = "#7FB9F0",
            ["Fl_0078D7"] = "#4FA3F7",
            ["Fl_707070"] = "#9A9A9A",
            ["Fl_D3D3D3"] = "#3A3A3C",
            ["Fl_F0F0F0"] = "#242425",
            ["Fl_FFFFFF"] = "#FFFFFF",

            // 语义色
            ["Ui.ButtonFace"] = "#333336",
            ["Ui.ButtonBorder"] = "#4A4A4E",
            ["Ui.ButtonText"] = "#E8E8E8",
            ["Ui.ButtonHover"] = "#1FFFFFFF",
            ["Ui.ButtonPressed"] = "#33FFFFFF",
            ["Ui.ScrollThumb"] = "#6E6E76",
            ["Ui.ScrollThumbHover"] = "#B4B4BC",
            ["Ui.ScrollTrack"] = "#3A3A3E",
            ["Ui.FocusRing"] = "#4FA3F7",
            ["Ui.Backdrop"] = "#66000000",
            ["Ui.CardBg"] = "#252526",
            ["Ui.CardBorder"] = "#3C3C3E",
            ["Ui.TitleText"] = "#F0F0F0",
            ["Ui.Separator"] = "#3C3C3E",
            ["Ui.InputBg"] = "#2B2B2D",
            ["Ui.SliderThumb"] = "#2E2E31",
            ["Ui.AccentFill"] = "#2F6FB0",
            ["Ui.CheckMark"] = "#FFFFFF",
            ["Ui.ScrollArrow"] = "#A8A8AE",
            ["Ui.HoverSoft"] = "#3A3A3C"
        };

        private static readonly Dictionary<string, string> ExtraLight =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ui.ButtonFace"] = "#E6E6E9",
            ["Ui.ButtonBorder"] = "#C8C8CC",
            ["Ui.ButtonText"] = "#1A1A1A",
            ["Ui.ButtonHover"] = "#1C000000",
            ["Ui.ButtonPressed"] = "#33000000",
            ["Ui.ScrollThumb"] = "#A6AAB2",
            ["Ui.ScrollThumbHover"] = "#7C8088",
            ["Ui.ScrollTrack"] = "#E4E6EA",
            ["Ui.FocusRing"] = "#0078D7",
            ["Ui.Backdrop"] = "#40000000",
            ["Ui.CardBg"] = "#FFFFFF",
            ["Ui.CardBorder"] = "#DDDDDD",
            ["Ui.TitleText"] = "#1A1A1A",
            ["Ui.Separator"] = "#E5E7EB",
            ["Ui.InputBg"] = "#FFFFFF",
            ["Ui.SliderThumb"] = "#FFFFFF",
            ["Ui.AccentFill"] = "#0078D7",
            ["Ui.CheckMark"] = "#FFFFFF",
            ["Ui.ScrollArrow"] = "#7C7C82",
            ["Ui.HoverSoft"] = "#ECECEC"
        };

        // ================================================================
        // 初始化 / 应用
        // ================================================================

        public static void Initialize()
        {
            if (_theme != null) return;
            _theme = new ResourceDictionary();
            foreach (var key in Keys) _theme[key] = new SolidColorBrush(SafeColor(GetLightHex(key), Colors.White));

            try
            {
                if (Application.Current != null)
                    Application.Current.Resources.MergedDictionaries.Insert(0, _theme);
            }
            catch { }

            try
            {
                string saved = IniHelper.ReadValue("UI", "Theme", "system", App.SettingsFile);
                Mode = ParseMode(saved);
            }
            catch { Mode = ThemeMode.System; }

            _lastSystemLight = IsSystemLightTheme();
            Apply();

            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += (s, e) =>
                {
                    if (Mode != ThemeMode.System) return;
                    bool light = IsSystemLightTheme();
                    if (_lastSystemLight != light) { _lastSystemLight = light; Apply(); }
                };
                _timer.Start();
            }
        }

        public static void SetMode(ThemeMode mode)
        {
            RuntimeLog.Info($"切换主题：{ModeToText(mode)}");
            Mode = mode;
            try { IniHelper.WriteValue("UI", "Theme", ModeToText(mode), App.SettingsFile); } catch { }
            _lastSystemLight = IsSystemLightTheme();
            Apply();
        }

        /// <summary>当前生效的主题（System 会被解析成 Light / Dark）。</summary>
        public static ThemeMode EffectiveMode => IsDark ? ThemeMode.Dark : ThemeMode.Light;

        public static void Apply()
        {
            if (_theme == null) return;

            bool dark = Mode == ThemeMode.Dark || (Mode == ThemeMode.System && !(_lastSystemLight ?? true));
            IsDark = dark;

            foreach (var key in Keys)
            {
                string hex = dark ? GetDarkHex(key) : GetLightHex(key);
                SetBrush(key, SafeColor(hex, Colors.White));
            }

            ApplySystemColors(dark);
            ApplyWindowChromeAll();

            try { ThemeChanged?.Invoke(null, EventArgs.Empty); } catch { }
        }

        // ================================================================
        // 标题栏深色（Windows 11 / DWM）
        // ================================================================

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        public static void ApplyWindowChrome(Window w)
        {
            if (w == null) return;

            IntPtr hwnd = IntPtr.Zero;
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(w);
                hwnd = helper.Handle;
            }
            catch { }

            if (hwnd == IntPtr.Zero) return;

            SetDarkCaption(hwnd, IsDark);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_FRAME = 0x0400;

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;

        private static void SetDarkCaption(IntPtr hwnd, bool dark)
        {
            try
            {
                int value = dark ? 1 : 0;

                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 18985+ / Win11），19 = 更早的 1809~1903
                if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
                {
                    // 20 不可用（旧版 Win10），回退到 19
                    value = dark ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
                }
            }
            catch { }

            // Windows 10：仅设置属性常常只刷新了标题文字，背景与按钮不重绘。
            // 不要临时摘挂 WS_CAPTION：那会改变非客户区高度，导致切换主题时界面跳动。
            // 仅更新 DWM 属性并重绘，保持客户端区域尺寸稳定。
            try { InvalidateRect(hwnd, IntPtr.Zero, true); } catch { }
            // 强制立即重绘非客户区，解决 Win10 启动后标题栏要等失去焦点才变深的问题
            try { RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_FRAME | RDW_UPDATENOW); } catch { }
        }

        private static void ApplyWindowChromeAll()
        {
            try
            {
                if (Application.Current == null) return;
                foreach (Window w in Application.Current.Windows) ApplyWindowChrome(w);
            }
            catch { }
        }

        private static void SetBrush(string key, Color color)
        {
            try
            {
                if (_theme[key] is SolidColorBrush b && !b.IsFrozen)
                {
                    if (b.Color != color) b.Color = color;
                    return;
                }
            }
            catch { }

            try { _theme[key] = new SolidColorBrush(color); } catch { }
        }

        /// <summary>覆盖 WPF 系统画刷，让默认模板控件跟随主题。</summary>
        private static void ApplySystemColors(bool dark)
        {
            try
            {
                SetSystemBrush(SystemColors.WindowBrushKey, dark ? "#1E1E1E" : "#FFFFFF");
                SetSystemBrush(SystemColors.WindowTextBrushKey, dark ? "#E8E8E8" : "#1A1A1A");
                SetSystemBrush(SystemColors.ControlBrushKey, dark ? "#252526" : "#F0F0F0");
                SetSystemBrush(SystemColors.ControlTextBrushKey, dark ? "#E8E8E8" : "#1A1A1A");
                SetSystemBrush(SystemColors.GrayTextBrushKey, dark ? "#9E9E9E" : "#6B6B6B");
                SetSystemBrush(SystemColors.HighlightBrushKey, dark ? "#2A5D8F" : "#CCE8FF");
                SetSystemBrush(SystemColors.HighlightTextBrushKey, dark ? "#FFFFFF" : "#1A1A1A");
                SetSystemBrush(SystemColors.MenuBrushKey, dark ? "#252526" : "#FFFFFF");
                SetSystemBrush(SystemColors.MenuTextBrushKey, dark ? "#E8E8E8" : "#1A1A1A");
                SetSystemBrush(SystemColors.MenuBarBrushKey, dark ? "#252526" : "#F5F5F5");
                SetSystemBrush(SystemColors.ControlLightBrushKey, dark ? "#3A3A3C" : "#F0F0F0");
                SetSystemBrush(SystemColors.ControlLightLightBrushKey, dark ? "#444446" : "#FFFFFF");
                SetSystemBrush(SystemColors.ControlDarkBrushKey, dark ? "#2A2A2C" : "#C0C0C0");
                SetSystemBrush(SystemColors.ControlDarkDarkBrushKey, dark ? "#1A1A1C" : "#909090");
                SetSystemBrush(SystemColors.ActiveBorderBrushKey, dark ? "#4A4A4C" : "#C0C0C0");
                SetSystemBrush(SystemColors.InactiveBorderBrushKey, dark ? "#3C3C3E" : "#DDDDDD");
                SetSystemBrush(SystemColors.AppWorkspaceBrushKey, dark ? "#1E1E1E" : "#FFFFFF");
                SetSystemBrush(SystemColors.ScrollBarBrushKey, dark ? "#2A2A2C" : "#F0F0F0");
                SetSystemBrush(SystemColors.InfoBrushKey, dark ? "#2D2D30" : "#FFFFE1");
                SetSystemBrush(SystemColors.InfoTextBrushKey, dark ? "#E8E8E8" : "#1A1A1A");
            }
            catch { }
        }

        private static void SetSystemBrush(ResourceKey key, string hex)
        {
            try
            {
                Color c = SafeColor(hex, Colors.White);
                if (_theme[key] is SolidColorBrush b && !b.IsFrozen)
                {
                    if (b.Color != c) b.Color = c;
                    return;
                }
            }
            catch { }

            try { _theme[key] = new SolidColorBrush(SafeColor(hex, Colors.White)); } catch { }
        }

        // ================================================================
        // 颜色工具
        // ================================================================

        private static string GetLightHex(string key)
        {
            if (ExtraLight.TryGetValue(key, out string v)) return v;
            int idx = key.IndexOf('_');
            if (idx >= 0 && idx + 1 < key.Length) return "#" + key.Substring(idx + 1);
            return "#FFFFFF";
        }

        private static string GetDarkHex(string key)
        {
            if (DarkMap.TryGetValue(key, out string v)) return v;
            return GetLightHex(key);
        }

        /// <summary>取当前主题下的画刷（代码里需要设置 Foreground/Background 时使用）。</summary>
        public static Brush GetBrush(string key)
        {
            try
            {
                if (_theme != null && _theme[key] is SolidColorBrush b) return b;
            }
            catch { }
            return new SolidColorBrush(SafeColor(GetLightHex(key), Colors.Black));
        }

        /// <summary>把十六进制颜色按当前主题解析：浅色主题原样返回，深色主题返回深色映射。</summary>
        public static Brush GetThemedBrushFromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Brushes.Transparent;
            string key = "Fg_" + hex.TrimStart('#').ToUpperInvariant();
            if (DarkMap.TryGetValue(key, out string d) && IsDark) return new SolidColorBrush(SafeColor(d, Colors.Gray));
            return new SolidColorBrush(SafeColor(hex, Colors.Gray));
        }

        public static Color SafeColor(string hex, Color fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return fallback;
                var obj = ColorConverter.ConvertFromString(hex);
                if (obj is Color c) return c;
            }
            catch { }
            return fallback;
        }

        public static ThemeMode ParseMode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ThemeMode.System;
            switch (text.Trim().ToLowerInvariant())
            {
                case "dark":
                case "深色": return ThemeMode.Dark;
                case "light":
                case "浅色": return ThemeMode.Light;
                default: return ThemeMode.System;
            }
        }

        public static string ModeToText(ThemeMode mode)
        {
            switch (mode)
            {
                case ThemeMode.Dark: return "dark";
                case ThemeMode.Light: return "light";
                default: return "system";
            }
        }

        /// <summary>读取系统（任务栏 / 应用）是否为浅色主题。</summary>
        public static bool IsSystemLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int i) return i != 0;
                    }
                }
            }
            catch { }
            return true;
        }
    }
}
