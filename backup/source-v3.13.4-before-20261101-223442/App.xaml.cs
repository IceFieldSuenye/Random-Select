using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace RandomDrawer
{
    public partial class App : Application
    {
        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>是否以 --dsh 参数启动：仅此时「关于」页的 AI 说明才有下划线并弹出说明窗口。</summary>
        public static readonly bool DshMode = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--dsh", StringComparison.OrdinalIgnoreCase));

        /// <summary>配置目录/配置文件的读写错误（为空表示正常）。</summary>
        public static string ConfigInitError = null;

        /// <summary>本次启动读取卡池/资源目录时发现的问题（由主窗口统一提示）。</summary>
        public static readonly List<string> StartupErrors = new List<string>();

        private static string _lastLoggedConfigSignature;

        public static string AppDataDir = EnsureAppDataDir();
        public static string SettingsFile = Path.Combine(AppDataDir, "settings.ini");

        /// <summary>
        /// 配置目录：%APPDATA%\Random Select（新版本只使用此目录，不再兼容旧版的 Radom Select）。
        /// 目录不存在时自动创建，配置文件随后自动生成。
        /// </summary>
        private static string EnsureAppDataDir()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDir = Path.Combine(roaming, "Random Select");

            try
            {
                if (!Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);
            }
            catch (Exception ex)
            {
                ConfigInitError = ex.Message;
            }

            return configDir;
        }
        public static List<string> SearchDirectories = new List<string>();
        public static string ResourcesDir = Path.Combine(BaseDir, "Resources");
        public static string ScreenshotDir = "";

        public static string LastSelectedPoolName = "";
        public static string LastSelectedPoolDir = "";

        public static bool IsNoRepeat = true;
        public static bool IsHistoryDisabled = false;
        public static bool IsAnimationDisabled = false;

        /// <summary>
        /// 真实系统构建号。兼容模式与 manifest 都会影响 Environment.OSVersion，
        /// 因此优先读取注册表里的 CurrentBuildNumber。
        /// </summary>
        public static readonly int WindowsBuild = DetectWindowsBuild();

        /// <summary>低于该构建号视为「旧版 Windows」：Windows 7 / 8 / 10 早期版本。</summary>
        public const int LegacyWindowsBuild = 17763; // Windows 10 1809

        /// <summary>系统版本号的可读名称。</summary>
        public static string WindowsDisplayName => DescribeWindows(WindowsBuild);

        /// <summary>是否为旧版 Windows（Windows 7 / 8 / 10 早期版本）。</summary>
        public static bool IsLegacyWindows => WindowsBuild > 0 && WindowsBuild < LegacyWindowsBuild;

        /// <summary>系统图标字体能否显示界面用到的全部图标；不能时需要进入图标兼容模式。</summary>
        public static bool IconCompatActive => IconSupport.CompatActive;

        /// <summary>图标统一使用的字体（由 IconSupport 在启动时决定）。</summary>
        public static FontFamily IconFontFamily => IconSupport.IconFontFamily;

        public static string LastMusicPath = "";
        public static bool IsMusicEnabled = false;
        public static double MusicVolume = 0.5;
        public static bool IsMuted = false;

        /// <summary>失去焦点（最小化 / 切到其它窗口）时是否静音背景音乐。</summary>
        public static bool MuteWhenUnfocused = true;

        public static double SoundEffectVolume = 0.5;
        public static bool IsSoundEffectMuted = false;

        // ★ 功能隐藏（保存到 %appdata%）
        public static bool HideProbabilityOption = false;
        public static bool HideAdvancedGacha = false;
        public static bool HideEditPool = false;
        public static bool HideOptionSection = false;
        /// <summary>视觉呈现模式：流畅优先 / 加载速度优先。</summary>
        public enum RenderPreferenceKind { Smooth = 0, Fast = 1 }

        /// <summary>当前视觉呈现模式（默认流畅优先）。</summary>
        public static RenderPreferenceKind RenderPreference = RenderPreferenceKind.Smooth;

        public static bool HideGachaReport = false;

        /// <summary>已勾选「不再提示」后，不再显示图标兼容模式启动提示。</summary>
        public static bool SuppressIconCompatNotice = false;

        /// <summary>true = Windows 11 风格右键菜单；false = 旧版经典菜单。</summary>
        public static bool UseModernContextMenu = true;

        /// <summary>从哪个页面进入抽取报告（确定后返回该页面）。</summary>
        public static string GachaReportReturnPage = "Main";

        // ★ F12 临时显示/隐藏（不保存）
        public static bool IsHiddenRevealed = false;

        // ★ 有效可见性（结合 F12 临时显示）
        public static bool IsProbabilityVisible => !HideProbabilityOption || IsHiddenRevealed;
        public static bool IsAdvancedVisible => !HideAdvancedGacha || IsHiddenRevealed;
        public static bool IsEditPoolVisible => !HideEditPool || IsHiddenRevealed;
        public static bool IsOptionSectionVisible => !HideOptionSection || IsHiddenRevealed;
        public static bool IsGachaReportVisible => !HideGachaReport || IsHiddenRevealed;

        /// <summary>是否有任何隐藏项（用于 F12 判断是否有效）。</summary>
        public static bool HasAnyHiddenOption =>
            HideProbabilityOption || HideAdvancedGacha || HideEditPool || HideOptionSection || HideGachaReport;

        private static Mutex _mutex;

        /// <summary>
        /// 是否为冒烟测试进程（tests\SmokeTest 会设置 RS_SMOKE_TEST=1）。
        /// 测试要能反复创建主窗口，必须跳过单实例互斥体，否则用户正开着程序时测试会直接退出。
        /// </summary>
        internal static readonly bool IsSmokeTest =
            Environment.GetEnvironmentVariable("RS_SMOKE_TEST") == "1";

        private static void TextInput_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.ContextMenu == null)
                control.ContextMenu = new ThemedTextContextMenu();
        }

        private static void IconTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock) IconSupport.Normalize(textBlock);
        }

        private static void IconAccessText_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is AccessText accessText) IconSupport.Normalize(accessText);
        }

        private static void Window_Loaded_ForIcons(object sender, RoutedEventArgs e)
        {
            if (!IconCompatActive || sender is not Window window) return;
            try
            {
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { IconSupport.NormalizeTree(window); } catch { }
                    try { IconSupport.RequestSweep(window); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        private static void ContextMenu_Opened_ForIcons(object sender, RoutedEventArgs e)
        {
            if (!IconCompatActive || sender is not ContextMenu menu) return;
            try { IconSupport.NormalizeTree(menu); } catch { }
        }

        private static void ToolTip_Opening(object sender, ToolTipEventArgs e)
        {
            // 按需求统一关闭所有浮动提示，避免遮挡按钮/界面
            e.Handled = true;
        }

        private static void ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox combo)
            {
                try { combo.ApplyTemplate(); } catch { }
                combo.DropDownOpened -= ComboBox_DropDownOpened;
                combo.DropDownOpened += ComboBox_DropDownOpened;
                SetComboPopupAnimation(combo);
            }
        }

        private static void ComboBox_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is not ComboBox combo) return;
            SetComboPopupAnimation(combo);
            UpdateComboPopupCorner(combo);
        }

        private static void SetComboPopupAnimation(ComboBox combo)
        {
            try
            {
                if (combo.Template.FindName("PART_Popup", combo) is Popup popup)
                    popup.PopupAnimation = IsAnimationDisabled ? PopupAnimation.None : PopupAnimation.Fade;
            }
            catch { }
        }

        /// <summary>
        /// 下拉列表比下拉框更宽时，右上角也要圆角，否则会露出一个直角缺口。
        /// 与下拉框同宽时保持上沿平直，和下拉框贴合。
        /// </summary>
        private static void UpdateComboPopupCorner(ComboBox combo)
        {
            if (combo == null) return;
            try
            {
                combo.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup) return;
                        if (popup.Child is not Border border) return;

                        ApplyComboPopupCorner(border, combo);
                        border.SizeChanged -= ComboPopupBorder_SizeChanged;
                        border.SizeChanged += ComboPopupBorder_SizeChanged;
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch { }
        }

        private static void ComboPopupBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Border border && border.TemplatedParent is ComboBox combo)
                ApplyComboPopupCorner(border, combo);
        }

        private static void ApplyComboPopupCorner(Border border, ComboBox combo)
        {
            if (border == null || combo == null) return;
            bool widerThanBox = border.ActualWidth > combo.ActualWidth + 0.5;
            border.CornerRadius = widerThanBox
                ? new CornerRadius(0, 8, 8, 8)
                : new CornerRadius(0, 0, 8, 8);
        }

        private static void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!IsAnimationDisabled || sender is not ContextMenu menu) return;

            // ContextMenu 本身没有 PopupAnimation 属性，关闭其内部 Popup 的动画
            DependencyObject node = menu;
            while (node != null)
            {
                if (node is Popup popup)
                {
                    popup.PopupAnimation = PopupAnimation.None;
                    break;
                }
                node = VisualTreeHelper.GetParent(node);
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                RuntimeLog.StartSession(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString());
                RuntimeLog.Info("系统识别：" + WindowsDisplayName);
            }
            catch { }

            EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(TextInput_Loaded));
            EventManager.RegisterClassHandler(typeof(PasswordBox), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(TextInput_Loaded));
            EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(IconTextBlock_Loaded));
            EventManager.RegisterClassHandler(typeof(AccessText), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(IconAccessText_Loaded));
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(Window_Loaded_ForIcons));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler(ContextMenu_Opened_ForIcons));
            // 移除所有原生悬浮提示（ToolTip）
            EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
                new ToolTipEventHandler(ToolTip_Opening));
            EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(ComboBox_Loaded));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler(ContextMenu_Opened));

            try
            {
                AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                    RuntimeLog.Error(args.ExceptionObject as Exception, "未处理的异常");
                DispatcherUnhandledException += (s, args) =>
                    RuntimeLog.Error(args.Exception, "UI 线程未处理的异常");
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
                {
                    RuntimeLog.Error(args.Exception, "未观察的任务异常");
                    args.SetObserved();
                };
            }
            catch { }

            bool createdNew;
            try { _mutex = new Mutex(true, "RandomDrawer_SingleInstance_Mutex_v3", out createdNew); }
            catch { createdNew = true; }

            // ★ 冒烟测试（tests\SmokeTest）会设置 RS_SMOKE_TEST=1：
            //   跳过单实例检查，免得用户正开着程序时测试进程被互斥体直接踢掉。
            if (!IsSmokeTest && !createdNew)
            {
                // ★ 已有实例在运行：把它带到前台，然后安静退出
                SingleInstance.BroadcastActivate();
                Environment.Exit(0);
                return;
            }

            // ★ 图标字体检测：必须在任何界面元素创建之前完成
            try
            {
                IconSupport.Initialize();
                this.Resources["AppIconFont"] = IconSupport.IconFontFamily;
                RuntimeLog.Info($"图标字体：{IconSupport.StatusDescription}；系统：{WindowsDisplayName}（{WindowsBuild}）");
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "初始化图标字体失败"); }
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }

            // ★ 配置文件：能读就用；缺失/为空则立即生成；生成失败要明确报错
            try
            {
                if (!Directory.Exists(AppDataDir)) Directory.CreateDirectory(AppDataDir);
            }
            catch (Exception ex) { ConfigInitError = ex.Message; }

            try { LoadSettings(); }
            catch (Exception ex) { ConfigInitError = ex.Message; }

            bool configReady = false;
            try
            {
                configReady = File.Exists(SettingsFile) && new FileInfo(SettingsFile).Length > 0;
                if (!configReady)
                {
                    SaveSettings();
                    configReady = File.Exists(SettingsFile) && new FileInfo(SettingsFile).Length > 0;
                }
            }
            catch (Exception ex) { ConfigInitError = ex.Message; }

            if (!configReady)
            {
                try
                {
                    MessageBox.Show(
                        "无法读取或创建配置文件：\n\n" + SettingsFile + "\n\n"
                        + (!string.IsNullOrEmpty(ConfigInitError) ? ConfigInitError
                           : (!string.IsNullOrEmpty(IniHelper.LastError) ? IniHelper.LastError : "未知错误（可能是没有写入权限）"))
                        + "\n\n程序将以默认设置继续运行，但本次的修改无法保存。\n"
                        + "请检查该文件夹的写入权限，或将本程序加入杀毒软件的白名单。",
                        "配置读写失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }
            // ★ 全局主题（浅色 / 深色 / 跟随系统），必须在主窗口创建前初始化
            try { ThemeManager.Initialize(); } catch { }

            try
            {
                base.OnStartup(e);
                var mainWindow = new MainWindow();
                this.MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("主窗口初始化失败：\n\n" + ex.ToString(), "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                var p = System.Diagnostics.Process.GetCurrentProcess();
                RuntimeLog.EndSession($"工作集={p.WorkingSet64 / 1024 / 1024}MB，私有内存={p.PrivateMemorySize64 / 1024 / 1024}MB");
            }
            catch { }
            try { SaveSettings(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
            base.OnExit(e);
        }

        public static bool IsValidDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            foreach (char c in path) { if (c == '\uFFFD' || c == '?') return false; }
            try
            {
                var invalid = Path.GetInvalidPathChars();
                foreach (char c in path) { if (Array.IndexOf(invalid, c) >= 0) return false; }
                string full = Path.GetFullPath(path);
                if (string.IsNullOrEmpty(full)) return false;
                if (!Path.IsPathRooted(full)) return false;
                return true;
            }
            catch { return false; }
        }

        public static bool IsDirectoryWritable(string dir, bool createIfMissing = true)
        {
            if (!IsValidDirectoryPath(dir)) return false;
            try
            {
                if (!Directory.Exists(dir))
                {
                    if (!createIfMissing)
                    {
                        // ★ 启动检查/默认路径探测时不创建目录：
                        //   只检查最近一个已存在的父目录是否可写。
                        string parent = Path.GetDirectoryName(dir);
                        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        {
                            string next = Path.GetDirectoryName(parent);
                            if (string.IsNullOrEmpty(next) || string.Equals(next, parent, StringComparison.OrdinalIgnoreCase))
                                break;
                            parent = next;
                        }
                        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return false;
                        return CanWriteToDirectory(parent);
                    }

                    Directory.CreateDirectory(dir);
                }

                return CanWriteToDirectory(dir);
            }
            catch { return false; }
        }

        private static bool CanWriteToDirectory(string dir)
        {
            try
            {
                string testFile = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch { return false; }
        }

        public static string GetFallbackResourcesDir()
        {
            string p = Path.Combine(AppDataDir, "Resources");
            try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); } catch { }
            return p;
        }

        public static string GetDefaultScreenshotDir()
        {
            try
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (!string.IsNullOrEmpty(pictures))
                    return Path.Combine(pictures, "Saved Pictures");
            }
            catch { }
            return Path.Combine(AppDataDir, "Reports");
        }

        public static string GetFallbackPoolDir()
        {
            string p = Path.Combine(AppDataDir, "SelectBox");
            try { if (!Directory.Exists(p)) Directory.CreateDirectory(p); } catch { }
            return p;
        }

        /// <summary>
        /// 读取真实系统构建号。兼容模式与 manifest 都会影响 Environment.OSVersion，
        /// 因此优先使用注册表；注册表不可用时退回 Environment.OSVersion。
        /// </summary>
        private static int DetectWindowsBuild()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string buildText = key?.GetValue("CurrentBuildNumber")?.ToString();
                    if (int.TryParse(buildText, out int build) && build > 0) return build;
                }
            }
            catch { }

            try { return Environment.OSVersion.Version.Build; }
            catch { return 0; }
        }

        /// <summary>把构建号转成可读的系统名称。</summary>
        private static string DescribeWindows(int build)
        {
            if (build <= 0) return "未知系统";
            if (build < 2600) return "Windows XP 或更早版本";
            if (build < 6000) return "Windows Vista";
            if (build < 7600) return "Windows 7 之前的版本";
            if (build < 9200) return "Windows 7";
            if (build < 10240) return "Windows 8 / 8.1";
            if (build < LegacyWindowsBuild) return $"Windows 10 早期版本（Build {build}）";
            if (build < 22000) return $"Windows 10（Build {build}）";
            return $"Windows 11（Build {build}）";
        }

        public static void LoadSettings()
        {
            try
            {
                if (!Directory.Exists(AppDataDir)) Directory.CreateDirectory(AppDataDir);
                StartupErrors.Clear();

                string dirsStr = IniHelper.ReadValue("Paths", "SearchDirectories", "", SettingsFile);
                SearchDirectories.Clear();
                if (!string.IsNullOrEmpty(dirsStr))
                {
                    var raw = dirsStr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    foreach (var d in raw)
                        if (IsValidDirectoryPath(d)) SearchDirectories.Add(d.Trim());
                }

                if (SearchDirectories.Count == 0)
                {
                    // ★ 只使用默认路径，不在启动时创建目录
                    SearchDirectories.Add(Path.Combine(BaseDir, "SelectBox"));
                }

                string resDir = IniHelper.ReadValue("Paths", "ResourceDir", Path.Combine(BaseDir, "Resources"), SettingsFile);
                if (string.IsNullOrEmpty(resDir) || !IsValidDirectoryPath(resDir))
                    resDir = Path.Combine(BaseDir, "Resources");
                // ★ 资源目录同样只记录路径；真正创建发生在用户导入音频并选择保存位置时
                ResourcesDir = resDir;

                string screenshotDir = IniHelper.ReadValue("Paths", "ScreenshotDir", "", SettingsFile);
                ScreenshotDir = string.IsNullOrWhiteSpace(screenshotDir)
                    ? GetDefaultScreenshotDir()
                    : screenshotDir.Trim();

                LastSelectedPoolName = IniHelper.ReadValue("State", "LastSelectedPool", "", SettingsFile);
                LastSelectedPoolDir = IniHelper.ReadValue("State", "LastSelectedPoolDir", "", SettingsFile);
                IsNoRepeat = IniHelper.ReadValue("State", "IsNoRepeat", "1", SettingsFile) == "1";
                IsHistoryDisabled = IniHelper.ReadValue("State", "IsHistoryDisabled", "0", SettingsFile) == "1";
                IsAnimationDisabled = IniHelper.ReadValue("State", "IsAnimationDisabled", "0", SettingsFile) == "1";

                LastMusicPath = IniHelper.ReadValue("Music", "MusicPath", "", SettingsFile);
                IsMusicEnabled = IniHelper.ReadValue("Music", "EnableMusic", "0", SettingsFile) == "1";
                IsMuted = IniHelper.ReadValue("Music", "IsMuted", "0", SettingsFile) == "1";
                MuteWhenUnfocused = IniHelper.ReadValue("Music", "MuteWhenUnfocused", "1", SettingsFile) == "1";
                // ★ 解析失败时回落到默认 50，而不是静默变成 0（0 = 静音，用户会以为「没声音」）
                if (!double.TryParse(IniHelper.ReadValue("Music", "Volume", "50", SettingsFile), out double vol))
                    vol = 50;
                MusicVolume = vol / 100.0;
                // ★ 与音效音量一致：必须夹紧到 [0,1]，否则 MediaPlayer.Volume 会抛异常被吞掉
                if (MusicVolume < 0) MusicVolume = 0;
                if (MusicVolume > 1) MusicVolume = 1;

                double.TryParse(IniHelper.ReadValue("Music", "SoundEffectVolume", "50", SettingsFile), out double sev);
                SoundEffectVolume = sev / 100.0;
                if (SoundEffectVolume < 0) SoundEffectVolume = 0;
                if (SoundEffectVolume > 1) SoundEffectVolume = 1;

                IsSoundEffectMuted = IniHelper.ReadValue("Music", "SoundEffectMuted", "0", SettingsFile) == "1";

                // ★ 功能隐藏
                HideProbabilityOption = IniHelper.ReadValue("UI", "HideProbabilityOption", "0", SettingsFile) == "1";
                HideAdvancedGacha = IniHelper.ReadValue("UI", "HideAdvancedGacha", "0", SettingsFile) == "1";
                HideEditPool = IniHelper.ReadValue("UI", "HideEditPool", "0", SettingsFile) == "1";
                HideOptionSection = IniHelper.ReadValue("UI", "HideOptionSection", "0", SettingsFile) == "1";
                HideGachaReport = IniHelper.ReadValue("UI", "HideGachaReport", "0", SettingsFile) == "1";
            try
            {
                RenderPreference = IniHelper.ReadValue("UI", "RenderPreference", "smooth", SettingsFile) == "fast"
                    ? RenderPreferenceKind.Fast
                    : RenderPreferenceKind.Smooth;
            }
            catch { RenderPreference = RenderPreferenceKind.Smooth; }
                UseModernContextMenu = IniHelper.ReadValue("UI", "ContextMenuStyle", "modern", SettingsFile) != "legacy";
                SuppressIconCompatNotice = IniHelper.ReadValue("UI", "SuppressIconCompatNotice", "0", SettingsFile) == "1"
                    || IniHelper.ReadValue("UI", "SuppressWin7CompatNotice", "0", SettingsFile) == "1";

                // F12 临时显示状态不保存，每次启动重置
                IsHiddenRevealed = false;

                string configSignature = string.Join(" | ", SearchDirectories) + "|" + ResourcesDir + "|" + ScreenshotDir
                + "|" + ThemeManager.Mode + "|" + UseModernContextMenu + "|" + LastSelectedPoolName;
            if (!string.Equals(configSignature, _lastLoggedConfigSignature, StringComparison.Ordinal))
            {
                _lastLoggedConfigSignature = configSignature;
                RuntimeLog.Info($"配置加载：卡池目录={string.Join(" | ", SearchDirectories)}；资源目录={ResourcesDir}；截图目录={ScreenshotDir}；主题={ThemeManager.Mode}；右键菜单={(UseModernContextMenu ? "现代" : "经典")}；最近卡池={LastSelectedPoolName}");
            }
                CollectStartupErrors();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "LoadSettings 失败");
                System.Diagnostics.Debug.WriteLine("LoadSettings Error: " + ex.Message);
            }
        }

        /// <summary>启动时只检查并记录问题，不创建任何目录。</summary>
        private static void CollectStartupErrors()
        {
            try
            {
                foreach (var dir in SearchDirectories)
                {
                    if (!Directory.Exists(dir))
                    {
                        // 首次启动或目录尚未创建：不弹窗，创建卡池 / 导入资源后再生成
                        continue;
                    }

                    if (!IsDirectoryWritable(dir, createIfMissing: false))
                    {
                        AddStartupError($"卡池目录无法读取或写入：\n{dir}\n\n请检查文件夹权限。");
                        continue;
                    }

                    try { Directory.EnumerateDirectories(dir).Take(1).ToList(); }
                    catch (Exception ex) { AddStartupError($"读取卡池目录失败：\n{dir}\n\n{ex.Message}"); }
                }

                if (!Directory.Exists(ResourcesDir))
                {
                    // 资源目录在首次导入音频时自动创建，不在启动阶段报错
                }
                else
                {
                    if (!IsDirectoryWritable(ResourcesDir, createIfMissing: false))
                        AddStartupError($"资源目录无法读取或写入：\n{ResourcesDir}\n\n请检查文件夹权限。");

                    try { Directory.EnumerateFiles(ResourcesDir, "*", SearchOption.TopDirectoryOnly).Take(1).ToList(); }
                    catch (Exception ex) { AddStartupError($"读取资源目录失败：\n{ResourcesDir}\n\n{ex.Message}"); }
                }

                if (Directory.Exists(ResourcesDir) && !string.IsNullOrEmpty(LastMusicPath))
                {
                    string fullMusicPath = Path.IsPathRooted(LastMusicPath)
                        ? LastMusicPath
                        : Path.Combine(ResourcesDir, LastMusicPath);
                    if (!File.Exists(fullMusicPath))
                        AddStartupError($"背景音乐文件不存在：\n{fullMusicPath}\n\n请重新导入或选择音频文件。");
                }
            }
            catch (Exception ex)
            {
                AddStartupError("检查卡池/资源目录时发生错误：\n" + ex.Message);
            }
        }

        private static void AddStartupError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            RuntimeLog.Error(message);
            if (!StartupErrors.Contains(message)) StartupErrors.Add(message);
        }

        public static void SaveSettings()
        {
            try
            {
                RuntimeLog.Info("保存设置");
                if (!Directory.Exists(AppDataDir)) Directory.CreateDirectory(AppDataDir);

                SearchDirectories = SearchDirectories
                    .Where(d => IsValidDirectoryPath(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (SearchDirectories.Count == 0)
                {
                    // ★ 保存默认路径即可，不在这里创建目录
                    SearchDirectories.Add(Path.Combine(BaseDir, "SelectBox"));
                }

                if (string.IsNullOrEmpty(ResourcesDir) || !IsValidDirectoryPath(ResourcesDir))
                    ResourcesDir = Path.Combine(BaseDir, "Resources");
                if (string.IsNullOrWhiteSpace(ScreenshotDir))
                    ScreenshotDir = GetDefaultScreenshotDir();

                IniHelper.WriteValue("Paths", "SearchDirectories", string.Join(";", SearchDirectories), SettingsFile);
                IniHelper.WriteValue("Paths", "ResourceDir", ResourcesDir, SettingsFile);
                IniHelper.WriteValue("Paths", "ScreenshotDir", ScreenshotDir, SettingsFile);
                IniHelper.WriteValue("State", "LastSelectedPool", LastSelectedPoolName ?? "", SettingsFile);
                IniHelper.WriteValue("State", "LastSelectedPoolDir", LastSelectedPoolDir ?? "", SettingsFile);
                IniHelper.WriteValue("State", "IsNoRepeat", IsNoRepeat ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("State", "IsHistoryDisabled", IsHistoryDisabled ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("State", "IsAnimationDisabled", IsAnimationDisabled ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("Music", "MusicPath", LastMusicPath ?? "", SettingsFile);
                IniHelper.WriteValue("Music", "EnableMusic", IsMusicEnabled ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("Music", "IsMuted", IsMuted ? "1" : "0", SettingsFile);
            IniHelper.WriteValue("Music", "MuteWhenUnfocused", MuteWhenUnfocused ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("Music", "Volume", ((int)(MusicVolume * 100)).ToString(), SettingsFile);
                IniHelper.WriteValue("Music", "SoundEffectVolume", ((int)(SoundEffectVolume * 100)).ToString(), SettingsFile);
                IniHelper.WriteValue("Music", "SoundEffectMuted", IsSoundEffectMuted ? "1" : "0", SettingsFile);

                // ★ 功能隐藏
                IniHelper.WriteValue("UI", "HideProbabilityOption", HideProbabilityOption ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("UI", "HideAdvancedGacha", HideAdvancedGacha ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("UI", "HideEditPool", HideEditPool ? "1" : "0", SettingsFile);
                IniHelper.WriteValue("UI", "HideOptionSection", HideOptionSection ? "1" : "0", SettingsFile);
            IniHelper.WriteValue("UI", "HideGachaReport", HideGachaReport ? "1" : "0", SettingsFile);
            try
            {
                IniHelper.WriteValue("UI", "RenderPreference",
                    RenderPreference == RenderPreferenceKind.Fast ? "fast" : "smooth", SettingsFile);
            }
            catch { }
                IniHelper.WriteValue("UI", "ContextMenuStyle", UseModernContextMenu ? "modern" : "legacy", SettingsFile);
                IniHelper.WriteValue("UI", "SuppressIconCompatNotice", SuppressIconCompatNotice ? "1" : "0", SettingsFile);
            }
            catch (Exception ex)
            {
                ConfigInitError = ex.Message;
                RuntimeLog.Error(ex, "SaveSettings 失败");
                System.Diagnostics.Debug.WriteLine("SaveSettings Error: " + ex.Message);
            }
        }
    }
}