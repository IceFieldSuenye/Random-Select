using System;
using System.Windows;
using System.Windows.Media;

namespace RandomDrawer
{
    public static class ColorUtils
    {
        public static readonly Color GoldColor = Color.FromRgb(0xD4, 0xAF, 0x37);
        public static readonly Color PurpleColor = Color.FromRgb(0x9B, 0x59, 0xB6);
        public static readonly Color BlueColor = Color.FromRgb(0x1E, 0x90, 0xFF);

        public static readonly Brush GoldBrush = Freeze(new SolidColorBrush(GoldColor));
        public static readonly Brush PurpleBrush = Freeze(new SolidColorBrush(PurpleColor));
        public static readonly Brush BlueBrush = Freeze(new SolidColorBrush(BlueColor));

        /// <summary>
        /// ★ 静态粉色画刷（原为动画呼吸画刷）。
        /// 因为原来每帧对 Color 做动画会触发所有引用元素重绘（GPU/CPU 持续占用），
        /// 改为静态画刷后性能开销几乎为零。
        /// </summary>
        public static readonly Brush PinkPurpleBrush =
            Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x6C, 0xE0)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    }

    /// <summary>传给 DrawResultWindow / HistoryWindow 的高亮配置。</summary>
    public class HighlightConfig
    {
        public bool Enabled { get; set; }
        public string PresetName { get; set; }

        // HSR
        public string FiveStarCategory { get; set; } = "";
        public string FourStarCategory { get; set; } = "";

        // BA
        public string BA3StarCategory { get; set; } = "";
        public string BA2StarCategory { get; set; } = "";

        public static HighlightConfig FromSettings(GachaSettings gs)
        {
            if (gs == null || !gs.Enabled) return null;

            bool useHighlight =
                (gs.PresetName == "崩坏：星穹铁道" && gs.EnableHighlightColors) ||
                (gs.PresetName == "蔚蓝档案（旧）" && gs.EnableHighlightColorsBA);

            if (!useHighlight) return null;

            return new HighlightConfig
            {
                Enabled = true,
                PresetName = gs.PresetName,
                FiveStarCategory = gs.FiveStarCategory ?? "",
                FourStarCategory = gs.FourStarCategory ?? "",
                BA3StarCategory = gs.BA3StarCategory ?? "",
                BA2StarCategory = gs.BA2StarCategory ?? ""
            };
        }

        private static bool SameCategory(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public Brush GetBrushByCategory(string category)
        {
            if (!Enabled) return null;
            if (string.IsNullOrWhiteSpace(category)) return null;

            string cat = category.Trim();

            if (PresetName == "崩坏：星穹铁道")
            {
                if (SameCategory(cat, FiveStarCategory)) return ColorUtils.GoldBrush;
                if (SameCategory(cat, FourStarCategory)) return ColorUtils.PurpleBrush;
            }
            else if (PresetName == "蔚蓝档案（旧）")
            {
                if (SameCategory(cat, BA3StarCategory)) return ColorUtils.PinkPurpleBrush;
                if (SameCategory(cat, BA2StarCategory)) return ColorUtils.GoldBrush;

                if (!string.IsNullOrWhiteSpace(BA3StarCategory) &&
                    !string.IsNullOrWhiteSpace(BA2StarCategory) &&
                    !SameCategory(cat, BA3StarCategory) &&
                    !SameCategory(cat, BA2StarCategory))
                {
                    return ColorUtils.BlueBrush;
                }
            }

            return null;
        }
    }
}