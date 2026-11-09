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

        /// <summary>配置目录/配置文件的读写错误（为空表示正常）。</summary>
        public static string ConfigInitError = null;

        /// <summary>本次启动读取卡池/资源目录时发现的问题（由主窗口统一提示）。</summary>
        public static readonly List<string> StartupErrors = new List<string>();

        private static string _lastLoggedConfigSignature;

        public static string AppDataDir = EnsureAppDataDir();

        /// <summary>
        /// 配置文件路径。默认 %APPDATA%\Random Select\settings.ini；
        /// 冒烟测试用 RS_SETTINGS_FILE 指到 Test\ 下，避免改动用户真实配置
        /// （和 ReportShareCache 的 RS_CACHE_DIR 是同一套隔离思路）。
        /// </summary>
        public static string SettingsFile = ResolveSettingsFile();

        private static string ResolveSettingsFile()
        {
            try
            {
                string overridePath = Environment.GetEnvironmentVariable("RS_SETTINGS_FILE");
                if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
            }
            catch { }
            return Path.Combine(AppDataDir, "settings.ini");
        }

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
        /// 窗口标题模板（选项 → 常规 → 外观 → 窗口标题）。
        /// 支持 {version} {time} {date} {pool} {page}，留空则用默认标题「随机抽取器」。
        /// 展开逻辑在 <see cref="WindowTitleFormatter"/>。
        /// </summary>
        public static string WindowTitleFormat = "";

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

        /// <summary>
        /// 是否为低配设备（虚拟机常见）：2 核及以下，或可用内存不足 6 GB。
        /// 低配时默认「加载速度优先」、按更低分辨率生成报告图 / PDF，避免内存被吃满。
        /// </summary>
        public static readonly bool IsLowEndDevice = DetectLowEndDevice();

        /// <summary>
        /// 低性能模式（省内存）：默认按设备自动判断，用户可在「选项 → 系统」里手动开关。
        /// 开启后报告图 / PDF 分辨率更低、背景模糊工作宽度更小、缓存更少。
        /// </summary>
        public static bool LowEndMode = IsLowEndDevice;

        /// <summary>
        /// 用户是否主动设置过「低性能模式」（配置文件里存在 UI/LowEndMode）。
        /// 欢迎页据此决定是否勾选：没主动设置过就一律不勾，避免首次运行被自动打开。
        /// </summary>
        public static bool LowEndModeExplicitlySet = false;

        /// <summary>卡池封面视频带声音时，是否自动静音背景音乐（选项 → 常规 → 声音）。</summary>
        public static bool MuteMusicOnCoverAudio = true;

        /// <summary>
        /// 是否显示欢迎页：首次运行（找不到配置文件）或用户在「选项 → 系统」里打开了「启动时显示欢迎页」。
        /// 欢迎页点「开始使用」后会自动关掉。
        /// </summary>
        public static bool ShowWelcome = false;

        private static bool DetectLowEndDevice()
        {
            try
            {
                // 排障用：RS_LOW_END=1 可强制按低配处理（虚拟机 / 远程桌面复现问题）
                if (Environment.GetEnvironmentVariable("RS_LOW_END") == "1") return true;

                if (Environment.ProcessorCount <= 2) return true;

                long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (available > 0 && available <= 6L * 1024 * 1024 * 1024) return true;
            }
            catch { }
            return false;
        }

        /// <summary>系统图标字体能否显示界面用到的全部图标；不能时需要进入图标兼容模式。</summary>
        public static bool IconCompatActive => IconSupport.CompatActive;

        /// <summary>是否需要提醒用户图标显示异常（只有真正缺字形才提示，正常降级不打扰）。</summary>
        public static bool IconCompatNeedsNotice => IconSupport.NeedsCompatNotice;

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

        /// <summary>主界面底部布局：简洁（新方案）/ 具体（旧方案）。</summary>
        public enum MainPageLayoutKind { Compact = 0, Detailed = 1 }

        /// <summary>当前主页布局（3.64 起默认「简洁」）。</summary>
        public static MainPageLayoutKind MainPageLayout = MainPageLayoutKind.Compact;

        public static bool HideGachaReport = false;

        /// <summary>已勾选「不再提示」后，不再显示图标兼容模式启动提示。</summary>
        public static bool SuppressIconCompatNotice = false;

        /// <summary>true = Windows 11 风格右键菜单；false = 旧版经典菜单。</summary>
        public static bool UseModernContextMenu = true;

        /// <summary>
        /// 主界面背景模糊（全局效果，选项 → 常规 → 外观）。
        /// 开启后把当前选中卡池的封面原图模糊后铺在主界面背景上；
        /// 视频 / GIF 封面会按当前画面实时重新取帧模糊。
        /// </summary>
        public static bool MainBackgroundBlur = false;

        /// <summary>主界面背景模糊强度（0~100，对应 ImageBlurUtil.MaxRadius）。</summary>
        public static double MainBackgroundBlurRadius = 45;

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

        /// <summary>设备评估信息只在日志里写一次。</summary>
        private static bool _deviceInfoLogged;

        /// <summary>
        /// 是否为冒烟测试进程（tests\SmokeTest 会设置 RS_SMOKE_TEST=1）。
        /// 测试要能反复创建主窗口，必须跳过单实例互斥体，否则用户正开着程序时测试会直接退出。
        /// </summary>
        internal static readonly bool IsSmokeTest =
            Environment.GetEnvironmentVariable("RS_SMOKE_TEST") == "1";

        private static void TextInput_Loaded(object sender, RoutedEventArgs e)
        {
            // 3.37：RichTextBox 自带一套系统右键菜单（ContextMenu 不是 null），必须显式替换
            if (sender is RichTextBox rich)
            {
                if (rich.ContextMenu is not ThemedTextContextMenu)
                    rich.ContextMenu = new ThemedTextContextMenu();
                return;
            }

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

        /// <summary>任何控件拿到键盘焦点时都去掉焦点虚线框（需求：界面上不出现这类虚线）。</summary>
        private static void FrameworkElement_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                try { element.FocusVisualStyle = null; } catch { }
            }
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
            // 3.37：RichTextBox（封面编辑页的文本框）以前没挂到主题菜单，右键弹的是系统白底菜单
            EventManager.RegisterClassHandler(typeof(RichTextBox), FrameworkElement.LoadedEvent,
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

            // ★ 全局去掉键盘焦点虚线框：模板里的默认 FocusVisualStyle 不受 App 资源覆盖影响，
            //   这里在元素获得键盘焦点时直接把它清空（设置一次后该元素永久为 null）。
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(FrameworkElement_GotKeyboardFocus), true);

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
                // 首次运行（还没有配置文件）→ 先走欢迎页，引导建目录 / 选性能
                bool hadSettingsFile = false;
                try { hadSettingsFile = File.Exists(SettingsFile); } catch { }
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

                string resDir = IniHelper.ReadValue("Paths", "ResourceDir", null, SettingsFile);
                // 3.74.1：没存过这一项才用默认目录；用户主动清空的（存成空值）就保持「未设置」，
                // 不再自动变回 程序目录\Resources
                if (resDir == null) resDir = Path.Combine(BaseDir, "Resources");
                ResourcesDir = (resDir ?? "").Trim();
                if (ResourcesDir.Length > 0 && !IsValidDirectoryPath(ResourcesDir))
                    ResourcesDir = "";
                // ★ 资源目录同样只记录路径；真正创建发生在用户导入音频并选择保存位置时

                string screenshotDir = IniHelper.ReadValue("Paths", "ScreenshotDir", "", SettingsFile);
                ScreenshotDir = string.IsNullOrWhiteSpace(screenshotDir)
                    ? GetDefaultScreenshotDir()
                    : screenshotDir.Trim();

                LastSelectedPoolName = IniHelper.ReadValue("State", "LastSelectedPool", "", SettingsFile);
                LastSelectedPoolDir = IniHelper.ReadValue("State", "LastSelectedPoolDir", "", SettingsFile);
                IsNoRepeat = IniHelper.ReadBool("State", "IsNoRepeat", true, SettingsFile);
                IsHistoryDisabled = IniHelper.ReadBool("State", "IsHistoryDisabled", false, SettingsFile);
                IsAnimationDisabled = IniHelper.ReadBool("State", "IsAnimationDisabled", false, SettingsFile);

                LastMusicPath = IniHelper.ReadValue("Music", "MusicPath", "", SettingsFile);
                IsMusicEnabled = IniHelper.ReadBool("Music", "EnableMusic", false, SettingsFile);
                IsMuted = IniHelper.ReadBool("Music", "IsMuted", false, SettingsFile);
                MuteWhenUnfocused = IniHelper.ReadBool("Music", "MuteWhenUnfocused", true, SettingsFile);
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

                IsSoundEffectMuted = IniHelper.ReadBool("Music", "SoundEffectMuted", false, SettingsFile);

                // 卡池音频：封面视频有声音时是否静音背景音乐
                MuteMusicOnCoverAudio = IniHelper.ReadBool("Music", "MuteMusicOnCoverAudio", true, SettingsFile);

                // ★ 功能隐藏
                HideProbabilityOption = IniHelper.ReadBool("UI", "HideProbabilityOption", false, SettingsFile);
                HideAdvancedGacha = IniHelper.ReadBool("UI", "HideAdvancedGacha", false, SettingsFile);
                HideEditPool = IniHelper.ReadBool("UI", "HideEditPool", false, SettingsFile);
                HideOptionSection = IniHelper.ReadBool("UI", "HideOptionSection", false, SettingsFile);
                HideGachaReport = IniHelper.ReadBool("UI", "HideGachaReport", false, SettingsFile);
            try
            {
                // 低配模式：配置里没存过就按设备自动判断（虚拟机 / 2 核 4G 默认开启）；
                // LowEndModeExplicitlySet 让欢迎页能区分「用户选过」和「设备默认」。
                string lowEndRaw = IniHelper.ReadValue("UI", "LowEndMode", "", SettingsFile);
                LowEndModeExplicitlySet = !string.IsNullOrWhiteSpace(lowEndRaw);
                LowEndMode = IniHelper.ReadBool("UI", "LowEndMode", IsLowEndDevice, SettingsFile);

                // 欢迎页：默认只在首次运行时出现。也允许在 settings.ini 里手动写
                // [UI] ShowWelcome=true（或旧写法 [State]）强制显示——
                // 以前这里只判断配置文件是否存在，手改的 true 会被直接忽略。
                string welcomeUi = IniHelper.ReadValue("UI", "ShowWelcome", "", SettingsFile);
                string welcomeState = IniHelper.ReadValue("State", "ShowWelcome", "", SettingsFile);
                if (!string.IsNullOrWhiteSpace(welcomeUi))
                    ShowWelcome = IniHelper.ReadBool("UI", "ShowWelcome", false, SettingsFile);
                else if (!string.IsNullOrWhiteSpace(welcomeState))
                    ShowWelcome = IniHelper.ReadBool("State", "ShowWelcome", false, SettingsFile);
                else
                    ShowWelcome = !hadSettingsFile;

                // 没存过该项时按设备能力给默认值：低配默认「加载速度优先」，普通设备默认「流畅优先」
                string renderPref = IniHelper.ReadValue("UI", "RenderPreference", "", SettingsFile);
                if (string.IsNullOrWhiteSpace(renderPref))
                    RenderPreference = LowEndMode ? RenderPreferenceKind.Fast : RenderPreferenceKind.Smooth;
                else
                    RenderPreference = string.Equals(renderPref.Trim(), "fast", StringComparison.OrdinalIgnoreCase)
                        ? RenderPreferenceKind.Fast
                        : RenderPreferenceKind.Smooth;

                // 主页布局：简洁（新）/ 具体（旧），默认简洁；没存过或值不认识都按简洁
                string layout = IniHelper.ReadValue("UI", "MainPageLayout", "compact", SettingsFile);
                MainPageLayout = string.Equals((layout ?? "").Trim(), "detailed", StringComparison.OrdinalIgnoreCase)
                    ? MainPageLayoutKind.Detailed
                    : MainPageLayoutKind.Compact;
            }
            catch { RenderPreference = LowEndMode ? RenderPreferenceKind.Fast : RenderPreferenceKind.Smooth; }
                UseModernContextMenu = IniHelper.ReadValue("UI", "ContextMenuStyle", "modern", SettingsFile) != "legacy";
                MainBackgroundBlur = IniHelper.ReadBool("UI", "MainBackgroundBlur", false, SettingsFile);
                if (!double.TryParse(IniHelper.ReadValue("UI", "MainBackgroundBlurRadius", "45", SettingsFile), out MainBackgroundBlurRadius))
                    MainBackgroundBlurRadius = 45;
                if (double.IsNaN(MainBackgroundBlurRadius) || double.IsInfinity(MainBackgroundBlurRadius)) MainBackgroundBlurRadius = 45;
                MainBackgroundBlurRadius = Math.Max(1, Math.Min(100, MainBackgroundBlurRadius));
                WindowTitleFormat = IniHelper.ReadValue("UI", "WindowTitle", "", SettingsFile) ?? "";
                SuppressIconCompatNotice = IniHelper.ReadBool("UI", "SuppressIconCompatNotice", false, SettingsFile)
                    || IniHelper.ReadBool("UI", "SuppressWin7CompatNotice", false, SettingsFile);

                // F12 临时显示状态不保存，每次启动重置
                IsHiddenRevealed = false;

                string configSignature = string.Join(" | ", SearchDirectories) + "|" + ResourcesDir + "|" + ScreenshotDir
                + "|" + ThemeManager.Mode + "|" + UseModernContextMenu + "|" + LastSelectedPoolName;
            if (!string.Equals(configSignature, _lastLoggedConfigSignature, StringComparison.Ordinal))
            {
                _lastLoggedConfigSignature = configSignature;
                RuntimeLog.Info($"配置加载：卡池目录={string.Join(" | ", SearchDirectories)}；资源目录={ResourcesDir}；截图目录={ScreenshotDir}；主题={ThemeManager.Mode}；右键菜单={(UseModernContextMenu ? "现代" : "经典")}；最近卡池={LastSelectedPoolName}");
            }

                if (!_deviceInfoLogged)
                {
                    _deviceInfoLogged = true;
                    try
                    {
                        long availableMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
                        RuntimeLog.Info($"设备评估：{Environment.ProcessorCount} 核，可用内存 {availableMb} MB，低配={IsLowEndDevice}");
                    }
                    catch { }
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

                // 3.74.1：允许「未设置资源目录」——用户清空后保持空，不再自动填回默认目录
                if (!string.IsNullOrWhiteSpace(ResourcesDir) && !IsValidDirectoryPath(ResourcesDir))
                    ResourcesDir = Path.Combine(BaseDir, "Resources");
                if (string.IsNullOrWhiteSpace(ScreenshotDir))
                    ScreenshotDir = GetDefaultScreenshotDir();

                IniHelper.WriteValue("Paths", "SearchDirectories", string.Join(";", SearchDirectories), SettingsFile);
                IniHelper.WriteValue("Paths", "ResourceDir", ResourcesDir, SettingsFile);
                IniHelper.WriteValue("Paths", "ScreenshotDir", ScreenshotDir, SettingsFile);
                IniHelper.WriteBool("UI", "LowEndMode", LowEndMode, SettingsFile);
                IniHelper.WriteValue("State", "LastSelectedPool", LastSelectedPoolName ?? "", SettingsFile);
                IniHelper.WriteValue("State", "LastSelectedPoolDir", LastSelectedPoolDir ?? "", SettingsFile);
                IniHelper.WriteBool("State", "IsNoRepeat", IsNoRepeat, SettingsFile);
                IniHelper.WriteBool("State", "IsHistoryDisabled", IsHistoryDisabled, SettingsFile);
                IniHelper.WriteBool("State", "IsAnimationDisabled", IsAnimationDisabled, SettingsFile);
                IniHelper.WriteValue("Music", "MusicPath", LastMusicPath ?? "", SettingsFile);
                IniHelper.WriteBool("Music", "EnableMusic", IsMusicEnabled, SettingsFile);
                IniHelper.WriteBool("Music", "IsMuted", IsMuted, SettingsFile);
            IniHelper.WriteBool("Music", "MuteWhenUnfocused", MuteWhenUnfocused, SettingsFile);
                IniHelper.WriteValue("Music", "Volume", ((int)(MusicVolume * 100)).ToString(), SettingsFile);
                IniHelper.WriteValue("Music", "SoundEffectVolume", ((int)(SoundEffectVolume * 100)).ToString(), SettingsFile);
                IniHelper.WriteBool("Music", "SoundEffectMuted", IsSoundEffectMuted, SettingsFile);
                IniHelper.WriteBool("Music", "MuteMusicOnCoverAudio", MuteMusicOnCoverAudio, SettingsFile);

                // ★ 功能隐藏
                IniHelper.WriteBool("UI", "HideProbabilityOption", HideProbabilityOption, SettingsFile);
                IniHelper.WriteBool("UI", "HideAdvancedGacha", HideAdvancedGacha, SettingsFile);
                IniHelper.WriteBool("UI", "HideEditPool", HideEditPool, SettingsFile);
                IniHelper.WriteBool("UI", "HideOptionSection", HideOptionSection, SettingsFile);
            IniHelper.WriteBool("UI", "HideGachaReport", HideGachaReport, SettingsFile);
            try
            {
                IniHelper.WriteValue("UI", "RenderPreference",
                    RenderPreference == RenderPreferenceKind.Fast ? "fast" : "smooth", SettingsFile);
                IniHelper.WriteValue("UI", "MainPageLayout",
                    MainPageLayout == MainPageLayoutKind.Detailed ? "detailed" : "compact", SettingsFile);
            }
            catch { }
                IniHelper.WriteValue("UI", "ContextMenuStyle", UseModernContextMenu ? "modern" : "legacy", SettingsFile);
                IniHelper.WriteBool("UI", "MainBackgroundBlur", MainBackgroundBlur, SettingsFile);
                IniHelper.WriteValue("UI", "MainBackgroundBlurRadius",
                    ((int)Math.Round(Math.Max(1, Math.Min(100, MainBackgroundBlurRadius)))).ToString(), SettingsFile);
                IniHelper.WriteValue("UI", "WindowTitle", WindowTitleFormat ?? "", SettingsFile);
                IniHelper.WriteBool("UI", "SuppressIconCompatNotice", SuppressIconCompatNotice, SettingsFile);
                // 3.74.1：欢迎页只有第一次（或手动写 ShowWelcome=true）才出现；用户点「开始使用」后
                // 这里会把 false 落盘，避免下次启动又被弹出
                IniHelper.WriteBool("UI", "ShowWelcome", ShowWelcome, SettingsFile);
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