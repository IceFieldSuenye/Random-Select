using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// 悬浮提示气泡：样式与「历史记录页搜索框」上的提示完全一致
    /// （圆角 6、卡片底色 + 边框色跟随主题、12 号字、最多 300 宽）。
    ///
    /// 位置：跟随鼠标，允许显示到主窗口之外（气泡是独立 Popup），
    /// 但会被限制在当前屏幕的工作区内 —— 靠近窗口边缘时自动翻到鼠标左侧 / 上方。
    ///
    /// 用法：
    ///   var hint = new HintBubble(placementTarget, "提示文字");
    ///   在 MouseEnter / MouseMove 里 hint.Show(e.GetPosition(placementTarget));
    ///   在 MouseLeave 里 hint.Hide();
    /// </summary>
    public sealed class HintBubble
    {
        private readonly Popup _popup;
        private readonly FrameworkElement _placementTarget;
        private readonly TextBlock _text;

        private const uint MonitorDefaultToNearest = 2;
        private const double GapX = 16;
        private const double GapY = 22;
        private const double ScreenMargin = 8;

        public HintBubble(FrameworkElement placementTarget, string text)
        {
            _placementTarget = placementTarget;
            _text = new TextBlock
            {
                Text = text ?? "",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.TitleText");

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                Child = _text,
                IsHitTestVisible = false
            };
            border.SetResourceReference(Border.BackgroundProperty, "Ui.CardBg");
            border.SetResourceReference(Border.BorderBrushProperty, "Ui.CardBorder");

            _popup = new Popup
            {
                Child = border,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
                Focusable = false,
                Placement = PlacementMode.Relative,
                PlacementTarget = placementTarget
            };
        }

        /// <summary>气泡文字（可在运行时替换）。</summary>
        public string Text
        {
            get => _text.Text;
            set => _text.Text = value ?? "";
        }

        public bool IsOpen => _popup.IsOpen;

        /// <summary>在指定位置附近显示气泡（坐标相对于 <c>placementTarget</c>）。</summary>
        public void Show(Point positionInTarget)
        {
            try
            {
                if (_placementTarget == null) return;
                if (_placementTarget.ActualWidth <= 0 && _placementTarget.ActualHeight <= 0) return;
                if (!_placementTarget.IsVisible) return;

                _popup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = _popup.Child.DesiredSize;

                double dpiX = 1, dpiY = 1;
                if (PresentationSource.FromVisual(_placementTarget) is HwndSource source
                    && source.CompositionTarget != null)
                {
                    var m = source.CompositionTarget.TransformToDevice;
                    if (m.M11 > 0) dpiX = m.M11;
                    if (m.M22 > 0) dpiY = m.M22;
                }

                Point targetScreen;
                try { targetScreen = _placementTarget.PointToScreen(new Point(0, 0)); }
                catch { return; }

                // 全部换算成屏幕像素（PointToScreen 返回的就是设备像素）
                double cursorX = targetScreen.X + (positionInTarget.X * dpiX);
                double cursorY = targetScreen.Y + (positionInTarget.Y * dpiY);
                double w = size.Width * dpiX;
                double h = size.Height * dpiY;

                ScreenRect work = GetWorkArea(cursorX, cursorY);

                // 默认显示在光标右下方；右/下放不下就翻到左侧 / 上方
                double x = cursorX + (GapX * dpiX);
                double y = cursorY + (GapY * dpiY);

                if (x + w > work.Right - (ScreenMargin * dpiX)) x = cursorX - w - (GapX * dpiX);
                if (y + h > work.Bottom - (ScreenMargin * dpiY)) y = cursorY - h - (GapY * dpiY);

                // 最后夹到屏幕工作区内：允许超出主窗口，但不允许超出屏幕
                double minX = work.Left + (ScreenMargin * dpiX);
                double maxX = work.Right - w - (ScreenMargin * dpiX);
                double minY = work.Top + (ScreenMargin * dpiY);
                double maxY = work.Bottom - h - (ScreenMargin * dpiY);

                x = maxX >= minX ? Math.Max(minX, Math.Min(x, maxX)) : minX;
                y = maxY >= minY ? Math.Max(minY, Math.Min(y, maxY)) : minY;

                _popup.HorizontalOffset = (x - targetScreen.X) / dpiX;
                _popup.VerticalOffset = (y - targetScreen.Y) / dpiY;
                if (!_popup.IsOpen) _popup.IsOpen = true;
            }
            catch { }
        }

        public void Hide()
        {
            try { if (_popup.IsOpen) _popup.IsOpen = false; } catch { }
        }

        // ================================================================
        // 屏幕工作区（多显示器按鼠标所在那一块算）
        // ================================================================

        private struct ScreenRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private static ScreenRect GetWorkArea(double screenX, double screenY)
        {
            var fallback = new ScreenRect
            {
                Left = 0,
                Top = 0,
                Right = (int)Math.Round(SystemParameters.PrimaryScreenWidth),
                Bottom = (int)Math.Round(SystemParameters.PrimaryScreenHeight)
            };

            try
            {
                var pt = new POINT { X = (int)Math.Round(screenX), Y = (int)Math.Round(screenY) };
                IntPtr monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return fallback;

                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(monitor, ref info)) return fallback;

                var work = info.rcWork;
                if (work.Right <= work.Left || work.Bottom <= work.Top) return fallback;

                return new ScreenRect { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Bottom };
            }
            catch { return fallback; }
        }
    }
}
