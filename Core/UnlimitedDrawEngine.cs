using System;
using System.Collections.Generic;

namespace RandomDrawer
{
    /// <summary>无限次抽取的结果：实际抽取总数 +（最多前 100 万条的）逐条明细。</summary>
    public sealed class UnlimitedDrawOutcome
    {
        public List<DrawItem> Items { get; } = new List<DrawItem>();

        /// <summary>实际抽取次数（可能大于 Items.Count，超出部分只计数不保留）。</summary>
        public long TotalDrawn { get; set; }

        /// <summary>是否因为 100 万条上限而截断了逐条明细。</summary>
        public bool Truncated => TotalDrawn > Items.Count;
    }

    /// <summary>
    /// 3.77.0-snapshot-17：实验性「无上限抽取次数」的批量抽取。
    /// 仅在「开启实验性开关 + 输入超过 9 999 + 未开启不重复抽取」时由 DrawSession 调用，
    /// 原有 DrawEngine.ExecuteDraw / WriteDrawHistory 完全不受影响。
    /// 与普通抽取一致的规则：概率列 &gt; 0 的对象按权重抽取，全部非法时退化为均匀抽取。
    /// </summary>
    internal static class UnlimitedDrawEngine
    {
        /// <summary>逐条保留的结果上限（其余只累计次数，避免占用几十 GB 内存）。</summary>
        public const int MaxKeptResults = 1_000_000;

        private const int ProgressBatch = 10000;

        internal static UnlimitedDrawOutcome Execute(int count, PoolInfo pool, Action<long> progress, HistoryStreamWriter history)
        {
            var outcome = new UnlimitedDrawOutcome();
            if (pool == null || pool.Items == null || pool.Items.Count == 0 || count <= 0) return outcome;

            List<DrawItem> drawable = pool.Items;

            // 与 DrawEngine 一致的语义：概率 <= 0 / NaN / Infinity 的对象不参与抽取；
            // 只有当「一个可抽项都没有」时才整池均匀抽取。
            bool weighted = false;
            double[] cumulative = null;
            double totalWeight = 0;

            if (pool.HasProbability)
            {
                var positive = new List<DrawItem>(pool.Items.Count);
                double sum = 0;
                foreach (var item in pool.Items)
                {
                    if (item.Probability > 0 && !double.IsNaN(item.Probability) && !double.IsInfinity(item.Probability))
                    {
                        positive.Add(item);
                        sum += item.Probability;
                    }
                }

                if (positive.Count > 0)
                {
                    weighted = true;
                    totalWeight = sum;
                    if (positive.Count != pool.Items.Count) drawable = positive;
                }
            }

            if (weighted)
            {
                cumulative = new double[drawable.Count];
                double running = 0;
                for (int i = 0; i < drawable.Count; i++)
                {
                    running += drawable[i].Probability;
                    cumulative[i] = running;
                }
            }

            var random = new Random();
            var textCache = new Dictionary<DrawItem, string>(pool.Items.Count);

            for (int i = 0; i < count; i++)
            {
                DrawItem picked;

                if (weighted)
                {
                    double r = random.NextDouble() * totalWeight;
                    int idx = Array.BinarySearch(cumulative, r);
                    if (idx < 0) idx = ~idx;
                    if (idx >= cumulative.Length) idx = cumulative.Length - 1;
                    picked = drawable[idx];
                }
                else
                {
                    picked = drawable[random.Next(drawable.Count)];
                }

                if (outcome.Items.Count < MaxKeptResults) outcome.Items.Add(picked);
                outcome.TotalDrawn++;

                if (history != null)
                {
                    if (!textCache.TryGetValue(picked, out string fieldsLine))
                    {
                        fieldsLine = string.Join("\t", picked.Fields);
                        textCache[picked] = fieldsLine;
                    }
                    history.Write(fieldsLine);
                }

                if ((i + 1) % ProgressBatch == 0) progress?.Invoke(i + 1);
            }

            progress?.Invoke(outcome.TotalDrawn);
            return outcome;
        }
    }
}