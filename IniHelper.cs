using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RandomDrawer
{
    /// <summary>
    /// 纯 C# 实现的 INI 读写（UTF-8 编码，支持中文路径和值）
    /// </summary>
    public static class IniHelper
    {
        /// <summary>最近一次写入失败的原因（为空表示成功）。</summary>
        public static string LastError = "";

        public static string ReadValue(string section, string key, string defaultValue, string filePath)
        {
            if (!File.Exists(filePath)) return defaultValue;

            try
            {
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                string currentSection = "";
                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith(";") || line.StartsWith("#")) continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    int eqIndex = line.IndexOf('=');
                    if (eqIndex < 0) continue;

                    string k = line.Substring(0, eqIndex).Trim();
                    string v = line.Substring(eqIndex + 1).Trim();

                    if (currentSection.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                        k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }
                }
            }
            catch { }
            return defaultValue;
        }

        /// <summary>
        /// 读取布尔配置：兼容旧版本的 1 / 0，也接受 true / false、yes / no、on / off。
        /// 新版本统一写成 true / false（配置文件里更直观）。
        /// </summary>
        public static bool ReadBool(string section, string key, bool defaultValue, string filePath)
        {
            string raw = (ReadValue(section, key, "", filePath) ?? "").Trim();
            if (raw.Length == 0) return defaultValue;

            if (raw == "1"
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase))
                return true;

            if (raw == "0"
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("no", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("off", StringComparison.OrdinalIgnoreCase))
                return false;

            return defaultValue;
        }

        /// <summary>写入布尔配置：统一写 true / false。</summary>
        public static void WriteBool(string section, string key, bool value, string filePath)
        {
            WriteValue(section, key, value ? "true" : "false", filePath);
        }

        public static void WriteValue(string section, string key, string value, string filePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                List<string> lines = new List<string>();
                if (File.Exists(filePath))
                {
                    try { lines.AddRange(File.ReadAllLines(filePath, Encoding.UTF8)); }
                    catch { }
                }

                string currentSection = "";
                bool sectionFound = false;
                bool keyUpdated = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        // 即将离开目标 section
                        if (sectionFound && !keyUpdated)
                        {
                            lines.Insert(i, $"{key}={value}");
                            keyUpdated = true;
                            break;
                        }
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        if (currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
                            sectionFound = true;
                        continue;
                    }

                    if (currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
                    {
                        int eqIndex = line.IndexOf('=');
                        if (eqIndex >= 0)
                        {
                            string k = line.Substring(0, eqIndex).Trim();
                            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                            {
                                lines[i] = $"{key}={value}";
                                keyUpdated = true;
                                break;
                            }
                        }
                    }
                }

                if (!keyUpdated)
                {
                    if (!sectionFound)
                    {
                        if (lines.Count > 0 && !string.IsNullOrEmpty(lines[lines.Count - 1].Trim()))
                            lines.Add("");
                        lines.Add($"[{section}]");
                    }
                    lines.Add($"{key}={value}");
                }

                // ★ 先清掉只读属性，避免写入被静默拒绝


                try


                {


                    if (File.Exists(filePath))


                    {


                        var attr = File.GetAttributes(filePath);


                        if ((attr & FileAttributes.ReadOnly) != 0)


                            File.SetAttributes(filePath, attr & ~FileAttributes.ReadOnly);


                    }


                }


                catch { }



                // ★ 先写临时文件再替换：避免写到一半中断导致配置损坏


                string tmpPath = filePath + ".tmp";


                try


                {


                    File.WriteAllLines(tmpPath, lines, new UTF8Encoding(false));


                    if (File.Exists(filePath)) File.Replace(tmpPath, filePath, null);


                    else File.Move(tmpPath, filePath);


                }


                catch


                {


                    try { File.WriteAllLines(filePath, lines, new UTF8Encoding(false)); } catch { }


                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }


                }
            }
            catch (Exception ex) { LastError = ex.Message; }
        }
    }
}
