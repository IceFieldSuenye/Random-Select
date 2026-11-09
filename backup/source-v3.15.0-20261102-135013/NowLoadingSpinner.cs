using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RandomDrawer
{
    /// <summary>
    /// Windows 10 风格加载动画（角度关键帧与 Loading/NowLoading.cpp 一致）。
    /// 用 WPF 动画（变换 + 透明度）驱动，动画在渲染线程播放，
    /// 即使 UI 线程忙着生成内容（例如一次抽 9999 次）也能持续转圈。
    /// </summary>
    public class NowLoadingSpinner : Grid
    {
        private const int DotCount = 6;
        private const double DotDuration = 3.470;
        private const double DelayStep = 0.167;
        private const double Cycle = DelayStep * (DotCount - 1) + DotDuration;
        private const double FadeOutTime = 3.220;

        /// <summary>关键帧采样间隔（秒）。</summary>
        private const double SampleStep = 0.04;

        private static readonly double[,] Segments =
        {
            { 0.000, -110.0, 0.433,   10.0, 0.02, 0.33, 0.38, 0.77 },
            { 0.433,   10.0, 1.200,   93.0, 0.00, 0.00, 1.00, 1.00 },
            { 1.200,   93.0, 1.617,  205.0, 0.57, 0.17, 0.95, 0.75 },
            { 1.617,  205.0, 2.017,  357.0, 0.00, 0.19, 0.07, 0.72 },
            { 2.017,  357.0, 2.783,  439.0, 0.00, 0.00, 1.00, 1.00 },
            { 2.783,  439.0, 3.217,  585.0, 0.00, 0.00, 0.95, 0.37 },
            { 3.217,  585.0, 3.470,  720.0, 0.00, 0.00, 1.00, 1.00 }
        };

        private readonly Canvas _canvas = new Canvas();
        private readonly Ellipse[] _dots = new Ellipse[DotCount];
        private readonly RotateTransform[] _rotates = new RotateTransform[DotCount];

        // 冻结的关键帧动画（可跨实例共享，直接交给渲染线程播放）
        private static readonly DoubleAnimationUsingKeyFrames[] AngleAnimations = BuildAngleAnimations();
        private static readonly DoubleAnimationUsingKeyFrames[] OpacityAnimations = BuildOpacityAnimations();

        private double _radius = 7.0;
        private double _dotRadius = 1.0;
        private Brush _fill = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private bool _running;

        // ── 「禁用动画」时改用 \uF16A 图标持续旋转（圆点那种关键帧动画不播） ──
        /// <summary>图标模式下的旋转一圈时间。</summary>
        public static readonly TimeSpan IconSpinDuration = TimeSpan.FromSeconds(2);

        /// <summary>图标模式下统一使用的字形。</summary>
        public const string IconGlyph = "\uF16A";

        private readonly TextBlock _icon = new TextBlock
        {
            Text = IconGlyph,
            FontFamily = App.IconFontFamily,
            FontSize = 100,          // 实际大小交给 Viewbox 缩放
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        private readonly RotateTransform _iconRotate = new RotateTransform(0);
        private readonly Viewbox _iconView;

        public NowLoadingSpinner()
        {
            Width = 18;
            Height = 18;

            _icon.Foreground = _fill;
            _icon.RenderTransform = _iconRotate;
            _iconView = new Viewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _icon,
                Visibility = Visibility.Collapsed
            };

            Children.Add(_canvas);
            Children.Add(_iconView);
            BuildDots();
        }

        /// <summary>当前用的是不是「图标 + 匀速旋转」模式（禁用动画时启用）。</summary>
        public static bool UseIconMode => App.IsAnimationDisabled;

        /// <summary>当前是否在转（测试用）。</summary>
        public bool IsRunning => _running;

        /// <summary>当前显示的是不是「图标 + 匀速旋转」（禁用动画时才是）。</summary>
        public bool IsIconMode => _iconView != null && _iconView.Visibility == Visibility.Visible;

        /// <summary>图标模式的旋转周期（测试用）。</summary>
        public static TimeSpan IconPeriod => IconSpinDuration;

        /// <summary>轨道半径（圆心到圆点的距离）。</summary>
        public double OrbitRadius
        {
            get => _radius;
            set { _radius = value; if (_running) Start(); }
        }

        public void SetDotRadius(double radius)
        {
            if (radius <= 0) return;
            _dotRadius = radius;
            foreach (var dot in _dots)
            {
                if (dot == null) continue;
                dot.Width = radius * 2;
                dot.Height = radius * 2;
            }
            if (_running) Start();
        }

        public void SetColor(Brush brush)
        {
            _fill = brush ?? _fill;
            foreach (var dot in _dots)
            {
                if (dot != null) dot.Fill = _fill;
            }

            try { if (_icon != null) _icon.Foreground = _fill; } catch { }
        }

        private void BuildDots()
        {
            _canvas.Children.Clear();

            for (int i = 0; i < DotCount; i++)
            {
                var dot = new Ellipse
                {
                    Width = _dotRadius * 2,
                    Height = _dotRadius * 2,
                    Fill = _fill,
                    IsHitTestVisible = false
                };

                var rotate = new RotateTransform(0);
                dot.RenderTransform = rotate;

                _dots[i] = dot;
                _rotates[i] = rotate;
                _canvas.Children.Add(dot);
            }
        }

        /// <summary>开始播放（动画交给渲染线程，UI 线程忙也不卡）。</summary>
        public void Start()
        {
            // ★ 「禁用动画」时：圆点关键帧动画不播，改用 \uF16A 图标匀速旋转（2 秒一圈）
            if (App.IsAnimationDisabled)
            {
                StartIconSpin();
                return;
            }

            StopIconSpin();

            try
            {
                double size = Math.Max(8, Width is double w && !double.IsNaN(w) ? w : ActualWidth);
                if (double.IsNaN(size) || size <= 0) size = 56;

                double cx = size / 2.0;
                double cy = size / 2.0;

                for (int i = 0; i < DotCount; i++)
                {
                    var dot = _dots[i];
                    var rotate = _rotates[i];
                    if (dot == null || rotate == null) continue;

                    double r = _dotRadius;
                    // 圆点放在圆环最上方，然后整体绕圆心旋转
                    Canvas.SetLeft(dot, cx - r);
                    Canvas.SetTop(dot, cy - _radius - r);

                    rotate.CenterX = r;
                    rotate.CenterY = _radius + r;

                    dot.BeginAnimation(OpacityProperty, null);
                    rotate.BeginAnimation(RotateTransform.AngleProperty, null);

                    dot.Opacity = 1;
                    rotate.Angle = AngleAt(NormalizeTime(-i * DelayStep));

                    dot.BeginAnimation(OpacityProperty, OpacityAnimations[i]);
                    rotate.BeginAnimation(RotateTransform.AngleProperty, AngleAnimations[i]);
                }

                _running = true;
            }
            catch { }
        }

        /// <summary>停止播放并复位。</summary>
        public void Stop()
        {
            try
            {
                for (int i = 0; i < DotCount; i++)
                {
                    _dots[i]?.BeginAnimation(OpacityProperty, null);
                    _rotates[i]?.BeginAnimation(RotateTransform.AngleProperty, null);
                    if (_dots[i] != null) _dots[i].Opacity = 1;
                    if (_rotates[i] != null) _rotates[i].Angle = 0;
                }
            }
            catch { }

            StopIconSpin();
            _running = false;
        }

        /// <summary>「禁用动画」模式：显示 \uF16A 图标并让它一直转（2 秒一圈）。</summary>
        private void StartIconSpin()
        {
            try
            {
                _canvas.Visibility = Visibility.Collapsed;
                _iconView.Visibility = Visibility.Visible;
                _icon.Visibility = Visibility.Visible;

                _iconRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                _iconRotate.Angle = 0;

                var spin = new DoubleAnimation(0, 360, IconSpinDuration)
                {
                    RepeatBehavior = RepeatBehavior.Forever
                };
                _iconRotate.BeginAnimation(RotateTransform.AngleProperty, spin);

                Visibility = Visibility.Visible;
                _running = true;
            }
            catch { }
        }

        private void StopIconSpin()
        {
            try
            {
                _iconRotate?.BeginAnimation(RotateTransform.AngleProperty, null);
                if (_iconRotate != null) _iconRotate.Angle = 0;

                if (_iconView != null) _iconView.Visibility = Visibility.Collapsed;
                if (_icon != null) _icon.Visibility = Visibility.Collapsed;
                if (_canvas != null) _canvas.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private static double NormalizeTime(double t)
        {
            double tl = t % Cycle;
            return tl < 0 ? tl + Cycle : tl;
        }

        private static DoubleAnimationUsingKeyFrames[] BuildAngleAnimations()
        {
            var list = new DoubleAnimationUsingKeyFrames[DotCount];
            for (int i = 0; i < DotCount; i++)
            {
                var anim = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromSeconds(Cycle),
                    RepeatBehavior = RepeatBehavior.Forever
                };

                for (double t = 0; t <= Cycle + 0.0001; t += SampleStep)
                {
                    double tl = NormalizeTime(t - i * DelayStep);
                    anim.KeyFrames.Add(new LinearDoubleKeyFrame(AngleAt(tl), KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
                }

                anim.Freeze();
                list[i] = anim;
            }
            return list;
        }

        private static DoubleAnimationUsingKeyFrames[] BuildOpacityAnimations()
        {
            var list = new DoubleAnimationUsingKeyFrames[DotCount];
            for (int i = 0; i < DotCount; i++)
            {
                var anim = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromSeconds(Cycle),
                    RepeatBehavior = RepeatBehavior.Forever
                };

                for (double t = 0; t <= Cycle + 0.0001; t += SampleStep)
                {
                    double tl = NormalizeTime(t - i * DelayStep);
                    anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(tl < FadeOutTime ? 1.0 : 0.0,
                        KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
                }

                anim.Freeze();
                list[i] = anim;
            }
            return list;
        }

        private static double AngleAt(double tl)
        {
            if (tl <= Segments[0, 0]) return Segments[0, 1];

            int last = Segments.GetLength(0) - 1;
            if (tl >= Segments[last, 2]) return Segments[last, 3];

            for (int i = 0; i <= last; i++)
            {
                double t0 = Segments[i, 0];
                double t1 = Segments[i, 2];

                if (tl > t1) continue;

                double v0 = Segments[i, 1];
                double v1 = Segments[i, 3];
                double x1 = Segments[i, 4];
                double y1 = Segments[i, 5];
                double x2 = Segments[i, 6];
                double y2 = Segments[i, 7];

                double p = (tl - t0) / Math.Max(0.0001, t1 - t0);
                return v0 + (v1 - v0) * Ease(p, x1, y1, x2, y2);
            }

            return Segments[last, 3];
        }

        private static double Ease(double p, double x1, double y1, double x2, double y2)
        {
            if (p <= 0) return 0;
            if (p >= 1) return 1;

            double tt = SolveBezierT(p, x1, x2);
            return Bezier(tt, y1, y2);
        }

        private static double Bezier(double t, double p1, double p2)
        {
            double mt = 1 - t;
            return 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t;
        }

        private static double BezierDx(double t, double x1, double x2)
        {
            double mt = 1 - t;
            return 3 * mt * mt * x1 + 6 * mt * t * (x2 - x1) + 3 * t * t * (1 - x2);
        }

        private static double SolveBezierT(double x, double x1, double x2)
        {
            double t = x;

            for (int i = 0; i < 8; i++)
            {
                double err = Bezier(t, x1, x2) - x;
                if (Math.Abs(err) < 1e-10) return t;

                double d = BezierDx(t, x1, x2);
                if (Math.Abs(d) < 1e-12) break;

                t -= err / d;
                if (t < 0) t = 0;
                if (t > 1) t = 1;
            }

            double lo = 0, hi = 1;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                if (Bezier(mid, x1, x2) < x) lo = mid; else hi = mid;
            }

            return (lo + hi) / 2;
        }
    }
}
