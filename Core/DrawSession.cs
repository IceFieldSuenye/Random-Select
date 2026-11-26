using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RandomDrawer
{
    public class DrawSession
    {
        /// <summary>
        /// 抽取串行锁：主页「抽取」与结果页「继续抽取」可能并发（其中一个还在后台线程），
        /// 而 history.txt 是 append 写入、保底状态是读-改-写，必须串行化。
        /// </summary>
        private static readonly object DrawLock = new object();

        public static DrawSession Current { get; set; }
        public DrawEngine Engine { get; } = new DrawEngine();
        public PoolInfo Pool { get; set; }
        public PersonalizationSettings Pers { get; set; } = new PersonalizationSettings();
        public HighlightConfig Highlight { get; set; }
        public DrawResultDisplayMode DisplayMode { get; set; } = DrawResultDisplayMode.CardGrid;
        public bool UseAnimation { get; set; }
        public bool NoRepeat { get; set; } = true;
        public int LastDrawCount { get; set; } = 1;

        /// <summary>
        /// 3.77.0-snapshot-17：最近一次「无限次抽取」实际抽取的总次数（普通抽取时为 0，结果页用 Items.Count）。
        /// </summary>
        public long TotalDrawn { get; private set; }

        public List<DrawResultData> PerformDraw(int count)
        {
            // ★ 抽取 + 写历史 + 更新保底状态必须串行，避免后台线程与「继续抽取」并发
            lock (DrawLock)
            {
                if (Pool == null || Pool.Items == null || Pool.Items.Count == 0)
                    return new List<DrawResultData>();

                var results = Engine.ExecuteDraw(count, Pool, NoRepeat, null);
                if (results.Count == 0) return new List<DrawResultData>();

                string drawType = count == 1 ? "单次抽取" : $"{count}次抽取";
                Engine.WriteDrawHistory(Pool, results, drawType);
                LastDrawCount = count;
                return BuildDrawResultData(results);
            }
        }

        /// <summary>
        /// 3.77.0-snapshot-17：实验性「无上限抽取次数」的抽取路径。
        /// 只在开启实验性开关、输入 &gt; 9 999、且关闭「不重复抽取」时由主页调用；
        /// 原有 PerformDraw / DrawEngine.ExecuteDraw 的行为完全不变。
        /// </summary>
        public List<DrawResultData> PerformDrawUnlimited(int count, Action<long> progress = null)
        {
            lock (DrawLock)
            {
                TotalDrawn = 0;
                if (Pool == null || Pool.Items == null || Pool.Items.Count == 0)
                    return new List<DrawResultData>();

                string drawType = count == 1 ? "单次抽取" : $"{count}次抽取";
                HistoryStreamWriter history = null;
                try
                {
                    if (!App.IsHistoryDisabled)
                        history = new HistoryStreamWriter(Pool.Dir, drawType);

                    var outcome = UnlimitedDrawEngine.Execute(count, Pool, progress, history);
                    TotalDrawn = outcome.TotalDrawn;
                    LastDrawCount = count;

                    if (outcome.Items.Count == 0) return new List<DrawResultData>();
                    return BuildDrawResultData(outcome.Items);
                }
                finally
                {
                    history?.Dispose();
                }
            }
        }

        public List<DrawResultData> BuildDrawResultData(List<DrawItem> results)
        {
            var list = new List<DrawResultData>();
            if (Pool == null) return list;

            int catIndex = Pool.Headers.IndexOf("分类");
            int probIndex = Pool.Headers.IndexOf("概率");

            // 用户可能把「序号」放在第一列，真正的对象名称列需要按表头识别
            int objectIndex = Pool.Headers.FindIndex(h =>
                string.Equals((h ?? "").Trim(), "抽取对象", StringComparison.Ordinal));
            if (objectIndex < 0) objectIndex = 0;
            if (objectIndex >= Pool.Headers.Count) objectIndex = 0;

            var otherIndices = new List<int>();
            var otherHeaders = new List<string>();
            for (int i = 0; i < Pool.Headers.Count; i++)
            {
                string header = (Pool.Headers[i] ?? "").Trim();
                if (i == objectIndex || i == probIndex || header == "序号") continue;
                otherIndices.Add(i);
                otherHeaders.Add(Pool.Headers[i]);
            }

            // 大量抽取时避免每行都做一次 FirstOrDefault / 复制表头
            var persLookup = new Dictionary<string, PersonalizationItem>(StringComparer.Ordinal);
            if (Pers?.Items != null)
            {
                foreach (var p in Pers.Items)
                {
                    if (p == null || string.IsNullOrEmpty(p.ObjectName)) continue;
                    if (!persLookup.ContainsKey(p.ObjectName)) persLookup[p.ObjectName] = p;
                }
            }

            for (int i = 0; i < results.Count; i++)
            {
                var item = results[i];
                string name = objectIndex < item.Fields.Count ? (item.Fields[objectIndex] ?? "").Trim() : "";

                string category = "";
                if (catIndex >= 0 && catIndex < item.Fields.Count)
                    category = (item.Fields[catIndex] ?? "").Trim();

                var otherFields = new List<string>();
                foreach (int index in otherIndices)
                    otherFields.Add(index < item.Fields.Count ? (item.Fields[index] ?? "") : "");

                string avatarPath = "";
                string illustrationPath = "";
                string colorHex = "";
                if (persLookup.TryGetValue(name, out var pi))
                {
                    avatarPath = pi.AvatarFile ?? "";
                    illustrationPath = pi.IllustrationFile ?? "";
                    colorHex = pi.ColorHex ?? "";
                }

                list.Add(new DrawResultData
                {
                    Index = i + 1,
                    ObjectName = name,
                    ObjectHeader = objectIndex < Pool.Headers.Count ? (Pool.Headers[objectIndex] ?? "抽取对象") : "抽取对象",
                    ColorHex = colorHex,
                    Category = category,
                    OtherHeaders = otherHeaders,
                    OtherFields = otherFields,
                    AvatarPath = avatarPath,
                    IllustrationPath = illustrationPath
                });
            }
            return list;
        }

        public void RefreshDisplayMode()
        {
            bool hasPers = HasPersonalizationContent();
            if (hasPers) DisplayMode = DrawResultDisplayMode.CardGrid;
            else DisplayMode = LastDrawCount == 1 ? DrawResultDisplayMode.Text : DrawResultDisplayMode.List;
        }

        // ★ 新逻辑：仅当任意一项设置了头像或立绘才视为有个性化内容
        public bool HasPersonalizationContent()
        {
            var s = Pers;
            if (s == null) return false;
            if (s.Items == null || s.Items.Count == 0) return false;

            foreach (var it in s.Items)
            {
                if (it == null) continue;
                if (!string.IsNullOrEmpty(it.AvatarFile)) return true;
                if (!string.IsNullOrEmpty(it.IllustrationFile)) return true;
            }
            return false;
        }
    }
}