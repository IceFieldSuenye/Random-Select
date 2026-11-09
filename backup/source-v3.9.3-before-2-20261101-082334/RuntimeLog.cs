using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>单个日志文件的大小上限，超过后不再写入，避免日志无限增长。</summary>
        private const long MaxFileBytes = 8L * 1024 * 1024;

        /// <summary>保留最近的日志文件数量，启动时清理更早的会话。</summary>
        private const int KeepLogFiles = 20;

        private static string _resolvedLogDirectory;

        // 连续重复的日志合并成一行，避免「配置加载」「保存设置」这类内容刷屏
        private static string _lastKey;
        private static string _lastMessagePreview;
        private static int _repeatCount;
        private static DateTime _repeatFirstAt;
        private static bool _sizeLimitNotified;

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

            // 受限环境下回退到程序目录，保证日志尽量可写
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

        /// <summary>本次运行已经过的时间。</summary>
        public static TimeSpan Elapsed => Clock.Elapsed;

        /// <summary>
        /// 会话开始时调用一次：写入环境信息，并清理过旧的历史日志。
        /// </summary>
        public static void StartSession(string appVersion)
        {
            try
            {
                PruneOldLogs();

                var sb = new StringBuilder();
                sb.AppendLine("================ 会话开始 ================");
                sb.AppendLine("启动时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("程序版本：" + (appVersion ?? "-"));
                sb.AppendLine("系统：" + SafeOs());
                sb.AppendLine("运行时：" + SafeRuntime());
                sb.AppendLine("进程：PID " + Environment.ProcessId + "，64 位进程=" + Environment.Is64BitProcess);
                sb.AppendLine("程序目录：" + AppDomain.CurrentDomain.BaseDirectory);
                sb.AppendLine("当前用户：" + SafeUser());
                sb.AppendLine("日志文件：" + CurrentLogFile);
                sb.AppendLine("==========================================");

                WriteRaw(sb.ToString());
            }
            catch { }
        }

        /// <summary>会话结束时调用：补写重复次数与结束信息。</summary>
        public static void EndSession(string extra = "")
        {
            try
            {
                lock (SyncRoot)
                {
                    FlushRepeatLocked();
                }
                Info("会话结束：运行时长=" + FormatElapsed(Clock.Elapsed) + (string.IsNullOrEmpty(extra) ? "" : "，" + extra));
                lock (SyncRoot)
                {
                    FlushRepeatLocked();
                }
            }
            catch { }
        }

        private static string SafeOs()
        {
            try
            {
                string arch = RuntimeInformation.OSArchitecture.ToString();
                return RuntimeInformation.OSDescription + "（" + arch + "）";
            }
            catch { return "-"; }
        }

        private static string SafeRuntime()
        {
            try { return RuntimeInformation.FrameworkDescription; }
            catch { return "-"; }
        }

        private static string SafeUser()
        {
            try
            {
                string user = Environment.UserName;
                string domain = Environment.UserDomainName;
                return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
            }
            catch { return "-"; }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message, Exception ex = null)
        {
            Write("WARN", message, ex);
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
                lock (SyncRoot)
                {
                    string key = level + "|" + message + "|" + (ex == null ? "" : ex.GetType().FullName);

                    // 完全相同的连续日志只累计次数，换下一条时再补一行汇总
                    if (ex == null && key == _lastKey)
                    {
                        _repeatCount++;
                        return;
                    }

                    FlushRepeatLocked();

                    _lastKey = ex == null ? key : null;
                    _lastMessagePreview = message;
                    _repeatFirstAt = DateTime.Now;

                    WriteRaw(BuildLine(level, message, ex));
                }
            }
            catch
            {
                // 日志本身失败不能影响应用
            }
        }

        private static string BuildLine(string level, string message, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ');
            sb.Append('+').Append(FormatElapsed(Clock.Elapsed)).Append("] ");
            sb.Append('[').Append(level).Append("] ");
            sb.Append(message ?? "");

            if (ex != null)
            {
                sb.AppendLine();
                sb.Append("    异常：").Append(ex.GetType().FullName).Append("：").Append(ex.Message);
                string stack = ex.StackTrace;
                if (!string.IsNullOrEmpty(stack))
                {
                    sb.AppendLine();
                    sb.Append("    堆栈：").Append(stack.Replace("\r\n", "\r\n    "));
                }

                var inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.AppendLine();
                    sb.Append("    内部异常：").Append(inner.GetType().FullName).Append("：").Append(inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }
            }

            return sb.ToString();
        }

        private static void FlushRepeatLocked()
        {
            if (_repeatCount <= 0) return;

            var sb = new StringBuilder();
            sb.Append('[').Append(_repeatFirstAt.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ');
            sb.Append('+').Append(FormatElapsed(Clock.Elapsed)).Append("] ");
            sb.Append("[INFO] 上一条「").Append(Preview(_lastMessagePreview)).Append("」又重复 ")
              .Append(_repeatCount).Append(" 次");
            WriteRaw(sb.ToString());

            _repeatCount = 0;
        }

        private static string Preview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= 40 ? flat : flat.Substring(0, 40) + "…";
        }

        private static string FormatElapsed(TimeSpan span)
        {
            return span.TotalHours >= 1
                ? span.ToString(@"hh\:mm\:ss") + "." + span.Milliseconds.ToString("000")
                : span.ToString(@"mm\:ss") + "." + span.Milliseconds.ToString("000");
        }

        private static void WriteRaw(string text)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                string file = CurrentLogFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    if (!_sizeLimitNotified)
                    {
                        _sizeLimitNotified = true;
                        File.AppendAllText(file,
                            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [WARN] 日志已超过 "
                            + (MaxFileBytes / 1024 / 1024) + "MB，本次会话不再写入" + Environment.NewLine,
                            new UTF8Encoding(false));
                    }
                    return;
                }

                File.AppendAllText(file, text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 日志本身失败不能影响应用
            }
        }

        /// <summary>只保留最近若干份日志，避免日志目录无限膨胀。</summary>
        private static void PruneOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return;
                var files = new List<FileInfo>(new DirectoryInfo(LogDirectory).GetFiles("*.log"));
                if (files.Count <= KeepLogFiles) return;

                files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                for (int i = KeepLogFiles; i < files.Count; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        public static List<string> GetLogFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory)) return new List<string>();
                var dir = new DirectoryInfo(LogDirectory);
                var infos = new List<FileInfo>(dir.GetFiles("*.log"));
                infos.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                var files = new List<string>(infos.Count);
                foreach (var info in infos) files.Add(info.FullName);
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
