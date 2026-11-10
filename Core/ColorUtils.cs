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

        /// <summary>RGB → HSV（h = 0~360，s / v = 0~1）。颜色选择弹窗与内嵌颜色面板共用。</summary>
        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            v = max;
            s = max <= 0 ? 0 : delta / max;

            if (delta <= 0) { h = 0; return; }

            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);

            if (h < 0) h += 360;
        }

        /// <summary>HSV → RGB（h = 0~360，s / v = 0~1）。</summary>
        public static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            s = Math.Max(0, Math.Min(1, s));
            v = Math.Max(0, Math.Min(1, v));

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb((byte)Math.Round((r + m) * 255),
                                 (byte)Math.Round((g + m) * 255),
                                 (byte)Math.Round((b + m) * 255));
        }
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
                ((gs.PresetName == "蔚蓝档案（旧）" || gs.PresetName == "蔚蓝档案（新）") && gs.EnableHighlightColorsBA);

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
            else if (PresetName == "蔚蓝档案（旧）" || PresetName == "蔚蓝档案（新）")
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