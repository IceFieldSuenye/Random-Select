using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace RandomDrawer
{
    // ============================================================
    // 抽取相关的数据模型
    // ============================================================

    public class DrawItem
    {
        public List<string> Fields { get; set; } = new List<string>();
        public string DisplayName => string.Join(" ", Fields);
        public double Probability { get; set; } = -1;
    }

    public class PoolInfo
    {
        public string Name { get; set; }
        public string UniqueName { get; set; }
        public string Dir { get; set; }
        public List<string> Headers { get; set; } = new List<string>();
        public List<DrawItem> Items { get; set; } = new List<DrawItem>();
        /// <summary>卡池条目数；允许未选中的卡池释放 Items 后仍能判断是否为空。</summary>
        public int ItemCount { get; set; }
        public string CoverPath { get; set; } = "";
        public bool HasProbability { get; set; } = false;
        public bool IsLocked { get; set; } = false;

        // ★ FIX（问题1）：data.txt 内容的 SHA256 指纹。
        //   · 由 MainPage_UC.LoadSinglePoolFromDirStatic 计算并写入；
        //   · MainPage_UC.RefreshPoolsKeepingSelection 对比新旧指纹，
        //     若变化则重置 Engine.DrawnIndices 并刷新计数，
        //     让用户能直观看到"本次抽取次数：0"，说明数据已刷新。
        //   · 内容未变时保持不变，从而保留"不重复抽取"的进度。
        public string ContentHash { get; set; } = "";
    }

    public class GachaState
    {
        public int Pity5 { get; set; }
        public int Pity4 { get; set; }
        public bool Guaranteed5UP { get; set; }
        public bool Guaranteed4UP { get; set; }
    }

    // ============================================================
    // 抽取引擎
    // ============================================================

    public class DrawEngine
    {
        private readonly Random _random;

        public HashSet<int> DrawnIndices { get; } = new HashSet<int>();

        public DrawEngine()
        {
            _random = new Random();
        }

        public Random Random => _random;

        // ============================================================
        // 抽取入口
        // ============================================================

        public List<DrawItem> ExecuteDraw(int count, PoolInfo pool, bool noRepeat, Window owner)
        {
            var results = new List<DrawItem>();
            if (pool == null || pool.Items == null || pool.Items.Count == 0) return results;

            var gachaSettings = GetGachaSettings(pool.Dir);

            if (gachaSettings != null)
            {
                if (gachaSettings.PresetName == "蔚蓝档案（旧）")
                    results = PerformBADraw(pool, gachaSettings, count);
                else
                {
                    var state = LoadGachaState(pool.Dir);
                    for (int i = 0; i < count; i++)
                    {
                        var picked = PerformSingleGachaPull(pool, gachaSettings, state);
                        if (picked != null) results.Add(picked);
                    }
                    SaveGachaState(pool.Dir, state);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    List<DrawItem> availableItems;
                    if (noRepeat)
                        availableItems = pool.Items.Where((item, idx) => !DrawnIndices.Contains(idx)).ToList();
                    else
                        availableItems = pool.Items.ToList();

                    if (availableItems.Count == 0)
                    {
                        if (i == 0 && owner != null)
                        {
                            var resetDialog = new ResetPromptDialog(pool.Name) { Owner = owner };
                            if (resetDialog.ShowDialog() == true && resetDialog.IsReset)
                            {
                                DrawnIndices.Clear();
                                ResetGachaState(pool.Dir);
                            }
                        }
                        break;
                    }

                    DrawItem selectedItem;

                    // ★ 只有"所有可用项概率严格 > 0"才走加权随机
                    bool useWeighted = pool.HasProbability
                                    && availableItems.All(item => item.Probability > 0);

                    if (useWeighted)
                    {
                        var weights = availableItems.Select(item => item.Probability).ToList();
                        double totalWeight = weights.Sum();
                        double r = _random.NextDouble() * totalWeight;
                        double cumulative = 0;
                        selectedItem = availableItems.Last();
                        for (int j = 0; j < availableItems.Count; j++)
                        {
                            cumulative += weights[j];
                            if (r <= cumulative) { selectedItem = availableItems[j]; break; }
                        }
                    }
                    else
                    {
                        // 均匀随机
                        selectedItem = availableItems[_random.Next(availableItems.Count)];
                    }

                    if (noRepeat)
                    {
                        int originalIndex = pool.Items.IndexOf(selectedItem);
                        DrawnIndices.Add(originalIndex);
                    }
                    results.Add(selectedItem);
                }
            }

            return results;
        }

        public void ResetDrawnIndices() => DrawnIndices.Clear();

        public int DrawnCount => DrawnIndices.Count;

        // ============================================================
        // 历史记录
        // ============================================================

        public void WriteDrawHistory(PoolInfo pool, List<DrawItem> results, string drawType)
        {
            if (App.IsHistoryDisabled) return;
            if (pool == null || results == null || results.Count == 0) return;

            string historyFile = Path.Combine(pool.Dir, "history.txt");
            try
            {
                using (StreamWriter sw = File.AppendText(historyFile))
                {
                    foreach (var item in results)
                        sw.WriteLine($"{DateTime.Now:yyyy/MM/dd HH:mm:ss}|{drawType}|{string.Join("\t", item.Fields)}");
                }
            }
            catch { }
        }

        // ============================================================
        // 状态管理
        // ============================================================

        public GachaState LoadGachaState(string poolDir)
        {
            var s = new GachaState();
            string file = Path.Combine(poolDir, "gacha_state.ini");
            if (!File.Exists(file)) return s;
            try
            {
                int.TryParse(IniHelper.ReadValue("State", "Pity5", "0", file), out int p5); s.Pity5 = p5;
                int.TryParse(IniHelper.ReadValue("State", "Pity4", "0", file), out int p4); s.Pity4 = p4;
                s.Guaranteed5UP = IniHelper.ReadValue("State", "Guaranteed5UP", "0", file) == "1";
                s.Guaranteed4UP = IniHelper.ReadValue("State", "Guaranteed4UP", "0", file) == "1";

                string settingsFile = Path.Combine(poolDir, "gacha_settings.ini");
                if (File.Exists(settingsFile))
                {
                    var gs = GachaSettings.LoadSettingsFromFile(settingsFile);
                    if (gs != null && gs.PresetName != "蔚蓝档案（旧）")
                    {
                        int fivePity = gs.FiveStarPity > 0 ? gs.FiveStarPity : 90;
                        int fourPity = gs.FourStarPity > 0 ? gs.FourStarPity : 10;
                        if (s.Pity5 < 0) s.Pity5 = 0;
                        if (s.Pity4 < 0) s.Pity4 = 0;
                        if (s.Pity5 >= fivePity) s.Pity5 = fivePity - 1;
                        if (s.Pity4 >= fourPity) s.Pity4 = fourPity - 1;
                    }
                }
            }
            catch { }
            return s;
        }

        public void SaveGachaState(string poolDir, GachaState s)
        {
            try
            {
                if (s.Pity5 < 0) s.Pity5 = 0;
                if (s.Pity4 < 0) s.Pity4 = 0;
                string file = Path.Combine(poolDir, "gacha_state.ini");
                IniHelper.WriteValue("State", "Pity5", s.Pity5.ToString(), file);
                IniHelper.WriteValue("State", "Pity4", s.Pity4.ToString(), file);
                IniHelper.WriteValue("State", "Guaranteed5UP", s.Guaranteed5UP ? "1" : "0", file);
                IniHelper.WriteValue("State", "Guaranteed4UP", s.Guaranteed4UP ? "1" : "0", file);
            }
            catch { }
        }

        public void ResetGachaState(string poolDir)
        {
            try
            {
                string file = Path.Combine(poolDir, "gacha_state.ini");
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        public GachaSettings GetGachaSettings(string poolDir)
        {
            try
            {
                string file = Path.Combine(poolDir, "gacha_settings.ini");
                if (!File.Exists(file)) return null;
                var s = GachaSettings.LoadSettingsFromFile(file);
                return (s != null && s.Enabled) ? s : null;
            }
            catch { return null; }
        }

        public bool HasActiveGachaStrategy(string poolDir)
        {
            try
            {
                string file = Path.Combine(poolDir, "gacha_settings.ini");
                if (!File.Exists(file)) return false;
                var s = GachaSettings.LoadSettingsFromFile(file);
                return s != null && s.Enabled;
            }
            catch { return false; }
        }

        // ============================================================
        // 辅助
        // ============================================================

        public static List<string> ParseUpObjectsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        private static bool CategoryEquals(string a, string b) => (a ?? "").Trim() == (b ?? "").Trim();

        // ============================================================
        // 高级抽卡：崩坏：星穹铁道
        // ============================================================

        private DrawItem PerformSingleGachaPull(PoolInfo pool, GachaSettings settings, GachaState state)
        {
            state.Pity5++;
            state.Pity4++;

            int fiveStarPityLimit = settings.FiveStarPity > 0 ? settings.FiveStarPity : 90;
            int fourStarPityLimit = settings.FourStarPity > 0 ? settings.FourStarPity : 10;

            double sumPercent = settings.FiveStarNonUpProb + settings.FiveStarUpProb
                              + settings.FourStarUpProb + settings.FourStarNonUpProb;
            if (sumPercent > 100.0) sumPercent = 100.0;

            double base5 = (settings.FiveStarNonUpProb + settings.FiveStarUpProb) / 100.0;
            if (base5 <= 0) base5 = 0.006;
            if (base5 > 1.0) base5 = 1.0;

            double current5 = base5;
            if (settings.SoftPityStart > 0 && state.Pity5 >= settings.SoftPityStart)
            {
                int extraPulls = state.Pity5 - settings.SoftPityStart + 1;
                current5 = base5 + extraPulls * settings.SoftPityIncrease;
                if (current5 > 1.0) current5 = 1.0;
            }

            bool isHardPity5 = state.Pity5 >= fiveStarPityLimit;
            if (isHardPity5) current5 = 1.0;

            double base4 = (settings.FourStarUpProb + settings.FourStarNonUpProb) / 100.0;
            if (base4 < 0) base4 = 0;
            if (base4 > 1.0) base4 = 1.0;

            double current4 = base4;
            if (current5 + current4 > 1.0) current4 = 1.0 - current5;
            if (current4 < 0) current4 = 0;

            bool isHardPity4 = state.Pity4 >= fourStarPityLimit;

            double roll = _random.NextDouble();

            if (isHardPity5 || roll < current5)
            {
                var picked5 = Pull5StarItem(pool, settings, state);
                if (picked5 == null)
                    picked5 = FallbackFindByStar(pool, 5, isGuaranteedUP: state.Guaranteed5UP,
                        upName: (settings.FiveStarUpObject ?? "").Trim());

                if (picked5 != null) { state.Pity5 = 0; state.Pity4 = 0; return picked5; }

                var fallbackAny = Pull3StarItem(pool, settings);
                if (fallbackAny != null) return fallbackAny;
                return pool.Items.Count > 0 ? pool.Items[_random.Next(pool.Items.Count)] : null;
            }

            if (isHardPity4 || roll < current5 + current4)
            {
                var picked4 = Pull4StarItem(pool, settings, state);
                if (picked4 == null) picked4 = FallbackFindByStar(pool, 4, isGuaranteedUP: state.Guaranteed4UP, upName: "");

                if (picked4 != null) { state.Pity4 = 0; return picked4; }

                var fallbackAny4 = Pull3StarItem(pool, settings);
                if (fallbackAny4 != null) return fallbackAny4;
                return pool.Items.Count > 0 ? pool.Items[_random.Next(pool.Items.Count)] : null;
            }

            var picked3 = Pull3StarItem(pool, settings);
            if (picked3 != null) return picked3;

            return pool.Items.Count > 0 ? pool.Items[_random.Next(pool.Items.Count)] : null;
        }

        private DrawItem Pull5StarItem(PoolInfo pool, GachaSettings settings, GachaState state)
        {
            var allCandidates = pool.Items.Where(it => it.Fields.Count >= 2 &&
                CategoryEquals(it.Fields[1], settings.FiveStarCategory)).ToList();
            if (allCandidates.Count == 0) return null;

            string upName = (settings.FiveStarUpObject ?? "").Trim();
            var upCandidates = string.IsNullOrEmpty(upName) ? new List<DrawItem>()
                : allCandidates.Where(it => it.Fields.Count > 0 &&
                    ((it.Fields[0] ?? "").Trim() == upName)).ToList();

            var nonUpCandidates = upCandidates.Count > 0
                ? allCandidates.Where(it => !upCandidates.Contains(it)).ToList()
                : allCandidates.ToList();

            if (state.Guaranteed5UP)
            {
                if (upCandidates.Count > 0) { state.Guaranteed5UP = false; return upCandidates[_random.Next(upCandidates.Count)]; }
                return null;
            }

            if (upCandidates.Count == 0)
            {
                if (nonUpCandidates.Count > 0) return nonUpCandidates[_random.Next(nonUpCandidates.Count)];
                return allCandidates[_random.Next(allCandidates.Count)];
            }

            if (_random.NextDouble() <= 0.5)
            { state.Guaranteed5UP = true; return nonUpCandidates[_random.Next(nonUpCandidates.Count)]; }
            else
            { state.Guaranteed5UP = false; return upCandidates[_random.Next(upCandidates.Count)]; }
        }

        private DrawItem Pull4StarItem(PoolInfo pool, GachaSettings settings, GachaState state)
        {
            var allCandidates = pool.Items.Where(it => it.Fields.Count >= 2 &&
                CategoryEquals(it.Fields[1], settings.FourStarCategory)).ToList();
            if (allCandidates.Count == 0) return null;

            var upObjectNames = ParseUpObjectsString(settings.FourStarUpObjects)
                .Select(x => (x ?? "").Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();

            var upCandidates = upObjectNames.Count == 0 ? new List<DrawItem>()
                : allCandidates.Where(it => it.Fields.Count > 0 &&
                    upObjectNames.Contains((it.Fields[0] ?? "").Trim())).ToList();

            var nonUpCandidates = upCandidates.Count > 0
                ? allCandidates.Where(it => !upCandidates.Contains(it)).ToList()
                : allCandidates.ToList();

            if (state.Guaranteed4UP)
            {
                if (upCandidates.Count > 0) { state.Guaranteed4UP = false; return upCandidates[_random.Next(upCandidates.Count)]; }
                return null;
            }

            if (upCandidates.Count == 0)
            {
                if (nonUpCandidates.Count > 0) return nonUpCandidates[_random.Next(nonUpCandidates.Count)];
                return allCandidates[_random.Next(allCandidates.Count)];
            }

            if (_random.NextDouble() <= 0.5)
            { state.Guaranteed4UP = true; return nonUpCandidates[_random.Next(nonUpCandidates.Count)]; }
            else
            { state.Guaranteed4UP = false; return upCandidates[_random.Next(upCandidates.Count)]; }
        }

        private DrawItem Pull3StarItem(PoolInfo pool, GachaSettings settings)
        {
            var candidates = pool.Items.Where(it =>
                it.Fields.Count >= 2 &&
                !CategoryEquals(it.Fields[1], settings.FiveStarCategory) &&
                !CategoryEquals(it.Fields[1], settings.FourStarCategory)).ToList();

            if (candidates.Count == 0) candidates = pool.Items.ToList();
            return candidates[_random.Next(candidates.Count)];
        }

        private DrawItem FallbackFindByStar(PoolInfo pool, int starLevel, bool isGuaranteedUP = false, string upName = "")
        {
            var all = new List<DrawItem>();
            var upOnly = new List<DrawItem>();
            upName = (upName ?? "").Trim();

            foreach (var item in pool.Items)
            {
                if (!IsStarLevel(item, starLevel)) continue;
                all.Add(item);
                if (!string.IsNullOrEmpty(upName) && item.Fields.Count > 0 &&
                    (item.Fields[0] ?? "").Trim() == upName)
                    upOnly.Add(item);
            }

            if (all.Count == 0) return null;
            if (isGuaranteedUP && upOnly.Count > 0) return upOnly[_random.Next(upOnly.Count)];
            return all[_random.Next(all.Count)];
        }

        private bool IsStarLevel(DrawItem item, int starLevel)
        {
            foreach (var f in item.Fields)
            {
                if (string.IsNullOrEmpty(f)) continue;

                int c1 = 0; foreach (char c in f) if (c == '⭐') c1++;
                if (c1 == starLevel) return true;

                int c2 = 0; foreach (char c in f) if (c == '★') c2++;
                if (c2 == starLevel) return true;

                if (f.Contains($"{starLevel}星")) return true;
                if (starLevel == 5 && (f.Contains("五星") || f.Contains("5星"))) return true;
                if (starLevel == 4 && (f.Contains("四星") || f.Contains("4星"))) return true;
                if (starLevel == 3 && (f.Contains("三星") || f.Contains("3星"))) return true;
                if (int.TryParse(f.Trim(), out int v) && v == starLevel) return true;
            }
            return false;
        }

        // ============================================================
        // 高级抽卡：蔚蓝档案（旧）
        // ============================================================

        private List<DrawItem> PerformBADraw(PoolInfo pool, GachaSettings settings, int count)
        {
            var results = new List<DrawItem>();
            var state = LoadGachaState(pool.Dir);

            var threeStarItems = pool.Items.Where(it => it.Fields.Count >= 2 &&
                CategoryEquals(it.Fields[1], settings.BA3StarCategory)).ToList();
            var twoStarItems = pool.Items.Where(it => it.Fields.Count >= 2 &&
                CategoryEquals(it.Fields[1], settings.BA2StarCategory)).ToList();
            var oneStarItems = pool.Items.Where(it => it.Fields.Count >= 2 &&
                !CategoryEquals(it.Fields[1], settings.BA3StarCategory) &&
                !CategoryEquals(it.Fields[1], settings.BA2StarCategory)).ToList();

            if (threeStarItems.Count == 0 || twoStarItems.Count == 0 || oneStarItems.Count == 0)
            {
                for (int i = 0; i < count; i++)
                    if (pool.Items.Count > 0) results.Add(pool.Items[_random.Next(pool.Items.Count)]);
                return results;
            }

            for (int i = 0; i < count; i++)
            {
                var picked = PerformBASinglePull(pool, settings, state,
                    threeStarItems, twoStarItems, oneStarItems);
                if (picked != null) results.Add(picked);
            }

            SaveGachaState(pool.Dir, state);
            return results;
        }

        private DrawItem PerformBASinglePull(PoolInfo pool, GachaSettings settings, GachaState state,
            List<DrawItem> threeStarItems, List<DrawItem> twoStarItems, List<DrawItem> oneStarItems)
        {
            if (threeStarItems == null || threeStarItems.Count == 0) return null;
            if (twoStarItems == null || twoStarItems.Count == 0) return null;
            if (oneStarItems == null) oneStarItems = new List<DrawItem>();

            state.Pity4++;
            int pityLimit = settings.BA2StarPity > 0 ? settings.BA2StarPity : 10;
            bool isPity = state.Pity4 >= pityLimit;

            double threeProb = settings.BA3StarProb;
            double twoProb = settings.BA2StarProb;
            if (threeProb < 0) threeProb = 0;
            if (twoProb < 0) twoProb = 0;
            if (threeProb + twoProb > 1.0)
            {
                double total = threeProb + twoProb;
                if (total > 0) { threeProb /= total; twoProb /= total; }
            }

            int starLevel;
            if (isPity)
            {
                double total = threeProb + twoProb;
                if (total <= 0) starLevel = 2;
                else { double r = _random.NextDouble() * total; starLevel = r < threeProb ? 3 : 2; }
            }
            else
            {
                double r = _random.NextDouble();
                if (r < threeProb) starLevel = 3;
                else if (r < threeProb + twoProb) starLevel = 2;
                else starLevel = 1;
            }

            DrawItem picked = null;
            if (starLevel == 3) { state.Pity4 = 0; picked = PullBA3Star(settings, threeStarItems); }
            else if (starLevel == 2) { state.Pity4 = 0; picked = PullBA2Star(settings, twoStarItems); }
            else picked = oneStarItems.Count > 0 ? oneStarItems[_random.Next(oneStarItems.Count)] : null;

            if (picked == null && pool.Items.Count > 0)
                picked = pool.Items[_random.Next(pool.Items.Count)];

            return picked;
        }

        private DrawItem PullBA3Star(GachaSettings settings, List<DrawItem> threeStarItems)
        {
            if (threeStarItems == null || threeStarItems.Count == 0) return null;
            string poolType = settings.BAPoolType;
            double totalThreeProb = settings.BA3StarProb;
            if (totalThreeProb <= 0) totalThreeProb = 0.03;

            if (poolType == "常驻招募") return threeStarItems[_random.Next(threeStarItems.Count)];
            else if (poolType == "限时招募")
            {
                double upProb = settings.BAUp3StarProb;
                if (upProb < 0) upProb = 0;
                if (upProb > totalThreeProb) upProb = totalThreeProb;

                double upConditionProb = totalThreeProb > 0 ? (upProb / totalThreeProb) : 0;
                var upItems = threeStarItems.Where(it => it.Fields.Count > 0 &&
                    (it.Fields[0] ?? "").Trim() == settings.BAUp3Star).ToList();
                var others = threeStarItems.Where(it => !upItems.Contains(it)).ToList();

                if (upItems.Count > 0 && _random.NextDouble() < upConditionProb)
                    return upItems[_random.Next(upItems.Count)];
                if (others.Count > 0) return others[_random.Next(others.Count)];
                return threeStarItems[_random.Next(threeStarItems.Count)];
            }
            else
            {
                double mainUpProb = settings.BAUp3StarProb;
                double subUpProb = settings.BASubUp3StarProb;
                if (mainUpProb < 0) mainUpProb = 0;
                if (subUpProb < 0) subUpProb = 0;
                if (mainUpProb + subUpProb > totalThreeProb)
                {
                    double sum = mainUpProb + subUpProb;
                    if (sum > 0)
                    {
                        mainUpProb = mainUpProb / sum * totalThreeProb;
                        subUpProb = subUpProb / sum * totalThreeProb;
                    }
                }

                double mainCondProb = totalThreeProb > 0 ? (mainUpProb / totalThreeProb) : 0;
                double subCondProb = totalThreeProb > 0 ? (subUpProb / totalThreeProb) : 0;

                var mainUpNames = ParseUpObjectsString(settings.BAMainUp3Stars);
                var subUpNames = ParseUpObjectsString(settings.BASubUp3Stars);

                var mainUps = threeStarItems.Where(it => it.Fields.Count > 0 &&
                    mainUpNames.Contains((it.Fields[0] ?? "").Trim())).ToList();
                var subUps = threeStarItems.Where(it => it.Fields.Count > 0 &&
                    subUpNames.Contains((it.Fields[0] ?? "").Trim())).ToList();
                var others = threeStarItems.Where(it => !mainUps.Contains(it) && !subUps.Contains(it)).ToList();

                double r = _random.NextDouble();
                if (mainUps.Count > 0 && r < mainCondProb) return mainUps[_random.Next(mainUps.Count)];
                if (subUps.Count > 0 && r < mainCondProb + subCondProb) return subUps[_random.Next(subUps.Count)];
                if (others.Count > 0) return others[_random.Next(others.Count)];
                return threeStarItems[_random.Next(threeStarItems.Count)];
            }
        }

        private DrawItem PullBA2Star(GachaSettings settings, List<DrawItem> twoStarItems)
        {
            if (twoStarItems == null || twoStarItems.Count == 0) return null;
            string poolType = settings.BAPoolType;

            if (poolType == "限时招募" && !string.IsNullOrEmpty(settings.BAUp2Star))
            {
                double totalTwoProb = settings.BA2StarProb;
                if (totalTwoProb <= 0) totalTwoProb = 0.185;

                double upProb = settings.BAUp2StarProb;
                if (upProb < 0) upProb = 0;
                if (upProb > totalTwoProb) upProb = totalTwoProb;

                double upCondProb = totalTwoProb > 0 ? (upProb / totalTwoProb) : 0;
                var upItems = twoStarItems.Where(it => it.Fields.Count > 0 &&
                    (it.Fields[0] ?? "").Trim() == settings.BAUp2Star).ToList();
                var others = twoStarItems.Where(it => !upItems.Contains(it)).ToList();

                if (upItems.Count > 0 && _random.NextDouble() < upCondProb)
                    return upItems[_random.Next(upItems.Count)];
                if (others.Count > 0) return others[_random.Next(others.Count)];
                return twoStarItems[_random.Next(twoStarItems.Count)];
            }
            return twoStarItems[_random.Next(twoStarItems.Count)];
        }
    }
}