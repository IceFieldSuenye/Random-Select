namespace RandomDrawer
{
    /// <summary>
    /// 界面上的「数量阈值」：集中定义，方便统一调整，也便于测试。
    /// 大量抽取时的体验约定：
    ///   · ≥ BackgroundWorkMinCount → 计算放后台线程（不显示遮罩，界面不卡）；
    ///   · ≥ LoadingOverlayMinCount → 才显示加载遮罩；
    ///   · 小于 LoadingOverlayMinCount → 直接做完，避免遮罩一闪而过（闪烁）。
    /// </summary>
    public static class UiThresholds
    {
        /// <summary>抽取数量达到该值就在后台线程计算（避免界面假死）。</summary>
        public const int BackgroundWorkMinCount = 50;

        /// <summary>达到该条数才显示「正在抽取 / 正在生成抽取结果」的加载遮罩。</summary>
        public const int LoadingOverlayMinCount = 500;
    }
}
