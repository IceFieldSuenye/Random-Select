using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// 悬浮提示气泡：样式与「历史记录页搜索框」上的提示完全一致
    /// （圆角 6、卡片底色 + 边框色跟随主题、12 号字、最多 300 宽、跟随鼠标且不越出容器）。
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

                _popup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = _popup.Child.DesiredSize;

                double x = positionInTarget.X + 16;
                double y = positionInTarget.Y + 22;
                if (x + size.Width > _placementTarget.ActualWidth - 8)
                    x = Math.Max(8, positionInTarget.X - size.Width - 16);
                if (y + size.Height > _placementTarget.ActualHeight - 8)
                    y = Math.Max(8, positionInTarget.Y - size.Height - 12);

                _popup.HorizontalOffset = x;
                _popup.VerticalOffset = y;
                if (!_popup.IsOpen) _popup.IsOpen = true;
            }
            catch { }
        }

        public void Hide()
        {
            try { if (_popup.IsOpen) _popup.IsOpen = false; } catch { }
        }
    }
}
