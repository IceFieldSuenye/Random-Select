using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RandomDrawer
{
    /// <summary>
    /// 窗口标题模板：把「选项 → 常规 → 外观 → 窗口标题」里填的格式串展开成真正的标题。
    ///
    /// 支持的关键词（大小写不敏感）：
    ///   {version} 软件版本号
    ///   {time}    当前时间 HH:mm:ss
    ///   {date}    当前日期 年/月/日（如 2026/11/2）
    ///   {pool}    当前选中的卡池名称（只在主界面生效，其它页面替换为空）
    ///   {page}    当前页面标题
    ///
    /// 模板留空 / 全空白 / 展开后为空时，回到默认标题「随机抽取器」。
    /// 纯静态函数，冒烟测试可以直接断言，不用起窗口。
    /// </summary>
    public static class WindowTitleFormatter
    {
        /// <summary>没有填模板时使用的默认窗口标题。</summary>
        public const string DefaultTitle = "随机抽取器";

        private static readonly Regex TokenRegex = new Regex(
            @"\{(version|time|date|pool|page)\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 展开模板。参数都能显式传入，方便测试固定时间与时区无关的断言。
        /// </summary>
        public static string Format(string format, string version, string pageTitle,
            string poolName, bool isMainPage, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(format)) return DefaultTitle;

            string result = TokenRegex.Replace(format, match =>
            {
                switch (match.Groups[1].Value.ToLowerInvariant())
                {
                    case "version": return version ?? "";
                    case "time": return now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    case "date": return now.ToString("yyyy/M/d", CultureInfo.InvariantCulture);
                    case "pool": return isMainPage ? (poolName ?? "") : "";
                    case "page": return pageTitle ?? "";
                    default: return match.Value;
                }
            });

            // 标题栏只显示一行：把换行折成空格，并去掉首尾空白
            result = result.Replace("\r", " ").Replace("\n", " ").Trim();
            return string.IsNullOrEmpty(result) ? DefaultTitle : result;
        }

        /// <summary>模板里是否含 {time}（只有含它才需要每秒刷新标题）。</summary>
        public static bool NeedsClock(string format)
        {
            if (string.IsNullOrWhiteSpace(format)) return false;
            return format.IndexOf("{time}", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>程序版本号（3.x.y 形式），与「抽取报告长图」页眉用的是同一套规则。</summary>
        public static string GetAppVersionText()
        {
            try
            {
                var asm = typeof(WindowTitleFormatter).Assembly;

                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info.IndexOf('+');
                    if (plus >= 0) info = info.Substring(0, plus);
                    if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
                }

                var v = asm.GetName().Version;
                if (v != null)
                {
                    if (v.Revision > 0) return v.ToString();
                    return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
                }
            }
            catch { }
            return "";
        }
    }
}
