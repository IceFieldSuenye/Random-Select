using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RandomDrawer
{
    public partial class OptionsWindow : Window
    {
        private bool _isLoading = true;

        private const string IconMute = "\uE74F";
        private const string IconVolumeLow = "\uE993";
        private const string IconVolumeMid = "\uE994";
        private const string IconVolumeHigh = "\uE995";

        // ★ 历史记录按钮图标
        //   启用历史记录（当前已禁用） → E72C（History）
        //   禁用历史记录（当前已启用） → E733（Blocked2）
        private const string IconHistoryOn = "\uE72C";
        private const string IconHistoryOff = "\uE733";

        private static readonly string[] SupportedAudioExtensions =
        {
            ".mp3", ".wav", ".wma", ".m4a", ".flac", ".aac", ".ogg"
        };

        private static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg" };

        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".jfif" };

        private class PoolEntry
        {
            public string DisplayName { get; set; }
            public string Dir { get; set; }
        }
        private List<PoolEntry> _allPoolEntries = new List<PoolEntry>();

        private List<Key> _cheatCode = new List<Key>
        {
            Key.Up, Key.Up, Key.Down, Key.Down,
            Key.Left, Key.Right, Key.Left, Key.Right
        };
        private int _cheatIndex = 0;

        private DispatcherTimer _gifTimer;
        private GifBitmapDecoder _gifDecoder;
        private int _gifFrameIndex;

        private string _currentGifPath = "";

        public OptionsWindow()
        {
            InitializeComponent();
            LoadUIState();
            LoadMusicSettingsUI();
            LoadAppVersion();
            _isLoading = false;
        }

        private void LoadAppVersion()
        {
            if (TxtVersion == null) return;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(infoVersion))
                {
                    int plusIndex = infoVersion.IndexOf('+');
                    if (plusIndex >= 0) infoVersion = infoVersion.Substring(0, plusIndex);
                    TxtVersion.Text = $"版本：{infoVersion}";
                    return;
                }

                var version = assembly.GetName().Version;
                if (version != null)
                {
                    string display = version.ToString();
                    if (display.EndsWith(".0") && version.Build >= 0 && version.Revision == 0)
                    {
                        display = display.TrimEnd('0').TrimEnd('.');
                        if (display.Split('.').Length < 3) display = version.ToString(3);
                    }
                    TxtVersion.Text = $"版本：{display}";
                }
            }
            catch
            {
                TxtVersion.Text = "版本：1.0.0";
            }
        }

        public void SelectNormalPage()
        {
            NavNormal.IsChecked = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try { StopGifPlayback(); } catch { }
            try
            {
                try { VidCover.Stop(); } catch { }
                try { VidCover.Source = null; } catch { }
            }
            catch { }
            try { SaveMusicSettings(); } catch { }
            try { App.SaveSettings(); } catch { }
            base.OnClosing(e);
        }

        private void LoadUIState()
        {
            ListDirs.ItemsSource = App.SearchDirectories;
            TxtResDir.Text = App.ResourcesDir;
            LoadPoolComboBox();
            UpdateHistoryButtonUI();
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is RadioButton rb)
            {
                string tag = rb.Tag.ToString();
                PanelNormal.Visibility = tag == "0" ? Visibility.Visible : Visibility.Collapsed;
                PanelPool.Visibility = tag == "1" ? Visibility.Visible : Visibility.Collapsed;
                PanelSystem.Visibility = tag == "2" ? Visibility.Visible : Visibility.Collapsed;
                PanelAbout.Visibility = tag == "3" ? Visibility.Visible : Visibility.Collapsed;
                PanelHelp.Visibility = tag == "4" ? Visibility.Visible : Visibility.Collapsed;

                if (tag == "1")
                {
                    try
                    {
                        if (VidCover.Source != null && VidCover.Visibility == Visibility.Visible)
                        {
                            VidCover.Position = TimeSpan.Zero;
                            VidCover.Play();
                        }
                    }
                    catch { }

                    try
                    {
                        if (!string.IsNullOrEmpty(_currentGifPath)
                            && File.Exists(_currentGifPath)
                            && ImgCover.Visibility == Visibility.Visible)
                        {
                            StartGifPlayback(_currentGifPath);
                        }
                    }
                    catch { }
                }
                else
                {
                    StopGifPlayback();
                    try { VidCover.Stop(); } catch { }
                }

                if (tag == "0")
                {
                    bool old = _isLoading;
                    _isLoading = true;
                    RefreshMusicList();
                    _isLoading = old;
                }
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (PanelAbout.Visibility != Visibility.Visible) return;

            if (e.Key == _cheatCode[_cheatIndex])
            {
                _cheatIndex++;
                if (_cheatIndex == _cheatCode.Count)
                {
                    _cheatIndex = 0;
                    TriggerEasterEgg();
                }
            }
            else
            {
                _cheatIndex = 0;
                if (e.Key == _cheatCode[0]) _cheatIndex = 1;
            }
        }

        private void TriggerEasterEgg()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.bilibili.com/video/BV1mj3t6sERy",
                    UseShellExecute = true
                });

                if (Application.Current.MainWindow is MainWindow mainWin)
                    mainWin.MinimizeAndPauseMusicForEasterEgg();

                this.Close();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this, "彩蛋触发失败：" + ex.Message, "彩蛋", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void AboutIcon_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.bilibili.com/video/BV1RVG766Ecx",
                    UseShellExecute = true
                });

                if (Application.Current.MainWindow is MainWindow mainWin)
                    mainWin.MinimizeAndPauseMusicForEasterEgg();

                this.Close();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this, "打开链接失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyQQ_Click(object sender, MouseButtonEventArgs e)
        {
            const string qq = "169253670";
            if (TrySetClipboardText(qq))
            {
                CustomMessageBox.Show(this, "QQ号已复制到剪贴板：" + qq, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                CustomMessageBox.Show(this, "复制失败，剪贴板可能被其他程序占用，请稍后重试。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch
                {
                    System.Threading.Thread.Sleep(50);
                }
            }
            return false;
        }

        private void StartGifPlayback(string path)
        {
            StopGifPlayback();
            try
            {
                _gifDecoder = new GifBitmapDecoder(
                    new Uri(path, UriKind.Absolute),
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (_gifDecoder.Frames.Count == 0) return;

                _gifFrameIndex = 0;
                ImgCover.Source = _gifDecoder.Frames[0];

                if (_gifDecoder.Frames.Count > 1)
                {
                    _gifTimer = new DispatcherTimer
                    {
                        Interval = GetFrameDelay(_gifDecoder.Frames[0])
                    };
                    _gifTimer.Tick += GifTimer_Tick;
                    _gifTimer.Start();
                }
            }
            catch
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    ImgCover.Source = bitmap;
                }
                catch { ImgCover.Source = null; }
            }
        }

        private void StopGifPlayback()
        {
            if (_gifTimer != null)
            {
                _gifTimer.Stop();
                _gifTimer.Tick -= GifTimer_Tick;
                _gifTimer = null;
            }
            _gifDecoder = null;
            _gifFrameIndex = 0;
        }

        private void GifTimer_Tick(object sender, EventArgs e)
        {
            if (_gifDecoder == null || _gifDecoder.Frames.Count == 0) return;

            _gifFrameIndex = (_gifFrameIndex + 1) % _gifDecoder.Frames.Count;
            ImgCover.Source = _gifDecoder.Frames[_gifFrameIndex];

            if (_gifTimer != null)
                _gifTimer.Interval = GetFrameDelay(_gifDecoder.Frames[_gifFrameIndex]);
        }

        private TimeSpan GetFrameDelay(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata meta)
                {
                    var delayObj = meta.GetQuery("/grctlext/Delay");
                    if (delayObj != null)
                    {
                        int delay = Convert.ToInt32(delayObj);
                        int ms = delay * 10;
                        if (ms < 20) ms = 100;
                        return TimeSpan.FromMilliseconds(ms);
                    }
                }
            }
            catch { }
            return TimeSpan.FromMilliseconds(100);
        }

        // ================================================================
        // ★ 更新历史记录按钮 UI
        //   启用历史记录（当前已禁用） → E72C（History）
        //   禁用历史记录（当前已启用） → E733（Blocked2）
        // ================================================================
        private void UpdateHistoryButtonUI()
        {
            if (BtnToggleHistoryIcon == null || BtnToggleHistoryText == null) return;
            if (App.IsHistoryDisabled)
            {
                BtnToggleHistoryIcon.Text = IconHistoryOn;    // E72C
                BtnToggleHistoryText.Text = "启用历史记录";
            }
            else
            {
                BtnToggleHistoryIcon.Text = IconHistoryOff;   // E733
                BtnToggleHistoryText.Text = "禁用历史记录";
            }
        }

        private void BtnToggleHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!App.IsHistoryDisabled)
            {
                var result = CustomMessageBox.Show(this, "确定要禁用历史记录吗？\n禁用后，后续的抽取操作将不再保存历史记录（已有的历史记录不会被删除）。", "确认禁用", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    App.IsHistoryDisabled = true;
                    App.SaveSettings();
                    UpdateHistoryButtonUI();
                    CustomMessageBox.Show(this, "历史记录已禁用。", "提示");
                }
            }
            else
            {
                App.IsHistoryDisabled = false;
                App.SaveSettings();
                UpdateHistoryButtonUI();
                CustomMessageBox.Show(this, "历史记录已重新启用。", "提示");
            }
        }

        private void SaveMusicSettings()
        {
            if (_isLoading) return;

            App.LastMusicPath = ComboMusic.SelectedItem?.ToString() ?? "";
            App.IsMusicEnabled = ChkMusic.IsChecked == true;
            App.MusicVolume = SliderVolume.Value / 100.0;

            App.SaveSettings();
            UpdateMainWindowMusic();
        }

        private void LoadMusicSettingsUI()
        {
            RefreshMusicList();

            SliderVolume.Value = App.MusicVolume * 100;
            TxtVolumeValue.Text = (App.MusicVolume * 100).ToString("0");
            UpdateVolumeIcon();

            ChkMusic.IsChecked = App.IsMusicEnabled;
        }

        private void UpdateVolumeIcon()
        {
            if (TxtMuteIcon == null) return;
            double vol = SliderVolume.Value;
            if (App.IsMuted || vol <= 0) TxtMuteIcon.Text = IconMute;
            else if (vol <= 33) TxtMuteIcon.Text = IconVolumeLow;
            else if (vol <= 66) TxtMuteIcon.Text = IconVolumeMid;
            else TxtMuteIcon.Text = IconVolumeHigh;
        }

        private bool EnsureResourcesDirExists()
        {
            string dir = App.ResourcesDir;
            if (string.IsNullOrEmpty(dir)) return false;

            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return true;
            }
            catch { return false; }
        }

        private void RefreshMusicList()
        {
            string previousSelection = ComboMusic.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(previousSelection))
                previousSelection = Path.GetFileName(App.LastMusicPath);

            ComboMusic.Items.Clear();

            if (!EnsureResourcesDirExists()) return;

            try
            {
                var files = Directory.GetFiles(App.ResourcesDir)
                                     .Where(f =>
                                     {
                                         string ext = Path.GetExtension(f);
                                         if (string.IsNullOrEmpty(ext)) return false;
                                         return SupportedAudioExtensions.Contains(ext.ToLowerInvariant());
                                     })
                                     .Select(Path.GetFileName)
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

                foreach (var file in files) ComboMusic.Items.Add(file);
            }
            catch { }

            if (!string.IsNullOrEmpty(previousSelection) && ComboMusic.Items.Contains(previousSelection))
                ComboMusic.SelectedItem = previousSelection;
            else if (ComboMusic.Items.Count > 0)
                ComboMusic.SelectedItem = ComboMusic.Items[0];
        }

        private void BtnImportMusic_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(App.ResourcesDir) || !App.IsDirectoryWritable(App.ResourcesDir))
            {
                var r = CustomMessageBox.Show(this,
                    "当前资源目录不可写：\n\n" + (App.ResourcesDir ?? "(未设置)") + "\n\n" +
                    "可能的原因：\n" +
                    "  • 软件安装在系统保护目录（如 Program Files）\n" +
                    "  • 当前用户没有该目录的写权限\n\n" +
                    "是否选择一个用户可写的新位置？\n" +
                    "（建议选择：文档、D 盘或用户目录）",
                    "资源目录不可用",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (r != MessageBoxResult.Yes) return;

                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择资源文件夹（建议选择用户可写的位置）",
                    Multiselect = false
                };

                if (dlg.ShowDialog() != true) return;

                string newDir = dlg.FolderName;
                if (string.IsNullOrWhiteSpace(newDir)) return;

                if (!App.IsDirectoryWritable(newDir))
                {
                    CustomMessageBox.Show(this,
                        "所选目录不可写，请重新选择。\n\n" + newDir,
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                App.ResourcesDir = newDir;
                App.SaveSettings();
                TxtResDir.Text = newDir;

                bool old = _isLoading;
                _isLoading = true;
                RefreshMusicList();
                _isLoading = old;

                CustomMessageBox.Show(this,
                    "资源目录已更新：\n\n" + newDir,
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.flac;*.aac;*.ogg|所有文件|*.*"
            };

            if (ofd.ShowDialog() == true)
            {
                try
                {
                    string fileName = Path.GetFileName(ofd.FileName);
                    string destPath = Path.Combine(App.ResourcesDir, fileName);

                    if (!string.Equals(ofd.FileName, destPath, StringComparison.OrdinalIgnoreCase))
                        File.Copy(ofd.FileName, destPath, true);

                    App.LastMusicPath = fileName;
                    RefreshMusicList();
                    ComboMusic.SelectedItem = fileName;
                    ChkMusic.IsChecked = true;
                    SaveMusicSettings();
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(this, "导入音乐失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnRefreshMusic_Click(object sender, RoutedEventArgs e)
        {
            RefreshMusicList();

            if (ComboMusic.Items.Count == 0)
            {
                CustomMessageBox.Show(this,
                    "在资源目录中没有找到任何音频文件。\n\n" +
                    "支持格式：mp3 / wav / wma / m4a / flac / aac / ogg\n" +
                    "资源目录：\n" + (App.ResourcesDir ?? "(未设置)"),
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ComboMusic_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboMusic.SelectedItem == null) return;

            App.LastMusicPath = ComboMusic.SelectedItem.ToString();
            SaveMusicSettings();
        }

        private void ChkMusic_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ChkMusic.IsChecked == true;

            if (isChecked)
            {
                if (!EnsureResourcesDirExists())
                {
                    CustomMessageBox.Show(this,
                        "无法访问资源目录，请检查设置：\n\n" + (App.ResourcesDir ?? "(空路径)"),
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ChkMusic.IsChecked = false;
                    return;
                }

                RefreshMusicList();

                if (ComboMusic.Items.Count == 0)
                {
                    CustomMessageBox.Show(this,
                        "资源目录中没有找到任何音频文件。\n\n" +
                        "请先点击“导入音乐”，或将音频文件放入：\n" + App.ResourcesDir,
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ChkMusic.IsChecked = false;
                    return;
                }

                if (ComboMusic.SelectedItem == null)
                    ComboMusic.SelectedIndex = 0;
            }

            SaveMusicSettings();
        }

        private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVolumeValue == null) return;
            int vol = (int)e.NewValue;
            TxtVolumeValue.Text = vol.ToString();
            UpdateVolumeIcon();
            if (vol > 0 && App.IsMuted) { App.IsMuted = false; UpdateVolumeIcon(); }
            if (vol == 0 && !App.IsMuted) { App.IsMuted = true; UpdateVolumeIcon(); }
            SaveMusicSettings();
        }

        private void SliderVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_isLoading) return;
            double step = 5;
            double newValue = e.Delta > 0 ? Math.Min(SliderVolume.Maximum, SliderVolume.Value + step) : Math.Max(SliderVolume.Minimum, SliderVolume.Value - step);
            SliderVolume.Value = newValue;
            e.Handled = true;
        }

        private void TxtMuteIcon_Click(object sender, MouseButtonEventArgs e)
        {
            if (SliderVolume.Value <= 0 && !App.IsMuted) SliderVolume.Value = 50;
            App.IsMuted = !App.IsMuted;
            UpdateVolumeIcon();
            SaveMusicSettings();
        }

        private void UpdateMainWindowMusic()
        {
            if (Application.Current.MainWindow is MainWindow mainWin)
                mainWin.UpdateBackgroundMusic(App.LastMusicPath, App.IsMusicEnabled, App.MusicVolume, App.IsMuted);
        }

        private void AddDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择卡池文件夹", Multiselect = false };
            if (dialog.ShowDialog() == true)
            {
                string newDir = dialog.FolderName;
                if (!App.SearchDirectories.Contains(newDir))
                {
                    App.SearchDirectories.Add(newDir);
                    ListDirs.ItemsSource = null; ListDirs.ItemsSource = App.SearchDirectories; App.SaveSettings();
                }
            }
        }

        private void DeleteSpecificDir_Click(object sender, RoutedEventArgs e)
        {
            if (App.SearchDirectories.Count <= 1) { CustomMessageBox.Show(this, "至少需要保留一个卡池目录！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string dir = (sender as Button).Tag.ToString();
            if (CustomMessageBox.Show(this, $"确定要移除目录“{dir}”吗？(不会删除文件)", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                App.SearchDirectories.Remove(dir);
                ListDirs.ItemsSource = null; ListDirs.ItemsSource = App.SearchDirectories; App.SaveSettings();
            }
        }

        private void OpenSpecificDir_Click(object sender, RoutedEventArgs e)
        {
            string dir = (sender as Button).Tag.ToString();
            if (Directory.Exists(dir)) Process.Start("explorer.exe", dir);
        }

        private void SelectResDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择资源文件夹",
                Multiselect = false
            };

            if (!string.IsNullOrEmpty(App.ResourcesDir) && Directory.Exists(App.ResourcesDir))
            {
                try { dialog.InitialDirectory = App.ResourcesDir; } catch { }
            }

            if (dialog.ShowDialog() != true) return;

            string newDir = dialog.FolderName;
            if (string.IsNullOrEmpty(newDir)) return;

            try
            {
                if (!Directory.Exists(newDir)) Directory.CreateDirectory(newDir);

                App.ResourcesDir = newDir;
                App.SaveSettings();

                TxtResDir.Text = newDir;

                bool old = _isLoading;
                _isLoading = true;
                RefreshMusicList();
                _isLoading = old;

                CustomMessageBox.Show(this,
                    "资源目录已更新：\n\n" + newDir,
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this,
                    "设置资源目录失败：" + ex.Message,
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadPoolComboBox()
        {
            ComboPools.Items.Clear();
            _allPoolEntries.Clear();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in App.SearchDirectories)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var subDir in Directory.GetDirectories(dir).OrderBy(d => d))
                {
                    string poolName = Path.GetFileName(subDir);
                    string uniqueName = poolName;
                    int suffix = 2;
                    while (usedNames.Contains(uniqueName))
                    {
                        uniqueName = $"{poolName} [{suffix}]";
                        suffix++;
                    }
                    usedNames.Add(uniqueName);

                    _allPoolEntries.Add(new PoolEntry { DisplayName = uniqueName, Dir = subDir });
                    ComboPools.Items.Add(uniqueName);
                }
            }

            int selectedIndex = -1;
            if (!string.IsNullOrEmpty(App.LastSelectedPoolDir))
                selectedIndex = _allPoolEntries.FindIndex(p => p.Dir == App.LastSelectedPoolDir);
            if (selectedIndex < 0 && !string.IsNullOrEmpty(App.LastSelectedPoolName))
                selectedIndex = _allPoolEntries.FindIndex(p => p.DisplayName == App.LastSelectedPoolName);

            ComboPools.SelectedIndex = selectedIndex;
        }

        private string GetSelectedPoolDir()
        {
            int idx = ComboPools.SelectedIndex;
            if (idx < 0 || idx >= _allPoolEntries.Count) return null;
            return _allPoolEntries[idx].Dir;
        }

        private string GetSelectedPoolName()
        {
            int idx = ComboPools.SelectedIndex;
            if (idx < 0 || idx >= _allPoolEntries.Count) return null;
            return Path.GetFileName(_allPoolEntries[idx].Dir);
        }

        private void ComboPools_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try { StopGifPlayback(); } catch { }

            _currentGifPath = "";

            ListPoolContent.Items.Clear();

            try
            {
                ImgCover.Visibility = Visibility.Collapsed;
                ImgCover.Source = null;
                VidCover.Visibility = Visibility.Collapsed;
                VidCover.Stop();
                VidCover.Source = null;
            }
            catch { }

            TxtCoverPlaceholder.Visibility = Visibility.Visible;
            TxtCoverPlaceholder.Text = "点击可选择文件";

            string poolDir = GetSelectedPoolDir();
            if (string.IsNullOrEmpty(poolDir)) return;
            string poolName = GetSelectedPoolName();

            try
            {
                var coverFiles = Directory.GetFiles(poolDir, "cover.*");
                if (coverFiles.Length > 0)
                {
                    string coverPath = coverFiles[0];
                    string ext = Path.GetExtension(coverPath).ToLowerInvariant();
                    bool loaded = false;

                    if (VideoExts.Contains(ext))
                    {
                        try
                        {
                            VidCover.Source = new Uri(coverPath);
                            VidCover.Visibility = Visibility.Visible;
                            TxtCoverPlaceholder.Visibility = Visibility.Collapsed;
                            VidCover.Play();
                            loaded = true;
                        }
                        catch { loaded = false; }
                    }
                    else if (ImageExts.Contains(ext))
                    {
                        if (ext == ".gif")
                        {
                            try
                            {
                                ImgCover.Visibility = Visibility.Visible;
                                TxtCoverPlaceholder.Visibility = Visibility.Collapsed;
                                StartGifPlayback(coverPath);
                                _currentGifPath = coverPath;
                                loaded = ImgCover.Source != null;
                            }
                            catch { loaded = false; }
                        }
                        else
                        {
                            try
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.UriSource = new Uri(coverPath);
                                bitmap.EndInit();
                                ImgCover.Source = bitmap;
                                ImgCover.Visibility = Visibility.Visible;
                                TxtCoverPlaceholder.Visibility = Visibility.Collapsed;
                                loaded = true;
                            }
                            catch { loaded = false; }
                        }
                    }

                    if (!loaded)
                    {
                        TryLoadDefaultCover(poolName);
                    }
                }
                else
                {
                    TryLoadDefaultCover(poolName);
                }
            }
            catch
            {
                TryLoadDefaultCover(poolName);
            }

            try
            {
                string content = "";
                string lockFile = Path.Combine(poolDir, "data.lock");
                string txtFile = Path.Combine(poolDir, "data.txt");
                if (File.Exists(lockFile)) content = CryptoUtils.DecryptString(File.ReadAllText(lockFile));
                else if (File.Exists(txtFile)) content = CryptoUtils.ReadAllTextWithEncoding(txtFile);
                foreach (var line in content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    ListPoolContent.Items.Add(line);
            }
            catch { ListPoolContent.Items.Add("加载失败"); }
        }

        private void TryLoadDefaultCover(string poolName)
        {
            try
            {
                var defaultBitmap = new BitmapImage();
                defaultBitmap.BeginInit();
                defaultBitmap.CacheOption = BitmapCacheOption.OnLoad;
                defaultBitmap.UriSource = new Uri("default_cover.jpg", UriKind.Relative);
                defaultBitmap.EndInit();
                ImgCover.Source = defaultBitmap;
                ImgCover.Visibility = Visibility.Visible;
            }
            catch
            {
                ImgCover.Visibility = Visibility.Collapsed;
            }

            TxtCoverPlaceholder.Text = poolName ?? "点击可选择文件";
            TxtCoverPlaceholder.Visibility = Visibility.Visible;
        }

        private void Cover_Click(object sender, MouseButtonEventArgs e)
        {
            string poolDir = GetSelectedPoolDir();
            if (string.IsNullOrEmpty(poolDir))
            {
                CustomMessageBox.Show(this, "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm" +
                         "|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp" +
                         "|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm" +
                         "|所有文件|*.*",
                FilterIndex = 1
            };

            if (ofd.ShowDialog() == true)
            {
                string ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
                if (!VideoExts.Contains(ext) && !ImageExts.Contains(ext))
                {
                    CustomMessageBox.Show(this,
                        $"不支持的文件类型：{ext}\n\n" +
                        "支持的图片格式：png / jpg / jpeg / gif / bmp / webp\n" +
                        "支持的视频格式：mp4 / avi / mkv / mov / wmv / m4v / webm\n\n" +
                        "封面未被修改。",
                        "不支持的格式", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    var oldCovers = Directory.GetFiles(poolDir, "cover.*");
                    foreach (var oldCover in oldCovers) File.Delete(oldCover);
                    File.Copy(ofd.FileName, Path.Combine(poolDir, $"cover{ext}"), true);
                    ComboPools_SelectionChanged(null, null);
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(this, "上传封面失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void VidCover_MediaEnded(object sender, RoutedEventArgs e)
        {
            var src = VidCover.Source;
            if (src == null) return;

            try
            {
                VidCover.Stop();
                VidCover.Source = null;
                VidCover.Source = src;

                if (VidCover.Visibility == Visibility.Visible)
                    VidCover.Play();
            }
            catch { }
        }

        private void VidCover_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            try
            {
                VidCover.Visibility = Visibility.Collapsed;
                VidCover.Stop();
                VidCover.Source = null;
            }
            catch { }

            try
            {
                string poolName = GetSelectedPoolName();
                TxtCoverPlaceholder.Text = string.IsNullOrEmpty(poolName) ? "视频无法播放" : poolName;
                TxtCoverPlaceholder.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void BtnNewPool_Click(object sender, RoutedEventArgs e)
        {
            if (new NewPoolWindow { Owner = this }.ShowDialog() == true) LoadPoolComboBox();
        }

        private void BtnModifyPool_Click(object sender, RoutedEventArgs e)
        {
            string poolDir = GetSelectedPoolDir();
            if (string.IsNullOrEmpty(poolDir))
            {
                CustomMessageBox.Show(this, "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string poolName = GetSelectedPoolName();

            string lockFile = Path.Combine(poolDir, "data.lock");

            if (File.Exists(lockFile))
            {
                var pwd = new PasswordDialog($"修改锁定的卡池“{poolName}”需要验证密码") { Owner = this };
                var result = pwd.ShowDialog();

                if (pwd.ForgotPasswordRequested)
                {
                    if (!TryResetPasswordBySecretQA(poolDir, poolName)) return;
                }
                else if (result == true)
                {
                    if (!CryptoUtils.VerifyPassword(poolName, pwd.Password))
                    {
                        CustomMessageBox.Show(this, "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            if (new NewPoolWindow(poolName, poolDir) { Owner = this }.ShowDialog() == true)
            {
                App.LastSelectedPoolDir = poolDir;
                LoadPoolComboBox();
                ComboPools_SelectionChanged(null, null);
            }
        }

        private void DeletePool_Click(object sender, RoutedEventArgs e)
        {
            string poolDir = GetSelectedPoolDir();
            if (string.IsNullOrEmpty(poolDir))
            {
                CustomMessageBox.Show(this, "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string poolName = GetSelectedPoolName();

            if (File.Exists(Path.Combine(poolDir, "data.lock")))
            {
                var pwd = new PasswordDialog($"删除锁定的卡池“{poolName}”需要验证密码") { Owner = this };
                var result = pwd.ShowDialog();

                if (pwd.ForgotPasswordRequested)
                {
                    if (!VerifySecretQA(poolDir)) return;
                }
                else if (result == true)
                {
                    if (!CryptoUtils.VerifyPassword(poolName, pwd.Password))
                    {
                        CustomMessageBox.Show(this, "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    CryptoUtils.DeletePasswordHash(poolName);
                }
                else
                {
                    return;
                }
            }

            if (CustomMessageBox.Show(this, $"确定要删除“{poolName}”吗？", "确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try { Directory.Delete(poolDir, true); }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(this, "删除失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (App.LastSelectedPoolDir == poolDir) App.LastSelectedPoolDir = "";
                if (App.LastSelectedPoolName == poolName) App.LastSelectedPoolName = "";

                LoadPoolComboBox();

                try { StopGifPlayback(); } catch { }
                try { VidCover.Stop(); } catch { }
                _currentGifPath = "";
                ListPoolContent.Items.Clear();

                try
                {
                    ImgCover.Source = null;
                    ImgCover.Visibility = Visibility.Collapsed;
                    VidCover.Source = null;
                    VidCover.Visibility = Visibility.Collapsed;
                }
                catch { }

                TxtCoverPlaceholder.Text = "点击可选择文件";
                TxtCoverPlaceholder.Visibility = Visibility.Visible;

                if (ComboPools.Items.Count > 0)
                {
                    ComboPools.SelectedIndex = 0;
                    ComboPools_SelectionChanged(null, null);
                }
            }
        }

        private void CopyPool_Click(object sender, RoutedEventArgs e)
        {
            string srcDir = GetSelectedPoolDir();
            if (string.IsNullOrEmpty(srcDir)) { CustomMessageBox.Show(this, "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            string srcName = GetSelectedPoolName();

            string newName = InputDialog.Show(this, "复制卡池", "请输入新的卡池名称：", srcName + "_副本");

            if (string.IsNullOrEmpty(newName) || newName == srcName) return;
            string destBase = App.SearchDirectories.FirstOrDefault() ?? App.BaseDir;
            string destDir = Path.Combine(destBase, newName);
            if (Directory.Exists(destDir)) { CustomMessageBox.Show(this, "已存在相同名称的卡池！", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            try
            {
                Directory.CreateDirectory(destDir);
                foreach (var file in Directory.GetFiles(srcDir)) File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
                string srcLock = Path.Combine(destDir, "data.lock");
                if (File.Exists(srcLock))
                {
                    string content = CryptoUtils.DecryptString(File.ReadAllText(srcLock));
                    File.WriteAllText(Path.Combine(destDir, "data.txt"), content);
                    File.Delete(srcLock);
                }
                CustomMessageBox.Show(this, "复制成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                App.LastSelectedPoolDir = destDir;
                LoadPoolComboBox();
            }
            catch (Exception ex) { CustomMessageBox.Show(this, "复制失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            string poolDir = GetSelectedPoolDir();
            if (string.IsNullOrEmpty(poolDir))
            {
                if (ComboPools.Items.Count > 0) ComboPools.SelectedIndex = 0;
                poolDir = GetSelectedPoolDir();
            }
            if (string.IsNullOrEmpty(poolDir)) { CustomMessageBox.Show(this, "当前没有任何卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string poolName = GetSelectedPoolName();
            new HistoryWindow(poolName, poolDir) { Owner = this }.ShowDialog();
        }

        private void OpenResDir_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureResourcesDirExists())
            {
                CustomMessageBox.Show(this,
                    "无法访问资源目录：\n\n" + (App.ResourcesDir ?? "(未设置)"),
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Process.Start("explorer.exe", App.ResourcesDir);
        }

        private void ResetApp_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ResetConfirmDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            try
            {
                if (File.Exists(App.SettingsFile)) File.Delete(App.SettingsFile);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this, "重置失败：" + ex.Message + "\n\n路径：" + App.SettingsFile, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (File.Exists(App.SettingsFile))
            {
                CustomMessageBox.Show(this, "无法删除配置文件，可能被其他程序占用。\n\n路径：" + App.SettingsFile, "重置失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string exePath = null;
            try { exePath = Environment.ProcessPath; } catch { }
            if (string.IsNullOrEmpty(exePath))
            {
                try { exePath = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
            }
            if (string.IsNullOrEmpty(exePath)) exePath = Application.ResourceAssembly.Location;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c ping 127.0.0.1 -n 2 >nul & start \"\" \"{exePath}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? App.BaseDir
                });
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this, "配置已重置，但无法自动重启，请手动打开应用。\n\n错误：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Environment.Exit(0);
        }

        // ================================================================
        // ★ 密保辅助：读取 / 写入 / 验证 / 重置密码
        // ================================================================

        private static (string question, string answer) ReadSecretQA(string poolDir)
        {
            try
            {
                string qaFile = Path.Combine(poolDir, "secret_qa.lock");
                if (!File.Exists(qaFile)) return ("", "");

                string content = CryptoUtils.DecryptString(File.ReadAllText(qaFile));
                if (string.IsNullOrEmpty(content)) return ("", "");

                int idx = content.IndexOf('\u0001');
                if (idx < 0) return ("", "");

                return (content.Substring(0, idx), content.Substring(idx + 1));
            }
            catch { return ("", ""); }
        }

        private static void WriteSecretQA(string poolDir, string question, string answer)
        {
            try
            {
                string qaFile = Path.Combine(poolDir, "secret_qa.lock");
                string content = question + "\u0001" + answer;
                File.WriteAllText(qaFile, CryptoUtils.EncryptString(content));
            }
            catch { }
        }

        private bool VerifySecretQA(string poolDir)
        {
            var (question, answer) = ReadSecretQA(poolDir);

            if (string.IsNullOrEmpty(question))
            {
                CustomMessageBox.Show(this,
                    "该卡池未设置密保问题，无法通过密保找回密码。\n\n" +
                    "若遗忘密码，只能手动删除卡池文件夹。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var win = new SecretQAWindow(question, answer) { Owner = this };
            return win.ShowDialog() == true;
        }

        private bool TryResetPasswordBySecretQA(string poolDir, string poolName)
        {
            if (!VerifySecretQA(poolDir)) return false;

            var newPwd = new PasswordDialog("请设置新密码", true) { Owner = this };
            if (newPwd.ShowDialog() != true)
            {
                CustomMessageBox.Show(this, "已取消密码重置。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            try
            {
                CryptoUtils.SavePasswordHash(poolName, newPwd.Password);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this, "更新密码失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!string.IsNullOrEmpty(newPwd.SecretQuestion))
            {
                WriteSecretQA(poolDir, newPwd.SecretQuestion, newPwd.SecretAnswer);
            }

            CustomMessageBox.Show(this,
                "密码已重置成功！\n\n" +
                "您现在可以使用新密码来修改或删除此卡池。",
                "成功", MessageBoxButton.OK, MessageBoxImage.Information);

            return true;
        }
    }
}