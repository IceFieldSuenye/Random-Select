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

        public static void SavePasswordHash(string poolName, string password)
        {
            string hash = ComputeSha256(password);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegPath))
            {
                if (key != null) key.SetValue(poolName, hash);
            }
        }

        public static bool VerifyPassword(string poolName, string password)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath))
            {
                if (key == null) return false;
                var savedHash = key.GetValue(poolName) as string;
                return savedHash == ComputeSha256(password);
            }
        }

        public static void DeletePasswordHash(string poolName)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath, true))
            {
                if (key != null) key.DeleteValue(poolName, false);
            }
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