using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RandomDrawer
{
    /// <summary>抽取报告查询缓存（保存在 %APPDATA%\Random Select\report_cache）。</summary>
    public static class ReportCache
    {
        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Random Select", "report_cache");

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

        /// <summary>缓存是否仍然有效（历史记录自上次查询后没有变化）。</summary>
        public static bool HasCache(string poolDir)
        {
            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;

                string history = Path.Combine(poolDir, "history.txt");
                if (!File.Exists(history)) return false;

                string saved = File.ReadAllText(file).Trim();
                return saved == File.GetLastWriteTime(history).Ticks.ToString();
            }
            catch { return false; }
        }

        public static void Save(string poolDir)
        {
            try
            {
                string file = FileFor(poolDir);
                if (string.IsNullOrEmpty(file)) return;

                Directory.CreateDirectory(CacheDir);
                string history = Path.Combine(poolDir, "history.txt");
                string stamp = File.Exists(history) ? File.GetLastWriteTime(history).Ticks.ToString() : "0";
                File.WriteAllText(file, stamp, Encoding.UTF8);
            }
            catch { }
        }

        public static void ClearAll()
        {
            try
            {
                if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
            }
            catch { }
        }

        public static bool HasAny()
        {
            try
            {
                return Directory.Exists(CacheDir)
                    && Directory.GetFiles(CacheDir, "*.cache").Length > 0;
            }
            catch { return false; }
        }
    }
}
