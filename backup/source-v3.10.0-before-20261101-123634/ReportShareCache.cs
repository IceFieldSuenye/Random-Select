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
        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Random Select", "share_cache");

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
                if (lines.Length < 2) return false;
                if (!long.TryParse(lines[0].Trim(), out historyTicks)) return false;

                string path = lines[1].Trim();
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
                File.WriteAllLines(file, new[] { historyTicks.ToString(), imagePath }, new UTF8Encoding(false));
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
                // 格式：ticks / "MULTI" / path1 / path2 / ...
                if (lines.Length < 3) return false;
                if (!long.TryParse(lines[0].Trim(), out historyTicks)) return false;
                if (!string.Equals(lines[1].Trim(), "MULTI", StringComparison.Ordinal)) return false;

                var paths = new List<string>();
                for (int i = 2; i < lines.Length; i++)
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

                var lines = new List<string> { historyTicks.ToString(), "MULTI" };
                foreach (var p in imagePaths)
                    if (!string.IsNullOrEmpty(p)) lines.Add(p);
                if (lines.Count <= 2) return;

                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>删除分享缓存记录（不删除用户已保存的图片文件）。</summary>
        public static bool ClearAll(out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(CacheDir)) return true;
                foreach (var file in Directory.GetFiles(CacheDir, "*.cache"))
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
