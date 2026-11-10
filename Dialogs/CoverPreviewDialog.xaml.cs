using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>切换封面前的预览窗口：支持图片 / GIF / 视频。</summary>
    public partial class CoverPreviewDialog : FloatingDialogBase
    {
        private static readonly string[] VideoExts =
        {
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm"
        };

        private GifPlayer _gifPlayer;
        private readonly string _path;

        public CoverPreviewDialog(string filePath, bool roundCover = false)
        {
            _path = filePath;
            InitializeComponent();

            // 「圆角封面」是卡池自己的设置：预览也跟着裁成 12px 圆角，所见即所得
            try { RoundCorner.SetEnabled(PreviewClipHost, roundCover); } catch { }

            TxtFileName.Text = string.IsNullOrEmpty(filePath) ? "" : Path.GetFileName(filePath);

            Unloaded += (s, e) => StopPlayback();

            try { Load(); }
            catch
            {
                TxtUnsupported.Visibility = Visibility.Visible;
            }
        }

        private void Load()
        {
            string path = _path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                TxtUnsupported.Visibility = Visibility.Visible;
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (Array.IndexOf(VideoExts, ext) >= 0)
            {
                PreviewVideo.Source = new Uri(path, UriKind.Absolute);
                PreviewVideo.Visibility = Visibility.Visible;
                PreviewVideo.MediaEnded += (s, e) =>
                {
                    try
                    {
                        PreviewVideo.Position = TimeSpan.Zero;
                        PreviewVideo.Play();
                    }
                    catch { }
                };
                PreviewVideo.Play();
                return;
            }

            if (ext == ".gif")
            {
                _gifPlayer = new GifPlayer(path);
                if (_gifPlayer.HasFrames)
                {
                    PreviewShape.Fill = _gifPlayer.Brush;
                    return;
                }
            }

            var bmp = LoadBitmap(path);
            if (bmp != null)
            {
                PreviewShape.Fill = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
                return;
            }

            TxtUnsupported.Visibility = Visibility.Visible;
        }

        private static BitmapImage LoadBitmap(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void StopPlayback()
        {
            try { _gifPlayer?.Stop(); } catch { }
            try { PreviewVideo.Stop(); } catch { }
            try { PreviewVideo.Source = null; } catch { }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            try { DialogResult = true; } catch { }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            try { DialogResult = false; } catch { }
        }
    }
}
