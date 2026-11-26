using System;
using System.IO;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 3.77.0-snapshot-17：无限次抽取用的流式历史写入。
    /// 行格式与原来的 history.txt 完全一致；单个文件写满 10 000 行后自动切到下一个分片。
    /// </summary>
    internal sealed class HistoryStreamWriter : IDisposable
    {
        private readonly string _poolDir;
        private readonly string _drawType;

        private StreamWriter _writer;
        private int _linesInFile;
        private DateTime _stamp;
        private int _stampLines;
        private bool _disposed;

        public HistoryStreamWriter(string poolDir, string drawType)
        {
            _poolDir = poolDir;
            _drawType = drawType ?? "";
            _stamp = DateTime.Now;

            string file = HistoryFiles.ResolveAppendTarget(_poolDir, out int lines, out int number);
            OpenFile(file, lines, number);
        }

        /// <summary>写入一行历史。参数是已经用 Tab 拼好的字段串（调用方负责缓存，避免重复拼接）。</summary>
        public void Write(string fieldsLine)
        {
            if (_disposed || _writer == null) return;

            if (_linesInFile >= HistoryFiles.MaxLinesPerFile)
            {
                string next = HistoryFiles.NextShardPath(_poolDir);
                OpenFile(next, 0, HistoryFilesFileNumber(next));
            }

            // 时间戳每 200 行刷新一次：同一批抽取共用秒级时间，既符合原有格式又避免每行格式化的开销。
            if (_stampLines >= 200)
            {
                _stamp = DateTime.Now;
                _stampLines = 0;
            }
            _stampLines++;

            _writer.WriteLine(_stamp.ToString("yyyy/MM/dd HH:mm:ss") + "|" + _drawType + "|" + fieldsLine);
            _linesInFile++;
        }

        private static int HistoryFilesFileNumber(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? "";
            if (string.Equals(name, "history", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.StartsWith("history", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.Substring("history".Length), out int number) && number >= 1)
                return number;
            return 1;
        }

        private void OpenFile(string file, int existingLines, int number)
        {
            CloseWriter();

            try
            {
                // 3.77.0-snapshot-19 修正：新分片在 History 子目录里，必须连父目录一起创建，
                // 否则首次写入会因为目录不存在而静默丢失历史。
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                else Directory.CreateDirectory(_poolDir);
                var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
                _linesInFile = existingLines;
            }
            catch
            {
                _writer = null;
            }
        }

        private void CloseWriter()
        {
            try
            {
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch { }
            finally { _writer = null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseWriter();
        }
    }
}