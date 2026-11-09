using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 背景图模糊：模仿 Windows 登录界面那种「很柔、很均匀」的高斯模糊。
    /// 做法是先把图缩小再模糊（半径按比例缩），边缘用九宫格平铺补齐，最后裁出中心区域，
    /// 这样既快、边缘也不会有透明渐隐，图片也不会因为模糊半径变大而被放大。
    /// </summary>
    public static class ImageBlurUtil
    {
        /// <summary>模糊强度上限（滑块最大值）。</summary>
        public const double MaxRadius = 100;

        /// <summary>模糊前缩小到的宽度：越小越快，模糊本身也更均匀。</summary>
        private const double WorkWidth = 640;

        private sealed class CacheEntry
        {
            public long Ticks;
            public BitmapImage Bitmap;
        }

        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按路径取（并缓存）源图，避免拖动时每次都读盘解码。</summary>
        public static BitmapImage LoadSource(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            long ticks = 0;
            try { ticks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }

            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var hit) && hit != null && hit.Ticks == ticks && hit.Bitmap != null)
                    return hit.Bitmap;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                lock (Cache) Cache[path] = new CacheEntry { Ticks = ticks, Bitmap = bmp };
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>
        /// 返回模糊后的位图；radius ≤ 0 或失败时原样返回。
        /// </summary>
        public static BitmapSource MakeBlurred(BitmapSource source, double radius) =>
            MakeBlurred(source, radius, WorkWidth);

        /// <summary>
        /// 可指定工作宽度的版本：拖动滑块时用更小的宽度（256）做实时预览，松手后用 640 出最终质量。
        /// </summary>
        public static BitmapSource MakeBlurred(BitmapSource source, double radius, double workWidth)
        {
            if (source == null || radius <= 0.5) return source;
            if (workWidth <= 0) workWidth = WorkWidth;

            try
            {
                double srcW = Math.Max(1, source.PixelWidth);
                double srcH = Math.Max(1, source.PixelHeight);

                // ① 缩到工作尺寸：模糊半径按同一比例缩，视觉一致但快得多
                double scale = Math.Min(1.0, workWidth / srcW);
                int w = Math.Max(8, (int)Math.Round(srcW * scale));
                int h = Math.Max(8, (int)Math.Round(srcH * scale));

                double blurPx = Math.Max(1.0, radius * scale);

                // ② 九宫格平铺：让模糊采样到「边缘之外」的真实像素，裁出来的中心区没有透明边、也不会被放大
                int pad = (int)Math.Ceiling(blurPx * 1.6) + 2;
                int bigW = w + pad * 2;
                int bigH = h + pad * 2;

                var tiled = new DrawingVisual();
                using (var dc = tiled.RenderOpen())
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            dc.DrawImage(source, new Rect(pad + ox * w, pad + oy * h, w, h));
                        }
                    }
                }

                var bigBmp = new RenderTargetBitmap(bigW, bigH, 96, 96, PixelFormats.Pbgra32);
                bigBmp.Render(tiled);
                bigBmp.Freeze();

                // ③ 对放大的整块做高斯模糊，再裁出中心 w×h
                var image = new Image
                {
                    Source = bigBmp,
                    Stretch = Stretch.Fill,
                    Width = bigW,
                    Height = bigH,
                    Effect = new BlurEffect
                    {
                        Radius = blurPx,
                        KernelType = KernelType.Gaussian,
                        RenderingBias = RenderingBias.Performance
                    }
                };

                var host = new Grid { Width = w, Height = h, ClipToBounds = true };
                image.Margin = new Thickness(-pad);
                host.Children.Add(image);

                host.Measure(new Size(w, h));
                host.Arrange(new Rect(0, 0, w, h));
                host.UpdateLayout();

                var result = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                result.Render(host);
                result.Freeze();
                return result;
            }
            catch
            {
                return source;
            }
        }
    }
}
