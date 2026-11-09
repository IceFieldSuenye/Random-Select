using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RandomDrawer
{
    public enum GachaGameKind { HSR, BlueArchive }

    public class GachaReportEntry
    {
        public string Name = "";
        public string AvatarPath = "";
        public int Pulls;        // 本次出金/出三星所花抽数
        public int Cumulative;   // 该次在卡池历史中的累计抽数
        public bool IsUp;        // 是否 UP
        public string Detail = "";   // 普通卡池：分类等附加信息
        public string ColorHex = ""; // 自定义颜色（用于名称着色）
        public bool HasAvatar => !string.IsNullOrEmpty(AvatarPath) && File.Exists(AvatarPath);
    }

    public class GachaReportData
    {
        public GachaGameKind Game = GachaGameKind.HSR;
        public string PoolName = "";
        public string Title = "数据不足";
        public string SubTitle = "";
        public string Summary = "";
        public List<string> Keywords = new List<string>();
        public List<GachaReportEntry> Entries = new List<GachaReportEntry>();
        public int TotalPulls;
        public int GreenMax = 50;    // 抽数 <= GreenMax → 绿色
        public int YellowMax = 70;   // 抽数 <= YellowMax → 黄色，其余红色
        public int ScaleMax = 90;    // 进度条满格对应的抽数
        public bool HasData;
        public bool IsCelebration;
        public bool IsSimple;          // 未启用高级抽卡规则：仅统计每个对象的抽取次数
        public bool IsPermanent;       // 蔚蓝档案（旧）常驻招募：不显示 UP 统计
        public int CurrentPity;        // 已垫抽数（距上次出金到现在）
        public int UpCount;            // 获得 UP 五星 / UP 三星的次数
    }

    /// <summary>根据卡池高级规则与历史记录生成「抽取报告」。</summary>
    public static class GachaReportAnalyzer
    {
        public static GachaReportData Analyze(string poolDir, string poolName)
        {
            var data = new GachaReportData { PoolName = poolName ?? "" };

            if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
            {
                data.SubTitle = "未选择卡池";
                return data;
            }

            string settingsFile = Path.Combine(poolDir, "gacha_settings.ini");
            if (!File.Exists(settingsFile))
            {
                BuildSimpleReport(data, poolDir, poolName);
                return data;
            }

            bool enabled = IniHelper.ReadValue("Gacha", "Enabled", "0", settingsFile) == "1";
            if (!enabled)
            {
                BuildSimpleReport(data, poolDir, poolName);
                return data;
            }

            string preset = IniHelper.ReadValue("Gacha", "PresetName", "", settingsFile);
            bool isBa = preset.Contains("蔚蓝");
            data.Game = isBa ? GachaGameKind.BlueArchive : GachaGameKind.HSR;

            string historyFile = Path.Combine(poolDir, "history.txt");
            if (!File.Exists(historyFile))
            {
                data.SubTitle = "该卡池还没有抽取记录";
                return data;
            }

            List<string> lines;
            try { lines = File.ReadAllLines(historyFile).ToList(); }
            catch { data.SubTitle = "读取历史记录失败"; return data; }

            // ---------- 解析每次抽取 ----------
            var pulls = new List<KeyValuePair<string, List<string>>>();  // 名称 → 字段
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string body = line;
                int firstPipe = body.IndexOf('|');
                if (firstPipe >= 0)
                {
                    int secondPipe = body.IndexOf('|', firstPipe + 1);
                    if (secondPipe < 0) continue;
                    body = body.Substring(secondPipe + 1);
                }

                var fields = body.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (fields.Count == 0) continue;

                string name = fields[0].Trim();
                if (string.IsNullOrEmpty(name)) continue;

                pulls.Add(new KeyValuePair<string, List<string>>(name, fields));
            }

            data.TotalPulls = pulls.Count;
            if (pulls.Count == 0)
            {
                data.SubTitle = "该卡池还没有抽取记录";
                return data;
            }

            // ---------- 稀有度分类 ----------
            string rareCat = isBa
                ? IniHelper.ReadValue("GachaBA", "ThreeStarCategory", "", settingsFile)
                : IniHelper.ReadValue("Gacha", "FiveStarCategory", "", settingsFile);

            // ---------- UP 名单 ----------
            var upNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            data.IsCelebration = false;
            string baPoolType = "";
            bool baPermanent = false;
            bool baLimited = false;

            if (isBa)
            {
                baPoolType = IniHelper.ReadValue("GachaBA", "PoolType", "常驻招募", settingsFile);
                data.IsCelebration = baPoolType.Contains("庆典");
                baLimited = baPoolType.Contains("限时");
                baPermanent = !data.IsCelebration && !baLimited;
                data.IsPermanent = baPermanent;

                // 庆典：只有主 UP 算 UP，其余 3 星（包括次 UP）都是「歪」；
                // 限时：主 UP（旧字段 Up3Star）+ 次 UP 都算 UP；
                // 常驻：所有 3 星都不显示「歪」。
                AddNames(upNames, IniHelper.ReadValue("GachaBA", "MainUp3Stars", "", settingsFile));
                if (data.IsCelebration)
                {
                    // 兼容旧配置：仅当 MainUp3Stars 为空时，才把 Up3Star 当作庆典主 UP
                    if (upNames.Count == 0)
                        AddNames(upNames, IniHelper.ReadValue("GachaBA", "Up3Star", "", settingsFile));
                }
                else
                {
                    AddNames(upNames, IniHelper.ReadValue("GachaBA", "SubUp3Stars", "", settingsFile));
                    AddNames(upNames, IniHelper.ReadValue("GachaBA", "Up3Star", "", settingsFile));
                }
            }
            else
            {
                AddNames(upNames, IniHelper.ReadValue("Gacha", "FiveStarUpObject", "", settingsFile));
            }

            bool IsUpEntry(string name)
            {
                if (isBa)
                {
                    if (baPermanent) return true;
                    // 限时 / 庆典：名单为空时不能把全部 3 星都当成 UP，
                    // 否则「歪」会全部丢失；此时按非 UP 处理，保证标记正确。
                    return upNames.Count > 0 && upNames.Contains(name);
                }
                return upNames.Count == 0 || upNames.Contains(name);
            }

            // ---------- 逐抽统计 ----------
            int sinceLast = 0;
            var hits = new List<GachaReportEntry>();

            for (int i = 0; i < pulls.Count; i++)
            {
                sinceLast++;

                var kv = pulls[i];
                bool isRare = !string.IsNullOrWhiteSpace(rareCat)
                    && kv.Value.Any(f => string.Equals(f.Trim(), rareCat.Trim(), StringComparison.OrdinalIgnoreCase));

                if (!isRare) continue;

                hits.Add(new GachaReportEntry
                {
                    Name = kv.Key,
                    Pulls = sinceLast,
                    Cumulative = i + 1,
                    IsUp = IsUpEntry(kv.Key)
                });

                sinceLast = 0;
            }

            // ---------- 头像 ----------
            try
            {
                string persFile = Path.Combine(poolDir, "personalization.json");
                if (File.Exists(persFile))
                {
                    var pers = PersonalizationSettings.Load(persFile);
                    foreach (var e in hits)
                    {
                        var item = pers?.Items?.FirstOrDefault(x =>
                            string.Equals((x.ObjectName ?? "").Trim(), e.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (item == null || string.IsNullOrWhiteSpace(item.AvatarFile)) continue;

                        string avatar = item.AvatarFile;
                        if (!Path.IsPathRooted(avatar)) avatar = Path.Combine(poolDir, avatar);
                        if (File.Exists(avatar)) e.AvatarPath = avatar;
                    }
                }
            }
            catch { }

            data.Entries = hits;
            data.CurrentPity = sinceLast;   // 距上次出金已垫抽数

            // ---------- 进度条刻度 ----------
            if (isBa)
            {
                if (data.IsCelebration)
                {
                    data.GreenMax = 10; data.YellowMax = 15; data.ScaleMax = 20;
                }
                else
                {
                    data.GreenMax = 20; data.YellowMax = 25; data.ScaleMax = 30;
                }
            }
            else
            {
                data.GreenMax = 50; data.YellowMax = 70; data.ScaleMax = 90;
            }

            // ---------- 整体判定 ----------
            if (isBa) AnalyzeBa(data, pulls.Count);
            else AnalyzeHsr(data, pulls.Count);

            data.HasData = hits.Count > 0;
            if (!data.HasData)
            {
                data.Title = "数据不足";
                data.SubTitle = "还没有抽到" + (isBa ? "3 星" : "5 星") + "，再接再厉。";
            }

            return data;
        }

        /// <summary>未启用高级抽卡规则：仅统计每个对象被抽到的次数。</summary>
        private static void BuildSimpleReport(GachaReportData data, string poolDir, string poolName)
        {
            data.IsSimple = true;
            data.PoolName = poolName ?? "";
            data.Title = "抽取次数统计";

            string historyFile = Path.Combine(poolDir, "history.txt");
            if (!File.Exists(historyFile))
            {
                data.SubTitle = "该卡池还没有抽取记录";
                return;
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;

            try
            {
                foreach (var line in File.ReadAllLines(historyFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string body = line;
                    int firstPipe = body.IndexOf('|');
                    if (firstPipe >= 0)
                    {
                        int secondPipe = body.IndexOf('|', firstPipe + 1);
                        if (secondPipe < 0) continue;
                        body = body.Substring(secondPipe + 1);
                    }

                    string name = body.Split('\t')[0].Trim();
                    if (string.IsNullOrEmpty(name)) continue;

                    total++;
                    if (counts.ContainsKey(name)) counts[name]++;
                    else counts[name] = 1;

                    if (!details.ContainsKey(name))
                    {
                        var extra = body.Split('\t').Skip(1).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                        if (extra.Count > 0) details[name] = string.Join("  ·  ", extra);
                    }
                }
            }
            catch
            {
                data.SubTitle = "读取历史记录失败";
                return;
            }

            data.TotalPulls = total;
            if (total == 0)
            {
                data.SubTitle = "该卡池还没有抽取记录";
                return;
            }

            foreach (var kv in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
            {
                data.Entries.Add(new GachaReportEntry
                {
                    Name = kv.Key,
                    Pulls = kv.Value,
                    Cumulative = kv.Value,
                    Detail = details.ContainsKey(kv.Key) ? details[kv.Key] : "",
                    IsUp = true
                });
            }

            AttachAvatars(poolDir, data.Entries);
            data.Summary = $"共 {total} 次抽取   ·   {counts.Count} 个抽取对象";
            data.HasData = data.Entries.Count > 0;
        }

        private static void AttachAvatars(string poolDir, List<GachaReportEntry> entries)
        {
            if (entries == null || entries.Count == 0) return;

            try
            {
                string persFile = Path.Combine(poolDir, "personalization.json");
                if (!File.Exists(persFile)) return;

                var pers = PersonalizationSettings.Load(persFile);
                foreach (var e in entries)
                {
                    var item = pers?.Items?.FirstOrDefault(x =>
                        string.Equals((x.ObjectName ?? "").Trim(), e.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (item == null) continue;

                    if (!string.IsNullOrWhiteSpace(item.ColorHex)) e.ColorHex = item.ColorHex.Trim();

                    if (string.IsNullOrWhiteSpace(item.AvatarFile)) continue;

                    string avatar = item.AvatarFile;
                    if (!Path.IsPathRooted(avatar)) avatar = Path.Combine(poolDir, avatar);
                    if (File.Exists(avatar)) e.AvatarPath = avatar;
                }
            }
            catch { }
        }
        private static void AddNames(HashSet<string> set, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            foreach (var part in raw.Split(new[] { ',', '，', '、', '/', '|', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (!string.IsNullOrEmpty(name)) set.Add(name);
            }
        }

        // ================================================================
        // 崩坏：星穹铁道（角色活动跃迁）
        // ================================================================
        // 崩坏：星穹铁道（角色活动跃迁）—— 严格按《整体结果》规则判定
        // ================================================================
        private static void AnalyzeHsr(GachaReportData data, int totalPulls)
        {
            var fiveStars = data.Entries;   // 已按抽数升序
            int goldCount = fiveStars.Count;

            // 出金间隔（排除首个）
            var intervals = new List<int>();
            for (int i = 1; i < fiveStars.Count; i++) intervals.Add(fiveStars[i].Pulls);

            // 歪率与小保底样本：
            // 只有「上一次出金是 UP（或这是第一个金）」时，本次出金才是一次真正的 50/50 小保底；
            // 上一次出金歪了的话，本次是必出 UP 的保底，不能计入不歪率。
            int nPity = 0, nLose = 0;
            bool? prevIsUp = null;
            foreach (var e in fiveStars)
            {
                bool isPity = prevIsUp != false;   // null（首个）或 true（上次没歪）
                if (isPity)
                {
                    nPity++;
                    if (!e.IsUp) nLose++;
                }
                prevIsUp = e.IsUp;
            }

            double? pLose = nPity > 0 ? (double)nLose / nPity : (double?)null;
            double? notLoseRate = pLose.HasValue ? (1.0 - pLose.Value) * 100.0 : (double?)null;

            // 统计量
            double? avg = intervals.Count > 0 ? intervals.Average() : (double?)null;

            double? median = null;
            if (intervals.Count > 0)
            {
                var sorted = intervals.OrderBy(x => x).ToList();
                int n = sorted.Count;
                median = (n % 2 == 1) ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
            }

            double? std = null;
            if (intervals.Count >= 2 && avg.HasValue)
            {
                double sum = intervals.Sum(x => (x - avg.Value) * (x - avg.Value));
                std = Math.Sqrt(sum / intervals.Count);
            }

            int? minInterval = intervals.Count > 0 ? intervals.Min() : (int?)null;
            int? maxInterval = intervals.Count > 0 ? intervals.Max() : (int?)null;

            var modes = new List<int>();
            if (intervals.Count > 0)
            {
                var freq = intervals.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                int maxCount = freq.Values.Max();
                modes = freq.Where(kv => kv.Value == maxCount).Select(kv => kv.Key).ToList();
            }

            // 关键词
            if (HasDoubleGoldInTen(fiveStars)) data.Keywords.Add("十连双黄");
            if (HasThreeConsecutiveUp(fiveStars)) data.Keywords.Add("3连UP");
            // 「小保底必歪」只在所有小保底都没中时才是真的必歪；
            // 偶尔连续歪几次不代表必歪，不能据此下结论。
            if (nPity >= 3 && nLose == nPity) data.Keywords.Add("小保底必歪");

            // 摘要
            var sb = new List<string>();
            if (notLoseRate.HasValue) sb.Add($"小保底不歪率 {notLoseRate.Value:0.#}%");
            if (avg.HasValue) sb.Add($"平均每金 {avg.Value:0.#} 抽");
            sb.Add($"五星 {goldCount} 个 / 共 {totalPulls} 抽");
            data.UpCount = fiveStars.Count(x => x.IsUp);
            data.Summary = (notLoseRate.HasValue || avg.HasValue ? string.Join("   ·   ", sb) + "   ·   " : "")
                         + $"获得UP5星角色 {data.UpCount} 次";

            // 样本量拦截：少于 3 个五星判为数据不足
            if (goldCount < 3)
            {
                data.Title = "数据不足";
                return;
            }

            // ---------- 覆盖式特殊称号 ----------
            bool hasExtreme = data.Keywords.Contains("十连双黄") || data.Keywords.Contains("3连UP");
            string title = null;

            if (avg.HasValue && avg.Value <= 40)
            {
                title = (hasExtreme && avg.Value <= 35) ? "万里挑一的欧皇" : "终极无敌至尊欧皇";
            }
            else if (pLose.HasValue && pLose.Value >= 0.85 && avg.HasValue && avg.Value >= 65)
            {
                title = "终极无敌至尊非酋王";
            }
            else if (pLose.HasValue && pLose.Value >= 0.25 && pLose.Value <= 0.50
                  && avg.HasValue && avg.Value >= 60 && avg.Value <= 75)
            {
                title = "欧非守恒";
            }
            else if (minInterval.HasValue && minInterval.Value >= 70
                  && median.HasValue && median.Value >= 78)
            {
                title = "终极非酋";
            }

            // ---------- 基础加权评分 ----------
            if (title == null)
            {
                double scoreAvg = avg.HasValue ? ClampScore((62.5 - avg.Value) / 30.0) : 0;
                double scoreMedian = median.HasValue ? ClampScore((75.0 - median.Value) / 30.0) : 0;
                double scoreStd = std.HasValue ? ClampScore((15.0 - std.Value) / 15.0) : 0;

                double scoreLose = 0;
                if (pLose.HasValue && nPity >= 5) scoreLose = ClampScore((0.5 - pLose.Value) / 0.5);

                double scoreExtreme = 0;
                if (minInterval.HasValue && minInterval.Value <= 20) scoreExtreme += 0.5;
                if (maxInterval.HasValue && maxInterval.Value >= 85) scoreExtreme -= 0.5;

                double scoreMode = 0;
                if (modes.Count > 0)
                {
                    int minMode = modes.Min();
                    if (minMode <= 50) scoreMode = 0.5;
                    if (minMode >= 75) scoreMode = -0.5;
                }

                double score = 0.35 * scoreAvg + 0.25 * scoreLose + 0.15 * scoreMedian
                             + 0.10 * scoreStd + 0.10 * scoreExtreme + 0.05 * scoreMode;

                if (score >= 1.20) title = "天运所归";
                else if (score >= 0.80) title = "终极欧皇";
                else if (score >= 0.45) title = "大欧皇";
                else if (score >= 0.15) title = "小欧皇";
                else if (score >= -0.15) title = "小有运气";
                else if (score >= -0.45) title = "平民";
                else if (score >= -0.80) title = "小非酋";
                else if (score >= -1.20) title = "大非酋";
                else title = "终极非酋";
            }

            data.Title = title;
        }

        private static double ClampScore(double value) => Math.Max(-1.0, Math.Min(1.0, value));


        private static bool HasDoubleGoldInTen(List<GachaReportEntry> fiveStars)
        {
            // 累计抽数按 1 开始编号：第 1~10 抽属于第一发十连，(Cumulative - 1) / 10 才是十连序号
            for (int i = 1; i < fiveStars.Count; i++)
            {
                if (fiveStars[i].Cumulative > 0 && fiveStars[i - 1].Cumulative > 0
                    && (fiveStars[i].Cumulative - 1) / 10 == (fiveStars[i - 1].Cumulative - 1) / 10)
                    return true;
            }
            return false;
        }

        private static bool HasThreeConsecutiveUp(List<GachaReportEntry> fiveStars)
        {
            int run = 0;
            foreach (var e in fiveStars)
            {
                if (e.IsUp) { run++; if (run >= 3) return true; }
                else run = 0;
            }
            return false;
        }

        // ================================================================
        // 蔚蓝档案（旧）
        // ================================================================
        private static void AnalyzeBa(GachaReportData data, int totalPulls)
        {
            int threeStar = data.Entries.Count;
            double ratio = totalPulls > 0 ? (double)threeStar / totalPulls * 100.0 : 0;

            data.UpCount = data.IsPermanent ? 0 : data.Entries.Count(x => x.IsUp);
            data.Summary = data.IsPermanent
                ? $"3 星 {threeStar} 个 / 共 {totalPulls} 抽   ·   3 星占比 {ratio:0.##}%"
                : $"3 星 {threeStar} 个 / 共 {totalPulls} 抽   ·   3 星占比 {ratio:0.##}%   ·   获得UP3星学生 {data.UpCount} 次";

            double r = ratio;

            if (data.IsCelebration)
            {
                if (r < 2) data.Title = "终极无敌至尊非酋";
                else if (r < 3) data.Title = "终极非酋";
                else if (r < 4) data.Title = "大非酋";
                else if (r < 5) data.Title = "小非酋";
                else if (r < 7) data.Title = "欧非守恒";
                else if (r < 8) data.Title = "小欧皇";
                else if (r < 9) data.Title = "大欧皇";
                else if (r < 10) data.Title = "终极欧皇";
                else data.Title = "终极无敌至尊欧皇";
            }
            else
            {
                if (r < 1) data.Title = "终极无敌至尊非酋";
                else if (r < 1.5) data.Title = "终极非酋";
                else if (r < 2) data.Title = "大非酋";
                else if (r < 2.5) data.Title = "小非酋";
                else if (r < 3.5) data.Title = "欧非守恒";
                else if (r < 4) data.Title = "小欧皇";
                else if (r < 4.5) data.Title = "大欧皇";
                else if (r < 5) data.Title = "终极欧皇";
                else if (r < 5.5) data.Title = "终极无敌至尊欧皇";
                else data.Title = "终极无敌至尊欧皇";
            }
        }
    }
}
