using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 运行日志：每次启动生成一份 %APPDATA%\Random Select\logs\session-时间-进程.log。
    /// 日志写入失败时静默忽略，不影响主程序。
    /// </summary>
    public static class RuntimeLog
    {
        private static readonly object SyncRoot = new object();
        private static string _resolvedLogDirectory;

        // 每次进程启动使用独立日志文件，而不是按天共用
        private static readonly string SessionStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        private static readonly string SessionFile =
            "session-" + SessionStamp + "-" + Environment.ProcessId + ".log";

        public static string LogDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(_resolvedLogDirectory)) return _resolvedLogDirectory;
                _resolvedLogDirectory = ResolveLogDirectory();
                return _resolvedLogDirectory;
            }
        }

        private static string ResolveLogDirectory()
        {
            string appDataLogs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Random Select", "logs");

            try
            {
                if (CanWriteDirectory(appDataLogs))
                    return appDataLogs;
            }
            catch { }

            // ★ 受限环境下回退到程序目录，保证日志尽量可写
            try
            {
                string fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (CanWriteDirectory(fallback))
                    return fallback;
            }
            catch { }

            return appDataLogs;
        }

        private static bool CanWriteDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.WriteByte(0);
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string CurrentLogFile =>
            Path.Combine(LogDirectory, SessionFile);

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write("ERROR", message, ex);
        }

        public static void Error(Exception ex, string context = "")
        {
            Write("ERROR", context, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                if (ex != null)
                    text += Environment.NewLine + ex;

                lock (SyncRoot)
                {
                    File.AppendAllText(CurrentLogFile, text + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志本身失败不能影响应用
            }
        }

        public static List<string> GetLogFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return new List<string>();
                var files = new List<string>(Directory.GetFiles(LogDirectory, "*.log"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                return files;
            }
            catch { return new List<string>(); }
        }

        /// <summary>删除所有运行日志（不删除卡池、历史记录和图片）。</summary>
        public static bool ClearLogs(out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(LogDirectory)) return true;
                foreach (var file in Directory.GetFiles(LogDirectory, "*.log"))
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
