using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池文本规则。
    /// 卡池数据（data.txt / data.lock）以空格、Tab、全角空格分隔列，
    /// 历史记录（history.txt）以「|」和 Tab 分隔字段，
    /// 所以这些符号如果出现在名称或数值里，表格和历史记录就会错位、甚至整行显示异常。
    /// </summary>
    internal static class PoolTextRules
    {
        /// <summary>卡池数据与历史记录中不可用的符号。</summary>
        public static readonly char[] IllegalChars = { '|', '\t', '\r', '\n', ' ', '\u3000' };

        /// <summary>用一句话列出所有不可用符号（用于提示框）。</summary>
        public static string IllegalCharsText =>
            "|（竖线）、Tab（制表符）、空格（半角）、全角空格、换行（回车）";

        /// <summary>Windows 文件名里不可用的符号（用于卡池名称提示）。</summary>
        public static string InvalidFileNameCharsText => "\\  /  :  *  ?  \"  <  >  |";

        /// <summary>返回第一个不可用符号；没有则返回 '\0'。</summary>
        public static char FirstIllegalChar(string text)
        {
            if (string.IsNullOrEmpty(text)) return '\0';
            foreach (char c in text)
            {
                if (Array.IndexOf(IllegalChars, c) >= 0) return c;
            }
            return '\0';
        }

        /// <summary>单个符号的中文说明。</summary>
        public static string Describe(char c)
        {
            switch (c)
            {
                case '|': return "|（竖线）";
                case '\t': return "Tab（制表符）";
                case '\r': return "回车";
                case '\n': return "换行";
                case ' ': return "半角空格";
                case '\u3000': return "全角空格";
                default: return c.ToString();
            }
        }

        /// <summary>把若干符号合并成「|（竖线）、半角空格」这样的文本。</summary>
        public static string DescribeList(IEnumerable<char> chars)
        {
            if (chars == null) return "";
            var list = chars.Distinct()
                             .OrderBy(c => Array.IndexOf(IllegalChars, c))
                             .Select(Describe);
            return string.Join("、", list);
        }

        /// <summary>名称里是否含有 Windows 不允许的字符。</summary>
        public static bool HasInvalidFileNameChar(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
        }

        /// <summary>
        /// 检查整张数据表是否含有不可用符号。
        /// 返回 true 表示内容合法；返回 false 时 report 为提示文本（只说明符号，不逐条列举）。
        /// </summary>
        public static bool TryBuildIssueReport(System.Data.DataTable dt, out string report)
        {
            report = "";
            if (dt == null || dt.Columns.Count == 0) return true;

            foreach (System.Data.DataColumn column in dt.Columns)
            {
                if (FirstIllegalChar(column.ColumnName) != '\0')
                {
                    report = BuildIssueReport();
                    return false;
                }
            }

            foreach (System.Data.DataRow row in dt.Rows)
            {
                foreach (object cell in row.ItemArray)
                {
                    if (FirstIllegalChar(cell?.ToString() ?? "") != '\0')
                    {
                        report = BuildIssueReport();
                        return false;
                    }
                }
            }

            return true;
        }

        public static string BuildIssueReport() =>
            "内容包含非法符号，请修改后再保存。\n\n"
            + "以下符号不能使用：\n"
            + IllegalCharsText;
    }
}
