using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池导入 / 导出的公共逻辑。
    /// 导出：把选中的卡池文件夹打包成一个 zip（zip 内每个卡池一个顶层目录）。
    /// 导入：读取 zip，找出其中可识别的卡池，校验通过后再解压到目标目录。
    /// </summary>
    public static class PoolTransfer
    {
        /// <summary>卡池数据文件的候选名，存在任意一个即视为卡池。</summary>
        public static readonly string[] DataFileNames = { "data.txt", "data.zip", "data.lock" };

        // ============================================================
        // 基础工具
        // ============================================================

        public static bool IsPoolFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            return DataFileNames.Any(n => File.Exists(Path.Combine(dir, n)));
        }

        /// <summary>去掉 Windows 文件夹名里的非法字符。</summary>
        public static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "卡池";
            var sb = new StringBuilder();
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name.Trim())
                sb.Append(invalid.Contains(c) ? '_' : c);

            string result = sb.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(result)) result = "卡池";
            if (result.Length > 80) result = result.Substring(0, 80).Trim();
            return result;
        }

        /// <summary>生成不冲突的目标目录名（重名时追加 [2] [3] …）。</summary>
        public static string MakeUniqueDirectory(string parent, string desiredName)
        {
            string name = SanitizeFolderName(desiredName);
            string dir = Path.Combine(parent, name);
            int index = 2;
            while (Directory.Exists(dir) || File.Exists(dir))
            {
                dir = Path.Combine(parent, $"{name} [{index}]");
                index++;
            }
            return dir;
        }

        /// <summary>统计卡池里的对象数量；加密卡池或读取失败返回 -1。</summary>
        public static int CountPoolItems(string poolDir)
        {
            try
            {
                string txt = Path.Combine(poolDir, "data.txt");
                if (File.Exists(txt))
                    return CountLines(File.ReadAllText(txt));

                string zipPath = Path.Combine(poolDir, "data.zip");
                if (File.Exists(zipPath))
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    var entry = zip.Entries.FirstOrDefault(e => NormalizeEntry(e) == "data.txt");
                    if (entry != null)
                    {
                        using var s = entry.Open();
                        using var reader = new StreamReader(s, Encoding.UTF8, true);
                        return CountLines(reader.ReadToEnd());
                    }
                }
            }
            catch { }
            return -1;
        }

        private static int CountLines(string text)
        {
            int count = text.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
            return Math.Max(0, count - 1);
        }

        private static string NormalizeEntry(ZipArchiveEntry entry)
        {
            return (entry.FullName ?? "").Replace('\\', '/').TrimStart('/');
        }

        // ============================================================
        // 导出
        // ============================================================

        public class ExportResult
        {
            public int Exported;
            public int Total;
            public List<string> Errors { get; } = new List<string>();
            public bool Canceled;
        }

        /// <summary>
        /// 把若干卡池打包成一个 zip。
        /// </summary>
        /// <param name="pools">(显示名, 目录)</param>
        /// <param name="zipPath">目标 zip 路径</param>
        /// <param name="progress">(已完成, 总数, 当前卡池名)</param>
        /// <param name="decryptDirs">
        /// 需要「解密后导出」的卡池目录（加密卡池在导出前已经验证过密码）。
        /// 这些卡池不会打包 data.lock / data.zip / secret_qa.lock，而是把明文写成 data.txt。
        /// </param>
        public static ExportResult Export(
            IReadOnlyList<(string Name, string Dir)> pools,
            string zipPath,
            Action<int, int, string> progress = null,
            ISet<string> decryptDirs = null)
        {
            var result = new ExportResult { Total = pools.Count };

            try
            {
                string parent = Path.GetDirectoryName(zipPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);

                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"无法创建压缩包：{ex.Message}");
                return result;
            }

            ZipArchive zip = null;
            try
            {
                zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int done = 0;

                foreach (var pool in pools)
                {
                    done++;
                    progress?.Invoke(done, result.Total, pool.Name);

                    try
                    {
                        if (!IsPoolFolder(pool.Dir))
                        {
                            result.Errors.Add($"{pool.Name}：不是有效的卡池文件夹，已跳过");
                            continue;
                        }

                        string root = SanitizeFolderName(pool.Name);
                        string unique = root;
                        int suffix = 2;
                        while (usedNames.Contains(unique))
                        {
                            unique = $"{root} ({suffix})";
                            suffix++;
                        }
                        usedNames.Add(unique);

                        // 该卡池是否需要「解密后导出」（加密卡池在 UI 层已验过密码）
                        bool decrypt = decryptDirs != null && decryptDirs.Contains(pool.Dir);

                        foreach (var file in Directory.GetFiles(pool.Dir, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(pool.Dir, file).Replace('\\', '/');
                            if (rel.StartsWith("../", StringComparison.Ordinal)) continue;

                            if (decrypt)
                            {
                                string fileName = Path.GetFileName(file);
                                // 解密导出：不打包加密数据与密保文件
                                if (fileName.Equals("data.lock", StringComparison.OrdinalIgnoreCase)
                                    || fileName.Equals("data.zip", StringComparison.OrdinalIgnoreCase)
                                    || fileName.Equals("data.zip.bak", StringComparison.OrdinalIgnoreCase)
                                    || fileName.Equals("data.lock.bak", StringComparison.OrdinalIgnoreCase)
                                    || fileName.Equals("secret_qa.lock", StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            try
                            {
                                zip.CreateEntryFromFile(file, unique + "/" + rel, CompressionLevel.Optimal);
                            }
                            catch (Exception ex)
                            {
                                result.Errors.Add($"{pool.Name} / {rel}：{ex.Message}");
                            }
                        }

                        // 解密导出：把名单以明文 data.txt 写进压缩包
                        if (decrypt)
                        {
                            string plain = PoolArchive.ReadDataTxtFromArchive(pool.Dir);
                            if (string.IsNullOrEmpty(plain))
                            {
                                result.Errors.Add($"{pool.Name}：无法解密名单，已跳过");
                                continue;
                            }

                            try
                            {
                                var entry = zip.CreateEntry(unique + "/data.txt", CompressionLevel.Optimal);
                                using (var stream = entry.Open())
                                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                                {
                                    writer.Write(plain);
                                }
                            }
                            catch (Exception ex)
                            {
                                result.Errors.Add($"{pool.Name} / data.txt：{ex.Message}");
                                continue;
                            }
                        }

                        result.Exported++;
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{pool.Name}：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"导出失败：{ex.Message}");
            }
            finally
            {
                try { zip?.Dispose(); } catch { }
            }

            return result;
        }

        // ============================================================
        // 导入：识别
        // ============================================================

        public class ImportItem
        {
            public string Name { get; set; } = "";
            public string ZipPath { get; set; } = "";
            public string RootInZip { get; set; } = "";
            public string Container { get; set; } = "";
            public int ItemCount { get; set; } = -1;
            public bool IsValid { get; set; }
            public string Reason { get; set; } = "";

            public string DisplayName => string.IsNullOrEmpty(RootInZip) ? Name : Name;
        }

        /// <summary>
        /// 读取一个 zip，列出其中的卡池。
        /// 返回的条目里 IsValid=false 的表示无法识别（会在导入结束后统一提示）。
        /// </summary>
        public static List<ImportItem> InspectZip(string zipPath, out string error)
        {
            error = null;
            var items = new List<ImportItem>();

            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var groups = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
                bool hasAnyFile = false;

                foreach (var entry in zip.Entries)
                {
                    string full = NormalizeEntry(entry);
                    if (string.IsNullOrEmpty(full) || entry.Name == "") continue; // 目录项
                    hasAnyFile = true;

                    int slash = full.IndexOf('/');
                    string root = slash < 0 ? "" : full.Substring(0, slash);

                    if (!groups.TryGetValue(root, out var list))
                        groups[root] = list = new List<ZipArchiveEntry>();
                    list.Add(entry);
                }

                if (!hasAnyFile)
                {
                    error = "压缩包是空的";
                    return items;
                }

                foreach (var kv in groups)
                {
                    string root = kv.Key;
                    var entries = kv.Value;

                    string dataEntry = entries
                        .Select(e => NormalizeEntry(e))
                        .FirstOrDefault(f => DataFileNames.Any(d =>
                            string.Equals(root.Length == 0 ? f : f.Substring(Math.Min(f.Length, root.Length + 1)), d, StringComparison.OrdinalIgnoreCase)));

                    var item = new ImportItem
                    {
                        ZipPath = zipPath,
                        Container = Path.GetFileName(zipPath),
                        RootInZip = root,
                        Name = string.IsNullOrEmpty(root) ? Path.GetFileNameWithoutExtension(zipPath) : root
                    };

                    if (dataEntry == null)
                    {
                        item.IsValid = false;
                        item.Reason = "缺少卡池数据文件（data.txt / data.zip / data.lock）";
                    }
                    else
                    {
                        item.IsValid = true;

                        string dataFile = dataEntry.Substring(root.Length == 0 ? 0 : root.Length + 1);
                        if (string.Equals(dataFile, "data.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            var entry = entries.First(e => NormalizeEntry(e) == dataEntry);
                            try
                            {
                                using var s = entry.Open();
                                using var reader = new StreamReader(s, Encoding.UTF8, true);
                                item.ItemCount = CountLines(reader.ReadToEnd());
                            }
                            catch { item.ItemCount = -1; }
                        }
                    }

                    items.Add(item);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return items;
        }

        // ============================================================
        // 导入：解压
        // ============================================================

        public class ImportResult
        {
            public int Imported;
            public int Total;
            public List<string> Failed { get; } = new List<string>();
            public List<string> ImportedDirs { get; } = new List<string>();
        }

        /// <summary>
        /// 把识别好的卡池解压到目标目录。
        /// 单个卡池失败不会影响其它卡池，失败原因收集在 Failed 里。
        /// </summary>
        public static ImportResult Import(
            IReadOnlyList<ImportItem> items,
            string targetParent,
            Action<int, int, string> progress = null)
        {
            var result = new ImportResult { Total = items.Count };

            try
            {
                if (!Directory.Exists(targetParent))
                    Directory.CreateDirectory(targetParent);
            }
            catch (Exception ex)
            {
                result.Failed.Add($"无法创建目标目录：{ex.Message}");
                return result;
            }

            int done = 0;
            foreach (var item in items)
            {
                done++;
                progress?.Invoke(done, result.Total, item.Name);

                if (!item.IsValid)
                {
                    result.Failed.Add($"{item.Name}：{item.Reason}");
                    continue;
                }

                string destDir = MakeUniqueDirectory(targetParent, item.Name);
                try
                {
                    Directory.CreateDirectory(destDir);

                    using (var zip = ZipFile.OpenRead(item.ZipPath))
                    {
                        string prefix = string.IsNullOrEmpty(item.RootInZip) ? "" : item.RootInZip + "/";

                        foreach (var entry in zip.Entries)
                        {
                            string full = NormalizeEntry(entry);
                            if (string.IsNullOrEmpty(full) || entry.Name == "") continue;
                            if (prefix.Length > 0 && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                            string rel = prefix.Length == 0 ? full : full.Substring(prefix.Length);
                            if (string.IsNullOrEmpty(rel)) continue;

                            string targetPath = Path.GetFullPath(Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                            string rootPath = Path.GetFullPath(destDir);

                            // 防止 zip 里带 ../ 越界写文件
                            if (!targetPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(targetPath, rootPath, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException($"压缩包内包含非法路径：{rel}");
                            }

                            string dir = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            entry.ExtractToFile(targetPath, true);
                        }
                    }

                    if (!IsPoolFolder(destDir))
                        throw new InvalidDataException("解压后没有找到卡池数据文件");

                    result.Imported++;
                    result.ImportedDirs.Add(destDir);
                }
                catch (Exception ex)
                {
                    result.Failed.Add($"{item.Name}：{ex.Message}");
                    try { if (Directory.Exists(destDir)) Directory.Delete(destDir, true); } catch { }
                }
            }

            return result;
        }
    }
}
