using System;

namespace RandomDrawer
{
    public enum PageEnterSource
    {
        Main,
        Options,
        Other
    }

    public interface IPageAnimation
    {
        /// <summary>播放进入动画。</summary>
        void PlayEnterAnimation(Action onComplete);

        /// <summary>播放退出动画（同时作为"进入动画的倒放"）。</summary>
        void PlayExitAnimation(Action onComplete);

        /// <summary>以"进入动画的倒放"方式出现（返回上一级时使用）。</summary>
        void PlayEnterAsReverseAnimation(Action onComplete);
    }

    public static class NavigationContext
    {
        public static PageEnterSource CurrentSource { get; set; } = PageEnterSource.Other;
    }
}