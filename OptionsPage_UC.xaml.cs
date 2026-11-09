using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RandomDrawer.Messaging;

namespace RandomDrawer;

public partial class OptionsPage_UC : UserControl, IPageAnimation, IComponentConnector, IStyleConnector
{
	private class PoolEntry
	{
		public string DisplayName { get; set; }

		public string Dir { get; set; }
	}

	private bool _isLoading = true;

	private string _musicScanError = "";

	/// <summary>资源目录还没创建（不是错误）：刷新 / 打开目录时给一句可操作的提示，而不是报错。</summary>
	private bool _musicDirMissing;

	private const string IconMute = "\ue74f";

	private const string IconVolumeLow = "\ue993";

	private const string IconVolumeMid = "\ue994";

	private const string IconVolumeHigh = "\ue995";

	private const string IconHistoryOn = "\ue72c";

	private const string IconHistoryOff = "\ue711";

	private const string IconAnimationOn = "\ue72c";

	private const string IconAnimationOff = "\ue711";

	private static readonly string[] SupportedAudioExtensions = new string[7] { ".mp3", ".wav", ".wma", ".m4a", ".flac", ".aac", ".ogg" };

	private static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg" };

	private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".jfif" };

	private List<PoolEntry> _allPoolEntries = new List<PoolEntry>();

	private List<Key> _cheatCode = new List<Key>
	{
		(Key)24,
		(Key)24,
		(Key)26,
		(Key)26,
		(Key)23,
		(Key)25,
		(Key)23,
		(Key)25
	};

	private int _cheatIndex = 0;

	private DispatcherTimer _gifTimer;

	private GifBitmapDecoder _gifDecoder;

	private int _gifFrameIndex;

	private string _currentGifPath = "";

	private bool _initialized = false;

	private int _currentNavIndex = 0;

	private Action<AppMessages.PageActivated> _onPageActivated;

	private Action<AppMessages.PoolListChanged> _onPoolListChanged;

	private Action<AppMessages.UiOptionsChanged> _onUiOptionsChanged;

	private DispatcherTimer _poolWatchTimer;

	private string _lastPoolSignature = "";

	private static readonly TimeSpan NavEnterDuration = TimeSpan.FromMilliseconds(280L);

	private static readonly TimeSpan NavExitDuration = TimeSpan.FromMilliseconds(150L);

	private const double NavPanelHiddenX = -150.0;

	private const double NavPanelReverseX = 150.0;

	private const double ContentPanelHiddenY = 60.0;

	private const double ContentPanelReverseY = -60.0;

	private ScrollViewer _edgeTarget;

	public bool IsAboutVisible => PanelAbout != null && PanelAbout.Visibility == Visibility.Visible;

	public OptionsPage_UC()
	{
		InitializeComponent();
		_onPageActivated = OnPageActivated;
		_onPoolListChanged = OnPoolListChanged;
		_onUiOptionsChanged = OnUiOptionsChanged;
		MessageBus.Subscribe(_onPageActivated);
		MessageBus.Subscribe(_onPoolListChanged);
		MessageBus.Subscribe(_onUiOptionsChanged);
		Loaded += OptionsPage_UC_Loaded;
		Unloaded += OptionsPage_UC_Unloaded;
		InitWindowTitleUI();

		// 目录行限宽：窗口变宽时不再把输入框拉成一整行
		if (ContentPanel != null)
		{
			ContentPanel.SizeChanged += (s, e) => UpdateDirectoryRowWidths();
		}
	}

	/// <summary>
	/// 「卡池目录 / 资源目录 / 截图目录」三行的最大宽度：760，窗口更窄时按可用宽度收缩，
	/// 保证按钮不会被挤到屏幕边缘。
	/// </summary>
	private void UpdateDirectoryRowWidths()
	{
		try
		{
			if (ContentPanel == null) return;

			double available = ContentPanel.ActualWidth - 24;
			if (available <= 0) return;

			double width = Math.Max(360, Math.Min(760, available));

			if (ResourceDirRow != null) ResourceDirRow.Width = width;
			if (ScreenshotDirRow != null) ScreenshotDirRow.Width = width;
		}
		catch { }
	}

	/// <summary>
	/// 刷新「卡池目录 / 资源目录」两块的状态显示：
	/// 没创建时只显示「未创建或添加…」占位，不显示路径文本框、「打开文件夹」按钮，
	/// 也不再拿一个并不存在的默认目录冒充已配置目录。
	/// </summary>
	private void RefreshDirectoryUI()
	{
		try
		{
			// ① 卡池目录：只列真实存在的目录；一个都没有时显示占位
			var existingDirs = new List<string>();
			try
			{
				foreach (string dir in App.SearchDirectories ?? new List<string>())
				{
					if (string.IsNullOrWhiteSpace(dir)) continue;
					try { if (Directory.Exists(dir)) existingDirs.Add(dir); }
					catch { }
				}
			}
			catch { }

			if (ListDirs != null)
			{
				ListDirs.ItemsSource = null;
				ListDirs.ItemsSource = existingDirs;
				ListDirs.Visibility = existingDirs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			}
			if (EmptyPoolDirsPanel != null)
				EmptyPoolDirsPanel.Visibility = existingDirs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

			// ② 资源目录
			bool resExists = ResourcesDirExists();
			if (TxtResDir != null) TxtResDir.Text = resExists ? (App.ResourcesDir ?? "") : "";
			if (ResDirLabel != null) ResDirLabel.Visibility = resExists ? Visibility.Visible : Visibility.Collapsed;
			if (ResDirBox != null) ResDirBox.Visibility = resExists ? Visibility.Visible : Visibility.Collapsed;
			if (ResDirEmptyPanel != null) ResDirEmptyPanel.Visibility = resExists ? Visibility.Collapsed : Visibility.Visible;
			if (BtnOpenResDir != null) BtnOpenResDir.Visibility = resExists ? Visibility.Visible : Visibility.Collapsed;
		}
		catch { }
	}


	private void OptionsPage_UC_Loaded(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;
		StartPoolWatcher();
		UpdateDirectoryRowWidths();
		if (EdgeScrollBar != null)
		{
			EdgeScrollBar.ValueChanged += EdgeScrollBar_ValueChanged;
			BindEdgeScrollBarFor(_currentNavIndex);
		}
		LoadUIState();
		LoadWindowTitleUI();
		LoadMusicSettingsUI();
		LoadAppVersion();
		LoadRenderPreferenceUI();
		_isLoading = false;
		UpdateContextMenuStyleUI();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
		{
			try
			{
				double y = (double)_currentNavIndex * 50.0;
				NavFillTransform.BeginAnimation(TranslateTransform.YProperty, null);
				NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
				NavFillTransform.Y = y;
				NavIndicatorTransform.Y = y;
				NavFillTransform.X = 0.0;
				NavFill.Opacity = 1.0;
			}
			catch
			{
			}
		}), (DispatcherPriority)6, Array.Empty<object>());
	}

	private void OptionsPage_UC_Unloaded(object sender, RoutedEventArgs e)
	{
		try
		{
			StopGifPlayback();
		}
		catch
		{
		}
		StopPoolWatcher();
		try
		{
			VidCover.Stop();
			VidCover.Source = null;
		}
		catch
		{
		}
		try
		{
			SaveMusicSettings();
		}
		catch
		{
		}
		try
		{
			App.SaveSettings();
		}
		catch
		{
		}
	}

	private void OnPageActivated(AppMessages.PageActivated msg)
	{
		if (msg == null || msg.PageKey != "Options")
		{
			return;
		}
		((DispatcherObject)this).Dispatcher.Invoke((Action)(() =>
		{
			if (_initialized)
			{
				LoadUIState();
				bool isLoading = _isLoading;
				_isLoading = true;
				RefreshMusicList();
				_isLoading = isLoading;
				ComboPools_SelectionChanged(null, null);
			}
		}));
	}

	private void OnPoolListChanged(AppMessages.PoolListChanged msg)
	{
		((DispatcherObject)this).Dispatcher.Invoke((Action)(() =>
		{
			if (_initialized)
			{
				LoadPoolComboBox();
			}
		}));
	}

	private void OnUiOptionsChanged(AppMessages.UiOptionsChanged msg)
	{
		((DispatcherObject)this).Dispatcher.Invoke((Action)(() =>
		{
			if (_initialized)
			{
				RefreshHideOptionsUI();
			}
		}));
	}

	private void LoadAppVersion()
	{
		if (TxtVersion == null)
		{
			return;
		}
		try
		{
			Assembly executingAssembly = Assembly.GetExecutingAssembly();
			string text = executingAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			if (!string.IsNullOrEmpty(text))
			{
				int num = text.IndexOf('+');
				if (num >= 0)
				{
					text = text.Substring(0, num);
				}
				TxtVersion.Text = text;
				return;
			}
			Version version = executingAssembly.GetName().Version;
			if (!(version != null))
			{
				return;
			}
			string text2 = version.ToString();
			if (text2.EndsWith(".0") && version.Build >= 0 && version.Revision == 0)
			{
				text2 = text2.TrimEnd('0').TrimEnd('.');
				if (text2.Split('.').Length < 3)
				{
					text2 = version.ToString(3);
				}
			}
			TxtVersion.Text = text2;
		}
		catch
		{
			TxtVersion.Text = "1.0.0";
		}
	}

	public void SelectNormalPage()
	{
		NavNormal.IsChecked = true;
	}

	private void UpdateThemeUI()
	{
		if (ComboTheme == null)
		{
			return;
		}
		bool isLoading = _isLoading;
		_isLoading = true;
		try
		{
			int selectedIndex = 0;
			if (ThemeManager.Mode == ThemeMode.Dark)
			{
				selectedIndex = 2;
			}
			else if (ThemeManager.Mode == ThemeMode.Light)
			{
				selectedIndex = 1;
			}
			ComboTheme.SelectedIndex = selectedIndex;
		}
		catch
		{
		}
		_isLoading = isLoading;
	}

	private void ComboTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_isLoading && ComboTheme != null)
		{
			string text = (ComboTheme.SelectedItem as ComboBoxItem)?.Tag as string;
			ThemeManager.SetMode(ThemeManager.ParseMode(text));
		}
	}

	// 3.64：主页布局（简洁 = 新方案 / 具体 = 旧方案）
	private void UpdateMainPageLayoutUI()
	{
		if (ComboMainPageLayout == null) return;
		bool isLoading = _isLoading;
		_isLoading = true;
		try
		{
			ComboMainPageLayout.SelectedIndex =
				App.MainPageLayout == App.MainPageLayoutKind.Detailed ? 1 : 0;
		}
		catch
		{
		}
		_isLoading = isLoading;
	}

	private void ComboMainPageLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isLoading || ComboMainPageLayout == null) return;
		try
		{
			App.MainPageLayout = ComboMainPageLayout.SelectedIndex == 1
				? App.MainPageLayoutKind.Detailed
				: App.MainPageLayoutKind.Compact;
			App.SaveSettings();
			MessageBus.Publish(new AppMessages.UiOptionsChanged());
		}
		catch
		{
		}
	}

	private void UpdateContextMenuStyleUI()
	{
		if (ComboContextMenuStyle != null)
		{
			bool isLoading = _isLoading;
			_isLoading = true;
			try
			{
				ComboContextMenuStyle.SelectedIndex = ((!App.UseModernContextMenu) ? 1 : 0);
			}
			catch
			{
			}
			_isLoading = isLoading;
		}
	}

	private void ComboContextMenuStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isLoading || ComboContextMenuStyle == null)
		{
			return;
		}
		string text = (ComboContextMenuStyle.SelectedItem as ComboBoxItem)?.Tag as string;
		App.UseModernContextMenu = text != "legacy";
		try
		{
			App.SaveSettings();
		}
		catch
		{
		}
		try
		{
			MessageBus.Publish(new AppMessages.UiOptionsChanged());
		}
		catch
		{
		}
	}

	// ================================================================
	// 窗口标题（选项 → 常规 → 外观）
	// 支持 {version} / {time} / {date} / {pool} / {page}，留空回默认标题。
	// ================================================================
	private bool _loadingWindowTitle;
	private DispatcherTimer _windowTitleSaveTimer;
	private HintBubble _windowTitleHint;

	// ================================================================
	// 主界面背景模糊（选项 → 常规 → 外观，全局效果）
	// ================================================================
	private bool _loadingMainBgBlur;
	private DispatcherTimer _mainBgBlurSaveTimer;

	/// <summary>气泡提示的文字：和历史记录页搜索框用的是同一个气泡样式。</summary>
	private const string WindowTitleHintText =
		"窗口标题可以带参数，显示时会自动替换：\n" +
		"{version}：软件版本\n" +
		"{time}：当前时间（HH:mm:ss）\n" +
		"{date}：当前日期（年/月/日）\n" +
		"{pool}：当前卡池名称（仅主界面生效）\n" +
		"{page}：当前页面标题\n" +
		"自定义标题将在去除参数后写入拆分分享长图或分享PDF。\n" +
		"留空则使用默认标题「随机抽取器」。";

	private void InitWindowTitleUI()
	{
		try
		{
			_windowTitleHint = new HintBubble(this, WindowTitleHintText);
		}
		catch { }
	}

	private void LoadWindowTitleUI()
	{
		if (TxtWindowTitle == null) return;

		_loadingWindowTitle = true;
		try
		{
			TxtWindowTitle.Text = App.WindowTitleFormat ?? "";
			ApplyWindowTitleValidation(WindowTitleValidator.IsValid(App.WindowTitleFormat), App.WindowTitleFormat);
		}
		catch { }
		_loadingWindowTitle = false;
	}

	/// <summary>输入合法：恢复默认边框、隐藏提示；不合法：红框 + 列出不可用符号。</summary>
	private void ApplyWindowTitleValidation(bool valid, string text)
	{
		try
		{
			if (TxtWindowTitle == null) return;

			if (valid)
			{
				TxtWindowTitle.ClearValue(Control.BorderBrushProperty);
				TxtWindowTitle.ClearValue(Control.BorderThicknessProperty);
				TxtWindowTitle.ToolTip = null;

				if (TxtWindowTitleInvalid != null)
				{
					TxtWindowTitleInvalid.Text = "";
					TxtWindowTitleInvalid.Visibility = Visibility.Collapsed;
				}
				return;
			}

			var red = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));
			red.Freeze();
			TxtWindowTitle.BorderBrush = red;
			TxtWindowTitle.BorderThickness = new Thickness(2);

			string message = "不可用符号：" + WindowTitleValidator.DescribeInvalid(text);
			TxtWindowTitle.ToolTip = message;

			if (TxtWindowTitleInvalid != null)
			{
				TxtWindowTitleInvalid.Text = message;
				TxtWindowTitleInvalid.Visibility = Visibility.Visible;
			}
		}
		catch { }
	}

	private void TxtWindowTitle_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_loadingWindowTitle || TxtWindowTitle == null) return;

		// ★ 只拦「会导致错误」的字符：回车 / 换行 / 制表符这类控制字符会把 settings.ini 的一行拆成两行
		string text = TxtWindowTitle.Text ?? "";
		bool valid = WindowTitleValidator.IsValid(text);
		ApplyWindowTitleValidation(valid, text);
		if (!valid) return;   // 非法输入不生效、不落盘，等用户改好

		App.WindowTitleFormat = text;

		// 立刻生效：主窗口自己按模板重算标题
		try { (Window.GetWindow(this) as MainWindow)?.UpdateWindowTitle(); }
		catch { }

		ScheduleWindowTitleSave();
	}

	// ================================================================
	// 主界面背景模糊：复选框 + 强度滑块（全局生效，不再是单个卡池的开关）
	// ================================================================

	/// <summary>把「主界面背景模糊」的开关与强度回填到界面。</summary>
	private void LoadMainBgBlurUI()
	{
		if (ChkMainBgBlur == null) return;

		_loadingMainBgBlur = true;
		try
		{
			ChkMainBgBlur.IsChecked = App.MainBackgroundBlur;

			if (SliderMainBgBlur != null)
			{
				double value = Math.Max(SliderMainBgBlur.Minimum,
					Math.Min(SliderMainBgBlur.Maximum, App.MainBackgroundBlurRadius));
				SliderMainBgBlur.Value = value;
			}

			if (TxtMainBgBlurValue != null)
				TxtMainBgBlurValue.Text = ((int)Math.Round(App.MainBackgroundBlurRadius)).ToString();

			UpdateMainBgBlurControls();
		}
		catch { }
		finally { _loadingMainBgBlur = false; }
	}

	/// <summary>没勾选时滑块置灰：避免「调了强度却没生效」的困惑。</summary>
	private void UpdateMainBgBlurControls()
	{
		try
		{
			if (SliderMainBgBlur != null)
				SliderMainBgBlur.IsEnabled = ChkMainBgBlur != null && ChkMainBgBlur.IsChecked == true;
		}
		catch { }
	}

	private void ChkMainBgBlur_Click(object sender, RoutedEventArgs e)
	{
		if (_isLoading || _loadingMainBgBlur) return;

		App.MainBackgroundBlur = ChkMainBgBlur != null && ChkMainBgBlur.IsChecked == true;
		UpdateMainBgBlurControls();
		App.SaveSettings();

		// 主界面 / 其它页面立即按新设置重算背景
		MessageBus.Publish(new AppMessages.UiOptionsChanged());
	}

	private void SliderMainBgBlur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (TxtMainBgBlurValue != null)
			TxtMainBgBlurValue.Text = ((int)Math.Round(e.NewValue)).ToString();

		if (_isLoading || _loadingMainBgBlur) return;

		// 3.75.1：允许拖到 0，表示「完全不模糊」（主界面直接用原图）
		App.MainBackgroundBlurRadius = Math.Max(0, Math.Min(100, e.NewValue));
		MessageBus.Publish(new AppMessages.UiOptionsChanged());

		// 拖动时每帧都写配置太重：停下来 350ms 再落盘
		ScheduleMainBgBlurSave();
	}

	private void ScheduleMainBgBlurSave()
	{
		try
		{
			if (_mainBgBlurSaveTimer == null)
			{
				_mainBgBlurSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
				_mainBgBlurSaveTimer.Tick += (s, e) =>
				{
					try
					{
						_mainBgBlurSaveTimer.Stop();
						App.SaveSettings();
					}
					catch { }
				};
			}

			_mainBgBlurSaveTimer.Stop();
			_mainBgBlurSaveTimer.Start();
		}
		catch { }
	}

	private void SliderMainBgBlur_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (_isLoading || SliderMainBgBlur == null) return;

		double step = 5.0;
		double value = e.Delta > 0
			? Math.Min(SliderMainBgBlur.Maximum, SliderMainBgBlur.Value + step)
			: Math.Max(SliderMainBgBlur.Minimum, SliderMainBgBlur.Value - step);
		SliderMainBgBlur.Value = value;
		e.Handled = true;
	}

	// ================================================================
	// 3.70：行内「？」说明——鼠标悬浮显示气泡（和窗口标题提示同一个 HintBubble），不再弹窗
	// ================================================================
	private readonly List<HintBubble> _infoHints = new List<HintBubble>();
	private bool _infoHintsInited;

	private const string RenderPreferenceHintText =
		"流畅优先：优先确保界面加载流畅，但可能会增加加载时间。\n\n" +
		"加载速度优先：优先考虑加载速度，可能导致部分动画消失或界面卡顿。";

	private const string LowEndModeHintText =
		"电脑较慢、内存较小时打开，能让切换页面和生成报告更顺一些。\n\n" +
		"开启后：\n" +
		"· 生成抽取报告图、分享预览图和 PDF 时占用内存更少，不容易卡住或提示内存不足；\n" +
		"· 历史记录选择「全部显示」时分段加载，先显示一部分，滚动到底部再继续补上；\n" +
		"· 封面、主页背景和报告头像这类大图会以更小的尺寸加载，画面会稍微模糊一点。\n\n" +
		"电脑配置正常时保持关闭就好，关闭后所有图片都按原来的清晰度显示。";

	private const string HideSectionHintText =
		"· 开启「隐藏基于概率的抽取」会一并开启并锁定「隐藏高级抽卡规则」。\n" +
		"· 「隐藏查看抽取报告」会同时隐藏主界面与历史记录页的报告入口。\n" +
		"· 在任意界面按 F12 可临时显示 / 隐藏所有已隐藏的功能（重启恢复）。\n" +
		"· 若连「功能隐藏」本身也被隐藏，只能用 F12 临时把它调出来。";

	private void AttachInfoHint(FrameworkElement target, string text)
	{
		try
		{
			if (target == null || string.IsNullOrEmpty(text)) return;
			var hint = new HintBubble(target, text);
			_infoHints.Add(hint);
			target.MouseEnter += (s, e) => hint.Show(new Point(target.ActualWidth / 2, target.ActualHeight));
			target.MouseMove += (s, e) => hint.Show(e.GetPosition(target));
			target.MouseLeave += (s, e) => hint.Hide();
		}
		catch { }
	}

	private void InitInfoHints()
	{
		if (_infoHintsInited) return;
		_infoHintsInited = true;
		AttachInfoHint(InfoMainBgBlur, "模糊选中的卡池封面并应用到主界面背景。");
		AttachInfoHint(InfoRenderPreference, RenderPreferenceHintText);
		AttachInfoHint(InfoLowEndMode, LowEndModeHintText);
		AttachInfoHint(InfoHideSection, HideSectionHintText);
	}

	/// <summary>按键停止 500 ms 后才落盘，避免每敲一个字符写一次配置。</summary>
	private void ScheduleWindowTitleSave()
	{
		try
		{
			if (_windowTitleSaveTimer == null)
			{
				_windowTitleSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
				_windowTitleSaveTimer.Tick += (s, e) =>
				{
					try
					{
						_windowTitleSaveTimer.Stop();
						App.SaveSettings();
					}
					catch { }
				};
			}

			_windowTitleSaveTimer.Stop();
			_windowTitleSaveTimer.Start();
		}
		catch { }
	}

	private void TxtWindowTitle_MouseEnter(object sender, MouseEventArgs e) => ShowWindowTitleHint(e.GetPosition(this));

	private void TxtWindowTitle_MouseMove(object sender, MouseEventArgs e) => ShowWindowTitleHint(e.GetPosition(this));

	private void TxtWindowTitle_MouseLeave(object sender, MouseEventArgs e)
	{
		try { _windowTitleHint?.Hide(); } catch { }
	}

	private void ShowWindowTitleHint(Point pos)
	{
		try
		{
			if (!IsVisible) return;
			if (_windowTitleHint == null) InitWindowTitleUI();
			_windowTitleHint?.Show(pos);
		}
		catch { }
	}

	private void BtnChangelog_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			ChangelogDialog.Show(Window.GetWindow((DependencyObject)(object)this));
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "无法打开更新日志：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	/// <summary>打开「开源许可」声明（MIT 与第三方组件说明）。</summary>
	private void BtnLicenses_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			LicensesDialog.Show(Window.GetWindow((DependencyObject)(object)this));
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "无法打开开源许可：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	// ================================================================
	// 性能：视觉呈现模式
	// ================================================================
	private bool _loadingRenderPreference;

	private void LoadRenderPreferenceUI()
	{
		try
		{
			if (ComboRenderPreference == null) return;
			_loadingRenderPreference = true;
			ComboRenderPreference.SelectedIndex =
				App.RenderPreference == App.RenderPreferenceKind.Fast ? 1 : 0;

			// 3.58：低配模式（3.63 起不再有「启动时显示欢迎页」开关）
			// 3.66：右侧不再显示说明文字，改成文字旁边的「?」
			if (ChkLowEndMode != null) ChkLowEndMode.IsChecked = App.LowEndMode;
		}
		catch { }
		finally { _loadingRenderPreference = false; }
	}

	private void ChkLowEndMode_Click(object sender, RoutedEventArgs e)
	{
		if (_loadingRenderPreference) return;
		try
		{
			App.LowEndMode = ChkLowEndMode != null && ChkLowEndMode.IsChecked == true;
			App.LowEndModeExplicitlySet = true;
			App.SaveSettings();
		}
		catch { }
	}

	private void ComboRenderPreference_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loadingRenderPreference) return;

		try
		{
			App.RenderPreference = ComboRenderPreference.SelectedIndex == 1
				? App.RenderPreferenceKind.Fast
				: App.RenderPreferenceKind.Smooth;
			App.SaveSettings();
		}
		catch { }
	}

	/// <summary>点击「?」说明图标：弹出视觉呈现模式的说明。</summary>
	private void BtnBiliHome_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "https://space.bilibili.com/353824557",
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "打开链接失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	// ================================================================
	// 帮助页：快速导航（平滑滚动）与搜索结果导航
	// ================================================================
	private sealed class HelpRunInfo
	{
		public string Text = "";
		/// <summary>这一项是行内换行（LineBreak），渲染恢复时必须原样重建。</summary>
		public bool IsLineBreak;
		public FontWeight Weight = FontWeights.Normal;
		public Brush Fore;
		public FontFamily Family;
		public double Size = double.NaN;
		public Brush Back;
	}

	private readonly List<(TextBlock Block, int Start, int Length)> _helpMatches =
		new List<(TextBlock, int, int)>();
	private int _helpMatchIndex = -1;
	private readonly Dictionary<TextBlock, List<HelpRunInfo>> _helpHighlighted =
		new Dictionary<TextBlock, List<HelpRunInfo>>();
	private DispatcherTimer _helpScrollTimer;
	/// <summary>搜索命中的底色：浅色浅蓝 / 深色深蓝（跟着主题走，深色下不能再用浅蓝底）。</summary>
	private static Brush GetHelpHighlightBrush(bool current)
	{
		try
		{
			Brush b = ThemeManager.GetBrush(current ? "Ui.SearchHighlightCurrent" : "Ui.SearchHighlight");
			if (b != null) return b;
		}
		catch { }
		return new SolidColorBrush(current
			? Color.FromRgb(0x5B, 0x9B, 0xD5)
			: Color.FromRgb(0xD6, 0xE6, 0xFF));
	}

	/// <summary>命中文字的颜色：浅色用深字 / 深色用浅字。</summary>
	private static Brush GetHelpHighlightFore(bool current)
	{
		try
		{
			Brush b = ThemeManager.GetBrush(current ? "Fg_FFFFFF" : "Fg_HelpText");
			if (b != null) return b;
		}
		catch { }
		return Brushes.White;
	}

	private readonly Dictionary<TextBlock, List<(int Start, int Length, int Index)>> _helpRanges =
		new Dictionary<TextBlock, List<(int, int, int)>>();

	private TextBlock _helpCurrentBlock;

	/// <summary>目录：平滑滚动到对应章节。</summary>
	private void HelpToc_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!(sender is FrameworkElement fe) || !(fe.Tag is string tag)) return;
			if (FindName(tag) is FrameworkElement target) ScrollHelpTo(target);
		}
		catch { }
	}

	private void HelpSearch_SearchChanged(string text)
	{
		try
		{
			string q = (text ?? "").Trim();
			_helpMatches.Clear();
			_helpMatchIndex = -1;
			ClearHelpHighlight();

			if (q.Length > 0)
			{
				// 按文档顺序（从上到下）收集每个命中位置
				foreach (var block in EnumerateHelpTextBlocks())
				{
					string content = GetHelpText(block);
					int from = 0;
					while (from <= content.Length - q.Length)
					{
						int hit = content.IndexOf(q, from, StringComparison.CurrentCultureIgnoreCase);
						if (hit < 0) break;
						_helpMatches.Add((block, hit, q.Length));
						from = hit + Math.Max(1, q.Length);
					}
				}
			}

			bool many = _helpMatches.Count > 1;
			ShowHelpMatchBar(many);
			SetHelpSearchError(q.Length > 0 && _helpMatches.Count == 0);

			if (_helpMatches.Count > 0)
			{
				HighlightAllHelpMatches();
				_helpMatchIndex = 0;
				GoToHelpMatch();
			}
			else
			{
				UpdateHelpMatchText();
			}
		}
		catch { }
	}

	/// <summary>无结果时：搜索框描边变红，并在下方显示「未找到任何结果」。</summary>
	private void SetHelpSearchError(bool error)
	{
		try
		{
			if (HelpNoResult != null)
			{
				bool wasShown = _helpNoResultShown;
				_helpNoResultShown = error;

				HelpNoResult.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
				if (error)
				{
					// 只在从「无提示」变为「有提示」时播放一次滑入 + 淡入
					if (!wasShown) AnimateHelpNoResultIn();
				}
				else ResetHelpNoResultIcon();
			}

			var border = HelpSearch?.FindName("ExpandedPanel") as Border;
			if (border == null) return;

			if (error)
			{
				border.BorderBrush = (TryFindResource("Fg_D32F2F") as Brush) ?? new SolidColorBrush(Colors.Red);
				border.BorderThickness = new Thickness(1.6);
			}
			else
			{
				border.BorderBrush = (TryFindResource("Bd_D0D0D0") as Brush) ?? border.BorderBrush;
				border.BorderThickness = new Thickness(1);
			}
		}
		catch { }
	}

	// ===== 搜索命中导航条（上一个 / 下一个 / 计数）=====

	private bool _helpNoResultShown;

	private bool _helpBarShouldShow;
	private int _helpBarOriginalIndex = -1;
	private DispatcherTimer _helpBarCollapseTimer;

	/// <summary>
	/// 结果多于 1 个时显示「上一个 / 下一个」：搜索框先平滑左移让出位置，按钮淡入并轻微放大回弹。
	/// 收起时按钮与计数从左到右依次「先放大再缩小消失」，同一时刻搜索框向右平移。
	/// </summary>
	private void ShowHelpMatchBar(bool show)
	{
		try
		{
			if (HelpMatchBar == null) return;

			if (show)
			{
				_helpBarShouldShow = true;
				StopHelpBarCollapseTimer();
				RestoreHelpBarFromAnimLayer();
				if (HelpMatchBar.Visibility == Visibility.Visible) return;

				ResetHelpMatchChildren();

				double beforeX = 0;
				if (HelpSearch != null)
					beforeX = HelpSearch.TransformToAncestor(GetHelpSearchAnchor()).Transform(new Point(0, 0)).X;

				HelpMatchBar.Visibility = Visibility.Visible;
				HelpSearchPanel?.UpdateLayout();

				double afterX = beforeX;
				if (HelpSearch != null)
					afterX = HelpSearch.TransformToAncestor(GetHelpSearchAnchor()).Transform(new Point(0, 0)).X;

				AnimateHelpSearchShift(beforeX - afterX, 0, 200);

				// 按钮：淡入 + 略放大后回弹
				HelpMatchBar.BeginAnimation(UIElement.OpacityProperty, null);
				if (App.IsAnimationDisabled)
				{
					HelpMatchBar.Opacity = 1;
					HelpMatchBar.RenderTransform = Transform.Identity;
					return;
				}

				HelpMatchBar.Opacity = 0;
				var scale = new ScaleTransform(0.84, 0.84);
				HelpMatchBar.RenderTransformOrigin = new Point(0.5, 0.5);
				HelpMatchBar.RenderTransform = scale;

				var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
				pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.84, KeyTime.FromTimeSpan(TimeSpan.Zero)));
				pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)),
					new CubicEase { EasingMode = EasingMode.EaseOut }));
				pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
					new CubicEase { EasingMode = EasingMode.EaseOut }));
				scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
				scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

				HelpMatchBar.BeginAnimation(UIElement.OpacityProperty,
					new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
				return;
			}

			// ---------------- 收起 ----------------
			_helpBarShouldShow = false;
			StopHelpBarCollapseTimer();

			if (HelpMatchBar.Visibility != Visibility.Visible && _helpBarOriginalIndex < 0) return;

			if (App.IsAnimationDisabled)
			{
				CollapseHelpMatchBar();
				return;
			}

			var anchor = GetHelpSearchAnchor() as FrameworkElement;
			if (anchor == null)
			{
				CollapseHelpMatchBar();
				return;
			}

			// 1) 先把按钮条整体搬到顶部的动画层，位置不变，避免收窄时子元素被挤压错位
			double hideBeforeX = 0;
			if (HelpSearch != null)
				hideBeforeX = HelpSearch.TransformToAncestor(anchor).Transform(new Point(0, 0)).X;

			var pos = HelpMatchBar.TransformToAncestor(anchor).Transform(new Point(0, 0));
			if (_helpBarOriginalIndex < 0 && HelpSearchPanel != null)
				_helpBarOriginalIndex = HelpSearchPanel.Children.IndexOf(HelpMatchBar);

			if (HelpSearchPanel != null && HelpSearchPanel.Children.Contains(HelpMatchBar))
				HelpSearchPanel.Children.Remove(HelpMatchBar);

			if (HelpAnimLayer != null && !HelpAnimLayer.Children.Contains(HelpMatchBar))
			{
				// Canvas 里 Margin 仍会偏移，先减掉，保证视觉位置完全不变
				double marginLeft = HelpMatchBar.Margin.Left;
				double marginTop = HelpMatchBar.Margin.Top;
				Canvas.SetLeft(HelpMatchBar, Math.Max(0, pos.X - marginLeft));
				Canvas.SetTop(HelpMatchBar, Math.Max(0, pos.Y - marginTop));
				HelpAnimLayer.Children.Add(HelpMatchBar);
			}

			HelpMatchBar.Visibility = Visibility.Visible;
			HelpMatchBar.ClipToBounds = false;
			HelpMatchBar.Width = double.NaN;
			anchor.UpdateLayout();

			// 2) 布局已经收好：搜索框从旧位置平滑滑到新位置（匀加速 → 匀减速）
			double hideAfterX = hideBeforeX;
			if (HelpSearch != null)
				hideAfterX = HelpSearch.TransformToAncestor(anchor).Transform(new Point(0, 0)).X;
			AnimateHelpSearchShift(hideBeforeX - hideAfterX, 60, 220);

			// 3) 子元素从左到右依次「先放大、再缩小消失」
			foreach (var child in HelpMatchBar.Children)
			{
				if (child is FrameworkElement element)
					AnimateHelpChildPopOut(element, 0);
			}

			// 4) 全部播完后收起
			var timer = new DispatcherTimer(DispatcherPriority.Background)
			{
				Interval = TimeSpan.FromMilliseconds(280)
			};
			timer.Tick += (s, e) =>
			{
				timer.Stop();
				if (_helpBarShouldShow) return;
				CollapseHelpMatchBar();
			};
			_helpBarCollapseTimer = timer;
			timer.Start();
		}
		catch { }
	}

	/// <summary>提示小字（图标 + 文字）整体淡入；位置随搜索框一起平移。</summary>
	private void AnimateHelpNoResultIn()
	{
		if (HelpNoResult == null) return;

		try
		{
			HelpNoResult.BeginAnimation(UIElement.OpacityProperty, null);

			if (App.IsAnimationDisabled)
			{
				HelpNoResult.Opacity = 1;
				return;
			}

			HelpNoResult.Opacity = 0;
			HelpNoResult.BeginAnimation(UIElement.OpacityProperty,
				new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
				{
					EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
				});
		}
		catch { }
	}

	private void ResetHelpNoResultIcon()
	{
		try
		{
			if (HelpNoResult != null)
			{
				HelpNoResult.BeginAnimation(UIElement.OpacityProperty, null);
				HelpNoResult.Opacity = 1;
				if (HelpNoResult.RenderTransform is TranslateTransform hintShift)
				{
					hintShift.BeginAnimation(TranslateTransform.XProperty, null);
					hintShift.X = 0;
				}
			}

			if (TxtHelpNoResultIcon?.RenderTransform is TranslateTransform iconShift)
			{
				iconShift.BeginAnimation(TranslateTransform.XProperty, null);
				iconShift.X = 0;
			}
		}
		catch { }
	}

	/// <summary>搜索框平移：delta 为「抵消布局位移所需的临时偏移」，动画到 0。</summary>
	private void AnimateHelpSearchShift(double delta, int delayMs = 0, int durationMs = 220)
	{
		// 搜索框和下方提示小字使用同一个偏移与同一条动画，保证完全同步平移
		AnimateHelpElementShift(HelpSearch, delta, delayMs, durationMs);
		AnimateHelpElementShift(HelpNoResult, delta, delayMs, durationMs);
	}

	private static void AnimateHelpElementShift(UIElement element, double delta, int delayMs, int durationMs)
	{
		if (element == null) return;

		var tt = element.RenderTransform as TranslateTransform;
		if (tt == null)
		{
			tt = new TranslateTransform();
			element.RenderTransform = tt;
		}

		tt.BeginAnimation(TranslateTransform.XProperty, null);

		if (App.IsAnimationDisabled || Math.Abs(delta) < 0.5)
		{
			tt.X = 0;
			return;
		}

		tt.X = delta;
		tt.BeginAnimation(TranslateTransform.XProperty,
			new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(durationMs))
			{
				BeginTime = TimeSpan.FromMilliseconds(delayMs),
				// 匀加速 → 匀减速
				EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
			});
	}

	private void StopHelpBarCollapseTimer()
	{
		_helpBarCollapseTimer?.Stop();
		_helpBarCollapseTimer = null;
	}

	/// <summary>把按钮条从动画层放回原位置（收起状态）。</summary>
	private void RestoreHelpBarFromAnimLayer()
	{
		try
		{
			if (HelpMatchBar == null || HelpAnimLayer == null) return;
			if (!HelpAnimLayer.Children.Contains(HelpMatchBar)) return;

			HelpAnimLayer.Children.Remove(HelpMatchBar);
			Canvas.SetLeft(HelpMatchBar, 0);
			Canvas.SetTop(HelpMatchBar, 0);

			if (HelpSearchPanel != null)
			{
				int index = _helpBarOriginalIndex;
				if (index < 0 || index > HelpSearchPanel.Children.Count)
					index = HelpSearchPanel.Children.Count;
				if (!HelpSearchPanel.Children.Contains(HelpMatchBar))
					HelpSearchPanel.Children.Insert(index, HelpMatchBar);
			}

			_helpBarOriginalIndex = -1;
			HelpMatchBar.Visibility = Visibility.Collapsed;
			ResetHelpMatchChildren();
		}
		catch { }
	}

	/// <summary>单个按钮 / 计数的消失动画：先略微放大，再立刻缩小并淡出。</summary>
	private static void AnimateHelpChildPopOut(FrameworkElement element, int delayMs)
	{
		if (element == null) return;

		var scale = element.RenderTransform as ScaleTransform;
		if (scale == null)
		{
			scale = new ScaleTransform(1, 1);
			element.RenderTransformOrigin = new Point(0.5, 0.5);
			element.RenderTransform = scale;
		}

		var duration = TimeSpan.FromMilliseconds(120);
		var begin = TimeSpan.FromMilliseconds(delayMs);

		var pop = new DoubleAnimationUsingKeyFrames { Duration = duration, BeginTime = begin };
		pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35)),
			new CubicEase { EasingMode = EasingMode.EaseOut }));
		pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(duration),
			new CubicEase { EasingMode = EasingMode.EaseIn }));
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

		var fade = new DoubleAnimationUsingKeyFrames { Duration = duration, BeginTime = begin };
		fade.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
		fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35))));
		fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(duration)));
		element.BeginAnimation(UIElement.OpacityProperty, fade);
	}

	/// <summary>复位子元素动画状态，供下次显示使用。</summary>
	private void ResetHelpMatchChildren()
	{
		if (HelpMatchBar == null) return;
		HelpMatchBar.Children.OfType<FrameworkElement>().ToList().ForEach(element =>
		{
			element.BeginAnimation(UIElement.OpacityProperty, null);
			element.Opacity = 1;
			element.RenderTransform = Transform.Identity;
		});
	}

	/// <summary>计算搜索框位移用的稳定参照（搜索框所在面板的父级）。</summary>
	private Visual GetHelpSearchAnchor()
	{
		var parent = HelpSearchPanel?.Parent as Visual;
		return parent ?? (Visual)this;
	}

	/// <summary>收起按钮条并复位所有动画状态。</summary>
	private void CollapseHelpMatchBar()
	{
		try
		{
			StopHelpBarCollapseTimer();
			RestoreHelpBarFromAnimLayer();

			HelpMatchBar.BeginAnimation(UIElement.OpacityProperty, null);
			HelpMatchBar.BeginAnimation(FrameworkElement.WidthProperty, null);
			HelpMatchBar.ClearValue(FrameworkElement.WidthProperty);
			HelpMatchBar.ClipToBounds = false;
			HelpMatchBar.Visibility = Visibility.Collapsed;
			HelpMatchBar.Opacity = 1;
			HelpMatchBar.RenderTransform = Transform.Identity;

			if (HelpSearch?.RenderTransform is TranslateTransform t2)
			{
				t2.BeginAnimation(TranslateTransform.XProperty, null);
				t2.X = 0;
			}
		}
		catch { }
	}

		private void HelpPrev_Click(object sender, RoutedEventArgs e) => StepHelpMatch(-1);

	private void HelpNext_Click(object sender, RoutedEventArgs e) => StepHelpMatch(1);

	private void StepHelpMatch(int delta)
	{
		try
		{
			if (_helpMatches.Count == 0) return;
			_helpMatchIndex = (_helpMatchIndex + delta + _helpMatches.Count) % _helpMatches.Count;
			GoToHelpMatch();
		}
		catch { }
	}

	private void GoToHelpMatch()
	{
		if (_helpMatchIndex < 0 || _helpMatchIndex >= _helpMatches.Count) return;

		// 所有命中保持浅色高亮；当前这条换成醒目的颜色，并滚动到它
		var match = _helpMatches[_helpMatchIndex];
		ApplyCurrentHelpHighlight(match.Block);
		ScrollHelpTo(match.Block);
		UpdateHelpMatchText();
	}

	/// <summary>把「当前命中」所在段落重绘一次：其余命中浅色，当前这条深色。</summary>
	private void ApplyCurrentHelpHighlight(TextBlock currentBlock)
	{
		try
		{
			var previous = _helpCurrentBlock;
			if (previous != null && !ReferenceEquals(previous, currentBlock)
				&& _helpHighlighted.TryGetValue(previous, out var prevRuns)
				&& _helpRanges.TryGetValue(previous, out var prevRanges))
			{
				RenderHelpRuns(previous, prevRuns, prevRanges, -1);
			}

			_helpCurrentBlock = currentBlock;

			if (currentBlock != null
				&& _helpHighlighted.TryGetValue(currentBlock, out var runs)
				&& _helpRanges.TryGetValue(currentBlock, out var ranges))
			{
				RenderHelpRuns(currentBlock, runs, ranges, _helpMatchIndex);
			}
		}
		catch { }
	}

	private void UpdateHelpMatchText()
	{
		if (TxtHelpMatch == null) return;
		TxtHelpMatch.Text = _helpMatches.Count == 0
			? ""
			: $"{_helpMatchIndex + 1} / {_helpMatches.Count}";
	}

	private void ScrollHelpTo(FrameworkElement target)
	{
		try
		{
			if (HelpScroll == null || target == null) return;
			var content = HelpScroll.Content as FrameworkElement;
			if (content == null) return;

			double y = target.TransformToAncestor(content).Transform(new Point(0, 0)).Y - 12;
			SmoothScrollHelpTo(y);
		}
		catch { }
	}

	/// <summary>平滑滚动（ScrollViewer 的 Offset 不能直接动画，这里用定时器做缓动）。</summary>
	private void SmoothScrollHelpTo(double targetOffset, int durationMs = 260)
	{
		if (HelpScroll == null) return;

		_helpScrollTimer?.Stop();

		double from = HelpScroll.VerticalOffset;
		double to = Math.Max(0, Math.Min(targetOffset, HelpScroll.ScrollableHeight));
		if (Math.Abs(to - from) < 0.5)
		{
			HelpScroll.ScrollToVerticalOffset(to);
			return;
		}

		var watch = Stopwatch.StartNew();
		var timer = new DispatcherTimer(DispatcherPriority.Render)
		{
			Interval = TimeSpan.FromMilliseconds(15)
		};
		_helpScrollTimer = timer;

		timer.Tick += (s, e) =>
		{
			double t = Math.Min(1.0, watch.ElapsedMilliseconds / (double)durationMs);
			double eased = 1 - Math.Pow(1 - t, 3);   // ease-out
			HelpScroll.ScrollToVerticalOffset(from + (to - from) * eased);
			if (t >= 1) timer.Stop();
		};
		timer.Start();
	}

	/// <summary>只高亮命中的关键词本身，而不是整段文字。</summary>
	/// <summary>把当前搜索命中的所有关键词一次性全部高亮（同一段落里的多处命中也会全部标出）。</summary>
	private void HighlightAllHelpMatches()
	{
		ClearHelpHighlight();
		if (_helpMatches.Count == 0) return;

		try
		{
			// 先按段落分好区间，并记下每个命中在结果列表里的序号
			for (int i = 0; i < _helpMatches.Count; i++)
			{
				var match = _helpMatches[i];
				if (match.Block == null) continue;

				if (!_helpRanges.TryGetValue(match.Block, out var list))
				{
					list = new List<(int Start, int Length, int Index)>();
					_helpRanges[match.Block] = list;
				}
				list.Add((match.Start, match.Length, i));
			}

			foreach (var pair in _helpRanges)
			{
				var block = pair.Key;
				var runs = CaptureHelpRuns(block);
				_helpHighlighted[block] = runs;
				RenderHelpRuns(block, runs, pair.Value.OrderBy(r => r.Start).ToList(), -1);
			}
		}
		catch { }
	}

	/// <summary>
	/// 按给定区间重建段落 Inlines：普通命中用浅黄，当前命中（Index == currentIndex）用深色。
	/// </summary>
	private static void RenderHelpRuns(TextBlock block, List<HelpRunInfo> runs,
									   List<(int Start, int Length, int Index)> ranges, int currentIndex)
	{
		var inlines = new List<Inline>();
		int offset = 0;

		foreach (var run in runs)
		{
			if (run.IsLineBreak)
			{
				inlines.Add(new LineBreak());
				continue;
			}

			string text = run.Text ?? "";
			int runStart = offset;
			int runEnd = offset + text.Length;
			offset = runEnd;

			int cursor = runStart;
			foreach (var range in ranges)
			{
				int hitStart = Math.Max(range.Start, runStart);
				int hitEnd = Math.Min(range.Start + range.Length, runEnd);
				if (hitEnd <= hitStart) continue;

				if (hitStart > cursor)
					inlines.Add(CreateRun(run, text.Substring(cursor - runStart, hitStart - cursor), null));

				// 3.52：高亮颜色跟着主题走（深色下不能用浅蓝底 + 白字，会变成一块看不清的白斑）
				bool isCurrent = range.Index == currentIndex;
				Brush brush = GetHelpHighlightBrush(isCurrent);
				inlines.Add(CreateRun(run, text.Substring(hitStart - runStart, hitEnd - hitStart), brush,
									  isCurrent ? GetHelpHighlightFore(true) : GetHelpHighlightFore(false)));
				cursor = hitEnd;
			}

			if (cursor < runEnd)
				inlines.Add(CreateRun(run, text.Substring(cursor - runStart, runEnd - cursor), null));
		}

		block.Inlines.Clear();
		foreach (var inline in inlines) block.Inlines.Add(inline);
	}

	private static Run CreateRun(HelpRunInfo info, string text, Brush highlight, Brush highlightFore = null)
	{
		var run = new Run(text);
		try
		{
			run.FontWeight = info.Weight;
			if (highlight != null)
			{
				// 高亮段用专门的前景色，保证在深色 / 浅色底上都看得清
				run.Foreground = highlightFore ?? info.Fore;
			}
			else if (info.Fore != null)
			{
				run.Foreground = info.Fore;
			}
			if (info.Family != null) run.FontFamily = info.Family;
			if (!double.IsNaN(info.Size)) run.FontSize = info.Size;
			run.Background = highlight ?? info.Back;
		}
		catch { }
		return run;
	}

	private List<HelpRunInfo> CaptureHelpRuns(TextBlock block)
	{
		var list = new List<HelpRunInfo>();
		if (block.Inlines != null && block.Inlines.Count > 0)
		{
			foreach (var inline in block.Inlines) CaptureInline(list, inline, block);
		}
		else if (!string.IsNullOrEmpty(block.Text))
		{
			list.Add(new HelpRunInfo { Text = block.Text, Fore = block.Foreground });
		}
		return list;
	}

	private static void CaptureInline(List<HelpRunInfo> list, Inline inline, TextBlock host)
	{
		if (inline is System.Windows.Documents.LineBreak)
		{
			list.Add(new HelpRunInfo { IsLineBreak = true });
			return;
		}
		if (inline is Run run)
		{
			list.Add(new HelpRunInfo
			{
				Text = run.Text ?? "",
				Weight = run.FontWeight,
				Fore = run.Foreground,
				Family = run.FontFamily,
				Size = run.FontSize,
				Back = run.Background
			});
			return;
		}
		if (inline is Span span)
		{
			foreach (var child in span.Inlines) CaptureInline(list, child, host);
		}
	}

	private void ClearHelpHighlight()
	{
		try
		{
			foreach (var pair in _helpHighlighted.ToList())
			{
				var block = pair.Key;
				var runs = pair.Value;
				if (block == null || runs == null) continue;

				block.Inlines.Clear();
				foreach (var info in runs)
				{
					if (info.IsLineBreak)
					{
						block.Inlines.Add(new LineBreak());
						continue;
					}
					block.Inlines.Add(CreateRun(info, info.Text ?? "", null));
				}
			}
		}
		catch { }
		_helpHighlighted.Clear();
		_helpRanges.Clear();
		_helpCurrentBlock = null;
	}
	private IEnumerable<TextBlock> EnumerateHelpTextBlocks()
	{
		var content = HelpScroll?.Content as DependencyObject;
		if (content == null) yield break;
		foreach (var block in EnumerateInDocumentOrder(content))
		{
			if (!(block is TextBlock tb) || string.IsNullOrWhiteSpace(GetHelpText(tb))) continue;
			// 「快速导航」按钮里也有文字，搜索时跳过它们，只搜正文
			if (IsInsideHelpToc(tb)) continue;
			yield return tb;
		}
	}

	/// <summary>该元素是否位于「快速导航」按钮组内。</summary>
	private bool IsInsideHelpToc(DependencyObject node)
	{
		if (HelpToc == null) return false;

		var current = node;
		for (int depth = 0; depth < 24 && current != null; depth++)
		{
			if (ReferenceEquals(current, HelpToc)) return true;
			try { current = VisualTreeHelper.GetParent(current); } catch { return false; }
		}
		return false;
	}

	/// <summary>按可视树顺序（从第一个子元素开始）深度优先，保证从上往下。</summary>
	private static IEnumerable<DependencyObject> EnumerateInDocumentOrder(DependencyObject root)
	{
		int count = 0;
		try { count = VisualTreeHelper.GetChildrenCount(root); } catch { }
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = null;
			try { child = VisualTreeHelper.GetChild(root, i); } catch { }
			if (child == null) continue;
			yield return child;
			foreach (var d in EnumerateInDocumentOrder(child)) yield return d;
		}
	}

	private static string GetHelpText(TextBlock block)
	{
		if (block == null) return "";
		if (block.Inlines != null && block.Inlines.Count > 0)
		{
			var sb = new System.Text.StringBuilder();
			foreach (var inline in block.Inlines) AppendInlineText(sb, inline);
			return sb.ToString();
		}
		return block.Text ?? "";
	}

	private static void AppendInlineText(System.Text.StringBuilder sb, Inline inline)
	{
		if (inline is Run run) { sb.Append(run.Text); return; }
		if (inline is Span span)
		{
			foreach (var child in span.Inlines) AppendInlineText(sb, child);
		}
	}


	private void LoadUIState()
	{
		InitInfoHints();
		RefreshDirectoryUI();
		if (TxtScreenshotDir != null)
		{
			TxtScreenshotDir.Text = App.ScreenshotDir;
		}
		LoadPoolComboBox();
		UpdateHistoryButtonUI();
		UpdateAnimationButtonUI();
		RefreshHideOptionsUI();
		UpdateThemeUI();
		UpdateContextMenuStyleUI();
		UpdateMainPageLayoutUI();
		LoadMainBgBlurUI();
	}

	private void RefreshHideOptionsUI()
	{
		bool isLoading = _isLoading;
		_isLoading = true;
		try
		{
			ChkHideProbability.IsChecked = App.HideProbabilityOption;
			ChkHideAdvanced.IsChecked = App.HideAdvancedGacha;
			ChkHideEditPool.IsChecked = App.HideEditPool;
			ChkHideOptionSection.IsChecked = App.HideOptionSection;
			if (ChkHideGachaReport != null)
			{
				ChkHideGachaReport.IsChecked = App.HideGachaReport;
			}
			ChkHideAdvanced.IsEnabled = !App.HideProbabilityOption;
			// 3.67：隐藏「功能隐藏」本身时，整张卡片一起收起，不能只收内容（会残留一条卡片白边）
			if (CardHideSection != null)
			{
				CardHideSection.Visibility = App.IsOptionSectionVisible ? Visibility.Visible : Visibility.Collapsed;
			}
			if (PanelHideSection != null)
			{
				PanelHideSection.Visibility = Visibility.Visible;
			}
			UpdateEditPoolButtonVisibility();
			UpdateHelpVisibility();
		}
		finally
		{
			_isLoading = isLoading;
		}
	}

	private void UpdateHelpVisibility()
	{
		try
		{
			// 3.70：隐藏章节时连外层卡片一起收起，否则卡片内边距会留下一条空白条
			if (CardHelpAdvanced != null)
			{
				CardHelpAdvanced.Visibility = App.IsAdvancedVisible ? Visibility.Visible : Visibility.Collapsed;
			}
			if (CardHelpHide != null)
			{
				CardHelpHide.Visibility = App.IsOptionSectionVisible ? Visibility.Visible : Visibility.Collapsed;
			}
			if (HelpPoolEdit != null)
			{
				HelpPoolEdit.Visibility = App.IsEditPoolVisible ? Visibility.Visible : Visibility.Collapsed;
			}
			if (HelpProbabilityDraw != null)
			{
				HelpProbabilityDraw.Visibility = App.IsProbabilityVisible ? Visibility.Visible : Visibility.Collapsed;
			}
			if (CardHelpReport != null)
			{
				CardHelpReport.Visibility = App.IsGachaReportVisible ? Visibility.Visible : Visibility.Collapsed;
			}
			if (HelpReportAdvanced != null)
			{
				HelpReportAdvanced.Visibility = App.IsAdvancedVisible ? Visibility.Visible : Visibility.Collapsed;
			}

			// 功能隐藏时，「快速导航」里对应的按钮也一起收起
			SetHelpTocButtonVisible("SecReport", App.IsGachaReportVisible);
			SetHelpTocButtonVisible("SecAdvanced", App.IsAdvancedVisible);
			SetHelpTocButtonVisible("HelpProbabilityDraw", App.IsProbabilityVisible);
			SetHelpTocButtonVisible("HelpPoolEdit", App.IsEditPoolVisible);
			SetHelpTocButtonVisible("SecHide", App.IsOptionSectionVisible);
		}
		catch
		{
		}
	}

	/// <summary>「快速导航」里 Tag 对应的按钮跟随所属章节一起显示 / 隐藏。</summary>
	private void SetHelpTocButtonVisible(string tag, bool visible)
	{
		try
		{
			if (HelpToc == null)
			{
				return;
			}
			foreach (var child in HelpToc.Children)
			{
				if (!(child is Button button))
				{
					continue;
				}
				if ((button.Tag as string) != tag)
				{
					continue;
				}
				button.Visibility = (visible ? Visibility.Visible : Visibility.Collapsed);
			}
		}
		catch
		{
		}
	}

	private void UpdateEditPoolButtonVisibility()
	{
		try
		{
			if (BtnModifyPool != null)
			{
				BtnModifyPool.Visibility = ((!App.IsEditPoolVisible) ? Visibility.Collapsed : Visibility.Visible);
			}
		}
		catch
		{
		}
	}

	public void PlayEnterAnimation(Action onComplete)
	{
		try
		{
			NavPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
			NavPanel.BeginAnimation(UIElement.OpacityProperty, null);
			ContentPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
			ContentPanel.BeginAnimation(UIElement.OpacityProperty, null);
			// 3.42：进入时把右侧内容的缩放复位（返回主界面时它是缩小淡出的）
			ContentPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
			ContentPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
			ContentPanelScale.ScaleX = 1.0;
			ContentPanelScale.ScaleY = 1.0;
			if (App.IsAnimationDisabled)
			{
				NavPanelTransform.X = 0.0;
				NavPanel.Opacity = 1.0;
				ContentPanelTransform.Y = 0.0;
				ContentPanel.Opacity = 1.0;
				onComplete?.Invoke();
				return;
			}
			NavPanelTransform.X = -150.0;
			NavPanel.Opacity = 0.0;
			ContentPanelTransform.Y = 60.0;
			ContentPanel.Opacity = 0.0;
			CubicEase easingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			};
			DoubleAnimation doubleAnimation = new DoubleAnimation(-150.0, 0.0, NavEnterDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation2 = new DoubleAnimation(0.0, 1.0, NavEnterDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation3 = new DoubleAnimation(60.0, 0.0, NavEnterDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation4 = new DoubleAnimation(0.0, 1.0, NavEnterDuration)
			{
				EasingFunction = easingFunction
			};
			int completed = 0;
			Action checkDone = () =>
			{
				if (++completed >= 4)
				{
					onComplete?.Invoke();
				}
			};
			doubleAnimation.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			doubleAnimation2.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			doubleAnimation3.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			doubleAnimation4.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			NavPanelTransform.BeginAnimation(TranslateTransform.XProperty, doubleAnimation);
			NavPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation2);
			ContentPanelTransform.BeginAnimation(TranslateTransform.YProperty, doubleAnimation3);
			ContentPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation4);
		}
		catch
		{
			onComplete?.Invoke();
		}
	}

	public void PlayExitAnimation(Action onComplete)
	{
		try
		{
			if (App.IsAnimationDisabled)
			{
				NavPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
				NavPanel.BeginAnimation(UIElement.OpacityProperty, null);
				ContentPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				ContentPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				ContentPanel.BeginAnimation(UIElement.OpacityProperty, null);
				NavPanelTransform.X = -150.0;
				NavPanel.Opacity = 0.0;
				ContentPanelScale.ScaleX = 1.0;
				ContentPanelScale.ScaleY = 1.0;
				ContentPanel.Opacity = 0.0;
				onComplete?.Invoke();
				return;
			}
			CubicEase easingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseIn
			};
			double x = NavPanelTransform.X;
			double opacity = NavPanel.Opacity;
			// 3.42：右侧内容改为缩小淡出（原来是与导航栏一起往下挪）
			double scale = ContentPanelScale.ScaleX <= 0.01 ? 1.0 : ContentPanelScale.ScaleX;
			double opacity2 = ContentPanel.Opacity;
			DoubleAnimation doubleAnimation = new DoubleAnimation(x, -150.0, NavExitDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation2 = new DoubleAnimation(opacity, 0.0, NavExitDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation3 = new DoubleAnimation(scale, 0.92, NavExitDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation4 = new DoubleAnimation(opacity2, 0.0, NavExitDuration)
			{
				EasingFunction = easingFunction
			};
			int completed = 0;
			Action checkDone = () =>
			{
				if (++completed >= 5)
				{
					try
					{
						NavPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
						NavPanel.BeginAnimation(UIElement.OpacityProperty, null);
						ContentPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
						ContentPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
						ContentPanel.BeginAnimation(UIElement.OpacityProperty, null);
						NavPanelTransform.X = -150.0;
						NavPanel.Opacity = 0.0;
						ContentPanelScale.ScaleX = 1.0;
						ContentPanelScale.ScaleY = 1.0;
						ContentPanel.Opacity = 0.0;
					}
					catch
					{
					}
					onComplete?.Invoke();
				}
			};
			doubleAnimation.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			doubleAnimation2.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			doubleAnimation3.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			doubleAnimation4.Completed += (object? s, EventArgs e) =>
			{
				checkDone();
			};
			NavPanelTransform.BeginAnimation(TranslateTransform.XProperty, doubleAnimation);
			NavPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation2);
			ContentPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, doubleAnimation3);
			ContentPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, doubleAnimation3);
			ContentPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation4);
		}
		catch
		{
			onComplete?.Invoke();
		}
	}

	public void PlayEnterAsReverseAnimation(Action onComplete)
	{
		PlayEnterAnimation(onComplete);
	}

	private void Nav_Checked(object sender, RoutedEventArgs e)
	{
		if (_isLoading || !(sender is RadioButton { Tag: var tag }) || !int.TryParse(tag?.ToString(), out var result) || result == _currentNavIndex)
		{
			return;
		}
		UIElement navPanel = GetNavPanel(_currentNavIndex);
		UIElement navPanel2 = GetNavPanel(result);
		for (int i = 0; i < 5; i++)
		{
			if (i != _currentNavIndex && i != result)
			{
				UIElement navPanel3 = GetNavPanel(i);
				if (navPanel3 != null)
				{
					EnsurePanelTransform(navPanel3);
					TranslateTransform translateTransform = (TranslateTransform)navPanel3.RenderTransform;
					translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
					navPanel3.BeginAnimation(UIElement.OpacityProperty, null);
					translateTransform.Y = 0.0;
					navPanel3.Opacity = 1.0;
					navPanel3.Visibility = Visibility.Collapsed;
				}
			}
		}
		PlayTabSwitchAnimation(_currentNavIndex, result, navPanel, navPanel2);
		BindEdgeScrollBarFor(result);
		if (App.IconCompatActive)
		{
			Dispatcher.BeginInvoke(new Action(() =>
			{
				try { IconSupport.NormalizeTree(this); } catch { }
				try { IconSupport.RequestSweep(this); } catch { }
			}), System.Windows.Threading.DispatcherPriority.Background);
		}
		if (result == 1)
		{
			try
			{
				if (VidCover.Source != null && VidCover.Visibility == Visibility.Visible)
				{
					VidCover.Position = TimeSpan.Zero;
					VidCover.Play();
				}
			}
			catch
			{
			}
			try
			{
				if (!string.IsNullOrEmpty(_currentGifPath) && File.Exists(_currentGifPath) && ImgCover.Visibility == Visibility.Visible)
				{
					StartGifPlayback(_currentGifPath);
				}
			}
			catch
			{
			}
			ComboPools_SelectionChanged(null, null);
		}
		else
		{
			StopGifPlayback();
			try
			{
				VidCover.Stop();
			}
			catch
			{
			}
		}
		if (result == 0)
		{
			bool isLoading = _isLoading;
			_isLoading = true;
			RefreshMusicList();
			_isLoading = isLoading;
		}
		if (result == 2)
		{
			RefreshHideOptionsUI();
		}
		if (result == 3)
		{
			UpdateHelpVisibility();
		}
		_currentNavIndex = result;
	}

	private void BindEdgeScrollBar(ScrollViewer target)
	{
		try
		{
			if (EdgeScrollBar == null)
			{
				return;
			}
			if (_edgeTarget != target)
			{
				if (_edgeTarget != null)
				{
					_edgeTarget.ScrollChanged -= EdgeTarget_ScrollChanged;
					_edgeTarget.SizeChanged -= EdgeTarget_SizeChanged;
				}
				_edgeTarget = target;
				if (_edgeTarget != null)
				{
					_edgeTarget.ScrollChanged += EdgeTarget_ScrollChanged;
					_edgeTarget.SizeChanged += EdgeTarget_SizeChanged;
				}
			}
			UpdateEdgeScrollBar();
		}
		catch
		{
		}
	}

	private void EdgeTarget_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		UpdateEdgeScrollBar();
	}

	private void EdgeTarget_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateEdgeScrollBar();
	}

	private void UpdateEdgeScrollBar()
	{
		try
		{
			if (EdgeScrollBar == null)
			{
				return;
			}
			if (_edgeTarget == null)
			{
				EdgeScrollBar.Visibility = Visibility.Collapsed;
				return;
			}
			double num = Math.Max(0.0, _edgeTarget.ExtentHeight - _edgeTarget.ViewportHeight);
			EdgeScrollBar.Maximum = num;
			EdgeScrollBar.ViewportSize = Math.Max(1.0, _edgeTarget.ViewportHeight);
			EdgeScrollBar.LargeChange = Math.Max(1.0, _edgeTarget.ViewportHeight);
			EdgeScrollBar.SmallChange = 48.0;
			if (Math.Abs(EdgeScrollBar.Value - _edgeTarget.VerticalOffset) > 0.5)
			{
				EdgeScrollBar.Value = _edgeTarget.VerticalOffset;
			}
			EdgeScrollBar.Visibility = ((!(num > 1.0)) ? Visibility.Collapsed : Visibility.Visible);
		}
		catch
		{
		}
	}

	private void EdgeScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		try
		{
			if (_edgeTarget != null && Math.Abs(_edgeTarget.VerticalOffset - EdgeScrollBar.Value) > 0.5)
			{
				_edgeTarget.ScrollToVerticalOffset(EdgeScrollBar.Value);
			}
		}
		catch
		{
		}
	}

	private void BindEdgeScrollBarFor(int navIndex)
	{
		ScrollViewer target = navIndex == 3 ? HelpScroll : GetNavPanel(navIndex) as ScrollViewer;
		BindEdgeScrollBar(target);
	}

	private UIElement GetNavPanel(int index)
	{
		return index switch
		{
			0 => (UIElement)PanelNormal, 
			1 => PanelPool, 
			2 => PanelSystem, 
			3 => PanelHelp, 
			4 => PanelAbout, 
			_ => null, 
		};
	}

	private static void EnsurePanelTransform(UIElement panel)
	{
		if (panel != null && !(panel.RenderTransform is TranslateTransform))
		{
			panel.RenderTransform = new TranslateTransform();
		}
	}

	private void PlayTabSwitchAnimation(int oldIndex, int newIndex, UIElement oldPanel, UIElement newPanel)
	{
		double oldY = (double)oldIndex * 50.0;
		double newY = (double)newIndex * 50.0;
		if (App.IsAnimationDisabled)
		{
			if (oldPanel != null)
			{
				oldPanel.Visibility = Visibility.Collapsed;
			}
			if (newPanel != null)
			{
				newPanel.Visibility = Visibility.Visible;
			}
			NavFillTransform.BeginAnimation(TranslateTransform.XProperty, null);
			NavFillTransform.BeginAnimation(TranslateTransform.YProperty, null);
			NavFill.BeginAnimation(UIElement.OpacityProperty, null);
			NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
			NavFillTransform.X = 0.0;
			NavFillTransform.Y = newY;
			NavFill.Opacity = 1.0;
			NavIndicatorTransform.Y = newY;
			int[] array = new int[5] { 0, 1, 2, 3, 4 };
			foreach (int index in array)
			{
				UIElement navPanel = GetNavPanel(index);
				if (navPanel != null)
				{
					EnsurePanelTransform(navPanel);
					TranslateTransform translateTransform = (TranslateTransform)navPanel.RenderTransform;
					translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
					navPanel.BeginAnimation(UIElement.OpacityProperty, null);
					translateTransform.Y = 0.0;
					navPanel.Opacity = 1.0;
				}
			}
			return;
		}
		CubicEase easeOut = new CubicEase
		{
			EasingMode = EasingMode.EaseOut
		};
		CubicEase easingFunction = new CubicEase
		{
			EasingMode = EasingMode.EaseIn
		};
		NavFillTransform.BeginAnimation(TranslateTransform.XProperty, null);
		NavFillTransform.BeginAnimation(TranslateTransform.YProperty, null);
		NavFill.BeginAnimation(UIElement.OpacityProperty, null);
		NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
		NavFillTransform.X = 0.0;
		NavFillTransform.Y = oldY;
		NavFill.Opacity = 1.0;
		NavIndicatorTransform.Y = oldY;
		if (oldPanel != null)
		{
			EnsurePanelTransform(oldPanel);
			TranslateTransform t = (TranslateTransform)oldPanel.RenderTransform;
			t.BeginAnimation(TranslateTransform.YProperty, null);
			oldPanel.BeginAnimation(UIElement.OpacityProperty, null);
			t.Y = 0.0;
			oldPanel.Opacity = 1.0;
			DoubleAnimation animation = new DoubleAnimation
			{
				From = 0.0,
				To = 40.0,
				Duration = TimeSpan.FromMilliseconds(140L),
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation = new DoubleAnimation
			{
				From = 1.0,
				To = 0.0,
				Duration = TimeSpan.FromMilliseconds(140L),
				EasingFunction = easingFunction
			};
			doubleAnimation.Completed += (object? s, EventArgs e) =>
			{
				try
				{
					oldPanel.Visibility = Visibility.Collapsed;
					t.Y = 0.0;
					oldPanel.Opacity = 1.0;
				}
				catch
				{
				}
			};
			t.BeginAnimation(TranslateTransform.YProperty, animation);
			oldPanel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
		}
		DoubleAnimation animation2 = new DoubleAnimation
		{
			From = 0.0,
			To = -100.0,
			Duration = TimeSpan.FromMilliseconds(100L),
			EasingFunction = easingFunction
		};
		DoubleAnimation doubleAnimation2 = new DoubleAnimation
		{
			From = 1.0,
			To = 0.0,
			Duration = TimeSpan.FromMilliseconds(100L),
			EasingFunction = easingFunction
		};
		doubleAnimation2.Completed += (object? s, EventArgs e) =>
		{
			try
			{
				NavFillTransform.BeginAnimation(TranslateTransform.XProperty, null);
				NavFill.BeginAnimation(UIElement.OpacityProperty, null);
				NavFillTransform.X = -100.0;
				NavFillTransform.Y = oldY;
				NavFill.Opacity = 0.0;
			}
			catch
			{
			}
			DoubleAnimation doubleAnimation3 = new DoubleAnimation
			{
				From = oldY,
				To = newY,
				Duration = TimeSpan.FromMilliseconds(180L),
				EasingFunction = easeOut
			};
			doubleAnimation3.Completed += (object? obj3, EventArgs e2) =>
			{
				try
				{
					NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, null);
					NavIndicatorTransform.Y = newY;
					NavFillTransform.BeginAnimation(TranslateTransform.XProperty, null);
					NavFill.BeginAnimation(UIElement.OpacityProperty, null);
					NavFillTransform.Y = newY;
					NavFillTransform.X = -100.0;
					NavFill.Opacity = 0.0;
				}
				catch
				{
				}
				DoubleAnimation animation3 = new DoubleAnimation
				{
					From = -100.0,
					To = 0.0,
					Duration = TimeSpan.FromMilliseconds(140L),
					EasingFunction = easeOut
				};
				DoubleAnimation animation4 = new DoubleAnimation
				{
					From = 0.0,
					To = 1.0,
					Duration = TimeSpan.FromMilliseconds(140L),
					EasingFunction = easeOut
				};
				NavFillTransform.BeginAnimation(TranslateTransform.XProperty, animation3);
				NavFill.BeginAnimation(UIElement.OpacityProperty, animation4);
				if (newPanel != null)
				{
					EnsurePanelTransform(newPanel);
					TranslateTransform translateTransform2 = (TranslateTransform)newPanel.RenderTransform;
					translateTransform2.BeginAnimation(TranslateTransform.YProperty, null);
					newPanel.BeginAnimation(UIElement.OpacityProperty, null);
					newPanel.Visibility = Visibility.Visible;
					newPanel.Opacity = 0.0;
					translateTransform2.Y = 40.0;
					DoubleAnimation animation5 = new DoubleAnimation
					{
						From = 40.0,
						To = 0.0,
						Duration = TimeSpan.FromMilliseconds(220L),
						EasingFunction = easeOut
					};
					DoubleAnimation animation6 = new DoubleAnimation
					{
						From = 0.0,
						To = 1.0,
						Duration = TimeSpan.FromMilliseconds(220L),
						EasingFunction = easeOut
					};
					translateTransform2.BeginAnimation(TranslateTransform.YProperty, animation5);
					newPanel.BeginAnimation(UIElement.OpacityProperty, animation6);
				}
			};
			NavIndicatorTransform.BeginAnimation(TranslateTransform.YProperty, doubleAnimation3);
		};
		NavFillTransform.BeginAnimation(TranslateTransform.XProperty, animation2);
		NavFill.BeginAnimation(UIElement.OpacityProperty, doubleAnimation2);
	}

	private void UC_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		if (!IsAboutVisible)
		{
			return;
		}
		if (e.Key == _cheatCode[_cheatIndex])
		{
			_cheatIndex++;
			if (_cheatIndex == _cheatCode.Count)
			{
				_cheatIndex = 0;
				TriggerEasterEggExternal();
			}
		}
		else
		{
			_cheatIndex = 0;
			if (e.Key == _cheatCode[0])
			{
				_cheatIndex = 1;
			}
		}
	}

	public void TriggerEasterEggExternal()
	{
		TriggerEasterEgg();
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
			Window window = Window.GetWindow((DependencyObject)(object)this);
			if (window != null)
			{
				window.WindowState = WindowState.Minimized;
			}
			MessageBus.Publish(new AppMessages.NavigateTo("Main"));
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "彩蛋触发失败：" + ex.Message, "彩蛋");
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
			Window window = Window.GetWindow((DependencyObject)(object)this);
			if (window != null)
			{
				window.WindowState = WindowState.Minimized;
			}
			MessageBus.Publish(new AppMessages.NavigateTo("Main"));
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "打开链接失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void CopyMail_Click(object sender, RoutedEventArgs e)
	{
		CopyMailAddress();
	}

	private void CopyMailAddress()
	{
		if (TrySetClipboardText("ssssssss888777@163.com"))
		{
			ToastNotification.ShowSuccess(Window.GetWindow((DependencyObject)(object)this), "已成功复制");
		}
		else
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "复制失败，剪贴板可能被其他程序占用，请稍后重试。", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private static bool TrySetClipboardText(string text)
	{
		for (int i = 0; i < 5; i++)
		{
			try
			{
				Clipboard.SetDataObject(text, copy: true);
				return true;
			}
			catch
			{
				Thread.Sleep(50);
			}
		}
		return false;
	}

	private void ChkHideProbability_Click(object sender, RoutedEventArgs e)
	{
		if (!_isLoading)
		{
			App.HideProbabilityOption = ChkHideProbability.IsChecked == true;
			if (App.HideProbabilityOption)
			{
				_isLoading = true;
				// 3.75.1：联动开关也要有动画，不能瞬间跳过去
				ChkHideAdvanced.SetCheckedAnimated(true);
				_isLoading = false;
				App.HideAdvancedGacha = true;
			}
			App.SaveSettings();
			RefreshHideOptionsUI();
			MessageBus.Publish(new AppMessages.UiOptionsChanged());
		}
	}

	private void ChkHideAdvanced_Click(object sender, RoutedEventArgs e)
	{
		if (!_isLoading)
		{
			if (App.HideProbabilityOption)
			{
				_isLoading = true;
				ChkHideAdvanced.SetCheckedAnimated(true);
				_isLoading = false;
				App.HideAdvancedGacha = true;
			}
			else
			{
				App.HideAdvancedGacha = ChkHideAdvanced.IsChecked == true;
				App.SaveSettings();
				RefreshHideOptionsUI();
				MessageBus.Publish(new AppMessages.UiOptionsChanged());
			}
		}
	}

	private void ChkHideEditPool_Click(object sender, RoutedEventArgs e)
	{
		if (!_isLoading)
		{
			App.HideEditPool = ChkHideEditPool.IsChecked == true;
			App.SaveSettings();
			RefreshHideOptionsUI();
			MessageBus.Publish(new AppMessages.UiOptionsChanged());
		}
	}

	private void ChkHideGachaReport_Click(object sender, RoutedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		App.HideGachaReport = ChkHideGachaReport.IsChecked == true;
		try
		{
			App.SaveSettings();
		}
		catch
		{
		}
		try
		{
			MessageBus.Publish(new AppMessages.UiOptionsChanged());
		}
		catch
		{
		}
	}

	private void ChkHideOptionSection_Click(object sender, RoutedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		bool valueOrDefault = ChkHideOptionSection.IsChecked == true;
		if (valueOrDefault)
		{
			MessageBoxResult messageBoxResult = CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "确定要隐藏「功能隐藏」本身吗？\n\n隐藏后可在任意界面按 F12 临时显示此区块（重启后恢复隐藏）。", "确认隐藏", MessageBoxButton.YesNo, MessageBoxImage.Question, "隐藏", "取消", "\ue73e");
			if (messageBoxResult != MessageBoxResult.Yes)
			{
				_isLoading = true;
				ChkHideOptionSection.IsChecked = false;
				_isLoading = false;
				return;
			}
		}
		App.HideOptionSection = valueOrDefault;
		App.SaveSettings();
		RefreshHideOptionsUI();
		MessageBus.Publish(new AppMessages.UiOptionsChanged());
	}

	private void ChkMuteWhenUnfocused_Click(object sender, RoutedEventArgs e)
	{
		if (!_isLoading)
		{
			App.MuteWhenUnfocused = ChkMuteWhenUnfocused.IsChecked == true;
			App.SaveSettings();
			MessageBus.Publish(new AppMessages.MusicSettingsChanged());
		}
	}

	/// <summary>卡池封面视频带声音时，是否自动静音背景音乐。</summary>
	private void ChkMuteOnPoolVideoAudio_Click(object sender, RoutedEventArgs e)
	{
		if (_isLoading) return;
		try
		{
			App.MuteMusicOnCoverAudio = ChkMuteOnPoolVideoAudio.IsChecked == true;
			App.SaveSettings();
			MessageBus.Publish(new AppMessages.PoolAudioSettingsChanged());
		}
		catch { }
	}

	private void SaveMusicSettings()
	{
		if (!_isLoading)
		{
			App.LastMusicPath = ComboMusic.SelectedItem?.ToString() ?? "";
			App.IsMusicEnabled = ChkMusic.IsChecked == true;
			App.MusicVolume = SliderVolume.Value / 100.0;
			App.SoundEffectVolume = SliderSoundVolume.Value / 100.0;
			App.SaveSettings();
			MessageBus.Publish(new AppMessages.MusicSettingsChanged());
		}
	}

	private void LoadMusicSettingsUI()
	{
		RefreshMusicList();
		SliderVolume.Value = App.MusicVolume * 100.0;
		TxtVolumeValue.Text = (App.MusicVolume * 100.0).ToString("0");
		UpdateVolumeIcon();
		ChkMusic.IsChecked = App.IsMusicEnabled;
		if (ChkMuteWhenUnfocused != null)
		{
			ChkMuteWhenUnfocused.IsChecked = App.MuteWhenUnfocused;
		}
		if (ChkMuteOnPoolVideoAudio != null)
		{
			ChkMuteOnPoolVideoAudio.IsChecked = App.MuteMusicOnCoverAudio;
		}
		SliderSoundVolume.Value = App.SoundEffectVolume * 100.0;
		if (ChkSoundEffect != null)
		{
			ChkSoundEffect.IsChecked = !App.IsSoundEffectMuted;
		}
		if (TxtSoundVolumeValue != null)
		{
			TxtSoundVolumeValue.Text = (App.SoundEffectVolume * 100.0).ToString("0");
		}
		UpdateSoundVolumeIcon();
		UpdateMusicControlsEnabled();
	}

	private void UpdateVolumeIcon()
	{
		if (TxtMuteIcon != null)
		{
			double value = SliderVolume.Value;
			if (App.IsMuted || value <= 0.0)
			{
				TxtMuteIcon.Text = "\ue74f";
			}
			else if (value <= 33.0)
			{
				TxtMuteIcon.Text = "\ue993";
			}
			else if (value <= 66.0)
			{
				TxtMuteIcon.Text = "\ue994";
			}
			else
			{
				TxtMuteIcon.Text = "\ue995";
			}
		}
	}

	private void UpdateSoundVolumeIcon()
	{
		if (TxtSoundIcon == null || SliderSoundVolume == null)
		{
			return;
		}
		if (App.IsSoundEffectMuted)
		{
			TxtSoundIcon.Text = "\ue74f";
			return;
		}
		double value = SliderSoundVolume.Value;
		if (value <= 0.0)
		{
			TxtSoundIcon.Text = "\ue74f";
		}
		else if (value <= 33.0)
		{
			TxtSoundIcon.Text = "\ue993";
		}
		else if (value <= 66.0)
		{
			TxtSoundIcon.Text = "\ue994";
		}
		else
		{
			TxtSoundIcon.Text = "\ue995";
		}
	}

	private bool ResourcesDirExists()
	{
		string resourcesDir = App.ResourcesDir;
		return !string.IsNullOrEmpty(resourcesDir) && Directory.Exists(resourcesDir);
	}

	private void RefreshMusicList()
	{
		_musicScanError = "";
		string text = ComboMusic.SelectedItem?.ToString();
		if (string.IsNullOrEmpty(text))
		{
			text = App.LastMusicPath;
		}
		ComboMusic.Items.Clear();
		if (!ResourcesDirExists())
		{
			// 资源目录还没建：这不是错误，导入音乐时会一并创建，这里只是列表为空
			_musicDirMissing = true;
			ComboMusic.ToolTip = "资源目录还没创建，点「导入音乐」选择音频并指定保存位置即可创建";
			return;
		}
		_musicDirMissing = false;
		ComboMusic.ToolTip = null;
		try
		{
			List<string> list = Directory.GetFiles(App.ResourcesDir, "*", SearchOption.AllDirectories).Where((string f) =>
			{
				string extension = Path.GetExtension(f);
				return !string.IsNullOrEmpty(extension) && SupportedAudioExtensions.Contains(extension.ToLowerInvariant());
			}).Select((string f) =>
			{
				try
				{
					return Path.GetRelativePath(App.ResourcesDir, f);
				}
				catch
				{
					return Path.GetFileName(f);
				}
			})
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy((string f) => f, StringComparer.OrdinalIgnoreCase)
				.ToList();
			foreach (string item in list)
			{
				ComboMusic.Items.Add(item);
			}
		}
		catch (Exception ex)
		{
			_musicScanError = "扫描资源目录失败：\n\n" + (App.ResourcesDir ?? "(未设置)") + "\n\n" + ex.Message;
		}
		if (!string.IsNullOrEmpty(text) && ComboMusic.Items.Contains(text))
		{
			ComboMusic.SelectedItem = text;
		}
		else if (ComboMusic.Items.Count > 0)
		{
			ComboMusic.SelectedItem = ComboMusic.Items[0];
		}
	}

	private void BtnImportMusic_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.flac;*.aac;*.ogg|所有文件|*.*",
			Title = "选择要导入的音频"
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string text = (ResourcesDirExists() ? App.ResourcesDir : App.BaseDir);
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "选择音频保存位置",
			Multiselect = false
		};
		try
		{
			if (!string.IsNullOrEmpty(text) && Directory.Exists(text))
			{
				openFolderDialog.InitialDirectory = text;
			}
		}
		catch
		{
		}
		if (openFolderDialog.ShowDialog() != true)
		{
			return;
		}
		string folderName = openFolderDialog.FolderName;
		if (string.IsNullOrWhiteSpace(folderName))
		{
			return;
		}
		if (!App.IsDirectoryWritable(folderName))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "所选保存位置不可写，请重新选择。\n\n" + folderName, "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		try
		{
			Directory.CreateDirectory(folderName);
			App.ResourcesDir = folderName;
			App.SaveSettings();
			RefreshDirectoryUI();
			string fileName = Path.GetFileName(openFileDialog.FileName);
			string text2 = Path.Combine(folderName, fileName);
			if (!string.Equals(openFileDialog.FileName, text2, StringComparison.OrdinalIgnoreCase))
			{
				File.Copy(openFileDialog.FileName, text2, overwrite: true);
			}
			App.LastMusicPath = fileName;
			RuntimeLog.Info($"导入音频：{fileName} -> {folderName}");
			bool isLoading = _isLoading;
			_isLoading = true;
			RefreshMusicList();
			_isLoading = isLoading;
			if (ComboMusic.Items.Contains(fileName))
			{
				ComboMusic.SelectedItem = fileName;
			}
			ChkMusic.IsChecked = true;
			SaveMusicSettings();
			ToastNotification.ShowSuccess(Window.GetWindow((DependencyObject)(object)this), "音频已导入");
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "导入音乐失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void BtnRefreshMusic_Click(object sender, RoutedEventArgs e)
	{
		RefreshMusicList();
		if (!string.IsNullOrEmpty(_musicScanError))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), _musicScanError, "无法读取资源目录", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (_musicDirMissing)
		{
			// 目录还没建时刷新是正常操作，给一句怎么做的提示就好
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this),
				"资源目录还没创建。\n\n点「导入音乐」选择音频并指定保存位置，程序会创建该目录并导入。", "提示",
				MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		if (ComboMusic.Items.Count == 0)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "在资源目录中没有找到任何音频文件。\n\n资源目录：\n" + (App.ResourcesDir ?? "(未设置)"));
			return;
		}
		ToastNotification.ShowSuccess(Window.GetWindow((DependencyObject)(object)this), $"已同步 {ComboMusic.Items.Count} 个音频文件");
	}

	private void ComboMusic_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_isLoading && ComboMusic.SelectedItem != null)
		{
			App.LastMusicPath = ComboMusic.SelectedItem.ToString();
			SaveMusicSettings();
		}
	}

	private void ChkMusic_Click(object sender, RoutedEventArgs e)
	{
		if (ChkMusic.IsChecked == true)
		{
			if (!ResourcesDirExists())
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this),
					"资源目录还没创建。\n\n点「导入音乐」选择音频并指定保存位置，程序会创建该目录并导入。", "提示",
					MessageBoxButton.OK, MessageBoxImage.Information);
				ChkMusic.IsChecked = false;
				UpdateMusicControlsEnabled();
				return;
			}
			RefreshMusicList();
			if (!string.IsNullOrEmpty(_musicScanError))
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), _musicScanError, "无法读取资源目录", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				ChkMusic.IsChecked = false;
				UpdateMusicControlsEnabled();
				return;
			}
			if (ComboMusic.Items.Count == 0)
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "资源目录中没有找到任何音频文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				ChkMusic.IsChecked = false;
				UpdateMusicControlsEnabled();
				return;
			}
			if (ComboMusic.SelectedItem == null)
			{
				ComboMusic.SelectedIndex = 0;
			}
		}
		SaveMusicSettings();
		UpdateMusicControlsEnabled();
	}

	private void UpdateMusicControlsEnabled()
	{
		if (ChkMusic != null)
		{
			bool valueOrDefault = ChkMusic.IsChecked == true;
			if (ComboMusic != null)
			{
				ComboMusic.IsEnabled = valueOrDefault;
			}
			if (BtnImportMusic != null)
			{
				BtnImportMusic.IsEnabled = true;
			}
			if (BtnRefreshMusic != null)
			{
				BtnRefreshMusic.IsEnabled = true;
			}
			if (SliderVolume != null)
			{
				SliderVolume.IsEnabled = valueOrDefault;
			}
			if (TxtMuteIcon != null)
			{
				TxtMuteIcon.Opacity = (valueOrDefault ? 1.0 : 0.45);
			}
			bool flag = !App.IsSoundEffectMuted;
			if (ChkSoundEffect != null)
			{
				bool isLoading = _isLoading;
				_isLoading = true;
				ChkSoundEffect.IsChecked = flag;
				_isLoading = isLoading;
			}
			if (SliderSoundVolume != null)
			{
				SliderSoundVolume.IsEnabled = flag;
			}
			if (TxtSoundIcon != null)
			{
				TxtSoundIcon.Opacity = (flag ? 1.0 : 0.45);
			}
		}
	}

	private void ChkSoundEffect_Click(object sender, RoutedEventArgs e)
	{
		if (_isLoading)
		{
			return;
		}
		App.IsSoundEffectMuted = ChkSoundEffect.IsChecked != true;
		UpdateSoundVolumeIcon();
		UpdateMusicControlsEnabled();
		try
		{
			App.SaveSettings();
		}
		catch
		{
		}
	}

	private void SliderVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (TxtVolumeValue != null)
		{
			int num = (int)e.NewValue;
			TxtVolumeValue.Text = num.ToString();
			UpdateVolumeIcon();
			if (num > 0 && App.IsMuted)
			{
				App.IsMuted = false;
				UpdateVolumeIcon();
			}
			if (num == 0 && !App.IsMuted)
			{
				App.IsMuted = true;
				UpdateVolumeIcon();
			}
			SaveMusicSettings();
		}
	}

	private void SliderVolume_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (!_isLoading)
		{
			double num = 5.0;
			double value = ((e.Delta > 0) ? Math.Min(SliderVolume.Maximum, SliderVolume.Value + num) : Math.Max(SliderVolume.Minimum, SliderVolume.Value - num));
			SliderVolume.Value = value;
			e.Handled = true;
		}
	}

	private void TxtMuteIcon_Click(object sender, MouseButtonEventArgs e)
	{
		if (ChkMusic == null || ChkMusic.IsChecked == true)
		{
			if (SliderVolume.Value <= 0.0 && !App.IsMuted)
			{
				SliderVolume.Value = 50.0;
			}
			App.IsMuted = !App.IsMuted;
			UpdateVolumeIcon();
			SaveMusicSettings();
		}
	}

	private void TxtSoundIcon_Click(object sender, MouseButtonEventArgs e)
	{
		if (ChkSoundEffect == null || ChkSoundEffect.IsChecked == true)
		{
			App.IsSoundEffectMuted = !App.IsSoundEffectMuted;
			UpdateSoundVolumeIcon();
			if (!_isLoading)
			{
				App.SaveSettings();
			}
		}
	}

	private void SliderSoundVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (TxtSoundVolumeValue != null)
		{
			int num = (int)e.NewValue;
			TxtSoundVolumeValue.Text = num.ToString();
			App.SoundEffectVolume = (double)num / 100.0;
			if (num > 0 && App.IsSoundEffectMuted)
			{
				App.IsSoundEffectMuted = false;
			}
			UpdateSoundVolumeIcon();
			if (!_isLoading)
			{
				App.SaveSettings();
			}
		}
	}

	private void SliderSoundVolume_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (!_isLoading)
		{
			double num = 5.0;
			double value = ((e.Delta > 0) ? Math.Min(SliderSoundVolume.Maximum, SliderSoundVolume.Value + num) : Math.Max(SliderSoundVolume.Minimum, SliderSoundVolume.Value - num));
			SliderSoundVolume.Value = value;
			e.Handled = true;
		}
	}

	private void UpdateHistoryButtonUI()
	{
		if (BtnToggleHistoryIcon != null && BtnToggleHistoryText != null)
		{
			if (App.IsHistoryDisabled)
			{
				BtnToggleHistoryIcon.Text = "\ue72c";
				BtnToggleHistoryText.Text = "启用历史记录";
			}
			else
			{
				BtnToggleHistoryIcon.Text = "\ue711";
				BtnToggleHistoryText.Text = "禁用历史记录";
			}
		}
	}

	private void BtnToggleHistory_Click(object sender, RoutedEventArgs e)
	{
		if (!App.IsHistoryDisabled)
		{
			MessageBoxResult messageBoxResult = CustomMessageBox.ShowCustom(Window.GetWindow((DependencyObject)(object)this), "确定要禁用历史记录吗？\n\n禁用后：\n  · 后续的抽取操作将不再记录到历史\n  · 已有的历史记录不会被清除\n\n是否继续？", "确认禁用历史记录", "继续", "\ue72a", "取消", "\ue711", MessageBoxImage.Exclamation);
			if (messageBoxResult == MessageBoxResult.Yes)
			{
				App.IsHistoryDisabled = true;
				App.SaveSettings();
				UpdateHistoryButtonUI();
				MessageBus.Publish(new AppMessages.HistorySettingsChanged());
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "历史记录已禁用。\n\n已有的历史记录仍然保留。");
			}
		}
		else
		{
			App.IsHistoryDisabled = false;
			App.SaveSettings();
			UpdateHistoryButtonUI();
			MessageBus.Publish(new AppMessages.HistorySettingsChanged());
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "历史记录已重新启用。");
		}
	}

	private void OpenHistory_Click(object sender, RoutedEventArgs e)
	{
		MessageBus.Publish(new AppMessages.NavigateTo("History"));
	}

	private void UpdateAnimationButtonUI()
	{
		if (BtnToggleAnimationIcon != null && BtnToggleAnimationText != null)
		{
			if (App.IsAnimationDisabled)
			{
				BtnToggleAnimationIcon.Text = "\ue72c";
				BtnToggleAnimationText.Text = "启用动画";
			}
			else
			{
				BtnToggleAnimationIcon.Text = "\ue711";
				BtnToggleAnimationText.Text = "禁用动画";
			}
		}
	}

	private void BtnToggleAnimation_Click(object sender, RoutedEventArgs e)
	{
		if (!App.IsAnimationDisabled)
		{
			MessageBoxResult messageBoxResult = CustomMessageBox.ShowCustom(Window.GetWindow((DependencyObject)(object)this), "确定要禁用动画吗？\n\n禁用后：\n  · 页面切换时将不再播放过渡动画\n  · 抽取结果动画也会被禁用\n\n（卡池封面的视频播放不受此设置影响）\n\n是否继续？", "确认禁用动画", "继续", "\ue72a", "取消", "\ue711", MessageBoxImage.Exclamation);
			if (messageBoxResult == MessageBoxResult.Yes)
			{
				App.IsAnimationDisabled = true;
				App.SaveSettings();
				UpdateAnimationButtonUI();
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "动画已禁用。");
			}
		}
		else
		{
			App.IsAnimationDisabled = false;
			App.SaveSettings();
			UpdateAnimationButtonUI();
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "动画已重新启用。");
		}
	}

	private void AddDir_Click(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "选择卡池文件夹",
			Multiselect = false
		};
		if (openFolderDialog.ShowDialog() == true)
		{
			string folderName = openFolderDialog.FolderName;
			if (!App.IsValidDirectoryPath(folderName))
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "所选目录无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			else if (!App.SearchDirectories.Contains(folderName))
			{
				App.SearchDirectories.Add(folderName);
				RefreshDirectoryUI();
				App.SaveSettings();
				MessageBus.Publish(new AppMessages.PoolListChanged());
			}
		}
	}

	private void DeleteSpecificDir_Click(object sender, RoutedEventArgs e)
	{
		if (App.SearchDirectories.Count <= 1)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "至少需要保留一个卡池目录！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string text = (sender as Button).Tag.ToString();
		// 3.67：按钮文字与图标都和「删除」动作一致（原来写「移除」、图标还是减号）
		if (CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "确定要删除目录“" + text + "”吗？(不会删除文件)", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question, "删除", "取消", "\ue74d") == MessageBoxResult.Yes)
		{
			App.SearchDirectories.Remove(text);
			RefreshDirectoryUI();
			App.SaveSettings();
			MessageBus.Publish(new AppMessages.PoolListChanged());
		}
	}

	private void OpenSpecificDir_Click(object sender, RoutedEventArgs e)
	{
		string text = (sender as Button).Tag.ToString();
		if (App.IsValidDirectoryPath(text) && Directory.Exists(text))
		{
			Process.Start("explorer.exe", text);
		}
	}

	private void SelectResDir_Click(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "选择资源文件夹",
			Multiselect = false
		};
		if (!string.IsNullOrEmpty(App.ResourcesDir) && Directory.Exists(App.ResourcesDir))
		{
			try
			{
				openFolderDialog.InitialDirectory = App.ResourcesDir;
			}
			catch
			{
			}
		}
		if (openFolderDialog.ShowDialog() != true)
		{
			return;
		}
		string folderName = openFolderDialog.FolderName;
		if (string.IsNullOrEmpty(folderName))
		{
			return;
		}
		if (!App.IsValidDirectoryPath(folderName))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "所选目录无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		try
		{
			if (!Directory.Exists(folderName))
			{
				Directory.CreateDirectory(folderName);
			}
			App.ResourcesDir = folderName;
			App.SaveSettings();
			RefreshDirectoryUI();
			bool isLoading = _isLoading;
			_isLoading = true;
			RefreshMusicList();
			_isLoading = isLoading;
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "资源目录已更新：\n\n" + folderName);
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "设置资源目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void OpenResDir_Click(object sender, RoutedEventArgs e)
	{
		// 目录没建时不再报「无法访问」：能建就顺手建好并打开，建不了就打开它的上一级
		string dir = (App.ResourcesDir ?? "").Trim();
		if (string.IsNullOrEmpty(dir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this),
				"还没有设置资源目录。\n\n点「选择文件夹」指定一个目录，或点「导入音乐」选择音频并指定保存位置。", "提示",
				MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}
		try
		{
			if (!Directory.Exists(dir))
			{
				Directory.CreateDirectory(dir);
			}
			Process.Start("explorer.exe", dir);
		}
		catch
		{
			// 建不了（权限 / 盘符不存在）就退到上一级，至少让用户能手动处理
			try
			{
				string parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
				{
					Process.Start("explorer.exe", parent);
					return;
				}
			}
			catch { }

			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this),
				"打不开资源目录：\n\n" + dir + "\n\n请点「选择文件夹」重新指定一个可用的目录。", "无法打开资源目录",
				MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	private void OpenScreenshotDir_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string text = (App.ScreenshotDir ?? "").Trim();
			if (string.IsNullOrEmpty(text))
			{
				text = App.GetDefaultScreenshotDir();
			}
			if (!Directory.Exists(text))
			{
				Directory.CreateDirectory(text);
			}
			Process.Start("explorer.exe", text);
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "无法打开截图目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void SelectScreenshotDir_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			OpenFolderDialog openFolderDialog = new OpenFolderDialog
			{
				Title = "选择截图目录",
				Multiselect = false
			};
			string screenshotDir = App.ScreenshotDir;
			if (!string.IsNullOrEmpty(screenshotDir) && Directory.Exists(screenshotDir))
			{
				openFolderDialog.InitialDirectory = screenshotDir;
			}
			if (openFolderDialog.ShowDialog() != true)
			{
				return;
			}
			string folderName = openFolderDialog.FolderName;
			if (string.IsNullOrEmpty(folderName))
			{
				return;
			}
			if (!App.IsValidDirectoryPath(folderName))
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "所选目录无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			if (!Directory.Exists(folderName))
			{
				Directory.CreateDirectory(folderName);
			}
			App.ScreenshotDir = folderName;
			App.SaveSettings();
			try
			{
				ReportShareCache.ClearAll(out string _);
			}
			catch
			{
			}
			if (TxtScreenshotDir != null)
			{
				TxtScreenshotDir.Text = folderName;
			}
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "截图目录已更新：\n\n" + folderName);
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "设置截图目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void TxtScreenshotDir_LostFocus(object sender, RoutedEventArgs e)
	{
		CommitScreenshotDir();
	}

	private void TxtScreenshotDir_KeyDown(object sender, KeyEventArgs e)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		if ((int)e.Key == 6)
		{
			e.Handled = true;
			CommitScreenshotDir();
		}
	}

	private void CommitScreenshotDir()
	{
		if (TxtScreenshotDir == null)
		{
			return;
		}
		string text = (TxtScreenshotDir.Text ?? "").Trim();
		if (string.IsNullOrEmpty(text))
		{
			text = App.GetDefaultScreenshotDir();
			TxtScreenshotDir.Text = text;
		}
		if (!App.IsValidDirectoryPath(text))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "截图目录路径无效。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			TxtScreenshotDir.Text = App.ScreenshotDir;
			return;
		}
		try
		{
			if (!Directory.Exists(text))
			{
				Directory.CreateDirectory(text);
			}
			if (string.Equals(App.ScreenshotDir, text, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			App.ScreenshotDir = text;
			App.SaveSettings();
			try
			{
				ReportShareCache.ClearAll(out string _);
			}
			catch
			{
			}
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "无法使用该截图目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			TxtScreenshotDir.Text = App.ScreenshotDir;
		}
	}

	private void ClearCache_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			ClearCacheDialog clearCacheDialog = new ClearCacheDialog
			{
				Owner = Window.GetWindow((DependencyObject)(object)this)
			};
			if (clearCacheDialog.ShowDialog() != true)
			{
				return;
			}
			List<string> list = new List<string>();
			List<string> list2 = new List<string>();
			if (clearCacheDialog.ClearLogs)
			{
				if (RuntimeLog.ClearLogs(out string error))
				{
					list.Add("运行日志");
				}
				else
				{
					list2.Add("运行日志：" + error);
				}
			}
			if (clearCacheDialog.ClearReportCache)
			{
				ReportCache.ClearAll();
				if (ReportShareCache.ClearAll(out string error2))
				{
					list.Add("抽取报告缓存");
				}
				else
				{
					list2.Add("抽取报告缓存：" + error2);
				}
			}
			if (clearCacheDialog.ClearTempImages)
			{
				try
				{
					int removed = PoolCover.CleanupTempOverlays();
					list.Add(removed > 0 ? $"临时图片（{removed} 个）" : "临时图片");
				}
				catch (Exception ex3)
				{
					list2.Add("临时图片：" + ex3.Message);
				}
			}
			RuntimeLog.Info("清理缓存：" + ((list.Count > 0) ? string.Join("、", list) : "无"));
			if (list2.Count > 0)
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "部分内容清理失败：\n\n" + string.Join("\n", list2), "清理缓存", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			else
			{
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "已清理：" + string.Join("、", list) + "。\n\n卡池、历史记录和已保存的图片不会被删除。", "清理缓存");
			}
		}
		catch (Exception ex)
		{
			RuntimeLog.Error(ex, "清理缓存失败");
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "清理失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void ResetApp_Click(object sender, RoutedEventArgs e)
	{
		ResetConfirmDialog resetConfirmDialog = new ResetConfirmDialog
		{
			Owner = Window.GetWindow((DependencyObject)(object)this)
		};
		if (resetConfirmDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			if (File.Exists(App.SettingsFile))
			{
				File.Delete(App.SettingsFile);
			}
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "重置失败：" + ex.Message + "\n\n路径：" + App.SettingsFile, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		if (File.Exists(App.SettingsFile))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "无法删除配置文件。", "重置失败", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		string text = null;
		try
		{
			text = Environment.ProcessPath;
		}
		catch
		{
		}
		if (string.IsNullOrEmpty(text))
		{
			try
			{
				text = Process.GetCurrentProcess().MainModule?.FileName;
			}
			catch
			{
			}
		}
		if (string.IsNullOrEmpty(text))
		{
			// ★ 单文件发布时 Assembly.Location 返回空字符串，这里退回 ProcessPath 兜底
			text = Environment.ProcessPath;
		}
		if (string.IsNullOrEmpty(text))
		{
			text = Application.ResourceAssembly?.Location;
		}
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c ping 127.0.0.1 -n 2 >nul & start \"\" \"" + text + "\"",
				WindowStyle = ProcessWindowStyle.Hidden,
				CreateNoWindow = true,
				UseShellExecute = false,
				WorkingDirectory = (Path.GetDirectoryName(text) ?? App.BaseDir)
			});
		}
		catch (Exception ex2)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "配置已重置，但无法自动重启，请手动打开应用。\n\n错误：" + ex2.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		Environment.Exit(0);
	}

	private static bool PathEquals(string a, string b)
	{
		if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
		{
			return false;
		}
		try
		{
			return string.Equals(Path.GetFullPath(a).TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			}), Path.GetFullPath(b).TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			}), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(a.TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			}), b.TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			}), StringComparison.OrdinalIgnoreCase);
		}
	}

	private void StartPoolWatcher()
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected Obj, but got Unknown
		try
		{
			StopPoolWatcher();
			_lastPoolSignature = PoolWatcher.BuildSignature();
			_poolWatchTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(1200L)
			};
			_poolWatchTimer.Tick += (object? s, EventArgs e) =>
			{
				try
				{
					string text = PoolWatcher.BuildSignature();
					if (!(text == _lastPoolSignature))
					{
						_lastPoolSignature = text;
						LoadPoolComboBox();
						UpdatePoolEmptyState();
					}
				}
				catch
				{
				}
			};
			_poolWatchTimer.Start();
		}
		catch
		{
		}
	}

	private void StopPoolWatcher()
	{
		try
		{
			if (_poolWatchTimer != null)
			{
				_poolWatchTimer.Stop();
				_poolWatchTimer = null;
			}
		}
		catch
		{
		}
	}

	private DispatcherTimer? _poolListScrollTimer;

	/// <summary>3.56：卡池列表的滚轮平滑滚动（像素级缓动，而不是一次跳一项）。</summary>
	private void ListPools_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		try
		{
			if (ListPools == null) return;
			var sv = FindDescendantScrollViewer(ListPools);
			if (sv == null || sv.ScrollableHeight <= 0.5) return;

			e.Handled = true;
			double from = sv.VerticalOffset;
			double to = Math.Max(0, Math.Min(from - e.Delta * 1.2, sv.ScrollableHeight));
			SmoothScrollPoolList(sv, from, to);
		}
		catch { }
	}

	private void SmoothScrollPoolList(ScrollViewer sv, double from, double to)
	{
		if (sv == null) return;
		_poolListScrollTimer?.Stop();
		if (Math.Abs(to - from) < 0.5)
		{
			sv.ScrollToVerticalOffset(to);
			return;
		}

		var watch = Stopwatch.StartNew();
		var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
		_poolListScrollTimer = timer;
		timer.Tick += (s, e) =>
		{
			double t = Math.Min(1.0, watch.ElapsedMilliseconds / 180.0);
			double eased = 1 - Math.Pow(1 - t, 3);
			sv.ScrollToVerticalOffset(from + (to - from) * eased);
			if (t >= 1) timer.Stop();
		};
		timer.Start();
	}

	private static ScrollViewer? FindDescendantScrollViewer(DependencyObject? root)
	{
		if (root == null) return null;
		if (root is ScrollViewer sv) return sv;
		int n = 0;
		try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
		for (int i = 0; i < n; i++)
		{
			var found = FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i));
			if (found != null) return found;
		}
		return null;
	}

	private void LoadPoolComboBox()
	{
		ListPools.SelectionChanged -= ComboPools_SelectionChanged;
		try
		{
			ListPools.Items.Clear();
			_allPoolEntries.Clear();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string searchDirectory in App.SearchDirectories)
			{
				if (!App.IsValidDirectoryPath(searchDirectory) || !Directory.Exists(searchDirectory))
				{
					continue;
				}
				foreach (string item in from d in Directory.GetDirectories(searchDirectory)
					orderby d
					select d)
				{
					string fileName = Path.GetFileName(item);
					string text = fileName;
					int num = 2;
					while (hashSet.Contains(text))
					{
						text = $"{fileName} [{num}]";
						num++;
					}
					hashSet.Add(text);
					_allPoolEntries.Add(new PoolEntry
					{
						DisplayName = text,
						Dir = item
					});
					ListPools.Items.Add(text);
				}
			}
			int num2 = -1;
			if (!string.IsNullOrEmpty(App.LastSelectedPoolDir))
			{
				num2 = _allPoolEntries.FindIndex((PoolEntry p) => PathEquals(p.Dir, App.LastSelectedPoolDir));
			}
			if (num2 < 0 && !string.IsNullOrEmpty(App.LastSelectedPoolName))
			{
				num2 = _allPoolEntries.FindIndex((PoolEntry p) => string.Equals(p.DisplayName, App.LastSelectedPoolName, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(p.Dir), App.LastSelectedPoolName, StringComparison.OrdinalIgnoreCase));
			}
			if (num2 < 0 && _allPoolEntries.Count > 0)
			{
				num2 = 0;
			}
			ListPools.SelectedIndex = num2;
		}
		finally
		{
			ListPools.SelectionChanged += ComboPools_SelectionChanged;
		}
		ComboPools_SelectionChanged(null, null);
	}

	private void UpdatePoolEmptyState()
	{
		bool flag = _allPoolEntries != null && _allPoolEntries.Count > 0;
		// 3.52：卡池页改成可以上下滚动（表格高度跟着行数走），可见性挂在滚动容器上
		if (PoolScroll != null)
		{
			PoolScroll.Visibility = (flag ? Visibility.Visible : Visibility.Collapsed);
		}
		if (PoolContentPanel != null)
		{
			PoolContentPanel.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (EmptyPoolPanel != null)
		{
			EmptyPoolPanel.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	/// <summary>把卡池的透明批注层叠到封面预览上（图片 / GIF / 视频都能带批注）。</summary>
	private void ApplyCoverOverlay(string poolDir)
	{
		try
		{
			if (ImgCoverOverlay == null) return;
			string overlay = string.IsNullOrEmpty(poolDir) ? "" : PoolCover.GetOverlayPath(poolDir);
			if (string.IsNullOrEmpty(overlay))
			{
				ImgCoverOverlay.Source = null;
				ImgCoverOverlay.Visibility = Visibility.Collapsed;
				return;
			}
			var bmp = ImageHelper.LoadBitmapFromFile(overlay);
			ImgCoverOverlay.Source = bmp;
			ImgCoverOverlay.Visibility = (bmp != null) ? Visibility.Visible : Visibility.Collapsed;
		}
		catch
		{
			try { ImgCoverOverlay.Visibility = Visibility.Collapsed; } catch { }
		}
	}

	/// <summary>
	/// 选项页封面预览的圆角 / 方角：跟着「当前选中卡池」的封面样式走，
	/// 并按封面真实显示区域裁剪（有黑边时圆角不会落在黑边上）。
	/// </summary>
	private void UpdateCoverRounding(string poolDir)
	{
		try
		{
			bool round = !string.IsNullOrEmpty(poolDir) && PoolCover.IsRoundCover(poolDir);

			Size content = Size.Empty;
			try
			{
				if (VidCover != null && VidCover.Visibility == Visibility.Visible && VidCover.NaturalVideoWidth > 0)
					content = new Size(VidCover.NaturalVideoWidth, VidCover.NaturalVideoHeight);
				else if (ImgCover != null && ImgCover.Visibility == Visibility.Visible && ImgCover.Source is BitmapSource bmp)
					content = new Size(bmp.PixelWidth, bmp.PixelHeight);
			}
			catch { }

			RoundCorner.SetEnabledForContent(CoverClipHost, round, content);
		}
		catch { }
	}

	private string GetSelectedPoolDir()
	{
		int selectedIndex = ListPools.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _allPoolEntries.Count)
		{
			return null;
		}
		return _allPoolEntries[selectedIndex].Dir;
	}

	private string GetSelectedPoolName()
	{
		int selectedIndex = ListPools.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _allPoolEntries.Count)
		{
			return null;
		}
		return Path.GetFileName(_allPoolEntries[selectedIndex].Dir);
	}

	private void ComboPools_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdatePoolEmptyState();
		try
		{
			StopGifPlayback();
		}
		catch
		{
		}
		_currentGifPath = "";
		GridPoolContent.ItemsSource = null;
		try
		{
			ImgCover.Visibility = Visibility.Collapsed;
			ImgCover.Source = null;
			// 批注层跟着封面一起清掉，避免「新封面还没出来、旧批注先显示」
			try
			{
				if (ImgCoverOverlay != null)
				{
					ImgCoverOverlay.Source = null;
					ImgCoverOverlay.Visibility = Visibility.Collapsed;
				}
			}
			catch
			{
			}
			VidCover.Visibility = Visibility.Collapsed;
			VidCover.Stop();
			VidCover.Source = null;
		}
		catch
		{
		}
		TxtCoverPlaceholder.Visibility = Visibility.Visible;
		TxtCoverPlaceholder.Text = "点击可选择文件";
		string selectedPoolDir = GetSelectedPoolDir();
		// 「圆角封面」是卡池自己的设置：选项页的封面预览也跟着变（图片 / GIF / 视频一起裁剪）
		try { UpdateCoverRounding(selectedPoolDir); } catch { }
		if (string.IsNullOrEmpty(selectedPoolDir))
		{
			return;
		}
		string selectedPoolName = GetSelectedPoolName();
		try
		{
			string[] coverFiles = PoolCover.GetCoverFiles(selectedPoolDir);
			if (coverFiles.Length != 0)
			{
				string text = coverFiles[0];
				string text2 = Path.GetExtension(text).ToLowerInvariant();
				bool flag = false;
				if (VideoExts.Contains(text2))
				{
					try
					{
						VidCover.Source = new Uri(text);
						VidCover.Visibility = Visibility.Visible;
						TxtCoverPlaceholder.Visibility = Visibility.Collapsed;
						VidCover.Play();
						flag = true;
					}
					catch
					{
						flag = false;
					}
				}
				else if (ImageExts.Contains(text2))
				{
					if (text2 == ".gif")
					{
						try
						{
							ImgCover.Visibility = Visibility.Visible;
							TxtCoverPlaceholder.Visibility = Visibility.Collapsed;
							StartGifPlayback(text);
							_currentGifPath = text;
							flag = ImgCover.Source != null;
						}
						catch
						{
							flag = false;
						}
					}
					else
					{
						try
						{
							BitmapSource bitmapSource = ImageHelper.LoadBitmapFromFile(text);
							if (bitmapSource != null)
							{
								ImgCover.Source = bitmapSource;
								ImgCover.Visibility = Visibility.Visible;
								TxtCoverPlaceholder.Visibility = Visibility.Collapsed;
								flag = true;
							}
							else
							{
								flag = false;
							}
						}
						catch
						{
							flag = false;
						}
					}
				}
				if (!flag)
				{
					TryLoadDefaultCover(selectedPoolName);
				}
			}
			else
			{
				TryLoadDefaultCover(selectedPoolName);
			}
		}
		catch
		{
			TryLoadDefaultCover(selectedPoolName);
		}
		// 3.52：选项页的封面预览也要带批注层（和主界面一致）
		ApplyCoverOverlay(selectedPoolDir);
		// 封面解码 / 视频尺寸就绪后再按内容矩形裁一次圆角（方角时清空裁剪）
		UpdateCoverRounding(selectedPoolDir);
		try
		{
			string text3 = "";
			string path = Path.Combine(selectedPoolDir, "data.lock");
			string path2 = Path.Combine(selectedPoolDir, "data.zip");
			string text4 = Path.Combine(selectedPoolDir, "data.txt");
			if (File.Exists(path) || File.Exists(path2))
			{
				try
				{
					text3 = PoolArchive.ReadDataTxtFromArchive(selectedPoolDir);
				}
				catch
				{
				}
			}
			if (string.IsNullOrEmpty(text3) && File.Exists(text4))
			{
				text3 = CryptoUtils.ReadAllTextWithEncoding(text4);
			}
			if (string.IsNullOrEmpty(text3) && File.Exists(path))
			{
				try
				{
					text3 = CryptoUtils.DecryptString(File.ReadAllText(path));
				}
				catch
				{
				}
			}
			if (!string.IsNullOrEmpty(text3))
			{
				SetPoolContentTable(text3);
			}
		}
		catch
		{
			SetPoolContentFailure();
		}
	}

	private void PoolContentClip_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (sender is Grid grid)
			grid.Clip = new RectangleGeometry(new Rect(0, 0, grid.ActualWidth, grid.ActualHeight), 6, 6);
	}

	private void SetPoolContentTable(string content)
	{
		try
		{
			string[] lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
			if (lines.Length == 0)
			{
				GridPoolContent.ItemsSource = null;
				return;
			}

			string[] headers = SplitPoolColumns(lines[0]);
			if (headers.Length == 0) headers = new[] { "内容" };
			for (int i = 0; i < headers.Length; i++)
			{
				if (headers[i] == "抽选对象") headers[i] = "抽取对象";
			}

			var table = new DataTable();
			var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < headers.Length; i++)
			{
				string name = string.IsNullOrWhiteSpace(headers[i]) ? "列" + (i + 1) : headers[i];
				string unique = name;
				int suffix = 2;
				while (!names.Add(unique)) unique = name + "_" + suffix++;
				table.Columns.Add(unique, typeof(string));
			}

			for (int i = 1; i < lines.Length; i++)
			{
				string[] cells = SplitPoolColumns(lines[i]);
				var row = table.NewRow();
				for (int c = 0; c < table.Columns.Count; c++)
					row[c] = c < cells.Length ? cells[c] : "";
				table.Rows.Add(row);
			}

			GridPoolContent.ItemsSource = table.DefaultView;
		}
		catch
		{
			SetPoolContentFailure();
		}
	}

	private void SetPoolContentFailure()
	{
		var table = new DataTable();
		table.Columns.Add("内容", typeof(string));
		table.Rows.Add("加载失败");
		GridPoolContent.ItemsSource = table.DefaultView;
	}

	private static string[] SplitPoolColumns(string line)
	{
		return (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
	}

	private static BitmapImage LoadEmbeddedImage(string resourcePath)
	{
		return EmbeddedImageLoader.Load(resourcePath);
	}

	private void TryLoadDefaultCover(string poolName)
	{
		BitmapImage bitmapImage = LoadEmbeddedImage("Assets/default_cover.jpg");
		if (bitmapImage != null)
		{
			ImgCover.Source = bitmapImage;
			ImgCover.Visibility = Visibility.Visible;
		}
		else
		{
			ImgCover.Source = null;
			ImgCover.Visibility = Visibility.Collapsed;
		}
		TxtCoverPlaceholder.Text = poolName ?? "点击可选择文件";
		TxtCoverPlaceholder.Visibility = Visibility.Visible;
	}

	private void Cover_Click(object sender, MouseButtonEventArgs e)
	{
		string selectedPoolDir = GetSelectedPoolDir();
		if (string.IsNullOrEmpty(selectedPoolDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|所有文件|*.*",
			FilterIndex = 1
		};
		if (openFileDialog.ShowDialog() != true)
		{
			return;
		}
		string text = Path.GetExtension(openFileDialog.FileName).ToLowerInvariant();
		if (!VideoExts.Contains(text) && !ImageExts.Contains(text))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "不支持的文件类型：" + text, "不支持的格式", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		try
		{
			PoolCover.SaveCover(selectedPoolDir, openFileDialog.FileName);
			ComboPools_SelectionChanged(null, null);
			MessageBus.Publish(new AppMessages.PoolListChanged());
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "上传封面失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void VidCover_MediaOpened(object sender, RoutedEventArgs e)
	{
		// 视频打开后才能拿到真实尺寸：此时按内容矩形重算圆角（方角卡池裁成空 Clip）
		UpdateCoverRounding(GetSelectedPoolDir());
	}

	private void VidCover_MediaEnded(object sender, RoutedEventArgs e)
	{
		Uri source = VidCover.Source;
		if (source == null)
		{
			return;
		}
		try
		{
			VidCover.Stop();
			VidCover.Source = null;
			VidCover.Source = source;
			if (VidCover.Visibility == Visibility.Visible)
			{
				VidCover.Play();
			}
		}
		catch
		{
		}
	}

	private void VidCover_MediaFailed(object sender, ExceptionRoutedEventArgs e)
	{
		try
		{
			VidCover.Visibility = Visibility.Collapsed;
			VidCover.Stop();
			VidCover.Source = null;
		}
		catch
		{
		}
		try
		{
			string selectedPoolName = GetSelectedPoolName();
			TxtCoverPlaceholder.Text = (string.IsNullOrEmpty(selectedPoolName) ? "视频无法播放" : selectedPoolName);
			TxtCoverPlaceholder.Visibility = Visibility.Visible;
		}
		catch
		{
		}
	}

	private void BtnNewPool_Click(object sender, RoutedEventArgs e)
	{
		MessageBus.Publish(new AppMessages.OpenPoolEditor("", fromMainPage: false));
	}

	/// <summary>带密码的卡池在「修改 / 复制」前要求输入密码；忘记密码可走密保重置。</summary>
	private bool PromptPoolPassword(string poolDir, string poolName, string action)
	{
		var owner = Window.GetWindow((DependencyObject)(object)this);

		var dialog = new PasswordDialog("要" + action + "锁定的卡池“" + poolName + "”需要验证密码") { Owner = owner };
		bool? result = dialog.ShowDialog();

		if (dialog.ForgotPasswordRequested)
		{
			return TryResetPasswordBySecretQA(poolDir, poolName);
		}

		if (result != true) return false;
		if (!CryptoUtils.VerifyPassword(poolName, dialog.Password))
		{
			CustomMessageBox.Show(owner, "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			return false;
		}
		return true;
	}

	/// <summary>
	/// 复制加密卡池前验证密码，并把密码原样带回去 —— 副本要保持加密，
	/// 需要用它为新卡池名写入密码哈希（否则副本会变成「有 data.lock 但没有密码」的死锁卡池）。
	/// </summary>
	private bool PromptCopyPassword(string poolDir, string poolName, out string password)
	{
		password = null;
		var owner = Window.GetWindow((DependencyObject)(object)this);

		var dialog = new PasswordDialog("要复制锁定的卡池“" + poolName + "”需要验证密码") { Owner = owner };
		if (dialog.ShowDialog() != true)
		{
			CustomMessageBox.Show(owner, "已取消复制。");
			return false;
		}

		if (!CryptoUtils.VerifyPassword(poolName, dialog.Password))
		{
			CustomMessageBox.Show(owner, "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			return false;
		}

		password = dialog.Password;
		return true;
	}

	private void BtnModifyPool_Click(object sender, RoutedEventArgs e)
	{
		string selectedPoolDir = GetSelectedPoolDir();
		if (string.IsNullOrEmpty(selectedPoolDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string selectedPoolName = GetSelectedPoolName();

		// 编辑带密码的卡池：先输入密码
		if (PoolFileOps.IsPoolLocked(selectedPoolDir) && !PromptPoolPassword(selectedPoolDir, selectedPoolName, "修改")) return;

		MessageBus.Publish(new AppMessages.OpenPoolEditor(selectedPoolDir, fromMainPage: false, passwordVerified: true));
	}

	private void DeletePool_Click(object sender, RoutedEventArgs e)
	{
		string poolDir = GetSelectedPoolDir();
		if (string.IsNullOrEmpty(poolDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		// 删除不需要密码（即使卡池设置了密码）
		string selectedPoolName = GetSelectedPoolName();
		if (CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "确定要删除“" + selectedPoolName + "”吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question, "删除", "取消", "\ue74d") != MessageBoxResult.Yes)
		{
			return;
		}
		bool deleteOk = true;
		string deleteError = "";
		ProgressDialog.Run(Window.GetWindow((DependencyObject)(object)this), "正在删除卡池", "正在删除「" + selectedPoolName + "」…", (Action<int, int, string> report) =>
		{
			try
			{
				DeleteDirectoryWithProgress(poolDir, report);
			}
			catch (Exception ex)
			{
				deleteOk = false;
				deleteError = ex.Message;
			}
		});
		if (!deleteOk)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "删除失败：" + deleteError, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		if (App.LastSelectedPoolDir == poolDir)
		{
			App.LastSelectedPoolDir = "";
		}
		if (App.LastSelectedPoolName == selectedPoolName)
		{
			App.LastSelectedPoolName = "";
		}
		LoadPoolComboBox();
		try
		{
			StopGifPlayback();
		}
		catch
		{
		}
		try
		{
			VidCover.Stop();
		}
		catch
		{
		}
		_currentGifPath = "";
		GridPoolContent.ItemsSource = null;
		try
		{
			ImgCover.Source = null;
			ImgCover.Visibility = Visibility.Collapsed;
			VidCover.Source = null;
			VidCover.Visibility = Visibility.Collapsed;
		}
		catch
		{
		}
		TxtCoverPlaceholder.Text = "点击可选择文件";
		TxtCoverPlaceholder.Visibility = Visibility.Visible;
		if (ListPools.Items.Count > 0)
		{
			ListPools.SelectedIndex = 0;
			ComboPools_SelectionChanged(null, null);
		}
		MessageBus.Publish(new AppMessages.PoolListChanged());
	}

	private void BtnExportPools_Click(object sender, RoutedEventArgs e)
	{
		if (_allPoolEntries == null || _allPoolEntries.Count == 0)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "当前没有可导出的卡池。");
			return;
		}
		List<(string, string)> pools = _allPoolEntries.Select((PoolEntry p) => (Name: p.DisplayName, Dir: p.Dir)).ToList();
		ExportPoolsDialog exportPoolsDialog = new ExportPoolsDialog(pools, GetSelectedPoolDir())
		{
			Owner = Window.GetWindow((DependencyObject)(object)this)
		};
		exportPoolsDialog.ShowDialog();
	}

	private void BtnImportPools_Click(object sender, RoutedEventArgs e)
	{
		string selectedPoolDir = GetSelectedPoolDir();
		string text = ((!string.IsNullOrEmpty(selectedPoolDir)) ? Path.GetDirectoryName(selectedPoolDir) : null);
		if (string.IsNullOrEmpty(text) || !App.IsValidDirectoryPath(text))
		{
			text = App.SearchDirectories.FirstOrDefault((string d) => App.IsValidDirectoryPath(d));
		}
		if (string.IsNullOrEmpty(text) || !App.IsDirectoryWritable(text))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "没有可以写入的卡池目录。\n\n请先到「选项 → 系统」添加一个卡池列表目录，并确认它有写入权限。", "无法导入", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		ImportPoolsDialog dialog = new ImportPoolsDialog(text)
		{
			Owner = Window.GetWindow((DependencyObject)(object)this)
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		LoadPoolComboBox();
		if (!string.IsNullOrEmpty(dialog.FirstImportedDir))
		{
			int num = _allPoolEntries.FindIndex((PoolEntry p) => string.Equals(p.Dir, dialog.FirstImportedDir, StringComparison.OrdinalIgnoreCase));
			if (num >= 0)
			{
				ListPools.SelectedIndex = num;
			}
		}
		UpdatePoolEmptyState();
		MessageBus.Publish(new AppMessages.PoolListChanged());
	}

	private void CopyPool_Click(object sender, RoutedEventArgs e)
	{
		// 复制带密码的卡池：先验证密码，并记住密码用于给副本重建密码哈希
		string copyPassword = null;
		bool sourceLocked = PoolFileOps.IsPoolLocked(GetSelectedPoolDir());
		if (sourceLocked && !PromptCopyPassword(GetSelectedPoolDir(), GetSelectedPoolName(), out copyPassword)) return;
		string srcDir = GetSelectedPoolDir();
		if (string.IsNullOrEmpty(srcDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string selectedPoolName = GetSelectedPoolName();
		string defaultDir = Path.GetDirectoryName(srcDir) ?? "";
		CopyPoolDialog copyPoolDialog = new CopyPoolDialog(selectedPoolName + "_副本", defaultDir)
		{
			Owner = Window.GetWindow((DependencyObject)(object)this)
		};
		if (copyPoolDialog.ShowDialog() != true)
		{
			return;
		}
		string poolName = copyPoolDialog.PoolName;
		string targetDir = copyPoolDialog.TargetDir;
		if (string.IsNullOrEmpty(poolName) || poolName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this),
				"名称包含非法符号，请修改后再继续。\n\n以下符号不能使用：\n" + PoolTextRules.InvalidFileNameCharsText,
				"名称不可用", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		string path = Path.GetDirectoryName(srcDir) ?? "";
		bool flag;
		try
		{
			flag = string.Equals(Path.GetFullPath(targetDir), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			flag = false;
		}
		if (flag && string.Equals(poolName, selectedPoolName, StringComparison.OrdinalIgnoreCase))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "新名称与原来的卡池相同。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string destDir = Path.Combine(targetDir, poolName);
		if (Directory.Exists(destDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "该位置已存在相同名称的卡池！", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			return;
		}
		try
		{
			ProgressDialog.Run(Window.GetWindow((DependencyObject)(object)this), "正在复制卡池", "正在复制「" + selectedPoolName + "」…", (Action<int, int, string> report) =>
			{
				CopyDirectoryWithProgress(srcDir, destDir, report);
			});
			// ★ 副本保持加密：复制过来的 data.lock 原样保留，不再解密成明文。
			//   同时把密码哈希迁移到新卡池名，保证副本能用原密码打开。
			if (sourceLocked && !string.IsNullOrEmpty(copyPassword))
			{
				try { CryptoUtils.SavePasswordHash(poolName, copyPassword); } catch { }
			}
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "复制成功！", "成功");
			App.LastSelectedPoolDir = destDir;
			LoadPoolComboBox();
			MessageBus.Publish(new AppMessages.PoolListChanged());
		}
		catch (Exception ex)
		{
			try
			{
				if (Directory.Exists(destDir))
				{
					Directory.Delete(destDir, recursive: true);
				}
			}
			catch
			{
			}
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "复制失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private static void DeleteDirectoryWithProgress(string dir, Action<int, int, string> report)
	{
		List<string> files = new List<string>();
		List<string> subDirs = new List<string>();
		Collect(dir);
		int num = files.Count + subDirs.Count + 1;
		int num2 = 0;
		report?.Invoke(0, num, "正在删除…");
		foreach (string item2 in files)
		{
			try
			{
				File.SetAttributes(item2, FileAttributes.Normal);
				File.Delete(item2);
			}
			catch
			{
			}
			num2++;
			if (num2 % 8 == 0)
			{
				report?.Invoke(num2, num, "正在删除文件…");
			}
		}
		foreach (string item3 in subDirs.OrderByDescending((string x) => x.Length))
		{
			try
			{
				Directory.Delete(item3, recursive: true);
			}
			catch
			{
			}
			num2++;
			if (num2 % 4 == 0)
			{
				report?.Invoke(num2, num, "正在删除文件夹…");
			}
		}
		try
		{
			Directory.Delete(dir, recursive: true);
		}
		catch
		{
		}
		report?.Invoke(num, num, "删除完成");
		void Collect(string d)
		{
			try
			{
				string[] files2 = Directory.GetFiles(d);
				foreach (string item in files2)
				{
					files.Add(item);
				}
				string[] directories = Directory.GetDirectories(d);
				foreach (string text in directories)
				{
					subDirs.Add(text);
					Collect(text);
				}
			}
			catch
			{
			}
		}
	}

	private static void CopyDirectoryWithProgress(string srcDir, string destDir, Action<int, int, string> report)
	{
		List<string> files = new List<string>();
		Collect(srcDir);
		int num = Math.Max(1, files.Count);
		int num2 = 0;
		report?.Invoke(0, num, "正在复制…");
		foreach (string item2 in files)
		{
			string relativePath = Path.GetRelativePath(srcDir, item2);
			string text = Path.Combine(destDir, relativePath);
			string directoryName = Path.GetDirectoryName(text);
			if (!string.IsNullOrEmpty(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.Copy(item2, text, overwrite: true);
			num2++;
			if (num2 % 8 == 0)
			{
				report?.Invoke(num2, num, "正在复制文件…");
			}
		}
		report?.Invoke(num, num, "复制完成");
		void Collect(string d)
		{
			string[] files2 = Directory.GetFiles(d);
			foreach (string item in files2)
			{
				files.Add(item);
			}
			string[] directories = Directory.GetDirectories(d);
			foreach (string d2 in directories)
			{
				Collect(d2);
			}
		}
	}

	private static void CopyDirectoryRecursive(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);
		string[] files = Directory.GetFiles(sourceDir);
		foreach (string text in files)
		{
			File.Copy(text, Path.Combine(destDir, Path.GetFileName(text)), overwrite: true);
		}
		string[] directories = Directory.GetDirectories(sourceDir);
		foreach (string text2 in directories)
		{
			CopyDirectoryRecursive(text2, Path.Combine(destDir, Path.GetFileName(text2)));
		}
	}

	private void StartGifPlayback(string path)
	{
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Expected Obj, but got Unknown
		StopGifPlayback();
		try
		{
			_gifDecoder = ImageHelper.LoadGifFromFile(path);
			if (_gifDecoder != null && _gifDecoder.Frames.Count != 0)
			{
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
		}
		catch
		{
			BitmapSource source = ImageHelper.LoadBitmapFromFile(path);
			ImgCover.Source = source;
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
		if (_gifDecoder != null && _gifDecoder.Frames.Count != 0)
		{
			_gifFrameIndex = (_gifFrameIndex + 1) % _gifDecoder.Frames.Count;
			ImgCover.Source = _gifDecoder.Frames[_gifFrameIndex];
			if (_gifTimer != null)
			{
				_gifTimer.Interval = GetFrameDelay(_gifDecoder.Frames[_gifFrameIndex]);
			}
		}
	}

	private TimeSpan GetFrameDelay(BitmapFrame frame)
	{
		try
		{
			if (frame.Metadata is BitmapMetadata bitmapMetadata)
			{
				object query = bitmapMetadata.GetQuery("/grctlext/Delay");
				if (query != null)
				{
					int num = Convert.ToInt32(query);
					int num2 = num * 10;
					if (num2 < 20)
					{
						num2 = 100;
					}
					return TimeSpan.FromMilliseconds(num2);
				}
			}
		}
		catch
		{
		}
		return TimeSpan.FromMilliseconds(100L);
	}

	private static (string question, string answer) ReadSecretQA(string poolDir)
	{
		try
		{
			string path = Path.Combine(poolDir, "secret_qa.lock");
			if (!File.Exists(path))
			{
				return (question: "", answer: "");
			}
			string text = CryptoUtils.DecryptString(File.ReadAllText(path));
			if (string.IsNullOrEmpty(text))
			{
				return (question: "", answer: "");
			}
			int num = text.IndexOf('\u0001');
			if (num < 0)
			{
				return (question: "", answer: "");
			}
			return (question: text.Substring(0, num), answer: text.Substring(num + 1));
		}
		catch
		{
			return (question: "", answer: "");
		}
	}

	private static void WriteSecretQA(string poolDir, string question, string answer)
	{
		try
		{
			string path = Path.Combine(poolDir, "secret_qa.lock");
			string plainText = question + "\u0001" + answer;
			File.WriteAllText(path, CryptoUtils.EncryptString(plainText));
		}
		catch
		{
		}
	}

	private bool VerifySecretQA(string poolDir)
	{
		var (text, correctAnswer) = ReadSecretQA(poolDir);
		if (string.IsNullOrEmpty(text))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "该卡池未设置密保问题。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return false;
		}
		SecretQAWindow secretQAWindow = new SecretQAWindow(text, correctAnswer)
		{
			Owner = Window.GetWindow((DependencyObject)(object)this)
		};
		return secretQAWindow.ShowDialog() == true;
	}

	private bool TryResetPasswordBySecretQA(string poolDir, string poolName)
	{
		if (!VerifySecretQA(poolDir))
		{
			return false;
		}
		PasswordDialog passwordDialog = new PasswordDialog("请设置新密码", setPassword: true)
		{
			Owner = Window.GetWindow((DependencyObject)(object)this)
		};
		if (passwordDialog.ShowDialog() != true)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "已取消密码重置。");
			return false;
		}
		try
		{
			CryptoUtils.SavePasswordHash(poolName, passwordDialog.Password);
		}
		catch (Exception ex)
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "更新密码失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
			return false;
		}
		if (!string.IsNullOrEmpty(passwordDialog.SecretQuestion))
		{
			WriteSecretQA(poolDir, passwordDialog.SecretQuestion, passwordDialog.SecretAnswer);
		}
		CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "密码已重置成功！", "成功");
		return true;
	}
}
