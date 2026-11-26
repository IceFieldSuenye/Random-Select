using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RandomDrawer
{
    /// <summary>
    /// 历史记录分片文件。3.77.0-snapshot-19 起：
    /// · 新记录统一写入卡池目录下的 History 子文件夹（history1.txt / history2.txt …），
    ///   避免在卡池根目录散落大量文件；
    /// · 旧版本的 history.txt / history2.txt 仍会读取（只读，不再追加），保证兼容；
    /// · 单个文件最多 10 000 行，写满后新建下一个编号；
    /// · 读取时按「旧位置 → History 目录」的顺序拼接，最新记录在最后。
    /// </summary>
    public static class HistoryFiles
    {
        /// <summary>单个历史分片文件最多保存的行数。</summary>
        public const int MaxLinesPerFile = 10000;

        /// <summary>历史页/报告在估算总行数时，最多对前 5 个新分片做精确计数。</summary>
        public const int ExactShardLimit = 5;

        private static readonly Regex NamePattern =
            new Regex(@"^history(\d*)\.txt$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>历史分片所在的子文件夹。</summary>
        public static string HistoryDir(string poolDir) => Path.Combine(poolDir, "History");

        /// <summary>新分片的文件名：1 → history1.txt，2 → history2.txt …</summary>
        public static string NewFileName(int number) => $"history{Math.Max(1, number)}.txt";

        /// <summary>分片信息（行数可能是按 10 000 估算出来的）。</summary>
        public sealed class HistoryShard
        {
            public string Path { get; set; }
            public bool InHistoryDir { get; set; }
            public int Order { get; set; }
            public long Lines { get; set; }
            public bool Exact { get; set; }
        }

        private static readonly object CacheLock = new object();
        private static string _cachePoolDir;
        private static string _cacheSignature;
        private static List<HistoryShard> _cacheShards;

        /// <summary>枚举卡池下的全部分片路径（旧位置在前、History 目录在后），按编号升序。</summary>
        public static List<string> EnumerateShards(string poolDir)
        {
            var paths = new List<string>();
            foreach (var shard in GetShards(poolDir)) paths.Add(shard.Path);
            return paths;
        }

        /// <summary>该卡池是否已经有历史记录（任意分片）。</summary>
        public static bool AnyExists(string poolDir)
        {
            if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return false;
            try
            {
                if (Directory.GetFiles(poolDir, "history*.txt").Length > 0) return true;
                string dir = HistoryDir(poolDir);
                return Directory.Exists(dir) && Directory.GetFiles(dir, "history*.txt").Length > 0;
            }
            catch { return false; }
        }

        /// <summary>按「旧 → 新」拼接顺序读取所有分片的全部行（数据大时请改用 <see cref="EnumerateLines"/>）。</summary>
        public static List<string> ReadAllLines(string poolDir)
        {
            var lines = new List<string>();
            foreach (string shard in EnumerateShards(poolDir))
            {
                try { lines.AddRange(File.ReadAllLines(shard)); }
                catch { }
            }
            return lines;
        }

        /// <summary>
        /// 逐行流式读取全部分片（旧 → 新）。每次只把一个分片读进内存，读完即释放，
        /// 供报告统计等只需要「过一遍」的场景使用。
        /// </summary>
        public static IEnumerable<string> EnumerateLines(string poolDir)
        {
            foreach (var shard in GetShards(poolDir))
            {
                foreach (string line in ReadLinesBlock(shard.Path))
                    yield return line;
            }
        }

        /// <summary>估算全部分片的总行数（最后一个分片精确，其余按 10 000 规则估算）。</summary>
        public static long CountTotalLines(string poolDir)
        {
            long total = 0;
            foreach (var shard in GetShards(poolDir)) total += shard.Lines;
            return total;
        }

        /// <summary>
        /// 按「最新在前」的顺序取出某一页的原始行。只会读取与这一页相交的分片，
        /// 因此翻页时不会把整个历史全部载入内存。
        /// </summary>
        public static List<string> ReadPageLines(string poolDir, int pageIndex, int pageSize)
        {
            var result = new List<string>();
            if (pageSize <= 0 || pageIndex < 1) return result;

            List<HistoryShard> shards = GetShards(poolDir);
            long total = 0;
            foreach (var shard in shards) total += shard.Lines;
            if (total <= 0) return result;

            long skipFromEnd = (long)(pageIndex - 1) * pageSize;
            if (skipFromEnd >= total) return result;

            long takeFromEnd = Math.Min(pageSize, total - skipFromEnd);
            long fromStart = total - skipFromEnd - takeFromEnd;
            long toStart = total - skipFromEnd;   // 不含

            long offset = 0;
            foreach (var shard in shards)
            {
                long shardStart = offset;
                long shardEnd = offset + shard.Lines;
                offset = shardEnd;

                if (shardEnd <= fromStart || shardStart >= toStart) continue;

                long localStart = Math.Max(0, fromStart - shardStart);
                long need = Math.Min(shardEnd, toStart) - Math.Max(shardStart, fromStart);
                if (need <= 0) continue;

                try
                {
                    foreach (string line in File.ReadLines(shard.Path).Skip((int)localStart).Take((int)need))
                        result.Add(line);
                }
                catch { }
            }

            result.Reverse();   // 页内最新在前
            return result;
        }

        /// <summary>统计单个文件的行数（文件不存在或读失败时返回 0）。</summary>
        public static int CountLines(string file)
        {
            try
            {
                int count = 0;
                using (var reader = new StreamReader(file))
                {
                    while (reader.ReadLine() != null) count++;
                }
                return count;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 找到「应该继续追加」的分片（只使用 History 子文件夹）：
        /// 最后一个尚未写满的文件；全部都满时返回下一个新文件。
        /// </summary>
        public static string ResolveAppendTarget(string poolDir, out int linesInFile, out int number)
        {
            string dir = HistoryDir(poolDir);
            List<HistoryShard> shards = DiscoverHistory(dir);
            linesInFile = 0;
            if (shards.Count == 0)
            {
                number = 1;
                return Path.Combine(dir, NewFileName(1));
            }

            HistoryShard last = shards[shards.Count - 1];
            number = last.Order;
            int count = CountLines(last.Path);
            if (count < MaxLinesPerFile)
            {
                linesInFile = count;
                return last.Path;
            }

            number++;
            return Path.Combine(dir, NewFileName(number));
        }

        /// <summary>下一个分片的完整路径（History 目录内最大编号 + 1）。</summary>
        public static string NextShardPath(string poolDir)
        {
            string dir = HistoryDir(poolDir);
            List<HistoryShard> shards = DiscoverHistory(dir);
            int number = shards.Count == 0 ? 1 : shards[shards.Count - 1].Order + 1;
            return Path.Combine(dir, NewFileName(number));
        }

        /// <summary>所有分片中最新的写入时间（UTC ticks），用于缓存签名。</summary>
        public static long GetLastWriteTicks(string poolDir)
        {
            long ticks = 0;
            foreach (string shard in EnumerateShards(poolDir))
            {
                try { ticks = Math.Max(ticks, File.GetLastWriteTimeUtc(shard).Ticks); }
                catch { }
            }
            return ticks;
        }

        /// <summary>所有分片的总字节数，用于缓存签名。</summary>
        public static long GetTotalLength(string poolDir)
        {
            long length = 0;
            foreach (string shard in EnumerateShards(poolDir))
            {
                try { length += new FileInfo(shard).Length; }
                catch { }
            }
            return length;
        }

        /// <summary>取全部（含行数）分片信息，带签名缓存，避免翻页时反复扫描文件。</summary>
        public static List<HistoryShard> GetShards(string poolDir)
        {
            if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir))
                return new List<HistoryShard>();

            List<HistoryShard> discovered = Discover(poolDir, false);
            if (discovered.Count == 0) return discovered;

            string signature = BuildSignature(poolDir, discovered);
            lock (CacheLock)
            {
                if (_cacheShards != null && _cachePoolDir == poolDir && _cacheSignature == signature)
                    return Clone(_cacheShards);
            }

            FillCounts(discovered);

            lock (CacheLock)
            {
                _cachePoolDir = poolDir;
                _cacheSignature = signature;
                _cacheShards = Clone(discovered);
            }
            return discovered;
        }

        /// <summary>清空分片缓存（重置历史后调用，避免继续用旧行数）。</summary>
        public static void InvalidateCache()
        {
            lock (CacheLock)
            {
                _cachePoolDir = null;
                _cacheSignature = null;
                _cacheShards = null;
            }
        }

        private static List<HistoryShard> Discover(string poolDir, bool onlyHistoryDir)
        {
            var list = new List<HistoryShard>();
            if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return list;

            if (!onlyHistoryDir) AddDir(list, poolDir, false);
            AddDir(list, HistoryDir(poolDir), true);
            return list;
        }

        /// <summary>只扫描已经算好的 History 子目录（不能再用 HistoryDir 拼接一次）。</summary>
        private static List<HistoryShard> DiscoverHistory(string historyDir)
        {
            var list = new List<HistoryShard>();
            AddDir(list, historyDir, true);
            return list;
        }

        private static void AddDir(List<HistoryShard> list, string dir, bool inHistory)
        {
            if (!Directory.Exists(dir)) return;

            var found = new List<KeyValuePair<int, string>>();
            try
            {
                foreach (string path in Directory.GetFiles(dir, "history*.txt"))
                {
                    Match m = NamePattern.Match(Path.GetFileName(path));
                    if (!m.Success) continue;

                    int number = 1;
                    string digits = m.Groups[1].Value;
                    if (digits.Length > 0 && !int.TryParse(digits, out number)) continue;
                    if (number < 1) continue;

                    found.Add(new KeyValuePair<int, string>(number, path));
                }
            }
            catch { }

            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            foreach (var item in found)
            {
                list.Add(new HistoryShard
                {
                    Path = item.Value,
                    InHistoryDir = inHistory,
                    Order = item.Key
                });
            }
        }

        private static string BuildSignature(string poolDir, List<HistoryShard> shards)
        {
            var sb = new System.Text.StringBuilder(poolDir);
            foreach (var shard in shards)
            {
                try
                {
                    var info = new FileInfo(shard.Path);
                    sb.Append('|').Append(shard.Path).Append(':')
                      .Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
                }
                catch { sb.Append('|').Append(shard.Path); }
            }
            return sb.ToString();
        }

        private static void FillCounts(List<HistoryShard> shards)
        {
            int historySeen = 0;
            for (int i = 0; i < shards.Count; i++)
            {
                HistoryShard shard = shards[i];
                bool isLast = i == shards.Count - 1;

                bool exact;
                if (shard.InHistoryDir)
                {
                    historySeen++;
                    // 只有前 5 个新分片和最后一个分片做精确计数，其余按写满 10 000 行估算
                    exact = isLast || historySeen <= ExactShardLimit;
                }
                else
                {
                    // 旧版本留在卡池根目录的文件数量少，直接精确计数
                    exact = true;
                }

                shard.Exact = exact;
                shard.Lines = exact ? CountLines(shard.Path) : MaxLinesPerFile;
            }
        }

        private static List<HistoryShard> Clone(List<HistoryShard> source)
        {
            var copy = new List<HistoryShard>(source.Count);
            foreach (var shard in source)
            {
                copy.Add(new HistoryShard
                {
                    Path = shard.Path,
                    InHistoryDir = shard.InHistoryDir,
                    Order = shard.Order,
                    Lines = shard.Lines,
                    Exact = shard.Exact
                });
            }
            return copy;
        }

        /// <summary>把一个分片整块读进内存（一次只读一个文件），失败时返回空列表。</summary>
        private static List<string> ReadLinesBlock(string path)
        {
            var lines = new List<string>(1024);
            try
            {
                using (var reader = new StreamReader(path))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            catch { lines.Clear(); }
            return lines;
        }

        private static int ShardNumber(string path)
        {
            Match m = NamePattern.Match(Path.GetFileName(path));
            if (!m.Success) return 1;
            string digits = m.Groups[1].Value;
            if (digits.Length == 0) return 1;
            return int.TryParse(digits, out int number) && number >= 1 ? number : 1;
        }
    }
}