using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RandomDrawer
{
    /// <summary>
    /// 「粗细」下拉项：一条粗细不同的水平直线（不显示文字）。
    ///
    /// 这个类型只为一个细节存在：全局 ComboBox 模板把「收起后显示的选中项」当成文本渲染，
    /// 所以内容的 ToString() 会被画成文字。这里重写成空串，收起时就不会出现
    /// 「System.Windows.Controls.Grid」或被裁剪成省略号；收起状态下的线条由页面上的
    /// 预览矩形单独绘制（见 CoverEditorPage_UC）。
    /// </summary>
    public sealed class PenWidthItem : Grid
    {
        private double _thickness = 6;

        public PenWidthItem()
        {
            // 3.32：下拉项加宽 / 加高，收起与展开时都能看清粗细
            Width = 60;
            Height = 38;
            Rebuild();
        }

        /// <summary>线宽（同时也是橡皮擦的尺寸）。</summary>
        public double Thickness
        {
            get => _thickness;
            set
            {
                _thickness = value;
                Rebuild();
            }
        }

        private void Rebuild()
        {
            Children.Clear();

            double thickness = _thickness < 1 ? 1 : _thickness;
            var line = new Rectangle
            {
                Height = thickness,
                Width = 52,
                RadiusX = thickness / 2.0,
                RadiusY = thickness / 2.0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 跟随主题（深色模式下不能是硬编码黑色）
            line.SetResourceReference(Shape.FillProperty, "Fg_333333");
            Children.Add(line);
        }

        /// <summary>收起后的标题文本：留空（线条由页面上的预览矩形负责）。</summary>
        public override string ToString() => "";
    }
}
