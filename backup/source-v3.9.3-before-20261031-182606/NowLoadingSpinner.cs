using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RandomDrawer
{
    /// <summary>
    /// Windows 10 风格加载动画（按 Loading/NowLoading.cpp 的角度关键帧实现）：
    /// 6 个圆点沿圆周先后加速、停顿、再加速，整体周期约 4.3 秒。
    /// </summary>
    public class NowLoadingSpinner : Grid
    {
        private const int DotCount = 6;
        private const double DotDuration = 3.470;
        private const double DelayStep = 0.167;
        private const double Cycle = DelayStep * (DotCount - 1) + DotDuration;
        private const double FadeOutTime = 3.220;
        private const int Fps = 60;

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

        private static readonly int FrameCount = Math.Max(1, (int)Math.Ceiling(Cycle * Fps));
        private static readonly double[,] FrameSin = BuildFrameSin();
        private static readonly double[,] FrameCos = BuildFrameCos();
        private static readonly bool[,] FrameVisible = BuildFrameVisible();

        private readonly Canvas _canvas = new Canvas();
        private readonly Ellipse[] _dots = new Ellipse[DotCount];
        private readonly TranslateTransform[] _transforms = new TranslateTransform[DotCount];

        private bool _isRendering;
        private int _lastRenderedFrame = -1;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private double _radius = 7.0;
        private double _dotRadius = 1.0;
        private Brush _fill;

        public NowLoadingSpinner()
        {
            _fill = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            Width = 18;
            Height = 18;

            Children.Add(_canvas);
            BuildDots();
        }

        /// <summary>圆点间距半径（默认 7）。</summary>
        public double OrbitRadius
        {
            get => _radius;
            set { _radius = value; }
        }

        /// <summary>调整圆点半径（用于放大整体动画时让圆点略小）。</summary>
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
        }

        public void SetColor(Brush brush)
        {
            _fill = brush ?? _fill;

            foreach (var dot in _dots)
            {
                if (dot != null) dot.Fill = _fill;
            }
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

                var transform = new TranslateTransform();
                dot.RenderTransform = transform;

                _dots[i] = dot;
                _transforms[i] = transform;
                _canvas.Children.Add(dot);
            }
        }

        private static double[,] BuildFrameSin()
        {
            var values = new double[DotCount, FrameCount];
            for (int i = 0; i < DotCount; i++)
                for (int f = 0; f < FrameCount; f++)
                    values[i, f] = Math.Sin(AngleAt(FrameTime(i, f)) * Math.PI / 180.0);
            return values;
        }

        private static double[,] BuildFrameCos()
        {
            var values = new double[DotCount, FrameCount];
            for (int i = 0; i < DotCount; i++)
                for (int f = 0; f < FrameCount; f++)
                    values[i, f] = Math.Cos(AngleAt(FrameTime(i, f)) * Math.PI / 180.0);
            return values;
        }

        private static bool[,] BuildFrameVisible()
        {
            var values = new bool[DotCount, FrameCount];
            for (int i = 0; i < DotCount; i++)
                for (int f = 0; f < FrameCount; f++)
                    values[i, f] = FrameTime(i, f) < FadeOutTime;
            return values;
        }

        private static double FrameTime(int dotIndex, int frame)
        {
            double t = frame / (double)Fps;
            double tl = (t - dotIndex * DelayStep) % Cycle;
            return tl < 0 ? tl + Cycle : tl;
        }

        /// <summary>开始播放。</summary>
        public void Start()
        {
            if (_isRendering) return;

            _clock.Restart();
            _lastRenderedFrame = -1;
            _isRendering = true;
            CompositionTarget.Rendering += OnRendering;
            UpdateFrame();
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
            try
            {
                if (!_isRendering) return;
                _isRendering = false;
                CompositionTarget.Rendering -= OnRendering;
            }
            catch { }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (!_isRendering) return;

            int frame = (int)(Math.Max(0, _clock.Elapsed.TotalSeconds) * Fps) % FrameCount;
            if (frame == _lastRenderedFrame) return;

            _lastRenderedFrame = frame;
            UpdateFrame(frame);
        }

        private void UpdateFrame(int? frameOverride = null)
        {
            try
            {
                int frame = frameOverride ?? (int)(Math.Max(0, _clock.Elapsed.TotalSeconds) * Fps) % FrameCount;
                double cx = Width / 2.0;
                double cy = Height / 2.0;

                for (int i = 0; i < DotCount; i++)
                {
                    var dot = _dots[i];
                    var transform = _transforms[i];
                    if (dot == null || transform == null) continue;

                    if (!FrameVisible[i, frame])
                    {
                        dot.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    dot.Visibility = Visibility.Visible;
                    double x = cx + _radius * FrameSin[i, frame];
                    double y = cy - _radius * FrameCos[i, frame];
                    transform.X = x - _dotRadius;
                    transform.Y = y - _dotRadius;
                }
            }
            catch { }
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
