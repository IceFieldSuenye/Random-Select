using RandomDrawer.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RandomDrawer
{
    public class DrawSession
    {
        public static DrawSession Current { get; set; }
        public DrawEngine Engine { get; } = new DrawEngine();
        public PoolInfo Pool { get; set; }
        public PersonalizationSettings Pers { get; set; } = new PersonalizationSettings();
        public HighlightConfig Highlight { get; set; }
        public DrawResultDisplayMode DisplayMode { get; set; } = DrawResultDisplayMode.CardGrid;
        public bool UseAnimation { get; set; }
        public bool NoRepeat { get; set; } = true;
        public int LastDrawCount { get; set; } = 1;

        public List<DrawResultData> PerformDraw(int count)
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