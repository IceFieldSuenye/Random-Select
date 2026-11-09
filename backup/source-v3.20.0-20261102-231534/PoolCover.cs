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
                    if (!result.Any(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase)))
                        result.Add(file);
                }

                return result.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        public static string GetCoverPath(string poolDir) => GetCoverFiles(poolDir).FirstOrDefault();

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

        /// <summary>删除卡池封面（封面文件与 cover.txt）。</summary>
        public static void RemoveCover(string poolDir)
        {
            foreach (var file in GetCoverFiles(poolDir))
            {
                try { File.Delete(file); } catch { }
            }

            try
            {
                string meta = Path.Combine(poolDir, MetaFileName);
                if (File.Exists(meta)) File.Delete(meta);
            }
            catch { }
        }
    }
}
