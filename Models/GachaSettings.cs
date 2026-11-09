using System;
using System.Globalization;
using System.IO;

namespace RandomDrawer
{
    /// <summary>
    /// 高级抽卡规则的数据模型 + ini 读写。
    /// </summary>
    public class GachaSettings
    {
        public bool Enabled { get; set; } = false;
        public string PresetName { get; set; } = "崩坏：星穹铁道";

        // HSR
        public string WarpType { get; set; } = "角色活动跃迁";
        public string FiveStarCategory { get; set; } = "";
        public string FiveStarUpObject { get; set; } = "";
        public string FourStarCategory { get; set; } = "";
        public string FourStarUpObjects { get; set; } = "";
        public double FiveStarNonUpProb { get; set; } = 0.300;
        public double FiveStarUpProb { get; set; } = 0.300;
        public double FourStarUpProb { get; set; } = 2.550;
        public double FourStarNonUpProb { get; set; } = 2.550;
        public double ThreeStarProb { get; set; } = 94.300;
        public int SoftPityStart { get; set; } = 74;
        public double SoftPityIncrease { get; set; } = 0.06;
        public int FourStarPity { get; set; } = 10;
        public int FiveStarPity { get; set; } = 90;

        public bool EnableStarColor { get; set; } = false;
        public bool EnableHighlightColors { get; set; } = false;

        // BA
        public string BAPoolType { get; set; } = "常驻招募";
        public string BA3StarCategory { get; set; } = "";
        public string BA2StarCategory { get; set; } = "";
        public double BA3StarProb { get; set; } = 0.03;
        public double BA2StarProb { get; set; } = 0.185;
        public string BAUp3Star { get; set; } = "";
        public string BAUp2Star { get; set; } = "";
        public string BAMainUp3Stars { get; set; } = "";
        public string BASubUp3Stars { get; set; } = "";
        public int BA2StarPity { get; set; } = 10;

        public double BAUp3StarProb { get; set; } = 0.007;
        public double BASubUp3StarProb { get; set; } = 0.003;
        public double BAUp2StarProb { get; set; } = 0.03;

        public bool EnableHighlightColorsBA { get; set; } = false;

        // ================================================================
        // ini 读写
        // ================================================================
        public static GachaSettings LoadSettingsFromFile(string path)
        {
            var settings = new GachaSettings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return settings;

            try
            {
                settings.Enabled = IniHelper.ReadValue("Gacha", "Enabled", "0", path) == "1";
                settings.PresetName = IniHelper.ReadValue("Gacha", "PresetName", "崩坏：星穹铁道", path);
                settings.WarpType = IniHelper.ReadValue("Gacha", "WarpType", "角色活动跃迁", path);
                settings.FiveStarCategory = IniHelper.ReadValue("Gacha", "FiveStarCategory", "", path);
                settings.FiveStarUpObject = IniHelper.ReadValue("Gacha", "FiveStarUpObject", "", path);
                settings.FourStarCategory = IniHelper.ReadValue("Gacha", "FourStarCategory", "", path);
                settings.FourStarUpObjects = IniHelper.ReadValue("Gacha", "FourStarUpObjects", "", path);
                settings.FiveStarNonUpProb = ParseDouble(IniHelper.ReadValue("Gacha", "FiveStarNonUpProb", "0.300", path), 0.300);
                settings.FiveStarUpProb = ParseDouble(IniHelper.ReadValue("Gacha", "FiveStarUpProb", "0.300", path), 0.300);
                settings.FourStarUpProb = ParseDouble(IniHelper.ReadValue("Gacha", "FourStarUpProb", "2.550", path), 2.550);
                settings.FourStarNonUpProb = ParseDouble(IniHelper.ReadValue("Gacha", "FourStarNonUpProb", "2.550", path), 2.550);
                // ★ 3 星概率不是独立配置：它恒等于 100 - 5星(UP/非UP) - 4星(UP/非UP)。
                //   ini 里仍写这个键（兼容旧版本、方便人看），但一律以推导值为准，
                //   避免「改了 3 星概率却没生效」这种误导。
                double derivedThreeStar = 100.0
                    - settings.FiveStarNonUpProb - settings.FiveStarUpProb
                    - settings.FourStarUpProb - settings.FourStarNonUpProb;
                settings.ThreeStarProb = derivedThreeStar < 0 ? 0 : derivedThreeStar;

                settings.SoftPityStart = ParseInt(IniHelper.ReadValue("Gacha", "SoftPityStart", "74", path), 74);
                settings.SoftPityIncrease = ParseDouble(IniHelper.ReadValue("Gacha", "SoftPityIncrease", "0.06", path), 0.06);
                settings.FourStarPity = ParseInt(IniHelper.ReadValue("Gacha", "FourStarPity", "10", path), 10);
                settings.FiveStarPity = ParseInt(IniHelper.ReadValue("Gacha", "FiveStarPity", "90", path), 90);
                settings.EnableStarColor = IniHelper.ReadValue("Gacha", "EnableStarColor", "0", path) == "1";
                settings.EnableHighlightColors = IniHelper.ReadValue("Gacha", "EnableHighlightColors", "0", path) == "1";

                settings.BAPoolType = IniHelper.ReadValue("GachaBA", "PoolType", "常驻招募", path);
                settings.BA3StarCategory = IniHelper.ReadValue("GachaBA", "ThreeStarCategory", "", path);
                settings.BA2StarCategory = IniHelper.ReadValue("GachaBA", "TwoStarCategory", "", path);
                settings.BA3StarProb = ParseDouble(IniHelper.ReadValue("GachaBA", "ThreeStarProb", "0.03", path), 0.03);
                settings.BA2StarProb = ParseDouble(IniHelper.ReadValue("GachaBA", "TwoStarProb", "0.185", path), 0.185);
                settings.BAUp3Star = IniHelper.ReadValue("GachaBA", "Up3Star", "", path);
                settings.BAUp2Star = IniHelper.ReadValue("GachaBA", "Up2Star", "", path);
                settings.BAMainUp3Stars = IniHelper.ReadValue("GachaBA", "MainUp3Stars", "", path);
                settings.BASubUp3Stars = IniHelper.ReadValue("GachaBA", "SubUp3Stars", "", path);
                settings.BA2StarPity = ParseInt(IniHelper.ReadValue("GachaBA", "TwoStarPity", "10", path), 10);

                settings.BAUp3StarProb = ParseDouble(IniHelper.ReadValue("GachaBA", "Up3StarProb", "0.007", path), 0.007);
                settings.BASubUp3StarProb = ParseDouble(IniHelper.ReadValue("GachaBA", "SubUp3StarProb", "0.003", path), 0.003);
                settings.BAUp2StarProb = ParseDouble(IniHelper.ReadValue("GachaBA", "Up2StarProb", "0.03", path), 0.03);

                settings.EnableHighlightColorsBA = IniHelper.ReadValue("GachaBA", "EnableHighlightColors", "0", path) == "1";

                if (settings.BA3StarProb > 1.0) settings.BA3StarProb /= 100.0;
                if (settings.BA2StarProb > 1.0) settings.BA2StarProb /= 100.0;

                if (ContainsGarbledText(settings.PresetName) ||
                    ContainsGarbledText(settings.FiveStarCategory) ||
                    ContainsGarbledText(settings.FourStarCategory) ||
                    ContainsGarbledText(settings.BA3StarCategory) ||
                    ContainsGarbledText(settings.BA2StarCategory))
                {
                    settings.Enabled = false;
                }
            }
            catch { }
            return settings;
        }

        public static void SaveSettingsToFile(GachaSettings settings, string path)
        {
            if (settings == null || string.IsNullOrEmpty(path)) return;
            try
            {
                IniHelper.WriteValue("Gacha", "Enabled", settings.Enabled ? "1" : "0", path);
                IniHelper.WriteValue("Gacha", "PresetName", settings.PresetName ?? "", path);
                IniHelper.WriteValue("Gacha", "WarpType", settings.WarpType ?? "角色活动跃迁", path);
                IniHelper.WriteValue("Gacha", "FiveStarCategory", settings.FiveStarCategory ?? "", path);
                IniHelper.WriteValue("Gacha", "FiveStarUpObject", settings.FiveStarUpObject ?? "", path);
                IniHelper.WriteValue("Gacha", "FourStarCategory", settings.FourStarCategory ?? "", path);
                IniHelper.WriteValue("Gacha", "FourStarUpObjects", settings.FourStarUpObjects ?? "", path);
                IniHelper.WriteValue("Gacha", "FiveStarNonUpProb", Fmt(settings.FiveStarNonUpProb), path);
                IniHelper.WriteValue("Gacha", "FiveStarUpProb", Fmt(settings.FiveStarUpProb), path);
                IniHelper.WriteValue("Gacha", "FourStarUpProb", Fmt(settings.FourStarUpProb), path);
                IniHelper.WriteValue("Gacha", "FourStarNonUpProb", Fmt(settings.FourStarNonUpProb), path);
                IniHelper.WriteValue("Gacha", "ThreeStarProb", Fmt(settings.ThreeStarProb), path);
                IniHelper.WriteValue("Gacha", "SoftPityStart", settings.SoftPityStart.ToString(), path);
                IniHelper.WriteValue("Gacha", "SoftPityIncrease", Fmt(settings.SoftPityIncrease), path);
                IniHelper.WriteValue("Gacha", "FourStarPity", settings.FourStarPity.ToString(), path);
                IniHelper.WriteValue("Gacha", "FiveStarPity", settings.FiveStarPity.ToString(), path);
                IniHelper.WriteValue("Gacha", "EnableStarColor", settings.EnableStarColor ? "1" : "0", path);
                IniHelper.WriteValue("Gacha", "EnableHighlightColors", settings.EnableHighlightColors ? "1" : "0", path);

                IniHelper.WriteValue("GachaBA", "PoolType", settings.BAPoolType ?? "常驻招募", path);
                IniHelper.WriteValue("GachaBA", "ThreeStarCategory", settings.BA3StarCategory ?? "", path);
                IniHelper.WriteValue("GachaBA", "TwoStarCategory", settings.BA2StarCategory ?? "", path);
                IniHelper.WriteValue("GachaBA", "ThreeStarProb", Fmt(settings.BA3StarProb), path);
                IniHelper.WriteValue("GachaBA", "TwoStarProb", Fmt(settings.BA2StarProb), path);
                IniHelper.WriteValue("GachaBA", "Up3Star", settings.BAUp3Star ?? "", path);
                IniHelper.WriteValue("GachaBA", "Up2Star", settings.BAUp2Star ?? "", path);
                IniHelper.WriteValue("GachaBA", "MainUp3Stars", settings.BAMainUp3Stars ?? "", path);
                IniHelper.WriteValue("GachaBA", "SubUp3Stars", settings.BASubUp3Stars ?? "", path);
                IniHelper.WriteValue("GachaBA", "TwoStarPity", settings.BA2StarPity.ToString(), path);
                IniHelper.WriteValue("GachaBA", "Up3StarProb", Fmt(settings.BAUp3StarProb), path);
                IniHelper.WriteValue("GachaBA", "SubUp3StarProb", Fmt(settings.BASubUp3StarProb), path);
                IniHelper.WriteValue("GachaBA", "Up2StarProb", Fmt(settings.BAUp2StarProb), path);
                IniHelper.WriteValue("GachaBA", "EnableHighlightColors", settings.EnableHighlightColorsBA ? "1" : "0", path);
            }
            catch { }
        }

        // ================================================================
        // 数值解析 / 格式化：一律按不变文化处理，
        // 避免「小数点用逗号」的系统区域把 0.300 解析成 0（概率静默归零）。
        // ================================================================

        private static double ParseDouble(string text, double fallback)
        {
            string raw = (text ?? "").Trim();
            if (raw.Length == 0) return fallback;

            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out double invariantValue))
                return invariantValue;

            // 兼容历史版本在「逗号小数点」区域写出的 0,3 这类内容
            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.CurrentCulture, out double currentValue))
                return currentValue;

            return fallback;
        }

        private static int ParseInt(string text, int fallback)
        {
            string raw = (text ?? "").Trim();
            if (raw.Length == 0) return fallback;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int invariantValue))
                return invariantValue;

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture, out int currentValue))
                return currentValue;

            return fallback;
        }

        /// <summary>写 ini 用：始终用「点」作小数点，最多 6 位小数。</summary>
        private static string Fmt(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);

        private static bool ContainsGarbledText(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c == '?' || c == '\uFFFD') return true;
            }
            return false;
        }
    }
}