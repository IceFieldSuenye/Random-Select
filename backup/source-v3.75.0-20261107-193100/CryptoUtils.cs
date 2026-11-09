using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RandomDrawer
{
    public static class CryptoUtils
    {
        private static readonly string RegPath = @"Software\RandomSelect\Pools";
        private static readonly string InternalKey = "RandomDrawer_Internal_Secret_Key_2024";

        // 1. 自动识别编码读取文本
        public static string ReadAllTextWithEncoding(string filePath)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                UTF8Encoding utf8 = new UTF8Encoding(false, true);
                return utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding("GBK").GetString(bytes);
            }
        }

        // 2. SHA-256 哈希
        public static string ComputeSha256(string rawData)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                foreach (byte b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        /// <summary>
        /// 保存密码哈希。返回是否写入成功 —— 注册表被策略限制时不抛异常，
        /// 由调用方决定要不要提示（否则会变成未处理异常）。
        /// </summary>
        public static bool SavePasswordHash(string poolName, string password)
        {
            if (string.IsNullOrEmpty(poolName)) return false;
            try
            {
                string hash = ComputeSha256(password);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath))
                {
                    if (key == null) return false;
                    key.SetValue(poolName, hash);
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool VerifyPassword(string poolName, string password)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    if (key == null)
                    {
                        RuntimeLog.Warn("密码验证：卡池=" + poolName + "，本机没有密码记录");
                        return false;
                    }
                    var savedHash = key.GetValue(poolName) as string;
                    bool ok = savedHash == ComputeSha256(password);
                    // 只记结果，绝不把密码本身写进日志
                    RuntimeLog.Info("密码验证：卡池=" + poolName + "，结果=" + (ok ? "通过" : "失败"));
                    return ok;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("密码验证异常：卡池=" + poolName, ex);
                return false;
            }
        }

        /// <summary>
        /// 本机是否存有这个卡池的密码记录。
        /// 从其它电脑导入的加密卡池通常没有（哈希存在 HKCU，不随卡池文件夹走），
        /// 这类卡池无法用密码验证，删除时需要走「明确警告」而不是密码框。
        /// </summary>
        public static bool HasPasswordHash(string poolName)
        {
            if (string.IsNullOrEmpty(poolName)) return false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath))
                {
                    var saved = key?.GetValue(poolName) as string;
                    return !string.IsNullOrEmpty(saved);
                }
            }
            catch { return false; }
        }

        public static bool DeletePasswordHash(string poolName)
        {
            if (string.IsNullOrEmpty(poolName)) return false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (key == null) return false;
                    key.DeleteValue(poolName, false);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>卡池重命名时迁移密码哈希，避免密码在新名称下失效。</summary>
        public static void MovePasswordHash(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) ||
                string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (key == null) return;
                    if (key.GetValue(oldName) is string hash)
                    {
                        key.SetValue(newName, hash);
                        key.DeleteValue(oldName, false);
                    }
                }
            }
            catch { }
        }

        // 3. 内部密钥 AES 加密/解密
        public static string EncryptString(string plainText)
        {
            byte[] iv = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);
            using (Aes aes = Aes.Create())
            {
                aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(InternalKey));
                aes.IV = iv;
                using (MemoryStream ms = new MemoryStream())
                {
                    ms.Write(iv, 0, iv.Length);
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    using (StreamWriter sw = new StreamWriter(cs)) sw.Write(plainText);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        public static string DecryptString(string cipherText)
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            byte[] iv = new byte[16];
            Array.Copy(cipherBytes, 0, iv, 0, 16);
            using (Aes aes = Aes.Create())
            {
                aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(InternalKey));
                aes.IV = iv;
                using (MemoryStream ms = new MemoryStream(cipherBytes, 16, cipherBytes.Length - 16))
                using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (StreamReader sr = new StreamReader(cs)) return sr.ReadToEnd();
            }
        }
    }
}