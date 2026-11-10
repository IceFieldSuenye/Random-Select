using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RandomDrawer
{
    /// <summary>
    /// 3.76：带投影的卡片。
    ///
    /// 直接把 <see cref="System.Windows.Media.Effects.DropShadowEffect"/> 加在卡片 Border 上，
    /// WPF 会把整块内容（含文字）渲染成位图再做模糊，文字会明显发虚。
    /// 这里拆成两层：底层只画背景 + 阴影，上层画边框和内容（不带 Effect），文字始终清晰。
    /// </summary>
    public class ShadowCard : ContentControl
    {
        static ShadowCard()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(ShadowCard),
                new FrameworkPropertyMetadata(typeof(ShadowCard)));
        }

        public ShadowCard()
        {
            Loaded += (s, e) => TryApplyTemplateNow();
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            // 3.76：XAML 解析完成时样式可能还没应用，先同步套一次；
            // 样式稍后应用时还会走 OnPropertyChanged(Template) 再补一次。
            TryApplyTemplateNow();
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            // 样式（含 Template）应用后立即套模板：Collapsed 的卡片不参与布局，
            // 不主动套一次的话卡片里的控件不会进入视觉树。
            if (e.Property == TemplateProperty) TryApplyTemplateNow();
        }

        protected override void OnVisualParentChanged(DependencyObject oldParent)
        {
            base.OnVisualParentChanged(oldParent);
            if (VisualParent != null) TryApplyTemplateNow();
        }

        private void TryApplyTemplateNow()
        {
            try { ApplyTemplate(); }
            catch { }
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(ShadowCard),
                new PropertyMetadata(new CornerRadius(12)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        public static readonly DependencyProperty ShadowOpacityProperty =
            DependencyProperty.Register(nameof(ShadowOpacity), typeof(double), typeof(ShadowCard),
                new PropertyMetadata(0.10));

        /// <summary>阴影浓度；0 表示不画阴影（仍然保留两层结构，文字不受影响）。</summary>
        public double ShadowOpacity
        {
            get => (double)GetValue(ShadowOpacityProperty);
            set => SetValue(ShadowOpacityProperty, value);
        }
    }
}
