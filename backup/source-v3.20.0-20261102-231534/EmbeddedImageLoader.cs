using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 内嵌图片（默认封面 Assets/default_cover.jpg、AI 说明图 Assets/dsh-code.jpg）的加载器。
    ///
    /// 旧实现每调用一次就打开 *.g.resources 并把整张资源表线性扫一遍，
    /// 卡池没有封面时每次切换卡池都要扫一次，明显卡顿。
    /// 这里改成：资源表只枚举一次并建索引，解码结果按资源名缓存，找不到的也记下来。
    /// </summary>
    public static class EmbeddedImageLoader
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BitmapImage> Cache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Miss =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>资源键（小写、不含扩展名的相对路径）→ 原始字节。首次调用时建立。</summary>
        private static Dictionary<string, byte[]> _index;

        /// <summary>
        /// 按「文件名片段」加载内嵌图片，找不到返回 null。
        /// 例：Load("Assets/default_cover.jpg") / Load("Assets/dsh-code.jpg")。
        /// </summary>
        public static BitmapImage Load(string resourcePath)
        {
            if (string.IsNullOrEmpty(resourcePath)) return null;
            string key = resourcePath;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                if (Miss.Contains(key)) return null;
            }

            try
            {
                var index = EnsureIndex();
                if (index == null) return null;

                string needle = Path.GetFileNameWithoutExtension(resourcePath).ToLowerInvariant();
                byte[] bytes = null;

                // 先精确匹配（键里包含文件名），再退化为子串匹配
                foreach (var kv in index)
                {
                    if (kv.Key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        bytes = kv.Value;
                        break;
                    }
                }

                if (bytes == null)
                {
                    lock (Gate) { Miss.Add(key); }
                    return null;
                }

                var bitmap = Decode(bytes);
                if (bitmap == null) return null;

                lock (Gate) { Cache[key] = bitmap; }
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, byte[]> EnsureIndex()
        {
            lock (Gate)
            {
                if (_index != null) return _index;

                var asm = Assembly.GetExecutingAssembly();
                string gresName = asm.GetName().Name + ".g.resources";
                using (var stream = asm.GetManifestResourceStream(gresName))
                {
                    if (stream == null) return null;

                    var index = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = new ResourceReader(stream))
                    {
                        IDictionaryEnumerator enumerator = reader.GetEnumerator();
                        while (enumerator.MoveNext())
                        {
                            string key = enumerator.Key as string;
                            if (string.IsNullOrEmpty(key)) continue;

                            byte[] bytes = null;
                            if (enumerator.Value is byte[] b) bytes = b;
                            else if (enumerator.Value is Stream s)
                            {
                                using (var ms = new MemoryStream())
                                {
                                    s.CopyTo(ms);
                                    bytes = ms.ToArray();
                                }
                            }

                            if (bytes != null) index[key] = bytes;
                        }
                    }

                    _index = index;
                    return _index;
                }
            }
        }

        private static BitmapImage Decode(byte[] bytes)
        {
            try
            {
                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch { return null; }
        }
    }
}
