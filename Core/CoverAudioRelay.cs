using System;
using System.IO;
using System.Windows.Media;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    /// <summary>
    /// 3.76.3：封面视频的音频改成「常驻一个播放器」——和背景音乐一样的做法：
    /// MediaPlayer 不随页面销毁，Open 一次以后一直放，页面切换只改变它在不在放，
    /// 不会再出现「切页后重新打开 → 断一下 / 从头开始」。
    ///
    /// 主页的视频画面仍然由 MediaElement 播，但画面静音（IsMuted=true），声音统一由这里出；
    /// 主页视频开始播放时把这里打开并和视频对齐进度，离开主页时视频停、这里继续放，
    /// 所以主页 → 历史记录 / 抽取报告 / 抽取结果 / 抽取动画之间是同一个播放器、同一段声音，
    /// 完全不断。回到主页时视频再按这里的进度 seek，画面和声音也对得上。
    ///
    /// 只有「视频封面音频」开启（App.CoverVideoAudio）时才出声；
    /// 打开 / 停止时把「封面音频正在播放」告诉主窗口，让背景音乐相应避让。
    /// </summary>
    public static class CoverAudioRelay
    {
        private static readonly MediaPlayer Player = new MediaPlayer();
        private static string _currentPath = "";
        private static string _currentPool = "";
        private static bool _windowActive = true;

        // 停止时记下的「放到哪了」，下次打开同一个卡池的封面音频时从这里接上
        private static string _rememberedPool = "";
        private static TimeSpan _rememberedPos = TimeSpan.Zero;
        private static TimeSpan _seekTarget = TimeSpan.Zero;

        private static readonly string[] VideoExts =
        {
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg"
        };

        static CoverAudioRelay()
        {
            Player.MediaOpened += (s, e) =>
            {
                try
                {
                    // 先把进度挪到记下的位置，再开始播：听起来就是接着上一页往下放
                    if (_seekTarget > TimeSpan.Zero)
                    {
                        var dur = Player.NaturalDuration.HasTimeSpan
                            ? Player.NaturalDuration.TimeSpan
                            : TimeSpan.Zero;
                        if (dur > TimeSpan.Zero && _seekTarget < dur - TimeSpan.FromMilliseconds(250))
                            Player.Position = _seekTarget;
                    }
                    _seekTarget = TimeSpan.Zero;
                    Player.Play();
                    ApplyMute();
                    MessageBus.Publish(new AppMessages.PoolCoverAudioChanged(
                        Player.HasAudio && App.CoverVideoAudio));
                }
                catch { }
            };
            Player.MediaFailed += (s, e) =>
            {
                try { MessageBus.Publish(new AppMessages.PoolCoverAudioChanged(false)); } catch { }
            };
            // 和背景音乐一样自己循环：视频封面本来就是循环播放的
            Player.MediaEnded += (s, e) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(_currentPath)) return;
                    Player.Stop();
                    Player.Position = TimeSpan.Zero;
                    Player.Play();
                }
                catch { }
            };
        }

        /// <summary>这个文件是不是视频（只有视频才可能有声音）。</summary>
        public static bool IsVideo(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path)
                       && Array.IndexOf(VideoExts, Path.GetExtension(path).ToLowerInvariant()) >= 0;
            }
            catch { return false; }
        }

        /// <summary>这个卡池现在有没有在放封面音频。</summary>
        public static bool IsPlaying => !string.IsNullOrEmpty(_currentPath);

        /// <summary>当前播放进度（没有在放时返回 0）。</summary>
        public static TimeSpan Position
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(_currentPath)) return TimeSpan.Zero;
                    return Player.NaturalDuration.HasTimeSpan ? Player.Position : _seekTarget;
                }
                catch { return TimeSpan.Zero; }
            }
        }

        /// <summary>
        /// 3.76.7：背景模糊直接从这个播放器抓画面——它和封面音频是同一个播放器，
        /// 画面永远和声音同步；没有在播 / 还没有画面时返回 null，调用方回退到独立背景播放器。
        /// </summary>
        public static MediaPlayer? FrameSource
        {
            get
            {
                try
                {
                    if (string.IsNullOrEmpty(_currentPath)) return null;
                    if (Player.NaturalVideoWidth <= 0 || Player.NaturalVideoHeight <= 0) return null;
                    return Player;
                }
                catch { return null; }
            }
        }

        /// <summary>这个文件是不是正在播（用于确认背景帧可以复用它）。</summary>
        public static bool IsPlayingPath(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path)
                       && !string.IsNullOrEmpty(_currentPath)
                       && string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 主页离开前调用：记住这个卡池的封面视频已经放到哪，下一页从同一个时间点接着放。
        /// （页面之间不停止时其实用不到，留作兜底。）
        /// </summary>
        public static void RememberPosition(string poolDir, TimeSpan position)
        {
            try
            {
                if (string.IsNullOrEmpty(poolDir)) return;
                _rememberedPool = poolDir;
                _rememberedPos = position > TimeSpan.Zero ? position : TimeSpan.Zero;
            }
            catch { }
        }

        /// <summary>
        /// 取走「这个卡池上次放到哪」并清零（主页重新加载封面视频时用来接着放）。
        /// </summary>
        public static TimeSpan TakeResumePosition(string poolDir)
        {
            try
            {
                if (!string.Equals(_rememberedPool, poolDir, StringComparison.OrdinalIgnoreCase))
                    return TimeSpan.Zero;
                var pos = _rememberedPos;
                _rememberedPos = TimeSpan.Zero;
                return pos;
            }
            catch { return TimeSpan.Zero; }
        }

        /// <summary>
        /// 为指定卡池播放封面视频的音频，返回「这次应该从第几秒开始」：
        /// 已经在放同一个文件时返回当前进度；刚打开时返回即将 seek 到的位置。
        /// 主页用它把视频画面 seek 到同一时间点，保证音画同步。
        /// </summary>
        public static TimeSpan PlayForPool(string poolDir)
        {
            try
            {
                if (!App.CoverVideoAudio) { Stop(); return TimeSpan.Zero; }

                string path = string.IsNullOrEmpty(poolDir) ? "" : PoolCover.GetOriginalCoverPath(poolDir);
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || !IsVideo(path))
                {
                    Stop();
                    return TimeSpan.Zero;
                }

                if (string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    // 已经在放同一个文件：不要重开，直接把当前进度交给调用方
                    if (Player.NaturalDuration.HasTimeSpan) return Player.Position;
                    return _seekTarget;
                }

                Stop();
                _currentPath = path;
                _currentPool = poolDir ?? "";
                _seekTarget = string.Equals(_rememberedPool, _currentPool, StringComparison.OrdinalIgnoreCase)
                    ? _rememberedPos
                    : TimeSpan.Zero;
                var startAt = _seekTarget;
                Player.Open(new Uri(path, UriKind.Absolute));
                Player.Volume = 1.0;
                // 真正的 Play 放在 MediaOpened 里：先 seek 再播
                return startAt;
            }
            catch { return TimeSpan.Zero; }
        }

        /// <summary>窗口失去焦点时，按「失去焦点时静音」选项把这段音频一起静音。</summary>
        public static void SetWindowActive(bool active)
        {
            _windowActive = active;
            ApplyMute();
        }

        private static void ApplyMute()
        {
            try
            {
                bool mute = !App.CoverVideoAudio
                            || (!_windowActive && App.MuteWhenUnfocused);
                Player.IsMuted = mute;
            }
            catch { }
        }

        /// <summary>「视频封面音频」/「失去焦点时静音」选项变化后重新算一次静音状态。</summary>
        public static void Refresh()
        {
            if (!App.CoverVideoAudio) Stop();
            else ApplyMute();
        }

        /// <summary>
        /// 停下并清空（离开主页去选项 / 编辑封面等页面时调用）。
        /// 停下前记下当前进度，下次再进这些页面时接着放。
        /// </summary>
        public static void Stop()
        {
            bool wasPlaying = !string.IsNullOrEmpty(_currentPath);
            try
            {
                if (wasPlaying)
                {
                    _rememberedPool = _currentPool;
                    if (Player.NaturalDuration.HasTimeSpan)
                        _rememberedPos = Player.Position;
                }
            }
            catch { }
            try { Player.Stop(); Player.Close(); } catch { }
            _currentPath = "";
            _seekTarget = TimeSpan.Zero;
            if (wasPlaying)
            {
                try { MessageBus.Publish(new AppMessages.PoolCoverAudioChanged(false)); } catch { }
            }
        }
    }
}
