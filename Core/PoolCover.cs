using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池封面的定位与保存。
    ///
    /// 新格式：封面保留用户原来的文件名（如 封面.png / my-cover.mp4），
    ///         在卡池目录下用 cover.txt 记录该文件名。
    /// 旧格式：cover.*（任意扩展名），仍然兼容读取。
    /// </summary>
    public static class PoolCover
    {
        public const string MetaFileName = "cover.txt";

        /// <summary>取得卡池的所有封面候选文件（新格式优先，其次旧格式 cover.*）。</summary>
        public static string[] GetCoverFiles(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
                    return Array.Empty<string>();

                var result = new List<string>();

                // 0) 有批注时优先用合成图显示（原封面文件不动）
                string preview = Path.Combine(poolDir, PreviewFileName);
                if (File.Exists(preview)) result.Add(preview);

                // 1) cover.txt 里记录的文件名
                string meta = Path.Combine(poolDir, MetaFileName);
                if (File.Exists(meta))
                {
                    try
                    {
                        string name = File.ReadAllText(meta, Encoding.UTF8).Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            string path = Path.Combine(poolDir, name);
                            if (File.Exists(path)) result.Add(path);
                        }
                    }
                    catch { }
                }

                // 2) 旧格式 cover.*
                foreach (var file in Directory.GetFiles(poolDir, "cover.*"))
                {
                    // 覆盖层 / 背景标记不是封面
                    string fn = Path.GetFileName(file);
                    if (string.Equals(fn, OverlayFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(fn, PreviewFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    // 编辑封面时临时写出的批注层（cover-overlay-<时间>.png）不是封面
                    if (fn.StartsWith("cover-overlay-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!result.Any(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)))
                        result.Add(file);
                }

                return result.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        public static string GetCoverPath(string poolDir) => GetCoverFiles(poolDir).FirstOrDefault();

        /// <summary>原始封面文件（不含批注合成图），编辑时用。</summary>
        public static string GetOriginalCoverPath(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return "";

                string meta = Path.Combine(poolDir, MetaFileName);
                if (File.Exists(meta))
                {
                    string name = File.ReadAllText(meta, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        string path = Path.Combine(poolDir, name);
                        if (File.Exists(path)) return path;
                    }
                }

                foreach (var file in Directory.GetFiles(poolDir, "cover.*"))
                {
                    string fn = Path.GetFileName(file);
                    if (string.Equals(fn, OverlayFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(fn, PreviewFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    // 编辑封面时临时写出的批注层（cover-overlay-<时间>.png）不是封面
                    if (fn.StartsWith("cover-overlay-", StringComparison.OrdinalIgnoreCase)) continue;
                    return file;
                }
            }
            catch { }
            return "";
        }

        /// <summary>用原封面 + 批注覆盖层重新生成显示用合成图；没有覆盖层时删除合成图。</summary>
        public static void RebuildPreview(string poolDir)
        {
            try
            {
                string cover = GetOriginalCoverPath(poolDir);
                string preview = Path.Combine(poolDir, PreviewFileName);
                string overlay = GetOverlayPath(poolDir);

                if (string.IsNullOrEmpty(cover) || string.IsNullOrEmpty(overlay))
                {
                    if (File.Exists(preview)) File.Delete(preview);
                    return;
                }

                var merged = LoadCompositeFrom(cover, overlay);
                if (merged == null)
                {
                    // 视频等无法合成为静态图的封面：清掉旧合成图，避免页面显示过期画面
                    try { if (File.Exists(preview)) File.Delete(preview); } catch { }
                    return;
                }

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(merged));
                using (var fs = File.Create(preview)) encoder.Save(fs);
            }
            catch { }
        }

        private static System.Windows.Media.Imaging.BitmapSource LoadCompositeFrom(string cover, string overlay)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapImage Load(string file)
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }

                var a = Load(cover);
                var b = Load(overlay);
                int w = a.PixelWidth, h = a.PixelHeight;

                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(a, new System.Windows.Rect(0, 0, w, h));
                    dc.DrawImage(b, new System.Windows.Rect(0, 0, w, h));
                }

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }


        public static bool HasCover(string poolDir) => GetCoverFiles(poolDir).Length > 0;

        /// <summary>
        /// 把 sourcePath 设为卡池封面：保留原文件名复制进卡池目录，并写入 cover.txt。
        /// 旧的封面文件（含旧格式 cover.*）会被删除。
        /// </summary>
        public static bool SaveCover(string poolDir, string sourcePath)
        {
            if (string.IsNullOrEmpty(poolDir) || string.IsNullOrEmpty(sourcePath)) return false;
            if (!File.Exists(sourcePath)) return false;

            try
            {
                Directory.CreateDirectory(poolDir);

                string name = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(name)) return false;

                string dest = Path.Combine(poolDir, name);

                // 删除旧封面（排除本次要保留的源文件 / 目标文件）
                foreach (var old in GetCoverFiles(poolDir))
                {
                    if (string.Equals(old, dest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(old, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(old); } catch { }
                }

                bool sameFile = string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase);
                if (!sameFile) File.Copy(sourcePath, dest, true);
                // 3.77.0-snapshot-13：File.Copy 会沿用源文件的修改时间，导致缩略图缓存误判「没变」，
                // 这里显式刷新时间戳，保证 EnsureAutoThumb 能感知到封面被换过。
                try { File.SetLastWriteTimeUtc(dest, DateTime.UtcNow); } catch { }

                File.WriteAllText(Path.Combine(poolDir, MetaFileName), name, new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 读取「封面原图 + 批注覆盖层」合成后的位图（用于列表 / 预览显示）。
        /// 没有覆盖层时直接返回封面本身。
        /// </summary>
        public static System.Windows.Media.Imaging.BitmapSource LoadComposite(string poolDir, int decodeWidth = 0)
        {
            try
            {
                string cover = GetCoverPath(poolDir);
                if (string.IsNullOrEmpty(cover) || !File.Exists(cover)) return null;

                string overlay = GetOverlayPath(poolDir);

                System.Windows.Media.Imaging.BitmapImage Load(string file)
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                    bmp.UriSource = new Uri(file, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }

                var baseBmp = Load(cover);
                if (string.IsNullOrEmpty(overlay)) return baseBmp;

                var overlayBmp = Load(overlay);

                int w = baseBmp.PixelWidth, h = baseBmp.PixelHeight;
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(baseBmp, new System.Windows.Rect(0, 0, w, h));
                    dc.DrawImage(overlayBmp, new System.Windows.Rect(0, 0, w, h));
                }

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }
        /// <summary>批注覆盖层（透明 PNG，叠在封面原图上显示）。</summary>
        public const string OverlayFileName = "cover_overlay.png";

        /// <summary>「模糊并应用到主界面背景」标记文件。</summary>
        public const string BlurBackgroundFileName = "cover_bg_blur.txt";

        /// <summary>显示用合成图（原封面 + 批注），原封面文件本身不修改。</summary>
        public const string PreviewFileName = "cover_preview.png";

        public static string GetOverlayPath(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return "";
                string path = Path.Combine(poolDir, OverlayFileName);
                return File.Exists(path) ? path : "";
            }
            catch { return ""; }
        }

        public static bool HasOverlay(string poolDir) => !string.IsNullOrEmpty(GetOverlayPath(poolDir));

        /// <summary>把编辑好的透明 PNG 存成该卡池的覆盖层。</summary>
        public static bool SaveOverlay(string poolDir, string pngPath)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return false;

                string dest = Path.Combine(poolDir, OverlayFileName);
                if (string.IsNullOrEmpty(pngPath) || !File.Exists(pngPath))
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    CleanupTempOverlays();
                    return true;   // 没有批注 = 清掉旧覆盖层
                }

                Directory.CreateDirectory(poolDir);
                if (!string.Equals(Path.GetFullPath(pngPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(pngPath, dest, true);
                try { File.SetLastWriteTimeUtc(dest, DateTime.UtcNow); } catch { }
                RebuildPreview(poolDir);
                CleanupTempOverlays();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 3.75：封面编辑时生成的临时批注层（cover-overlay-*.png）在保存后就没用了，
        /// 每次保存封面顺手清掉，避免 %APPDATA%\Random Select\cover-edits 越积越多。
        /// 只清理这两个临时目录里以 cover-overlay- 开头的 PNG；卡池目录里的正式文件不碰。
        /// 返回实际删掉的文件数（给「清理缓存」显示用）。
        /// </summary>
        public static int CleanupTempOverlays()
        {
            int removed = 0;
            var dirs = new List<string>();
            try { dirs.Add(Path.Combine(App.AppDataDir, "cover-edits")); } catch { }
            try { dirs.Add(Path.Combine(Path.GetTempPath(), "RadomSelectCover")); } catch { }

            foreach (string dir in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (string file in Directory.EnumerateFiles(dir, "cover-overlay-*.png"))
                    {
                        try { File.Delete(file); removed++; } catch { }
                    }
                }
                catch { }
            }
            return removed;
        }

        /// <summary>「圆角封面」标记文件。</summary>
        public const string RoundCoverFileName = "cover_round.txt";

        public static bool IsRoundCover(string poolDir) => GetCoverRadius(poolDir) > 0.5;

        /// <summary>3.77.0-snapshot-6：封面圆角半径（0 = 方角）。旧文件里是 "1"/"true"，按 9 处理。</summary>
        public static double GetCoverRadius(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return 0;
                string path = Path.Combine(poolDir, RoundCoverFileName);
                if (!File.Exists(path)) return 0;
                string text = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase)) return 9;
                if (text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Length == 0) return 0;
                if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                    return Math.Max(0, Math.Min(64, v));
                return 0;
            }
            catch { return 0; }
        }

        public static void SetCoverRadius(string poolDir, double radius)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return;
                string path = Path.Combine(poolDir, RoundCoverFileName);
                if (radius > 0.5)
                    File.WriteAllText(path, radius.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                        new UTF8Encoding(false));
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static void SetRoundCover(string poolDir, bool on) => SetCoverRadius(poolDir, on ? 12 : 0);

        /// <summary>
        /// 3.38：「编辑默认封面」标记 —— 用户点过之后，这个卡池再次打开封面编辑页
        /// 直接进入正常编辑模式（以内置默认封面为底图），不再显示「未添加封面」的空状态。
        /// </summary>
        public const string DefaultCoverEditFileName = "cover_default_edit.txt";

        public static bool HasDefaultCoverEdit(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return false;
                string path = Path.Combine(poolDir, DefaultCoverEditFileName);
                return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim() == "1";
            }
            catch { return false; }
        }

        public static void SetDefaultCoverEdit(string poolDir, bool on)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return;
                string path = Path.Combine(poolDir, DefaultCoverEditFileName);
                if (on) File.WriteAllText(path, "1", new UTF8Encoding(false));
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
        public static bool IsBlurBackground(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return false;
                string path = Path.Combine(poolDir, BlurBackgroundFileName);
                return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim() == "1";
            }
            catch { return false; }
        }

        public static void SetBlurBackground(string poolDir, bool on)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return;
                string path = Path.Combine(poolDir, BlurBackgroundFileName);
                if (on) File.WriteAllText(path, "1", new UTF8Encoding(false));
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
        // ================================================================
        // 3.77：卡池卡片缩略图
        //
        // 缩略图有三种来源：
        //   Default —— 不使用缩略图，主界面卡片保持原来的「纯文字」样式；
        //   Cover   —— 使用封面（图片 / GIF 第一帧 / 视频首帧，含批注）自动生成缩略图，
        //              缓存成 cover_thumb_auto.png，封面变化后按修改时间自动重建；
        //   Custom  —— 用户导入的图片，保存为 cover_thumb.png。
        //
        // 另外用 cover_thumb.txt 记录：模式、是否显示卡池名、卡池名文字颜色。
        // 缩略图一律不裁剪，只按最长边等比缩小。
        // ================================================================

        public const string ThumbMetaFileName = "cover_thumb.txt";
        public const string ThumbFileName = "cover_thumb.png";
        public const string ThumbAutoFileName = "cover_thumb_auto.png";

        /// <summary>缩略图的最长边上限（自定义导入）。</summary>
        private const int CustomThumbMaxSide = 1280;

        /// <summary>自动缩略图的最长边上限（主页卡片背景够用即可）。</summary>
        private const int AutoThumbMaxSide = 640;

        public enum ThumbMode
        {
            Default = 0,
            Cover = 1,
            Custom = 2
        }

        private static Dictionary<string, string> ReadThumbMeta(string poolDir)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return dict;
                string path = Path.Combine(poolDir, ThumbMetaFileName);
                if (!File.Exists(path)) return dict;

                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key.Length > 0) dict[key] = value;
                }
            }
            catch { }
            return dict;
        }

        private static void WriteThumbMeta(string poolDir, Dictionary<string, string> dict)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return;
                var sb = new StringBuilder();
                foreach (var kv in dict)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                File.WriteAllText(Path.Combine(poolDir, ThumbMetaFileName), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static ThumbMode GetThumbMode(string poolDir)
        {
            var meta = ReadThumbMeta(poolDir);
            if (meta.TryGetValue("mode", out string? raw))
            {
                if (string.Equals(raw, "cover", StringComparison.OrdinalIgnoreCase)) return ThumbMode.Cover;
                if (string.Equals(raw, "custom", StringComparison.OrdinalIgnoreCase)) return ThumbMode.Custom;
            }
            return ThumbMode.Default;
        }

        public static void SetThumbMode(string poolDir, ThumbMode mode)
        {
            var meta = ReadThumbMeta(poolDir);
            meta["mode"] = mode == ThumbMode.Cover ? "cover" : mode == ThumbMode.Custom ? "custom" : "default";
            WriteThumbMeta(poolDir, meta);
        }

        /// <summary>卡片缩略图是否圆角（整个卡片：边框与图片一起）。默认方角，与旧版列表一致。</summary>
        public static bool GetThumbRound(string poolDir)
        {
            var meta = ReadThumbMeta(poolDir);
            if (meta.TryGetValue("round", out string? raw))
                return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        public static void SetThumbRound(string poolDir, bool round)
        {
            var meta = ReadThumbMeta(poolDir);
            meta["round"] = round ? "true" : "false";
            // 3.77.0-snapshot-6：半径与「是否圆角」保持一致，避免两处口径打架
            meta["radius"] = round ? "9" : "0";
            WriteThumbMeta(poolDir, meta);
        }

        /// <summary>3.77.0-snapshot-6：主页卡片的圆角半径（0 = 方角，默认 9）。</summary>
        public static double GetThumbRadius(string poolDir)
        {
            var meta = ReadThumbMeta(poolDir);
            if (meta.TryGetValue("radius", out string? raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                return Math.Max(0, Math.Min(64, v));
            if (meta.TryGetValue("round", out string? roundRaw)
                && (string.Equals(roundRaw, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(roundRaw, "1", StringComparison.OrdinalIgnoreCase)))
                return 9;
            return 0;
        }

        public static void SetThumbRadius(string poolDir, double radius)
        {
            var meta = ReadThumbMeta(poolDir);
            meta["radius"] = Math.Max(0, Math.Min(64, radius))
                .ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            meta["round"] = radius > 0.5 ? "true" : "false";
            WriteThumbMeta(poolDir, meta);
        }

        /// <summary>卡片上是否叠上卡池名（默认显示；「默认」样式不受这个开关影响）。</summary>
        public static bool GetThumbShowText(string poolDir)
        {
            var meta = ReadThumbMeta(poolDir);
            if (meta.TryGetValue("showText", out string? raw))
                return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        public static void SetThumbShowText(string poolDir, bool show)
        {
            var meta = ReadThumbMeta(poolDir);
            meta["showText"] = show ? "true" : "false";
            WriteThumbMeta(poolDir, meta);
        }

        /// <summary>卡池名文字颜色；null 表示跟随主题（默认文字颜色）。</summary>
        public static System.Windows.Media.Color? GetThumbTextColor(string poolDir)
        {
            var meta = ReadThumbMeta(poolDir);
            if (!meta.TryGetValue("textColor", out string? raw) || string.IsNullOrWhiteSpace(raw)) return null;
            return ParseColor(raw);
        }

        public static void SetThumbTextColor(string poolDir, System.Windows.Media.Color? color)
        {
            var meta = ReadThumbMeta(poolDir);
            if (color.HasValue) meta["textColor"] = ToColorString(color.Value);
            else meta.Remove("textColor");
            WriteThumbMeta(poolDir, meta);
        }

        private static System.Windows.Media.Color? ParseColor(string raw)
        {
            try
            {
                string s = raw.Trim().TrimStart('#');
                if (s.Length == 6) s = "FF" + s;
                if (s.Length != 8) return null;
                uint v = Convert.ToUInt32(s, 16);
                return System.Windows.Media.Color.FromArgb(
                    (byte)((v >> 24) & 0xFF),
                    (byte)((v >> 16) & 0xFF),
                    (byte)((v >> 8) & 0xFF),
                    (byte)(v & 0xFF));
            }
            catch { return null; }
        }

        private static string ToColorString(System.Windows.Media.Color c) =>
            $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>用户导入的自定义缩略图路径（不存在返回空）。</summary>
        public static string GetCustomThumbPath(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return "";
                string path = Path.Combine(poolDir, ThumbFileName);
                return File.Exists(path) ? path : "";
            }
            catch { return ""; }
        }

        /// <summary>把用户选的图片存成自定义缩略图（不裁剪，只按最长边等比缩小）。</summary>
        public static bool SaveCustomThumb(string poolDir, string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || string.IsNullOrEmpty(sourcePath)) return false;
                if (!File.Exists(sourcePath)) return false;

                var bmp = LoadBitmapForThumb(sourcePath);
                if (bmp == null) return false;

                Directory.CreateDirectory(poolDir);
                SavePng(ScaleToMaxSide(bmp, CustomThumbMaxSide), Path.Combine(poolDir, ThumbFileName));
                return true;
            }
            catch { return false; }
        }

        public static void RemoveCustomThumb(string poolDir)
        {
            try
            {
                string path = Path.Combine(poolDir, ThumbFileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static string GetThumbAutoPath(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return "";
                string path = Path.Combine(poolDir, ThumbAutoFileName);
                return File.Exists(path) ? path : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 保证「使用原封面缩略图」的缓存存在且与新封面一致。
        /// 返回缓存路径；没有封面 / 无法取帧时返回空串。
        /// </summary>
        public static string EnsureAutoThumb(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return "";

                string cover = GetOriginalCoverPath(poolDir);
                if (string.IsNullOrEmpty(cover) || !File.Exists(cover)) cover = GetCoverPath(poolDir);
                if (string.IsNullOrEmpty(cover) || !File.Exists(cover)) return "";

                string preview = Path.Combine(poolDir, PreviewFileName);
                string overlay = GetOverlayPath(poolDir);
                string auto = Path.Combine(poolDir, ThumbAutoFileName);

                if (File.Exists(auto))
                {
                    DateTime autoTime = File.GetLastWriteTimeUtc(auto);
                    DateTime latest = File.GetLastWriteTimeUtc(cover);
                    if (File.Exists(preview)) latest = Max(latest, File.GetLastWriteTimeUtc(preview));
                    if (!string.IsNullOrEmpty(overlay) && File.Exists(overlay))
                        latest = Max(latest, File.GetLastWriteTimeUtc(overlay));
                    // 3.77.0-snapshot-13：缩略图设置（模式 / 圆角 / 文字颜色）变了也要重建，
                    // 否则「切成使用原封面缩略图」后主页可能一直显示上一次的结果。
                    string meta = Path.Combine(poolDir, ThumbMetaFileName);
                    if (File.Exists(meta)) latest = Max(latest, File.GetLastWriteTimeUtc(meta));
                    if (autoTime >= latest) return auto;
                }

                System.Windows.Media.Imaging.BitmapSource? baseBmp;
                if (File.Exists(preview))
                {
                    baseBmp = LoadBitmapForThumb(preview);
                }
                else
                {
                    baseBmp = LoadBitmapForThumb(cover);
                    if (baseBmp != null && !string.IsNullOrEmpty(overlay) && File.Exists(overlay))
                    {
                        var ov = LoadBitmapForThumb(overlay);
                        if (ov != null) baseBmp = Compose(baseBmp, ov);
                    }
                }

                if (baseBmp == null) return "";

                SavePng(ScaleToMaxSide(baseBmp, AutoThumbMaxSide), auto);
                return File.Exists(auto) ? auto : "";
            }
            catch { return ""; }
        }

        /// <summary>删掉自动缩略图缓存（封面改动后调用，下次按需重建）。</summary>
        public static void InvalidateAutoThumb(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return;
                string auto = Path.Combine(poolDir, ThumbAutoFileName);
                if (File.Exists(auto)) File.Delete(auto);
            }
            catch { }
        }

        /// <summary>主界面卡片该用哪张图：Default 返回空；其余返回可用的图片路径。</summary>
        public static string GetThumbDisplayPath(string poolDir)
        {
            switch (GetThumbMode(poolDir))
            {
                case ThumbMode.Custom:
                    {
                        string custom = GetCustomThumbPath(poolDir);
                        return custom;
                    }
                case ThumbMode.Cover:
                    return EnsureAutoThumb(poolDir);
                default:
                    return "";
            }
        }

        private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

        /// <summary>3.77.0-snapshot-10：取封面「第一帧」原图（不含批注层）。
        /// 视频取第 1 帧、GIF 取第 1 帧、图片直接读原图 —— 编辑页拼缩略图用。</summary>
        public static System.Windows.Media.Imaging.BitmapSource? LoadCoverFirstFrame(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return LoadBitmapForThumb(path);
            }
            catch { return null; }
        }

        private static System.Windows.Media.Imaging.BitmapSource? LoadBitmapForThumb(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".gif")
                {
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                        new Uri(path, UriKind.Absolute),
                        System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
                    if (frame != null) frame.Freeze();
                    return frame;
                }

                if (IsVideoExtension(ext)) return GrabVideoFrame(path);

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static bool IsVideoExtension(string ext)
        {
            switch (ext)
            {
                case ".mp4":
                case ".avi":
                case ".mkv":
                case ".mov":
                case ".wmv":
                case ".m4v":
                case ".webm":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>用 MediaPlayer 抓视频的第一帧（在 UI 线程调用；超时返回 null）。</summary>
        private static System.Windows.Media.Imaging.BitmapSource? GrabVideoFrame(string path)
        {
            System.Windows.Media.MediaPlayer? player = null;
            try
            {
                player = new System.Windows.Media.MediaPlayer
                {
                    ScrubbingEnabled = true,
                    Volume = 0,
                    IsMuted = true
                };

                bool opened = false, failed = false;
                player.MediaOpened += (_, __) => { try { opened = true; } catch { } };
                player.MediaFailed += (_, __) => { try { failed = true; } catch { } };
                player.Open(new Uri(path, UriKind.Absolute));

                // 3.77.0-snapshot-7：MediaOpened/MediaFailed 由 WPF 媒体管线在 UI 线程上抛出，
                // 这里如果直接 Thread.Sleep / 事件等待会阻塞 UI 线程，事件永远投递不到 → 必然超时。
                // 所以用 DispatcherFrame 一边泵消息一边等，事件才能到达。
                if (!PumpUntil(() => opened || failed, 4000))
                {
                    RuntimeLog.Info("封面缩略图：视频打开超时 " + Path.GetFileName(path));
                    return null;
                }
                if (failed || player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
                {
                    RuntimeLog.Info("封面缩略图：视频无法解码 " + Path.GetFileName(path));
                    return null;
                }

                // 3.77.0-snapshot-7：明确取「第一帧」——
                // 先把播放位置归零，再短暂播放让解码器真正解出第 0 帧（只 Pause 在还没开始播时拿到的是空帧），
                // 之后再回到 0 并暂停，最后截图。
                try { player.Position = TimeSpan.Zero; } catch { }
                player.Play();
                PumpFor(260);
                player.Pause();
                try { player.Position = TimeSpan.Zero; } catch { }
                PumpFor(200);

                int w = player.NaturalVideoWidth;
                int h = player.NaturalVideoHeight;

                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, w, h));
                    dc.DrawVideo(player, new System.Windows.Rect(0, 0, w, h));
                }

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
            finally
            {
                try { player?.Close(); } catch { }
            }
        }

        /// <summary>边泵 UI 消息边等待条件成立（不能直接阻塞 UI 线程，否则媒体事件收不到）。</summary>
        private static bool PumpUntil(Func<bool> condition, int timeoutMs)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
                    PumpFor(20);
                return condition();
            }
            catch { return false; }
        }

        /// <summary>泵 UI 消息指定时长。</summary>
        private static void PumpFor(int milliseconds)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < milliseconds)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => { try { frame.Continue = false; } catch { } }));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    System.Threading.Thread.Sleep(5);
                }
            }
            catch { }
        }

        private static System.Windows.Media.Imaging.BitmapSource ScaleToMaxSide(
            System.Windows.Media.Imaging.BitmapSource src, int maxSide)
        {
            try
            {
                int w = src.PixelWidth, h = src.PixelHeight;
                if (w <= 0 || h <= 0) return src;
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
                if (scale >= 0.999) return src;
                var tb = new System.Windows.Media.Imaging.TransformedBitmap(
                    src, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                return tb;
            }
            catch { return src; }
        }

        private static System.Windows.Media.Imaging.BitmapSource Compose(
            System.Windows.Media.Imaging.BitmapSource baseBmp,
            System.Windows.Media.Imaging.BitmapSource overlay)
        {
            int w = baseBmp.PixelWidth, h = baseBmp.PixelHeight;
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(baseBmp, new System.Windows.Rect(0, 0, w, h));
                dc.DrawImage(overlay, new System.Windows.Rect(0, 0, w, h));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private static void SavePng(System.Windows.Media.Imaging.BitmapSource bmp, string path)
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }

        /// <summary>删除卡池封面（封面文件与 cover.txt）。</summary>
        public static void RemoveCover(string poolDir)
        {
            foreach (var file in GetCoverFiles(poolDir))
            {
                try { File.Delete(file); } catch { }
            }

            try
            {
                foreach (var extra in new[] { OverlayFileName, PreviewFileName, BlurBackgroundFileName, RoundCoverFileName, DefaultCoverEditFileName, ThumbFileName, ThumbAutoFileName, ThumbMetaFileName })
                {
                    string path = Path.Combine(poolDir, extra);
                    if (File.Exists(path)) File.Delete(path);
                }
                string meta = Path.Combine(poolDir, MetaFileName);
                if (File.Exists(meta)) File.Delete(meta);
            }
            catch { }
        }
    }
}
