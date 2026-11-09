using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池目录监视：通过定时比对「磁盘上的卡池清单」与内存中的清单，
    /// 让界面能跟随外部的新增 / 删除（不占用文件夹，不影响外部操作）。
    /// </summary>
    public static class PoolWatcher
    {
        /// <summary>生成当前磁盘上卡池集合的签名，用于比较变化。</summary>
        public static string BuildSignature()
        {
            try
            {
                var parts = new List<string>();

                foreach (var dir in App.SearchDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

                    IEnumerable<string> subs;
                    try { subs = Directory.GetDirectories(dir); }
                    catch { continue; }

                    foreach (var sub in subs)
                    {
                        bool hasData = File.Exists(Path.Combine(sub, "data.txt"))
                                    || File.Exists(Path.Combine(sub, "data.lock"))
                                    || File.Exists(Path.Combine(sub, "data.zip"));

                        parts.Add(sub.ToLowerInvariant() + "|" + (hasData ? "1" : "0"));
                    }
                }

                parts.Sort(StringComparer.Ordinal);
                return string.Join(";", parts);
            }
            catch { return ""; }
        }
    }
}
