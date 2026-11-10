using System;
using System.IO;

namespace RandomDrawer
{
    /// <summary>
    /// 每个卡池独立的界面开关（不重复抽取 / 抽卡动画）。
    /// 保存在卡池目录下的 ui_state.ini，随卡池一起复制、移动、导出。
    /// </summary>
    public static class PoolUiState
    {
        public const string FileName = "ui_state.ini";

        private static string StateFile(string poolDir)
        {
            if (string.IsNullOrEmpty(poolDir) || !Directory.Exists(poolDir)) return "";
            return Path.Combine(poolDir, FileName);
        }

        public static bool GetNoRepeat(string poolDir, bool fallback)
        {
            try
            {
                string file = StateFile(poolDir);
                if (string.IsNullOrEmpty(file)) return fallback;
                return IniHelper.ReadValue("UI", "NoRepeat", fallback ? "1" : "0", file) == "1";
            }
            catch { return fallback; }
        }

        public static void SetNoRepeat(string poolDir, bool value)
        {
            try
            {
                string file = StateFile(poolDir);
                if (string.IsNullOrEmpty(file)) return;
                IniHelper.WriteValue("UI", "NoRepeat", value ? "1" : "0", file);
            }
            catch { }
        }

        public static bool GetDrawAnimation(string poolDir, bool fallback)
        {
            try
            {
                string file = StateFile(poolDir);
                if (string.IsNullOrEmpty(file)) return fallback;
                return IniHelper.ReadValue("UI", "DrawAnimation", fallback ? "1" : "0", file) == "1";
            }
            catch { return fallback; }
        }

        public static void SetDrawAnimation(string poolDir, bool value)
        {
            try
            {
                string file = StateFile(poolDir);
                if (string.IsNullOrEmpty(file)) return;
                IniHelper.WriteValue("UI", "DrawAnimation", value ? "1" : "0", file);
            }
            catch { }
        }
    }
}
