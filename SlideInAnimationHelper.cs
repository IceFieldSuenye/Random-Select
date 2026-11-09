using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RandomDrawer
{
    public static class SlideInAnimationHelper
    {
        private static readonly TimeSpan DefaultEnterDuration = TimeSpan.FromMilliseconds(280);
        /// <summary>3.45：退场统一加速（返回主界面时更利落）。</summary>
        private static readonly TimeSpan DefaultExitDuration = TimeSpan.FromMilliseconds(150);

        // ================================================================
        // PlaySlideIn：3 参数（默认时长）
        // ================================================================
        public static void PlaySlideIn(FrameworkElement root, double fromX, double fromY, Action onComplete)
            => PlaySlideIn(root, fromX, fromY, DefaultEnterDuration, onComplete);

        // ================================================================
        // PlaySlideIn：5 参数（指定时长）★ 新增
        // ================================================================
        public static void PlaySlideIn(FrameworkElement root, double fromX, double fromY,
                                       TimeSpan duration, Action onComplete)
        {
            if (root == null) { onComplete?.Invoke(); return; }

            var t = root.RenderTransform as TranslateTransform;
            if (t == null) { t = new TranslateTransform(); root.RenderTransform = t; }

            root.BeginAnimation(UIElement.OpacityProperty, null);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);

            // 禁用动画：直接跳到终态
            if (App.IsAnimationDisabled)
            {
                root.Opacity = 1;
                t.X = 0;
                t.Y = 0;
                onComplete?.Invoke();
                return;
            }

            root.Opacity = 0;
            t.X = fromX;
            t.Y = fromY;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var opaAnim = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            var xAnim = new DoubleAnimation(fromX, 0, duration) { EasingFunction = ease };
            var yAnim = new DoubleAnimation(fromY, 0, duration) { EasingFunction = ease };

            int completed = 0;
            Action check = () =>
            {
                if (++completed < 3) return;
                try
                {
                    // 收尾：把动画值落回属性基准值，否则下一次退场动画读到的会是旧的 0
                    root.BeginAnimation(UIElement.OpacityProperty, null);
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    root.Opacity = 1;
                    t.X = 0;
                    t.Y = 0;
                }
                catch { }
                onComplete?.Invoke();
            };
            opaAnim.Completed += (s, e) => check();
            xAnim.Completed += (s, e) => check();
            yAnim.Completed += (s, e) => check();

            root.BeginAnimation(UIElement.OpacityProperty, opaAnim);
            t.BeginAnimation(TranslateTransform.XProperty, xAnim);
            t.BeginAnimation(TranslateTransform.YProperty, yAnim);
        }

        // ================================================================
        // PlaySlideOut：3 参数
        // ================================================================
        public static void PlaySlideOut(FrameworkElement root, double toX, double toY, Action onComplete)
            => PlaySlideOut(root, toX, toY, DefaultExitDuration, onComplete);

        // ================================================================
        // PlaySlideOut：4 参数（指定时长）★ 新增（保持一致）
        // ================================================================
        public static void PlaySlideOut(FrameworkElement root, double toX, double toY,
                                        TimeSpan duration, Action onComplete)
        {
            if (root == null) { onComplete?.Invoke(); return; }

            var t = root.RenderTransform as TranslateTransform;
            if (t == null) { t = new TranslateTransform(); root.RenderTransform = t; }

            // 必须先读当前有效值，再清动画；否则会读到属性基准值（0），退场时页面会瞬间变透明
            double fromOpacity = root.Opacity;
            double fromX = t.X;
            double fromY = t.Y;

            root.BeginAnimation(UIElement.OpacityProperty, null);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);

            // 禁用动画：直接跳终态并复位
            if (App.IsAnimationDisabled)
            {
                root.Opacity = 1;
                t.X = 0;
                t.Y = 0;
                onComplete?.Invoke();
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

            var opaAnim = new DoubleAnimation(fromOpacity, 0, duration)
            { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
            var xAnim = new DoubleAnimation(fromX, toX, duration) { EasingFunction = ease };
            var yAnim = new DoubleAnimation(fromY, toY, duration) { EasingFunction = ease };

            opaAnim.Completed += (s, e) =>
            {
                try
                {
                    root.BeginAnimation(UIElement.OpacityProperty, null);
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    root.Opacity = 1;
                    t.X = 0;
                    t.Y = 0;
                }
                catch { }
                onComplete?.Invoke();
            };

            root.BeginAnimation(UIElement.OpacityProperty, opaAnim);
            t.BeginAnimation(TranslateTransform.XProperty, xAnim);
            t.BeginAnimation(TranslateTransform.YProperty, yAnim);
        }

        // ================================================================
        // PlayFadeOut：纯淡出
        // ================================================================
        public static void PlayFadeOut(FrameworkElement root, Action onComplete)
        {
            if (root == null) { onComplete?.Invoke(); return; }

            var t = root.RenderTransform as TranslateTransform;
            if (t == null) { t = new TranslateTransform(); root.RenderTransform = t; }

            double fromOpacity = root.Opacity;
            root.BeginAnimation(UIElement.OpacityProperty, null);

            if (App.IsAnimationDisabled)
            {
                root.Opacity = 1;
                t.X = 0;
                t.Y = 0;
                onComplete?.Invoke();
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var opaAnim = new DoubleAnimation(fromOpacity, 0, DefaultExitDuration)
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd
            };
            opaAnim.Completed += (s, e) =>
            {
                try
                {
                    root.BeginAnimation(UIElement.OpacityProperty, null);
                    t.BeginAnimation(TranslateTransform.XProperty, null);
                    t.BeginAnimation(TranslateTransform.YProperty, null);
                    root.Opacity = 1;
                    t.X = 0;
                    t.Y = 0;
                }
                catch { }
                onComplete?.Invoke();
            };
            root.BeginAnimation(UIElement.OpacityProperty, opaAnim);
        }
    }
}