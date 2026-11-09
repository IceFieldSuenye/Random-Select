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
                if (merged == null) return;

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
                    return true;   // 没有批注 = 清掉旧覆盖层
                }

                Directory.CreateDirectory(poolDir);
                if (!string.Equals(Path.GetFullPath(pngPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(pngPath, dest, true);
                RebuildPreview(poolDir);
                return true;
            }
            catch { return false; }
        }

        /// <summary>「圆角封面」标记文件。</summary>
        public const string RoundCoverFileName = "cover_round.txt";

        public static bool IsRoundCover(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return false;
                string path = Path.Combine(poolDir, RoundCoverFileName);
                return File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim() == "1";
            }
            catch { return false; }
        }

        public static void SetRoundCover(string poolDir, bool on)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return;
                string path = Path.Combine(poolDir, RoundCoverFileName);
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
        /// <summary>删除卡池封面（封面文件与 cover.txt）。</summary>
        public static void RemoveCover(string poolDir)
        {
            foreach (var file in GetCoverFiles(poolDir))
            {
                try { File.Delete(file); } catch { }
            }

            try
            {
                foreach (var extra in new[] { OverlayFileName, PreviewFileName, BlurBackgroundFileName, RoundCoverFileName })
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
