namespace RandomDrawer
{
    /// <summary>
    /// 抽取按钮宿主：需要在"抽取次数浮动面板"显示时隐藏自身的抽取按钮时实现此接口。
    /// </summary>
    public interface IDrawButtonsHost
    {
        /// <summary>显示 / 隐藏抽取按钮（带动画）。</summary>
        void SetDrawButtonsVisible(bool visible);

        /// <summary>立即显示 / 隐藏抽取按钮（不播放动画，避免与浮动面板动画抢帧）。</summary>
        void SetDrawButtonsVisibleInstant(bool visible);
    }
}
