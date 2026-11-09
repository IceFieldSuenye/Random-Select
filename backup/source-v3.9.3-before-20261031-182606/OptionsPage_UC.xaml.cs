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

	private static readonly TimeSpan NavExitDuration = TimeSpan.FromMilliseconds(220L);

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
	}

	private void OptionsPage_UC_Loaded(object sender, RoutedEventArgs e)
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;
		StartPoolWatcher();
		if (EdgeScrollBar != null)
		{
			EdgeScrollBar.ValueChanged += EdgeScrollBar_ValueChanged;
			BindEdgeScrollBarFor(_currentNavIndex);
		}
		LoadUIState();
		LoadMusicSettingsUI();
		LoadAppVersion();
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

	private void AiNote_Click(object sender, MouseButtonEventArgs e)
	{
		try
		{
			var dialog = new AiInfoDialog { Owner = Window.GetWindow((DependencyObject)(object)this) };
			dialog.ShowDialog();
		}
		catch { }
	}

	private void LoadUIState()
	{
		ListDirs.ItemsSource = null;
		ListDirs.ItemsSource = App.SearchDirectories;
		TxtResDir.Text = App.ResourcesDir;
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
			if (PanelHideSection != null)
			{
				PanelHideSection.Visibility = ((!App.IsOptionSectionVisible) ? Visibility.Collapsed : Visibility.Visible);
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
			if (HelpChapterAdvanced != null)
			{
				HelpChapterAdvanced.Visibility = ((!App.IsAdvancedVisible) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (HelpChapterHide != null)
			{
				HelpChapterHide.Visibility = ((!App.IsOptionSectionVisible) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (HelpPoolEdit != null)
			{
				HelpPoolEdit.Visibility = ((!App.IsEditPoolVisible) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (HelpProbabilityDraw != null)
			{
				HelpProbabilityDraw.Visibility = ((!App.IsProbabilityVisible) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (HelpChapterReport != null)
			{
				HelpChapterReport.Visibility = ((!App.IsGachaReportVisible) ? Visibility.Collapsed : Visibility.Visible);
			}
			if (HelpReportAdvanced != null)
			{
				HelpReportAdvanced.Visibility = ((!App.IsAdvancedVisible) ? Visibility.Collapsed : Visibility.Visible);
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
				ContentPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
				ContentPanel.BeginAnimation(UIElement.OpacityProperty, null);
				NavPanelTransform.X = -150.0;
				NavPanel.Opacity = 0.0;
				ContentPanelTransform.Y = 60.0;
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
			double y = ContentPanelTransform.Y;
			double opacity2 = ContentPanel.Opacity;
			DoubleAnimation doubleAnimation = new DoubleAnimation(x, -150.0, NavExitDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation2 = new DoubleAnimation(opacity, 0.0, NavExitDuration)
			{
				EasingFunction = easingFunction
			};
			DoubleAnimation doubleAnimation3 = new DoubleAnimation(y, 60.0, NavExitDuration)
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
				if (++completed >= 4)
				{
					try
					{
						NavPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
						NavPanel.BeginAnimation(UIElement.OpacityProperty, null);
						ContentPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
						ContentPanel.BeginAnimation(UIElement.OpacityProperty, null);
						NavPanelTransform.X = -150.0;
						NavPanel.Opacity = 0.0;
						ContentPanelTransform.Y = 60.0;
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
			ContentPanelTransform.BeginAnimation(TranslateTransform.YProperty, doubleAnimation3);
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
		BindEdgeScrollBar(GetNavPanel(navIndex) as ScrollViewer);
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
				ChkHideAdvanced.IsChecked = true;
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
				ChkHideAdvanced.IsChecked = true;
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

	private void HideSectionInfo_Click(object sender, MouseButtonEventArgs e)
	{
		try
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "· 勾选「隐藏基于概率的抽取」会一并勾选并锁定「隐藏高级抽卡规则」。\n· 「隐藏查看抽取报告」会同时隐藏主界面与历史记录页的报告入口。\n· 在任意界面按 F12 可临时显示/隐藏所有已隐藏的功能（重启恢复）。\n· 若连「功能隐藏」本身也被隐藏，只能用 F12 临时把它调出来。", "功能隐藏说明", MessageBoxButton.OK, MessageBoxImage.None);
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
			_musicScanError = "资源目录不存在：\n\n" + (App.ResourcesDir ?? "(未设置)") + "\n\n请点击「导入音乐」，选择音频并指定保存位置。";
			return;
		}
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
			TxtResDir.Text = folderName;
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
				CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "资源目录不存在。\n\n请点击「导入音乐」，选择音频并指定保存位置。", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
				ListDirs.ItemsSource = null;
				ListDirs.ItemsSource = App.SearchDirectories;
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
		if (CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "确定要移除目录“" + text + "”吗？(不会删除文件)", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question, "移除", "取消", "\ue738") == MessageBoxResult.Yes)
		{
			App.SearchDirectories.Remove(text);
			ListDirs.ItemsSource = null;
			ListDirs.ItemsSource = App.SearchDirectories;
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
			TxtResDir.Text = folderName;
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
		if (!ResourcesDirExists())
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "资源目录不存在：\n\n" + (App.ResourcesDir ?? "(未设置)") + "\n\n请点击「导入音乐」，选择音频并指定保存位置。", "无法访问资源目录", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		else
		{
			Process.Start("explorer.exe", App.ResourcesDir);
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
			text = Application.ResourceAssembly.Location;
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

	private void LoadPoolComboBox()
	{
		ComboPools.SelectionChanged -= ComboPools_SelectionChanged;
		try
		{
			ComboPools.Items.Clear();
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
					ComboPools.Items.Add(text);
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
			ComboPools.SelectedIndex = num2;
		}
		finally
		{
			ComboPools.SelectionChanged += ComboPools_SelectionChanged;
		}
		ComboPools_SelectionChanged(null, null);
	}

	private void UpdatePoolEmptyState()
	{
		bool flag = _allPoolEntries != null && _allPoolEntries.Count > 0;
		if (PoolContentPanel != null)
		{
			PoolContentPanel.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (EmptyPoolPanel != null)
		{
			EmptyPoolPanel.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
		}
	}

	private string GetSelectedPoolDir()
	{
		int selectedIndex = ComboPools.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _allPoolEntries.Count)
		{
			return null;
		}
		return _allPoolEntries[selectedIndex].Dir;
	}

	private string GetSelectedPoolName()
	{
		int selectedIndex = ComboPools.SelectedIndex;
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

	private static BitmapImage LoadEmbeddedImage(string resourcePath)	{
		try
		{
			Assembly executingAssembly = Assembly.GetExecutingAssembly();
			string name = executingAssembly.GetName().Name + ".g.resources";
			using (Stream stream = executingAssembly.GetManifestResourceStream(name))
			{
				if (stream == null)
				{
					return null;
				}
				using ResourceReader resourceReader = new ResourceReader(stream);
				string value = Path.GetFileNameWithoutExtension(resourcePath).ToLowerInvariant();
				IDictionaryEnumerator enumerator = resourceReader.GetEnumerator();
				while (enumerator.MoveNext())
				{
					string text = enumerator.Key as string;
					if (string.IsNullOrEmpty(text) || text.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}
					object value2 = enumerator.Value;
					Stream stream2 = null;
					if (value2 is byte[] buffer)
					{
						stream2 = new MemoryStream(buffer);
					}
					else
					{
						if (!(value2 is Stream stream3))
						{
							return null;
						}
						MemoryStream memoryStream = new MemoryStream();
						stream3.CopyTo(memoryStream);
						memoryStream.Position = 0L;
						stream2 = memoryStream;
					}
					using (stream2)
					{
						BitmapImage bitmapImage = new BitmapImage();
						bitmapImage.BeginInit();
						bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
						bitmapImage.StreamSource = stream2;
						bitmapImage.EndInit();
						((Freezable)bitmapImage).Freeze();
						return bitmapImage;
					}
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private void TryLoadDefaultCover(string poolName)
	{
		BitmapImage bitmapImage = LoadEmbeddedImage("default_cover.jpg");
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

	private void BtnModifyPool_Click(object sender, RoutedEventArgs e)
	{
		string selectedPoolDir = GetSelectedPoolDir();
		if (string.IsNullOrEmpty(selectedPoolDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string selectedPoolName = GetSelectedPoolName();
		string path = Path.Combine(selectedPoolDir, "data.lock");
		if (File.Exists(path))
		{
			PasswordDialog passwordDialog = new PasswordDialog("修改锁定的卡池“" + selectedPoolName + "”需要验证密码")
			{
				Owner = Window.GetWindow((DependencyObject)(object)this)
			};
			bool? flag = passwordDialog.ShowDialog();
			if (passwordDialog.ForgotPasswordRequested)
			{
				if (!TryResetPasswordBySecretQA(selectedPoolDir, selectedPoolName))
				{
					return;
				}
			}
			else
			{
				if (flag != true)
				{
					return;
				}
				if (!CryptoUtils.VerifyPassword(selectedPoolName, passwordDialog.Password))
				{
					CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
					return;
				}
			}
		}
		MessageBus.Publish(new AppMessages.OpenPoolEditor(selectedPoolDir, fromMainPage: false));
	}

	private void DeletePool_Click(object sender, RoutedEventArgs e)
	{
		string poolDir = GetSelectedPoolDir();
		if (string.IsNullOrEmpty(poolDir))
		{
			CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "请先选择一个卡池！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string selectedPoolName = GetSelectedPoolName();
		if (File.Exists(Path.Combine(poolDir, "data.lock")))
		{
			PasswordDialog passwordDialog = new PasswordDialog("删除锁定的卡池“" + selectedPoolName + "”需要验证密码")
			{
				Owner = Window.GetWindow((DependencyObject)(object)this)
			};
			bool? flag = passwordDialog.ShowDialog();
			if (passwordDialog.ForgotPasswordRequested)
			{
				if (!VerifySecretQA(poolDir))
				{
					return;
				}
			}
			else
			{
				if (flag != true)
				{
					return;
				}
				if (!CryptoUtils.VerifyPassword(selectedPoolName, passwordDialog.Password))
				{
					CustomMessageBox.Show(Window.GetWindow((DependencyObject)(object)this), "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
					return;
				}
				CryptoUtils.DeletePasswordHash(selectedPoolName);
			}
		}
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
		if (ComboPools.Items.Count > 0)
		{
			ComboPools.SelectedIndex = 0;
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
				ComboPools.SelectedIndex = num;
			}
		}
		UpdatePoolEmptyState();
		MessageBus.Publish(new AppMessages.PoolListChanged());
	}

	private void CopyPool_Click(object sender, RoutedEventArgs e)
	{
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
			string path2 = Path.Combine(destDir, "data.lock");
			if (File.Exists(path2))
			{
				string contents = CryptoUtils.DecryptString(File.ReadAllText(path2));
				File.WriteAllText(Path.Combine(destDir, "data.txt"), contents);
				File.Delete(path2);
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
		PasswordDialog passwordDialog = new PasswordDialog("请设置新密码", isConfirm: true)
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
