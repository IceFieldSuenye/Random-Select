using System;
using System.Windows;

namespace RandomDrawer
{
    /// <summary>
    /// 抽取流程中的通用校验与交互辅助。
    /// </summary>
    public static class DrawFlowHelper
    {
        /// <summary>
        /// 检查"不重复抽取"下剩余可抽取对象数量，并在越界时进行交互。
        ///
        /// · noRepeat=false 或启用了高级规则 → 直接返回 requestedCount；
        /// · remaining ≤ 0 → 弹"已抽完"，用户选"是"则重置并返回 pool.Items.Count，否则返回 null；
        /// · requestedCount &gt; remaining → 弹"超过可抽取数量"，用户选"继续抽取"则返回 remaining，否则返回 null。
        /// </summary>
        /// <returns>返回 null 表示用户取消；否则返回实际应抽取的数量。</returns>
        public static int? CheckAndAdjustCount(
            Window owner, PoolInfo pool, DrawEngine engine, bool noRepeat, int requestedCount)
        {
            if (pool == null || engine == null) return requestedCount;
            if (!noRepeat) return requestedCount;

            // 高级规则激活时不重复抽取无意义
            try
            {
                if (engine.HasActiveGachaStrategy(pool.Dir)) return requestedCount;
            }
            catch { }

            int remaining = pool.Items.Count - engine.DrawnCount;

            if (remaining <= 0)
            {
                var r = CustomMessageBox.Show(owner,
                    "当前卡池已经全部抽完，没有可抽取的对象了。\n\n是否重置抽取记录以重新开始？",
                    "卡池已抽完",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "重置", noText: "取消", yesIcon: "\uE72C");

                if (r != MessageBoxResult.Yes) return null;

                engine.ResetDrawnIndices();
                engine.ResetGachaState(pool.Dir);
                remaining = pool.Items.Count;
            }

            if (requestedCount > remaining)
            {
                var r = CustomMessageBox.ShowCustom(owner,
                    $"您输入的抽取次数（{requestedCount}）超过了当前卡池剩余可抽取对象数量（{remaining}）。\n\n" +
                    "将改为抽取剩余的全部对象。\n\n是否继续？",
                    "超过可抽取数量",
                    "继续抽取", "\uE72A",
                    "取消", "\uE711",
                    MessageBoxImage.Warning);

                if (r != MessageBoxResult.Yes) return null;

                return remaining;
            }

            return requestedCount;
        }
    }
}