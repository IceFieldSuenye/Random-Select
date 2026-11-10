using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 把报告的分页图片写成一个多页 PDF：每页一张 JPEG（DCTDecode 直接嵌入），
    /// 不依赖任何第三方库，单文件发布也不会缺东西。
    ///
    /// ★ 3.59：改成「逐页编码 → 立刻写进文件」，不再把所有页的 JPEG 先堆在内存里。
    ///   页数多时峰值内存明显下降（每页只在当前页编码期间占一份）。
    /// </summary>
    public static class ReportPdfWriter
    {
        /// <summary>96 DPI 的像素换算成 PDF 点（1 点 = 1/72 英寸）。</summary>
        private const double PixelToPoint = 72.0 / 96.0;

        /// <summary>
        /// 保存为 PDF；成功返回 true。pages 里每张图对应 PDF 的一页（按图片像素尺寸等比换算成点）。
        /// </summary>
        public static bool Save(string path, IList<BitmapSource> pages, int jpegQuality = 92)
        {
            if (string.IsNullOrEmpty(path)) return false;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                return SaveTo(stream, pages, jpegQuality);
            }
        }

        /// <summary>写入任意可写流（文件夹不可写时由调用方换目标）。</summary>
        public static bool SaveTo(Stream output, IList<BitmapSource> pages, int jpegQuality = 92,
            Action<int, byte[]> onPageEncoded = null)
        {
            if (output == null || pages == null || pages.Count == 0) return false;

            int pageCount = pages.Count;
            foreach (var page in pages)
            {
                if (page == null) return false;
            }

            // 对象编号：1 = 目录，2 = 页树，之后每页 3 个对象（页 / 内容流 / 图片）
            var offsets = new long[2 + pageCount * 3 + 1];

            void WriteAscii(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                output.Write(bytes, 0, bytes.Length);
            }

            void BeginObject(int number)
            {
                offsets[number] = output.Position;
                WriteAscii(number + " 0 obj\n");
            }

            void EndObject() => WriteAscii("\nendobj\n");

            // 文件头（第二行是二进制标记，告诉工具这是二进制 PDF）
            WriteAscii("%PDF-1.4\n");
            output.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

            // 1：目录
            BeginObject(1);
            WriteAscii("<< /Type /Catalog /Pages 2 0 R >>");
            EndObject();

            // 2：页树（Kids 的数量是已知的，不用等每页写完）
            BeginObject(2);
            var kids = new StringBuilder();
            for (int i = 0; i < pageCount; i++) kids.Append(PageObjectNumber(i)).Append(" 0 R ");
            WriteAscii($"<< /Type /Pages /Kids [ {kids.ToString().Trim()} ] /Count {pageCount} >>");
            EndObject();

            for (int i = 0; i < pageCount; i++)
            {
                var page = pages[i];
                int pageNumber = PageObjectNumber(i);
                int contentNumber = ContentObjectNumber(i);
                int imageNumber = ImageObjectNumber(i);

                double pageW = Math.Max(1, page.PixelWidth * PixelToPoint);
                double pageH = Math.Max(1, page.PixelHeight * PixelToPoint);

                // 只编码当前这一页；写完就把 JPEG 交给调用方（缓存预览图），本页字节不再长期持有
                byte[] jpeg;
                using (var buffer = new MemoryStream())
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = jpegQuality };
                    encoder.Frames.Add(BitmapFrame.Create(
                        new FormatConvertedBitmap(page, PixelFormats.Bgr24, null, 0)));
                    encoder.Save(buffer);
                    jpeg = buffer.ToArray();
                }

                // 页对象
                BeginObject(pageNumber);
                WriteAscii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(pageW)} {Num(pageH)}] " +
                           $"/Resources << /XObject << /Im0 {imageNumber} 0 R >> >> /Contents {contentNumber} 0 R >>");
                EndObject();

                // 内容流：把整张图铺满整页
                var content = $"q\n{Num(pageW)} 0 0 {Num(pageH)} 0 0 cm\n/Im0 Do\nQ\n";
                var contentBytes = Encoding.ASCII.GetBytes(content);
                BeginObject(contentNumber);
                WriteAscii($"<< /Length {contentBytes.Length} >>\nstream\n");
                output.Write(contentBytes, 0, contentBytes.Length);
                WriteAscii("endstream");
                EndObject();

                // 图片对象（JPEG 原始字节）
                BeginObject(imageNumber);
                WriteAscii("<< /Type /XObject /Subtype /Image " +
                           $"/Width {page.PixelWidth} /Height {page.PixelHeight} " +
                           "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode " +
                           $"/Length {jpeg.Length} >>\nstream\n");
                output.Write(jpeg, 0, jpeg.Length);
                WriteAscii("\nendstream");
                EndObject();

                try { onPageEncoded?.Invoke(i, jpeg); } catch { }
            }

            // 交叉引用表
            int objectCount = 2 + pageCount * 3;
            long xrefOffset = output.Position;
            WriteAscii($"xref\n0 {objectCount + 1}\n");
            WriteAscii("0000000000 65535 f \n");
            for (int i = 1; i <= objectCount; i++)
                WriteAscii($"{offsets[i]:D10} 00000 n \n");

            WriteAscii($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
            return true;
        }

        private static int PageObjectNumber(int index) => 3 + index * 3;
        private static int ContentObjectNumber(int index) => 4 + index * 3;
        private static int ImageObjectNumber(int index) => 5 + index * 3;

        /// <summary>PDF 里的小数统一用点格式，避免受系统区域影响。</summary>
        private static string Num(double value) =>
            Math.Round(value, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
