using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池目录的存储工具。
    ///
    /// ★ 新方案（不再使用 zip 打包）：
    ///   · data.txt              —— 明文数据
    ///   · data.lock             —— AES 加密后的纯文本数据
    ///   · 其他资源散落存放：
    ///       cover.*
    ///       personalization.json
    ///       illustrations/  avatars/  sounds/  backgrounds/
    ///
    /// 兼容旧格式：
    ///   · data.zip              —— 未加密 zip（含 data.txt + 资源）
    ///   · data.lock             —— 加密 zip（AES 包裹 Base64(zip)）
    ///   首次访问旧格式卡池时自动迁移为散落文件并删除旧 zip。
    /// </summary>
    public static class PoolArchive
    {
        public const string LockFileName = "data.lock";
        public const string ZipFileName = "data.zip";

        public const string IllustrationDir = "illustrations";
        public const string AvatarDir = "avatars";
        public const string SoundDir = "sounds";
        public const string BackgroundDir = "backgrounds";

        // ============================================================
        // 内部判定
        // ============================================================

        private static bool IsZipBytes(byte[] bytes)
        {
            return bytes != null
                && bytes.Length >= 4
                && bytes[0] == 0x50
                && bytes[1] == 0x4B
                && bytes[2] == 0x03
                && bytes[3] == 0x04;
        }

        /// <summary>data.lock 是否为旧版加密 zip（AES 加密 Base64(zip)）。</summary>
        private static bool IsLegacyEncryptedZip(string lockPath)
        {
            try
            {
                if (!File.Exists(lockPath)) return false;

                string encrypted = File.ReadAllText(lockPath);
                string base64;
                try { base64 = CryptoUtils.DecryptString(encrypted); }
                catch { return false; }

                try
                {
                    byte[] candidate = Convert.FromBase64String(base64);
                    return IsZipBytes(candidate);
                }
                catch { return false; }
            }
            catch { return false; }
        }

        private static bool HasScatteredContent(string poolDir)
        {
            bool hasDataTxt = File.Exists(Path.Combine(poolDir, "data.txt"));
            bool hasPers = File.Exists(Path.Combine(poolDir, "personalization.json"));

            bool hasCover = false;
            try { hasCover = PoolCover.GetCoverFiles(poolDir).Length > 0; } catch { }

            bool hasResourceSubDir =
                Directory.Exists(Path.Combine(poolDir, IllustrationDir)) ||
                Directory.Exists(Path.Combine(poolDir, AvatarDir)) ||
                Directory.Exists(Path.Combine(poolDir, SoundDir)) ||
                Directory.Exists(Path.Combine(poolDir, BackgroundDir));

            return hasDataTxt || hasPers || hasCover || hasResourceSubDir;
        }

        // ============================================================
        // 判断是否需要迁移
        // ============================================================

        public static bool NeedsUnpack(string poolDir)
        {
            if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return false;

            string lockPath = Path.Combine(poolDir, LockFileName);
            string zipPath = Path.Combine(poolDir, ZipFileName);

            bool hasLegacyZip =
                File.Exists(zipPath) ||
                (File.Exists(lockPath) && IsLegacyEncryptedZip(lockPath));

            if (!hasLegacyZip) return false;

            return !HasScatteredContent(poolDir);
        }

        // ============================================================
        // 迁移旧 zip → 散落文件
        // ============================================================

        public static bool UnpackFromArchive(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return false;

                string lockPath = Path.Combine(poolDir, LockFileName);
                string zipPath = Path.Combine(poolDir, ZipFileName);

                byte[] zipBytes = null;
                bool isEncryptedZip = false;

                if (File.Exists(zipPath))
                {
                    byte[] candidate = File.ReadAllBytes(zipPath);
                    if (IsZipBytes(candidate)) zipBytes = candidate;
                }

                if (zipBytes == null && File.Exists(lockPath) && IsLegacyEncryptedZip(lockPath))
                {
                    string encrypted = File.ReadAllText(lockPath);
                    string base64 = CryptoUtils.DecryptString(encrypted);
                    zipBytes = Convert.FromBase64String(base64);
                    isEncryptedZip = true;
                }

                if (zipBytes == null) return false;

                // 解包所有条目到磁盘
                using (var ms = new MemoryStream(zipBytes))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    string fullPool = Path.GetFullPath(poolDir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;

                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destPath = Path.Combine(poolDir, rel);
                        string fullDest = Path.GetFullPath(destPath);

                        if (!fullDest.StartsWith(fullPool, StringComparison.OrdinalIgnoreCase)) continue;

                        string destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                            Directory.CreateDirectory(destDir);

                        using (var es = entry.Open())
                        using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                        {
                            es.CopyTo(fs);
                        }
                    }
                }

                // 若原先是加密 zip：把 data.txt 再加密成新版 data.lock
                if (isEncryptedZip)
                {
                    string txtPath = Path.Combine(poolDir, "data.txt");
                    if (File.Exists(txtPath))
                    {
                        // ★ 先留一份原 lock（可回滚），再写入新格式
                        try
                        {
                            string lockBak = lockPath + ".bak";
                            if (File.Exists(lockBak)) File.Delete(lockBak);
                            File.Copy(lockPath, lockBak, true);
                        }
                        catch { }

                        string content = CryptoUtils.ReadAllTextWithEncoding(txtPath);
                        File.WriteAllText(lockPath, CryptoUtils.EncryptString(content));
                        try { File.Delete(txtPath); } catch { }
                    }
                }

                // ★ 旧 zip 不直接删除，改名成 .bak 便于回滚（迁移出错/想拿回原包时可用）
                if (File.Exists(zipPath))
                {
                    try
                    {
                        string zipBak = zipPath + ".bak";
                        if (File.Exists(zipBak)) File.Delete(zipBak);
                        File.Move(zipPath, zipBak);
                    }
                    catch
                    {
                        try { File.Delete(zipPath); } catch { }
                    }
                }

                return true;
            }
            catch { return false; }
        }

        // ============================================================
        // 读取数据
        // ============================================================

        /// <summary>
        /// 读取卡池数据文本。
        /// 优先级：data.txt → data.lock（新格式：加密文本；旧格式：加密 zip）→ data.zip（旧）。
        /// </summary>
        public static string ReadDataTxtFromArchive(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return null;

                string lockPath = Path.Combine(poolDir, LockFileName);
                string zipPath = Path.Combine(poolDir, ZipFileName);
                string txtPath = Path.Combine(poolDir, "data.txt");

                // 1) data.txt
                if (File.Exists(txtPath))
                {
                    try { return CryptoUtils.ReadAllTextWithEncoding(txtPath); } catch { }
                }

                // 2) data.lock
                if (File.Exists(lockPath))
                {
                    // 2a) 旧格式：加密 zip
                    if (IsLegacyEncryptedZip(lockPath))
                    {
                        try
                        {
                            string encrypted = File.ReadAllText(lockPath);
                            string base64 = CryptoUtils.DecryptString(encrypted);
                            byte[] candidate = Convert.FromBase64String(base64);

                            using (var ms = new MemoryStream(candidate))
                            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                            {
                                var entry = zip.GetEntry("data.txt");
                                if (entry != null)
                                {
                                    using (var es = entry.Open())
                                    using (var sr = new StreamReader(es, Encoding.UTF8))
                                    {
                                        return sr.ReadToEnd();
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // 2b) 新格式：纯文本 AES 加密
                    try { return CryptoUtils.DecryptString(File.ReadAllText(lockPath)); } catch { }
                }

                // 3) data.zip（旧）
                if (File.Exists(zipPath))
                {
                    try
                    {
                        byte[] candidate = File.ReadAllBytes(zipPath);
                        if (IsZipBytes(candidate))
                        {
                            using (var ms = new MemoryStream(candidate))
                            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                            {
                                var entry = zip.GetEntry("data.txt");
                                if (entry != null)
                                {
                                    using (var es = entry.Open())
                                    using (var sr = new StreamReader(es, Encoding.UTF8))
                                    {
                                        return sr.ReadToEnd();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                return null;
            }
            catch { return null; }
        }

        // ============================================================
        // 保存数据（新方案）
        // ============================================================

        /// <summary>
        /// 把内容保存到卡池目录：
        ///   encrypt=true  → 写 data.lock（AES 加密纯文本）
        ///   encrypt=false → 写 data.txt（UTF-8 无 BOM）
        /// 会一并清理 data.zip 与另一种数据源，保证只有一份数据。
        /// </summary>
        public static bool SaveData(string poolDir, string content, bool encrypt)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return false;
                content = content ?? "";

                string lockPath = Path.Combine(poolDir, LockFileName);
                string zipPath = Path.Combine(poolDir, ZipFileName);
                string txtPath = Path.Combine(poolDir, "data.txt");

                if (encrypt)
                {
                    File.WriteAllText(lockPath, CryptoUtils.EncryptString(content));
                    if (File.Exists(txtPath)) try { File.Delete(txtPath); } catch { }
                }
                else
                {
                    File.WriteAllText(txtPath, content, new UTF8Encoding(false));
                    if (File.Exists(lockPath)) try { File.Delete(lockPath); } catch { }
                }

                if (File.Exists(zipPath)) try { File.Delete(zipPath); } catch { }

                return true;
            }
            catch { return false; }
        }

        // ============================================================
        // 删除 / 清理
        // ============================================================

        /// <summary>删除所有数据文件（data.lock / data.zip / data.txt）。</summary>
        public static void DeleteArchives(string poolDir)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return;
                string lockPath = Path.Combine(poolDir, LockFileName);
                string zipPath = Path.Combine(poolDir, ZipFileName);
                string txtPath = Path.Combine(poolDir, "data.txt");

                if (File.Exists(lockPath)) try { File.Delete(lockPath); } catch { }
                if (File.Exists(zipPath)) try { File.Delete(zipPath); } catch { }
                if (File.Exists(txtPath)) try { File.Delete(txtPath); } catch { }
            }
            catch { }
        }

        // ============================================================
        // 资源路径处理
        // ============================================================

        /// <summary>
        /// 规范化所有资源路径：
        ///   · 输入：可能是绝对路径（旧数据或用户新选择）、也可能是相对路径
        ///   · 输出：保证只有【相对路径】或【空字符串】，绝不出现绝对路径
        ///   · 复制行为：将源文件复制到 poolDir/subDir/ 下（若尚未在目标位置）
        /// </summary>
        public static PersonalizationSettings NormalizeAssetPaths(
            PersonalizationSettings settings, string poolDir)
        {
            if (settings == null) return new PersonalizationSettings();
            var result = settings.Clone();
            if (string.IsNullOrEmpty(poolDir)) return result;

            if (!string.IsNullOrEmpty(result.GlobalSoundFile))
                result.GlobalSoundFile = NormalizeAssetPath(result.GlobalSoundFile, poolDir, SoundDir) ?? "";

            if (!string.IsNullOrEmpty(result.BackgroundImage))
                result.BackgroundImage = NormalizeAssetPath(result.BackgroundImage, poolDir, BackgroundDir) ?? "";

            if (result.Items != null)
            {
                foreach (var item in result.Items)
                {
                    if (item == null) continue;

                    if (!string.IsNullOrEmpty(item.AvatarFile))
                        item.AvatarFile = NormalizeAssetPath(item.AvatarFile, poolDir, AvatarDir) ?? "";

                    if (!string.IsNullOrEmpty(item.IllustrationFile))
                        item.IllustrationFile = NormalizeAssetPath(item.IllustrationFile, poolDir, IllustrationDir) ?? "";

                    if (!string.IsNullOrEmpty(item.SoundFile))
                        item.SoundFile = NormalizeAssetPath(item.SoundFile, poolDir, SoundDir) ?? "";
                }
            }
            return result;
        }

        /// <summary>
        /// 展开所有资源路径：relative → absolute（供磁盘加载使用）。
        /// ★ 兼容旧数据：若绝对路径文件不存在，尝试在 poolDir 下同名文件回退查找。
        /// </summary>
        public static PersonalizationSettings ExpandAssetPaths(
            PersonalizationSettings settings, string poolDir)
        {
            if (settings == null) return new PersonalizationSettings();
            var result = settings.Clone();
            if (string.IsNullOrEmpty(poolDir)) return result;

            result.GlobalSoundFile = MakeAbsolute(poolDir, result.GlobalSoundFile);
            result.BackgroundImage = MakeAbsolute(poolDir, result.BackgroundImage);

            if (result.Items != null)
            {
                foreach (var item in result.Items)
                {
                    if (item == null) continue;
                    item.AvatarFile = MakeAbsolute(poolDir, item.AvatarFile);
                    item.IllustrationFile = MakeAbsolute(poolDir, item.IllustrationFile);
                    item.SoundFile = MakeAbsolute(poolDir, item.SoundFile);
                }
            }
            return result;
        }

        // ============================================================
        // 内部工具
        // ============================================================

        private static string MakeAbsolute(string poolDir, string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                if (Path.IsPathRooted(path))
                {
                    if (File.Exists(path)) return path;

                    // ★ 旧数据兼容：绝对路径失效时，尝试在卡池目录下同名文件回退查找
                    if (!string.IsNullOrEmpty(poolDir))
                    {
                        string fileName = Path.GetFileName(path);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            string[] subDirs = { IllustrationDir, AvatarDir, SoundDir, BackgroundDir, "" };
                            foreach (var sub in subDirs)
                            {
                                string candidate = string.IsNullOrEmpty(sub)
                                    ? Path.Combine(poolDir, fileName)
                                    : Path.Combine(poolDir, sub, fileName);
                                if (File.Exists(candidate)) return candidate;
                            }
                        }
                    }
                    return path;
                }

                return Path.GetFullPath(Path.Combine(poolDir, path));
            }
            catch { return path; }
        }

        /// <summary>
        /// 将源路径规范化到 poolDir/subDir/ 下，返回 subDir/xxx 形式的相对路径。
        /// 返回 null 表示无法处理，调用方应清空该字段（绝不保留绝对路径）。
        /// </summary>
        private static string NormalizeAssetPath(string srcFile, string poolDir, string subDir)
        {
            if (string.IsNullOrEmpty(srcFile)) return null;
            if (string.IsNullOrEmpty(poolDir)) return null;

            string fileName = Path.GetFileName(srcFile);
            if (string.IsNullOrEmpty(fileName)) return null;

            try
            {
                string targetDir = Path.Combine(poolDir, subDir);
                string targetFile = Path.Combine(targetDir, fileName);
                string relPath = subDir + "/" + fileName;

                // A. 绝对路径
                if (Path.IsPathRooted(srcFile))
                {
                    if (File.Exists(srcFile))
                    {
                        if (string.Equals(Path.GetFullPath(srcFile), Path.GetFullPath(targetFile),
                                StringComparison.OrdinalIgnoreCase))
                            return relPath;

                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                        File.Copy(srcFile, targetFile, true);
                        return relPath;
                    }

                    // ★ 绝对路径失效（例如卡池重命名后），但目标位置已有同名文件
                    if (File.Exists(targetFile))
                        return relPath;

                    // 源和目标都不存在 → 返回 null，调用方清空该字段
                    return null;
                }

                // B. 相对路径
                string srcFull = Path.GetFullPath(Path.Combine(poolDir, srcFile));
                if (File.Exists(srcFull))
                {
                    if (string.Equals(srcFull, Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                        return relPath;

                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                    try { File.Copy(srcFull, targetFile, true); return relPath; } catch { }
                }

                if (File.Exists(targetFile))
                    return relPath;

                return null;
            }
            catch { return null; }
        }
    }
}