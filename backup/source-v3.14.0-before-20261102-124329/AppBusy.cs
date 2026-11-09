using System;
using System.Collections.Generic;

namespace RandomDrawer
{
    /// <summary>
    /// 「正在忙」标记：抽取、渲染结果、生成长图期间禁止关闭程序。
    /// 用「原因集合」而不是计数器，重复进入 / 重复退出都不会把计数搞乱。
    /// </summary>
    internal static class AppBusy
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<string> Reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否有任何耗时操作正在进行。</summary>
        public static bool IsBusy
        {
            get { lock (Sync) return Reasons.Count > 0; }
        }

        /// <summary>当前占用的原因（日志用）。</summary>
        public static string CurrentReasons
        {
            get { lock (Sync) return string.Join(",", Reasons); }
        }

        public static void Enter(string reason)
        {
            try
            {
                lock (Sync) Reasons.Add(string.IsNullOrEmpty(reason) ? "busy" : reason);
            }
            catch { }
        }

        public static void Exit(string reason)
        {
            try
            {
                lock (Sync) Reasons.Remove(string.IsNullOrEmpty(reason) ? "busy" : reason);
            }
            catch { }
        }

        public static void Clear()
        {
            try
            {
                lock (Sync) Reasons.Clear();
            }
            catch { }
        }
    }
}
