using System.Windows;

namespace RandomDrawer
{
    /// <summary>
    /// 统一的「加载中」转圈：高级抽卡规则页、抽取中遮罩、结果生成遮罩、历史记录加载遮罩共用。
    /// 尺寸固定 56×56，颜色跟随主题前景色（NowLoadingSpinner 内部用的是 DynamicResource），
    /// 调用方只需要 <see cref="NowLoadingSpinner.Start"/> / <see cref="NowLoadingSpinner.Stop"/>。
    /// </summary>
    public class BusySpinner : NowLoadingSpinner
    {
        public const double DefaultSize = 56;

        public BusySpinner()
        {
            Width = DefaultSize;
            Height = DefaultSize;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;
        }
    }
}
