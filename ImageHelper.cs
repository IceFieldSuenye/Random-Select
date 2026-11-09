using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 统一图片 / GIF 加载工具。
    ///
    /// 设计要点：
    ///   1. 用 File.ReadAllBytes 一次性读完并释放文件句柄（不锁文件）；
    ///   2. 用 MemoryStream 作为解码源（绕过 WPF 的 URI 缓存系统）；
    ///   3. 图片统一用 BitmapDecoder.Create 解码，与 GifBitmapDecoder 同源，行为一致且可靠；
    ///   4. 绝不用 BitmapImage + StreamSource + IgnoreImageCache 这个组合 —— 它在某些格式下会静默失败。
    /// </summary>
    public static class ImageHelper
    {
        /// <summary>
        /// 从文件安全加载单帧位图（png / jpg / jpeg / gif / bmp / webp / tiff / tif / ico / jfif）。
        /// 返回 BitmapSource（可直接赋给 Image.Source 或 ImageBrush.ImageSource）。
        /// 加载失败返回 null。
        /// </summary>
        public static BitmapSource LoadBitmapFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                // 一次性读完文件，立即释放文件句柄（不锁文件）
                byte[] fileBytes = File.ReadAllBytes(filePath);

                using (var ms = new MemoryStream(fileBytes))
                {
                    // BitmapDecoder.Create 与 GifBitmapDecoder 同源，兼容性好、行为可靠
                    var decoder = BitmapDecoder.Create(
                        ms,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);

                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        if (frame.CanFreeze) frame.Freeze();
                        return frame;
                    }
                }
            }
            catch
            {
                // 静默失败，由调用方决定回退到默认封面
            }
            return null;
        }

        /// <summary>
        /// 按「目标显示宽度」解码（DecodePixelWidth）：解码器直接按目标尺寸出图，
        /// 不再先解出全尺寸位图再缩放，JPEG / PNG 的内存与耗时都能降一个数量级。
        /// 返回值已 Freeze()，可跨线程安全共享；decodePixelWidth ≤ 0 时按原始尺寸解码。
        /// 这个方法不碰 UI 元素，可以在后台线程调用。
        /// </summary>
        public static BitmapSource LoadBitmapSized(string filePath, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            // ① 首选：BitmapImage + DecodePixelWidth（真正的「按目标尺寸解码」）
            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                using (var ms = new MemoryStream(fileBytes))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
            }
            catch (Exception)
            {
                // 落到下面的通用解码
            }

            // ② 回退：通用解码后再缩放（少数格式不支持 DecodePixelWidth）
            try
            {
                var full = LoadBitmapFromFile(filePath);
                if (full == null) return null;
                if (decodePixelWidth <= 0 || full.PixelWidth <= decodePixelWidth) return full;

                double ratio = (double)decodePixelWidth / full.PixelWidth;
                var scaled = new TransformedBitmap(full, new System.Windows.Media.ScaleTransform(ratio, ratio));
                if (scaled.CanFreeze) scaled.Freeze();
                return scaled;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在后台线程按目标宽度解码（调用方 await 结果后直接赋给 Image.Source / ImageBrush）。
        /// 位图已 Freeze()，跨线程赋值不需要任何同步开销。
        /// </summary>
        public static System.Threading.Tasks.Task<BitmapSource> LoadBitmapSizedAsync(string filePath, int decodePixelWidth)
        {
            return System.Threading.Tasks.Task.Run(() => LoadBitmapSized(filePath, decodePixelWidth));
        }

        /// <summary>
        /// 从文件安全加载 GIF 解码器（用于多帧播放）。
        /// 加载失败返回 null。
        /// </summary>
        public static GifBitmapDecoder LoadGifFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                return new GifBitmapDecoder(
                    new MemoryStream(fileBytes),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
            }
            catch
            {
                return null;
            }
        }
    }
}