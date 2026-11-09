using System;
using System.IO;

namespace RandomDrawer
{
    /// <summary>
    /// 高级抽卡规则的数据模型 + ini 读写。
    /// （原 AdvancedGachaWindow 内的 GachaSettings 迁移至此，
    ///   便于 UserControl 与其它页共享。）
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
                double.TryParse(IniHelper.ReadValue("Gacha", "FiveStarNonUpProb", "0.300", path), out double a); settings.FiveStarNonUpProb = a;
                double.TryParse(IniHelper.ReadValue("Gacha", "FiveStarUpProb", "0.300", path), out double b); settings.FiveStarUpProb = b;
                double.TryParse(IniHelper.ReadValue("Gacha", "FourStarUpProb", "2.550", path), out double c); settings.FourStarUpProb = c;
                double.TryParse(IniHelper.ReadValue("Gacha", "FourStarNonUpProb", "2.550", path), out double d); settings.FourStarNonUpProb = d;
                double.TryParse(IniHelper.ReadValue("Gacha", "ThreeStarProb", "94.300", path), out double e); settings.ThreeStarProb = e;
                int.TryParse(IniHelper.ReadValue("Gacha", "SoftPityStart", "74", path), out int f); settings.SoftPityStart = f;
                double.TryParse(IniHelper.ReadValue("Gacha", "SoftPityIncrease", "0.06", path), out double g); settings.SoftPityIncrease = g;
                int.TryParse(IniHelper.ReadValue("Gacha", "FourStarPity", "10", path), out int h); settings.FourStarPity = h;
                int.TryParse(IniHelper.ReadValue("Gacha", "FiveStarPity", "90", path), out int i); settings.FiveStarPity = i;
                settings.EnableStarColor = IniHelper.ReadValue("Gacha", "EnableStarColor", "0", path) == "1";
                settings.EnableHighlightColors = IniHelper.ReadValue("Gacha", "EnableHighlightColors", "0", path) == "1";

                settings.BAPoolType = IniHelper.ReadValue("GachaBA", "PoolType", "常驻招募", path);
                settings.BA3StarCategory = IniHelper.ReadValue("GachaBA", "ThreeStarCategory", "", path);
                settings.BA2StarCategory = IniHelper.ReadValue("GachaBA", "TwoStarCategory", "", path);
                double.TryParse(IniHelper.ReadValue("GachaBA", "ThreeStarProb", "0.03", path), out double ba3); settings.BA3StarProb = ba3;
                double.TryParse(IniHelper.ReadValue("GachaBA", "TwoStarProb", "0.185", path), out double ba2); settings.BA2StarProb = ba2;
                settings.BAUp3Star = IniHelper.ReadValue("GachaBA", "Up3Star", "", path);
                settings.BAUp2Star = IniHelper.ReadValue("GachaBA", "Up2Star", "", path);
                settings.BAMainUp3Stars = IniHelper.ReadValue("GachaBA", "MainUp3Stars", "", path);
                settings.BASubUp3Stars = IniHelper.ReadValue("GachaBA", "SubUp3Stars", "", path);
                int.TryParse(IniHelper.ReadValue("GachaBA", "TwoStarPity", "10", path), out int bap); settings.BA2StarPity = bap;

                double.TryParse(IniHelper.ReadValue("GachaBA", "Up3StarProb", "0.007", path), out double u3); settings.BAUp3StarProb = u3;
                double.TryParse(IniHelper.ReadValue("GachaBA", "SubUp3StarProb", "0.003", path), out double su3); settings.BASubUp3StarProb = su3;
                double.TryParse(IniHelper.ReadValue("GachaBA", "Up2StarProb", "0.03", path), out double u2); settings.BAUp2StarProb = u2;

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
                IniHelper.WriteValue("Gacha", "FiveStarNonUpProb", settings.FiveStarNonUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "FiveStarUpProb", settings.FiveStarUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "FourStarUpProb", settings.FourStarUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "FourStarNonUpProb", settings.FourStarNonUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "ThreeStarProb", settings.ThreeStarProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "SoftPityStart", settings.SoftPityStart.ToString(), path);
                IniHelper.WriteValue("Gacha", "SoftPityIncrease", settings.SoftPityIncrease.ToString(), path);
                IniHelper.WriteValue("Gacha", "FourStarPity", settings.FourStarPity.ToString(), path);
                IniHelper.WriteValue("Gacha", "FiveStarPity", settings.FiveStarPity.ToString(), path);
                IniHelper.WriteValue("Gacha", "EnableStarColor", settings.EnableStarColor ? "1" : "0", path);
                IniHelper.WriteValue("Gacha", "EnableHighlightColors", settings.EnableHighlightColors ? "1" : "0", path);

                IniHelper.WriteValue("GachaBA", "PoolType", settings.BAPoolType ?? "常驻招募", path);
                IniHelper.WriteValue("GachaBA", "ThreeStarCategory", settings.BA3StarCategory ?? "", path);
                IniHelper.WriteValue("GachaBA", "TwoStarCategory", settings.BA2StarCategory ?? "", path);
                IniHelper.WriteValue("GachaBA", "ThreeStarProb", settings.BA3StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "TwoStarProb", settings.BA2StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "Up3Star", settings.BAUp3Star ?? "", path);
                IniHelper.WriteValue("GachaBA", "Up2Star", settings.BAUp2Star ?? "", path);
                IniHelper.WriteValue("GachaBA", "MainUp3Stars", settings.BAMainUp3Stars ?? "", path);
                IniHelper.WriteValue("GachaBA", "SubUp3Stars", settings.BASubUp3Stars ?? "", path);
                IniHelper.WriteValue("GachaBA", "TwoStarPity", settings.BA2StarPity.ToString(), path);
                IniHelper.WriteValue("GachaBA", "Up3StarProb", settings.BAUp3StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "SubUp3StarProb", settings.BASubUp3StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "Up2StarProb", settings.BAUp2StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "EnableHighlightColors", settings.EnableHighlightColorsBA ? "1" : "0", path);
            }
            catch { }
        }

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