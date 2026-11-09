using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RandomDrawer
{
    /// <summary>卡池文件的批量操作（带进度回报），供主界面与选项页共用。</summary>
    public static class PoolFileOps
    {
        public static void DeleteWithProgress(string dir, Action<int, int, string> report)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var files = new List<string>();
            var subDirs = new List<string>();

            void Collect(string d)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(d)) files.Add(f);
                    foreach (var s in Directory.GetDirectories(d)) { subDirs.Add(s); Collect(s); }
                }
                catch { }
            }

            Collect(dir);

            int total = files.Count + subDirs.Count + 1;
            int done = 0;
            report?.Invoke(0, total, "正在删除…");

            foreach (var f in files)
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch { }

                done++;
                if (done % 8 == 0) report?.Invoke(done, total, "正在删除文件…");
            }

            foreach (var d in subDirs.OrderByDescending(x => x.Length))
            {
                try { Directory.Delete(d, true); } catch { }
                done++;
                if (done % 4 == 0) report?.Invoke(done, total, "正在删除文件夹…");
            }

            try { Directory.Delete(dir, true); } catch { }
            report?.Invoke(total, total, "删除完成");
        }

        public static void CopyWithProgress(string srcDir, string destDir, Action<int, int, string> report)
        {
            if (string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir)) return;

            var files = new List<string>();

            void Collect(string d)
            {
                foreach (var f in Directory.GetFiles(d)) files.Add(f);
                foreach (var s in Directory.GetDirectories(d)) Collect(s);
            }

            Collect(srcDir);

            int total = Math.Max(1, files.Count);
            int done = 0;
            report?.Invoke(0, total, "正在复制…");

            foreach (var src in files)
            {
                string rel = Path.GetRelativePath(srcDir, src);
                string target = Path.Combine(destDir, rel);
                string targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                File.Copy(src, target, true);
                done++;
                if (done % 8 == 0) report?.Invoke(done, total, "正在复制文件…");
            }

            report?.Invoke(total, total, "复制完成");
        }
    }
}
