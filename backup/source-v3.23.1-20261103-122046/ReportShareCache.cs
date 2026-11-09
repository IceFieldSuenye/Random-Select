using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 抽取报告分享长图缓存：记录卡池 history.txt 的时间戳与上次生成的图片路径。
    /// 下次分享时若历史记录未变化且图片仍存在，直接复用，不再重复生成。
    /// </summary>
    public static class ReportShareCache
    {
        private static string CacheDir
        {
            get
            {
                // 冒烟测试会用 RS_CACHE_DIR 指到项目内的临时目录，避免污染用户真实的分享缓存
                string custom = Environment.GetEnvironmentVariable("RS_CACHE_DIR");
                if (!string.IsNullOrWhiteSpace(custom)) return custom;

                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Random Select", "share_cache");
            }
        }

        private static string FileFor(string poolDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(poolDir)) return "";
                string normalized = Path.GetFullPath(poolDir).ToLowerInvariant();
                using (var md5 = MD5.Create())
                {
                    byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                    var sb = new StringBuilder();
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return Path.Combine(CacheDir, sb + ".cache");
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// 缓存格式标记：生成规则（尺寸、页眉、清晰度）一变就改这个值，
        /// 旧缓存自动作废，避免升级后还复用上一版生成的图片。
        /// 3.10.8：单张恢复原分辨率 + 多张加页眉 → 旧图不再复用。
        /// </summary>
        private const string FormatTag = "v3";   // v3：PDF 页眉改成「第X页，共N页」，旧缓存作废

        /// <summary>读取缓存；图片路径仍存在时才视为有效。</summary>
        public static bool TryGet(string poolDir, out long historyTicks, out string imagePath)
        {
            historyTicks = 0;
            imagePath = "";

            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                if (lines.Length < 3) return false;
                if (!string.Equals(lines[0].Trim(), FormatTag, StringComparison.Ordinal)) return false;
                if (!long.TryParse(lines[1].Trim(), out historyTicks)) return false;

                string path = lines[2].Trim();
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                imagePath = path;
                return true;
            }
            catch { return false; }
        }

        public static void Save(string poolDir, long historyTicks, string imagePath)
        {
            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(imagePath)) return;

                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(file, new[] { FormatTag, historyTicks.ToString(), imagePath }, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>读取「拆分多张」的缓存；所有图片都仍存在时才视为有效。</summary>
        public static bool TryGetMulti(string poolDir, out long historyTicks, out List<string> imagePaths)
        {
            historyTicks = 0;
            imagePaths = new List<string>();

            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                // 格式：格式标记 / ticks / "MULTI" / path1 / path2 / ...
                if (lines.Length < 4) return false;
                if (!string.Equals(lines[0].Trim(), FormatTag, StringComparison.Ordinal)) return false;
                if (!long.TryParse(lines[1].Trim(), out historyTicks)) return false;
                if (!string.Equals(lines[2].Trim(), "MULTI", StringComparison.Ordinal)) return false;

                var paths = new List<string>();
                for (int i = 3; i < lines.Length; i++)
                {
                    string p = lines[i].Trim();
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) return false;
                    paths.Add(p);
                }

                if (paths.Count == 0) return false;
                imagePaths = paths;
                return true;
            }
            catch { return false; }
        }

        public static void SaveMulti(string poolDir, long historyTicks, IList<string> imagePaths)
        {
            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || imagePaths == null || imagePaths.Count == 0) return;

                var lines = new List<string> { FormatTag, historyTicks.ToString(), "MULTI" };
                foreach (var p in imagePaths)
                    if (!string.IsNullOrEmpty(p)) lines.Add(p);
                if (lines.Count <= 3) return;

                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
            }
            catch { }
        }

        // ================================================================
        // PDF 缓存
        // ================================================================

        /// <summary>缓存文件里的 PDF 标记。</summary>
        private const string PdfTag = "PDF";

        /// <summary>
        /// 读取 PDF 缓存：history 时间戳、PDF 路径，以及（用于预览的）每页图片路径。
        /// 只做「路径存在 + 文件没坏」的粗校验，调用方还要用 <see cref="IsValidPdf"/> 再验一次。
        /// </summary>
        public static bool TryGetPdf(string poolDir, out long historyTicks, out string pdfPath, out List<string> pagePaths)
        {
            historyTicks = 0;
            pdfPath = "";
            pagePaths = new List<string>();

            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                // 格式：格式标记 / ticks / "PDF" / pdf 路径 / page1 / page2 / ...
                if (lines.Length < 4) return false;
                if (!string.Equals(lines[0].Trim(), FormatTag, StringComparison.Ordinal)) return false;
                if (!long.TryParse(lines[1].Trim(), out historyTicks)) return false;
                if (!string.Equals(lines[2].Trim(), PdfTag, StringComparison.Ordinal)) return false;

                string pdf = lines[3].Trim();
                if (string.IsNullOrEmpty(pdf) || !File.Exists(pdf)) return false;

                var pages = new List<string>();
                for (int i = 4; i < lines.Length; i++)
                {
                    string p = lines[i].Trim();
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) return false;
                    pages.Add(p);
                }

                if (pages.Count == 0) return false;

                pdfPath = pdf;
                pagePaths = pages;
                return true;
            }
            catch { return false; }
        }

        public static void SavePdf(string poolDir, long historyTicks, string pdfPath, IList<string> pagePaths)
        {
            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(pdfPath)) return;
                if (pagePaths == null || pagePaths.Count == 0) return;

                var lines = new List<string> { FormatTag, historyTicks.ToString(), PdfTag, pdfPath };
                foreach (var p in pagePaths)
                    if (!string.IsNullOrEmpty(p)) lines.Add(p);

                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>PDF 是否完好：能打开、够大、开头是 %PDF、结尾有 %%EOF。</summary>
        public static bool IsValidPdf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                var info = new FileInfo(path);
                if (info.Length < 512) return false;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var head = new byte[5];
                    if (stream.Read(head, 0, head.Length) < 5) return false;
                    if (head[0] != (byte)'%' || head[1] != (byte)'P' || head[2] != (byte)'D' || head[3] != (byte)'F')
                        return false;

                    // 末尾 2KB 里找 %%EOF（有些 PDF 会带尾部空白）
                    int tailLen = (int)Math.Min(2048, info.Length);
                    stream.Seek(-tailLen, SeekOrigin.End);
                    var tail = new byte[tailLen];
                    int read = stream.Read(tail, 0, tailLen);
                    var text = Encoding.ASCII.GetString(tail, 0, read);
                    return text.Contains("%%EOF");
                }
            }
            catch { return false; }
        }

        /// <summary>把 PDF 预览用的一页图片写进缓存目录，返回路径。</summary>
        public static string SavePdfPage(string poolDir, int index, byte[] jpegBytes)
        {
            try
            {
                if (jpegBytes == null || jpegBytes.Length == 0) return "";

                string key = Path.GetFileNameWithoutExtension(FileFor(poolDir));
                if (string.IsNullOrEmpty(key)) return "";

                Directory.CreateDirectory(CacheDir);
                string file = Path.Combine(CacheDir, $"{key}-p{index + 1}.jpg");
                File.WriteAllBytes(file, jpegBytes);
                return file;
            }
            catch { return ""; }
        }

        /// <summary>删除分享缓存记录（不删除用户已保存的图片文件）。</summary>
        public static bool ClearAll(out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(CacheDir)) return true;

                // 缓存文件 + PDF 预览用的分页图片一起清掉
                foreach (var file in Directory.GetFiles(CacheDir))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { error = ex.Message; }
                }
                return string.IsNullOrEmpty(error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
