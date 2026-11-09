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

        // ★ 3.20.0：报告按跃迁类型智能显示
        public string WarpType = "";       // 角色活动跃迁 / 光锥活动跃迁 / 群星跃迁
        public bool HasUp = true;          // 没有 UP 的跃迁（群星）不显示「歪」标记与 UP 统计
        public int PityLimit = 90;         // 保底抽数（决定进度条刻度与称号门槛）
        public string FiveStarName = "5星角色"; // 报告里对 5 星物品的称呼
    }

    /// <summary>根据卡池高级规则与历史记录生成「抽取报告」。</summary>
    public static class GachaReportAnalyzer
    {
        /// <summary>
        /// 找出对象名所在的列下标：优先取 data.txt 表头里「抽取对象」这一列。
        /// 历史记录（history.txt）只是把整行字段用 Tab 拼起来，本身不带表头，
        /// 所以必须回到卡池数据里查表头，否则「序号」放第一列时报告里的名字会全变成数字。
        /// 只读，不会触发旧格式迁移。
        /// </summary>
        private static int ResolveNameColumn(string poolDir)
        {
            try
            {
                string raw = PoolArchive.ReadDataTxtFromArchive(poolDir);
                if (string.IsNullOrEmpty(raw)) return 0;

                string first = raw.Replace("\r\n", "\n").Replace('\r', '\n')
                                  .Split('\n')
                                  .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (string.IsNullOrWhiteSpace(first)) return 0;

                var cols = first.Split(new[] { '\t', ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < cols.Length; i++)
                {
                    if (string.Equals(cols[i].Trim(), "抽取对象", StringComparison.Ordinal)) return i;
                }
            }
            catch { }

            return 0;
        }

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
            int nameIndex = ResolveNameColumn(poolDir);
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

                string name = (nameIndex < fields.Count ? fields[nameIndex] : fields[0]).Trim();
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
                // ★ 5 星分类可能是「5星角色,5星光锥」这种多分类（光锥 / 群星跃迁），逐个比对
                bool isRare = false;
                if (!string.IsNullOrWhiteSpace(rareCat))
                {
                    foreach (string part in rareCat.Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string cat = part.Trim();
                        if (cat.Length == 0) continue;
                        if (kv.Value.Any(f => string.Equals(f.Trim(), cat, StringComparison.OrdinalIgnoreCase)))
                        { isRare = true; break; }
                    }
                }

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
                // ★ 按跃迁类型取保底与刻度：光锥池 80 抽保底，角色 / 群星 90 抽
                string warpType = IniHelper.ReadValue("Gacha", "WarpType", "角色活动跃迁", settingsFile);
                bool isLightCone = warpType == "光锥活动跃迁";
                bool isStandard = warpType == "群星跃迁";

                int pity = ParseIntSetting(settingsFile, "FiveStarPity", 90);
                if (pity <= 0) pity = isLightCone ? 80 : 90;

                data.WarpType = warpType;
                data.PityLimit = pity;
                data.HasUp = !isStandard;
                data.FiveStarName = isStandard ? "5星物品" : isLightCone ? "5星光锥" : "5星角色";

                // 颜色门槛按保底比例算：绿 ≤55%、黄 ≤78%，这样 90 抽池是 50/70，光锥 80 抽池是 44/62
                data.GreenMax = (int)Math.Round(pity * 0.55);
                data.YellowMax = (int)Math.Round(pity * 0.78);
                data.ScaleMax = pity;
            }

            // ---------- 整体判定 ----------
            if (isBa) AnalyzeBa(data, pulls.Count);
            else
            {
                // ★ 群星跃迁（常驻）没有 UP：不要再算「小保底不歪率」
                bool hsrHasUp = !string.IsNullOrWhiteSpace(
                    IniHelper.ReadValue("Gacha", "FiveStarUpObject", "", settingsFile));

                // ★ 出金时「命中 UP」的基准概率：角色池 0.3/0.3=50%，光锥池 0.6/0.2=75%
                double upShare = 0.5;
                if (hsrHasUp)
                {
                    double upProb = ParseDoubleSetting(settingsFile, "FiveStarUpProb", 0.300);
                    double nonUpProb = ParseDoubleSetting(settingsFile, "FiveStarNonUpProb", 0.300);
                    double sum = upProb + nonUpProb;
                    if (sum > 0) upShare = upProb / sum;
                }

                AnalyzeHsr(data, pulls.Count, data.HasUp, data.PityLimit, upShare);
            }

            data.HasData = hits.Count > 0;
            if (!data.HasData)
            {
                data.Title = "数据不足";
                data.SubTitle = "还没有抽到" + (isBa ? "3 星" : data.FiveStarName) + "，再接再厉。";
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
        private static void AnalyzeHsr(GachaReportData data, int totalPulls, bool hasUp, int pity, double upShare)
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

            // ★ 中性歪率：角色池 50%（歪一半正常），光锥池 25%（75% UP）；称号按这个基准判「欧 / 非」
            double neutralLose = Math.Max(0.05, Math.Min(0.95, 1.0 - upShare));

            double? pLose = nPity > 0 ? (double)nLose / nPity : (double?)null;
            double? notLoseRate = pLose.HasValue ? (1.0 - pLose.Value) * 100.0 : (double?)null;

            // 没有 UP 的跃迁（群星跃迁）不存在 50/50 小保底，歪率相关统计一律不出现
            if (!hasUp) { pLose = null; notLoseRate = null; nPity = 0; nLose = 0; }

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

            // ★ 蓝色关键词按实际最长连 UP 次数显示（连续 4 次就写「4连UP」），没有 UP 的跃迁不显示
            if (hasUp)
            {
                int maxUpRun = MaxConsecutiveUp(fiveStars);
                if (maxUpRun >= 3) data.Keywords.Add($"{maxUpRun}连UP");
                // 「小保底必歪」只在所有小保底都没中时才是真的必歪；
                // 偶尔连续歪几次不代表必然，不能据此下结论。
                if (nPity >= 3 && nLose == nPity) data.Keywords.Add("小保底必歪");
            }

            // 摘要
            var sb = new List<string>();
            if (notLoseRate.HasValue) sb.Add($"小保底不歪率 {notLoseRate.Value:0.#}%");
            if (avg.HasValue) sb.Add($"平均每金 {avg.Value:0.#} 抽");
            sb.Add($"{data.FiveStarName} {goldCount} 个 / 共 {totalPulls} 抽");
            data.UpCount = hasUp ? fiveStars.Count(x => x.IsUp) : 0;
            data.Summary = (notLoseRate.HasValue || avg.HasValue ? string.Join("   ·   ", sb) + "   ·   " : "")
                         + (hasUp ? $"获得UP{data.FiveStarName} {data.UpCount} 次" : "常驻跃迁（无 UP）");

            // 样本量拦截：少于 3 个五星判为数据不足
            if (goldCount < 3)
            {
                data.Title = "数据不足";
                return;
            }

            // ---------- 覆盖式特殊称号 ----------
            // ★ 门槛按保底抽数缩放：光锥池 80 抽保底，同样的「欧」要比 90 抽池少一些抽数
            double scale = pity > 0 ? pity / 90.0 : 1.0;
            bool hasExtreme = data.Keywords.Contains("十连双黄")
                              || data.Keywords.Any(k => k.EndsWith("连UP", StringComparison.Ordinal));
            string title = null;

            if (avg.HasValue && avg.Value <= 40 * scale)
            {
                title = (hasExtreme && avg.Value <= 35 * scale) ? "万里挑一的欧皇" : "终极无敌至尊欧皇";
            }
            else if (pLose.HasValue && pLose.Value >= neutralLose + 0.35 && avg.HasValue && avg.Value >= 65 * scale)
            {
                title = "终极无敌至尊非酋王";
            }
            else if (pLose.HasValue && pLose.Value >= neutralLose * 0.5 && pLose.Value <= neutralLose
                  && avg.HasValue && avg.Value >= 60 * scale && avg.Value <= 75 * scale)
            {
                title = "欧非守恒";
            }
            else if (minInterval.HasValue && minInterval.Value >= 70 * scale
                  && median.HasValue && median.Value >= 78 * scale)
            {
                title = "终极非酋";
            }

            // ---------- 基础加权评分 ----------
            if (title == null)
            {
                double scoreAvg = avg.HasValue ? ClampScore((62.5 * scale - avg.Value) / (30.0 * scale)) : 0;
                double scoreMedian = median.HasValue ? ClampScore((75.0 * scale - median.Value) / (30.0 * scale)) : 0;
                double scoreStd = std.HasValue ? ClampScore((15.0 * scale - std.Value) / (15.0 * scale)) : 0;

                double scoreLose = 0;
                if (pLose.HasValue && nPity >= 5) scoreLose = ClampScore((neutralLose - pLose.Value) / neutralLose);

                double scoreExtreme = 0;
                if (minInterval.HasValue && minInterval.Value <= 20 * scale) scoreExtreme += 0.5;
                if (maxInterval.HasValue && maxInterval.Value >= 85 * scale) scoreExtreme -= 0.5;

                double scoreMode = 0;
                if (modes.Count > 0)
                {
                    int minMode = modes.Min();
                    if (minMode <= 50 * scale) scoreMode = 0.5;
                    if (minMode >= 75 * scale) scoreMode = -0.5;
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

        /// <summary>最长连续 UP 次数（蓝色关键词「N连UP」用真实次数，不再固定写 3）。</summary>
        private static int MaxConsecutiveUp(List<GachaReportEntry> fiveStars)
        {
            int best = 0, run = 0;
            foreach (var e in fiveStars)
            {
                if (e.IsUp) { run++; if (run > best) best = run; }
                else run = 0;
            }
            return best;
        }

        /// <summary>从卡池 ini 里读一个小数设置（读不到 / 非法时用默认值）。</summary>
        private static double ParseDoubleSetting(string settingsFile, string key, double fallback)
        {
            try
            {
                string raw = IniHelper.ReadValue("Gacha", key, "", settingsFile);
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double value))
                    return value;
            }
            catch { }
            return fallback;
        }

        /// <summary>从卡池 ini 里读一个整数设置（读不到 / 非法时用默认值）。</summary>
        private static int ParseIntSetting(string settingsFile, string key, int fallback)
        {
            try
            {
                string raw = IniHelper.ReadValue("Gacha", key, "", settingsFile);
                if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int value))
                    return value;
            }
            catch { }
            return fallback;
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

            // 称号档位：0 = 最非 … 8 = 最欧
            int tier;

            if (data.IsPermanent)
            {
                // 常驻招募没有「歪」的概念，只看 3 星占比
                tier = r < 1 ? 0
                     : r < 1.5 ? 1
                     : r < 2 ? 2
                     : r < 2.5 ? 3
                     : r < 3.5 ? 4
                     : r < 4 ? 5
                     : r < 4.5 ? 6
                     : r < 5.5 ? 7
                     : 8;
            }
            else
            {
                // 限时 / 庆典：先按 3 星占比定基础档位
                if (data.IsCelebration)
                {
                    tier = r < 2 ? 0
                         : r < 3 ? 1
                         : r < 4 ? 2
                         : r < 5 ? 3
                         : r < 7 ? 4
                         : r < 8 ? 5
                         : r < 9 ? 6
                         : r < 10 ? 7
                         : 8;
                }
                else
                {
                    tier = r < 1 ? 0
                         : r < 1.5 ? 1
                         : r < 2 ? 2
                         : r < 2.5 ? 3
                         : r < 3.5 ? 4
                         : r < 4 ? 5
                         : r < 4.5 ? 6
                         : r < 5 ? 7
                         : 8;
                }

                // 再按「歪」的比例加权：全部 UP 上调、全部歪掉下调
                tier = AdjustTierByLoseRate(tier, data, threeStar);
            }

            data.Title = TierToTitle(tier);
        }

        /// <summary>限时 / 庆典招募：用「歪」的比例（歪掉的比例越低越欧）调整档位。</summary>
        private static int AdjustTierByLoseRate(int tier, GachaReportData data, int threeStar)
        {
            if (threeStar <= 0) return tier;

            int up = data.Entries.Count(x => x.IsUp);
            int lose = Math.Max(0, threeStar - up);

            double loseRate = (double)lose / threeStar;   // 0（全中）～1（全歪）
            double loseScore = (0.5 - loseRate) / 0.5;    // +1 全中，-1 全歪，0 一半
            // 样本少时弱化歪率的影响，避免一两个 3 星就决定称号
            double confidence = Math.Min(1.0, threeStar / 8.0);

            int adjust = (int)Math.Round(loseScore * confidence * 2.0, MidpointRounding.AwayFromZero);
            return Math.Max(0, Math.Min(8, tier + adjust));
        }

        private static string TierToTitle(int tier)
        {
            switch (tier)
            {
                case 8: return "终极无敌至尊欧皇";
                case 7: return "终极欧皇";
                case 6: return "大欧皇";
                case 5: return "小欧皇";
                case 4: return "欧非守恒";
                case 3: return "小非酋";
                case 2: return "大非酋";
                case 1: return "终极非酋";
                default: return "终极无敌至尊非酋";
            }
        }
    }
}
