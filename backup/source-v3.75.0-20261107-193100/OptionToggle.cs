using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RandomDrawer
{
    /// <summary>
    /// 选项页 / 欢迎页的开关。
    ///
    /// 3.61：外观从「两个圆形选择框 + 开启 / 关闭文字」改成 iOS 风格的胶囊开关
    /// （灰色 / 蓝色轨道 + 白色圆点），点整块都能切换。
    /// 3.62：用户点击时滑块平滑滑到另一侧、轨道颜色渐变；代码里直接赋值
    /// （例如加载设置）不播动画。禁用动画时一律立即切换。
    ///
    /// 仍然继承 <see cref="CheckBox"/>：代码里原有的 <c>Chk.IsChecked</c> /
    /// <c>Checked</c> / <c>Unchecked</c> / <c>Click</c> 全部照常可用。
    /// </summary>
    public class OptionToggle : CheckBox
    {
        /// <summary>滑块从左到右的位移（44 宽 - 两侧各 3 - 滑块 18）。</summary>
        private const double KnobTravel = 20;

        private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(160);

        /// <summary>这一次 IsChecked 变化是否由用户点击 / 空格触发（决定要不要播动画）。</summary>
        private bool _userToggle;

        static OptionToggle()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(OptionToggle),
                new FrameworkPropertyMetadata(typeof(OptionToggle)));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            SyncVisual(animate: false);
        }

        protected override void OnToggle()
        {
            // 先记住旧颜色：base.OnToggle() 之后触发器已经换成新颜色了
            Color? oldColor = GetTrackColor();

            _userToggle = true;
            try { base.OnToggle(); }
            finally { _userToggle = false; }

            SyncVisual(animate: true, fromTrackColor: oldColor);
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            if (!_userToggle) SyncVisual(animate: false);
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            if (!_userToggle) SyncVisual(animate: false);
        }

        private TranslateTransform KnobShift =>
            Template?.FindName("KnobShift", this) as TranslateTransform;

        private Color? GetTrackColor()
        {
            if (Template?.FindName("Track", this) is Border track
                && track.Background is SolidColorBrush brush)
                return brush.Color;
            return null;
        }

        /// <summary>把滑块移到目标位置；animate 时顺带做轨道颜色渐变。</summary>
        private void SyncVisual(bool animate, Color? fromTrackColor = null)
        {
            try
            {
                bool on = IsChecked == true;
                double targetX = on ? KnobTravel : 0;

                var shift = KnobShift;
                if (shift != null)
                {
                    // 3.63：先把「当前看到的实际位置」写回基值，再排新动画。
                    // 以前动画结束后基值仍停在 0，下一次关闭时清除动画的一瞬间
                    // 滑块会被拉回 0 —— 表现就是「开有动画、关没有动画」。
                    double fromX = shift.X;
                    shift.BeginAnimation(TranslateTransform.XProperty, null);
                    shift.X = fromX;

                    if (!animate || App.IsAnimationDisabled)
                    {
                        shift.X = targetX;
                    }
                    else
                    {
                        shift.BeginAnimation(TranslateTransform.XProperty,
                            new DoubleAnimation(targetX, AnimDuration)
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                            });
                    }
                }

                if (!animate || App.IsAnimationDisabled) return;
                if (Template?.FindName("Track", this) is not Border track) return;

                Color toColor = (track.Background as SolidColorBrush)?.Color
                                ?? (on ? Colors.DodgerBlue : Color.FromRgb(0xD0, 0xD0, 0xD0));
                Color startColor = fromTrackColor ?? toColor;
                if (startColor == toColor) return;

                // 用本地画刷盖住触发器，做完动画再清掉，让主题触发器重新接管
                var brush = new SolidColorBrush(startColor);
                track.Background = brush;

                var colorAnim = new ColorAnimation(toColor, AnimDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                colorAnim.Completed += (s, e) =>
                {
                    try
                    {
                        // 快速连点时可能已经有新的画刷接手，别把新的清掉
                        if (ReferenceEquals(track.Background, brush))
                            track.ClearValue(Border.BackgroundProperty);
                    }
                    catch { }
                };
                brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
            }
            catch { }
        }
    }
}
