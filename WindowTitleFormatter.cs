using System;
using System.Collections.Generic;
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

        /// <summary>任意 {…}（用于「分享页眉里去掉全部关键词」）。</summary>
        private static readonly Regex AnyTokenRegex = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);

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

        /// <summary>
        /// 去掉模板里的全部 {…} 关键词（不管认不认识都去掉），并把多余空白收成一个空格。
        /// 分享长图 / PDF 的左上角标注用这个结果：那里是静态文字，不能出现没被替换掉的 {time} 外壳。
        /// </summary>
        public static string StripTokens(string format)
        {
            if (string.IsNullOrWhiteSpace(format)) return "";

            string text = AnyTokenRegex.Replace(format, " ");
            text = text.Replace("\r", " ").Replace("\n", " ");
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Trim();
        }

        /// <summary>
        /// 分享长图 / PDF 页眉里的应用名：优先用用户自定义的窗口标题（关键词已去掉）；
        /// 留空、或去掉关键词后什么都不剩时，回退成「随机抽取器 版本号」。
        /// </summary>
        public static string GetShareHeaderAppText(string version)
        {
            string custom = StripTokens(App.WindowTitleFormat);
            if (!string.IsNullOrEmpty(custom)) return custom;

            return string.IsNullOrWhiteSpace(version) ? DefaultTitle : $"{DefaultTitle} {version}";
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

    /// <summary>
    /// 窗口标题的输入校验。
    ///
    /// 标题会原样写进 settings.ini 的 <c>WindowTitle=</c> 这一行，也会画到标题栏 / 分享长图页眉上，
    /// 所以只拦「会导致系统错误」的字符：
    ///   · C0 控制字符（回车、换行、制表符、NUL…）——写进 ini 会把一行拆成两行，配置文件直接读坏；
    ///   · DEL（U+007F）——不可见，属于同一类。
    /// 其它符号（中文、空格、`|` `/` `:` `{}` 等）都允许，不做过度限制。
    /// </summary>
    public static class WindowTitleValidator
    {
        /// <summary>该字符是否属于「会导致错误」的不可见控制字符。</summary>
        public static bool IsForbidden(char c) => c < 0x20 || c == 0x7F;

        /// <summary>输入是否合法（不含控制字符）。</summary>
        public static bool IsValid(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            foreach (char c in text)
                if (IsForbidden(c)) return false;
            return true;
        }

        /// <summary>把输入里违规的字符列成人话，例如「回车、制表符」；没有违规返回空串。</summary>
        public static string DescribeInvalid(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var names = new List<string>();
            foreach (char c in text)
            {
                if (!IsForbidden(c)) continue;

                string name;
                switch (c)
                {
                    case '\r': name = "回车"; break;
                    case '\n': name = "换行"; break;
                    case '\t': name = "制表符"; break;
                    case '\0': name = "空字符"; break;
                    default: name = $"控制字符 U+{(int)c:X4}"; break;
                }
                if (!names.Contains(name)) names.Add(name);
            }
            return string.Join("、", names);
        }
    }
}
