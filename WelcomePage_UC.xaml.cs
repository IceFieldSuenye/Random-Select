using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using RandomDrawer.Messaging;

namespace RandomDrawer;

/// <summary>
/// 欢迎页（首次运行 / 用户主动重新打开）：分 4 个板块引导用户把
/// 「卡池文件夹 → 资源文件夹（可选）→ 性能」设好，避免没目录时主界面各处报错。
/// 没有顶栏和左侧导航，右下角「上一页 / 下一页」，最后一页「开始使用」。
/// 用户在这页直接关软件也安全（不写半截设置、不进入任何忙碌状态）。
/// 3.60：翻页改成「旧内容左移淡出 + 新内容右移淡入」；开始使用时整页淡出。
/// </summary>
public partial class WelcomePage_UC : UserControl, IPageAnimation
{
    private const int TotalSteps = 5;

    /// <summary>翻页时内容横向位移（DIP）。</summary>
    private const double SlideOffset = 40;
    private static readonly TimeSpan StepAnimDuration = TimeSpan.FromMilliseconds(170);

    private int _step;
    private bool _loading;
    private bool _animating;
    private bool _lowEndSelected;

    /// <summary>3.74.0：进入资源路径输入框时的内容，用来判断这次失焦是「没输入」还是「清空了」。</summary>
    private string _resDirOnFocus = "";

    public WelcomePage_UC()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            if (_animating) return;
            LoadState();
            ApplyStepInstant(_step);
        };
        Loaded += (s, e) => InitInfoHint();
    }

    /// <summary>3.70：第 4 页「视觉呈现模式」的「？」改成悬浮气泡（不再弹窗）。</summary>
    private HintBubble _renderHint;

    private void InitInfoHint()
    {
        try
        {
            if (_renderHint != null || InfoWelcomeRender == null) return;
            _renderHint = new HintBubble(InfoWelcomeRender,
                "流畅优先：优先确保界面加载流畅，但可能会增加加载时间。\n\n" +
                "加载速度优先：优先考虑加载速度，可能导致部分动画消失或界面卡顿。");
            InfoWelcomeRender.MouseEnter += (s, e) =>
                _renderHint.Show(new Point(InfoWelcomeRender.ActualWidth / 2, InfoWelcomeRender.ActualHeight));
            InfoWelcomeRender.MouseMove += (s, e) => _renderHint.Show(e.GetPosition(InfoWelcomeRender));
            InfoWelcomeRender.MouseLeave += (s, e) => _renderHint.Hide();
        }
        catch { }
    }

    // ================================================================
    // 板块切换
    // ================================================================

    /// <summary>立即切换到指定板块（测试 / 禁用动画时用）。</summary>
    private void ApplyStep(int step) => ApplyStepInstant(step);

    private void ApplyStepInstant(int step)
    {
        try
        {
            _step = Math.Max(0, Math.Min(TotalSteps - 1, step));

            // 第 1 页才有「软件图标 + 欢迎使用…」；后面的页从内容直接开始
            HeaderPanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;

            PanelWelcomeIntro.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelWelcomePools.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelWelcomeResources.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelWelcomePerformance.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
            PanelWelcomeLayout.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

            bool last = _step >= TotalSteps - 1;
            TxtNextLabel.Text = last ? "开始使用" : "下一页";
            TxtNextIcon.Text = last ? "\uE73E" : "\uE72A";

            BtnPrev.IsEnabled = _step > 0;
            BtnNext.IsEnabled = true;

            // 第 4 / 5 页是用户在选，不能再用配置值把选择刷掉
            if (_step != 3 && _step != 4) LoadState();

            // 复位翻页动画留下的状态
            ResetPanelAnimationState();

            try { StepScroll.ScrollToTop(); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 带动画翻页：下一页 = 旧内容向左淡出、新内容从右侧向左淡入；上一页相反。
    /// 「禁用动画」或动画进行中时直接切换 / 忽略。
    ///
    /// 3.61：所有动画都显式给 From / To，并且基值固定在「不透明 / 位移 0」。
    /// 以前在动画进行时把 Opacity 的基值改成 0、位移基值改成 ±40，下一次翻页
    /// 清除动画的瞬间会回落到这些「脏值」—— 表现就是整页突然消失、过一会儿又出现。
    /// </summary>
    private void GoToStep(int target, bool forward)
    {
        if (_animating) return;

        target = Math.Max(0, Math.Min(TotalSteps - 1, target));
        if (target == _step) return;

        if (App.IsAnimationDisabled)
        {
            ApplyStepInstant(target);
            return;
        }

        _animating = true;
        SetNavigationEnabled(false);
        ResetPanelAnimationState();

        double outX = forward ? -SlideOffset : SlideOffset;
        double inX = forward ? SlideOffset : -SlideOffset;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fadeOut = new DoubleAnimation(1, 0, StepAnimDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var moveOut = new DoubleAnimation(0, outX, StepAnimDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeOut.Completed += (s, e) =>
        {
            try
            {
                ApplyStepInstant(target);
                SetNavigationEnabled(false);

                var fadeIn = new DoubleAnimation(0, 1, StepAnimDuration)
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd
                };
                var moveIn = new DoubleAnimation(inX, 0, StepAnimDuration)
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd
                };
                fadeIn.Completed += (s2, e2) =>
                {
                    // 动画值已经是 1 / 0，先把基值复位再清动画，之后任何清除都不会闪
                    ResetPanelAnimationState();
                    _animating = false;
                    SetNavigationEnabled(true);
                };

                PageContentHost.BeginAnimation(OpacityProperty, fadeIn);
                PanelTransform.BeginAnimation(TranslateTransform.XProperty, moveIn);
            }
            catch
            {
                ResetPanelAnimationState();
                _animating = false;
                SetNavigationEnabled(true);
            }
        };

        PageContentHost.BeginAnimation(OpacityProperty, fadeOut);
        PanelTransform.BeginAnimation(TranslateTransform.XProperty, moveOut);
    }

    /// <summary>把翻页动画状态复位成「完全不透明、没有位移」，并清掉残留动画。</summary>
    private void ResetPanelAnimationState()
    {
        try
        {
            PageContentHost.BeginAnimation(OpacityProperty, null);
            PanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
        }
        catch { }
        PageContentHost.Opacity = 1;
        PanelTransform.X = 0;
    }

    /// <summary>
    /// 动画期间只挡住鼠标，不改 IsEnabled —— 否则按钮会闪一下灰色，
    /// 看起来像是「元素突然显示错误」。
    /// </summary>
    private void SetNavigationEnabled(bool enabled)
    {
        try
        {
            BtnPrev.IsHitTestVisible = enabled;
            BtnNext.IsHitTestVisible = enabled;
        }
        catch { }
    }

    private void LoadState()
    {
        try
        {
            _loading = true;

            // 卡池目录：直接显示成可编辑的路径；有多个时这里只显示 / 修改第一个
            var existing = GetExistingPoolDirs();
            if (existing.Count == 0)
            {
                TxtPoolDirBox.Text = "";
                TxtPoolDirHint.Text = "还没有可用的卡池文件夹，点「浏览文件夹」或「默认位置创建」；也可以直接输入路径。";
            }
            else
            {
                TxtPoolDirBox.Text = existing[0];
                TxtPoolDirHint.Text = existing.Count == 1
                    ? "可直接输入路径按回车生效，或点「浏览文件夹」选择"
                    : $"当前共 {existing.Count} 个卡池文件夹，这里显示并修改第一个；其余可在「选项 → 卡池」里管理";
            }

            // 资源目录：直接显示成可编辑的路径；没设置时留空并给出提示
            string resDir = (App.ResourcesDir ?? "").Trim();
            bool resOk = resDir.Length > 0 && Directory.Exists(resDir);
            TxtResDirBox.Text = resOk ? resDir : "";
            if (TxtResDirHint != null)
                TxtResDirHint.Text = resOk
                    ? "已设置，可直接修改路径，按回车生效"
                    : "可直接输入路径按回车生效，或点「浏览文件夹」选择";

            // ★ 欢迎页不默认勾选：只有用户以前在设置里主动开 / 关过低性能模式才沿用，
            //   首次运行即使检测到低配设备也保持标准模式，由用户自己决定。
            bool low = App.LowEndModeExplicitlySet && App.LowEndMode;
            _lowEndSelected = low;
            UpdateModeCards(low);

            ComboRenderPreference.SelectedIndex = App.RenderPreference == App.RenderPreferenceKind.Fast ? 1 : 0;

            // 3.71：主界面布局（独立第 5 页，与「选项 → 常规 → 外观 → 主页布局」共用同一个设置）
            _layoutDetailed = App.MainPageLayout == App.MainPageLayoutKind.Detailed;
            UpdateLayoutCards();
        }
        catch { }
        finally { _loading = false; }
    }

    /// <summary>两个模式卡片：选中的那张描边用强调色并显示蓝色图标，未选中的不显示图标。</summary>
    private void UpdateModeCards(bool low)
    {
        try
        {
            Brush accent = TryFindResource("Ui.AccentFill") as Brush;
            Brush normal = TryFindResource("Bd_E5E7EB") as Brush;

            if (ModeStandardCard != null) ModeStandardCard.BorderBrush = low ? normal : accent;
            if (ModeLowCard != null) ModeLowCard.BorderBrush = low ? accent : normal;

            if (TxtStandardModeIcon != null)
                TxtStandardModeIcon.Visibility = low ? Visibility.Collapsed : Visibility.Visible;
            if (TxtLowModeIcon != null)
                TxtLowModeIcon.Visibility = low ? Visibility.Visible : Visibility.Collapsed;

            if (TxtModeHint != null)
                TxtModeHint.Text = low
                    ? "低性能模式：更省内存，画面和图片清晰度会略降。"
                    : "标准模式：画面更清晰，占用内存更多。";
        }
        catch { }
    }

    private static List<string> GetExistingPoolDirs()
    {
        var result = new List<string>();
        try
        {
            foreach (string dir in App.SearchDirectories ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { if (Directory.Exists(dir)) result.Add(dir); } catch { }
            }
        }
        catch { }
        return result;
    }

    // ================================================================
    // 上一页 / 下一页
    // ================================================================

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => GoToStep(_step - 1, forward: false);

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_animating) return;

        // 卡池目录是必须项：没选好不让往下走（主界面没有卡池会一片空白）
        if (_step == 1 && GetExistingPoolDirs().Count == 0)
        {
            CustomMessageBox.Show(Window.GetWindow(this),
                "还没有可用的卡池文件夹。\n\n点「浏览文件夹」挑一个，或点「默认位置创建」自动建一个。",
                "还差一步", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_step >= TotalSteps - 1)
        {
            FinishWithFade();
            return;
        }

        GoToStep(_step + 1, forward: true);
    }

    /// <summary>「开始使用」：整页淡出后再进主界面。</summary>
    private void FinishWithFade()
    {
        if (App.IsAnimationDisabled)
        {
            Finish();
            return;
        }

        try
        {
            // 基值固定在 1；动画只负责 1 → 0，取消动画不会让页面自己跳回来
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fade.Completed += (s, e) =>
            {
                // 不在这一帧把 Opacity 恢复成 1：那一帧整页会闪回来。
                // 下次进入欢迎页时 PlayFadeIn 会重新复位成 1。
                Finish();
            };
            BeginAnimation(OpacityProperty, fade);
        }
        catch { Finish(); }
    }

    private void Finish()
    {
        try
        {
            App.LowEndMode = _lowEndSelected;
            App.LowEndModeExplicitlySet = true;
            App.RenderPreference = ComboRenderPreference.SelectedIndex == 1
                ? App.RenderPreferenceKind.Fast
                : App.RenderPreferenceKind.Smooth;
            // 3.71：布局选择也要在「开始使用」时落盘
            App.MainPageLayout = _layoutDetailed
                ? App.MainPageLayoutKind.Detailed
                : App.MainPageLayoutKind.Compact;

            // 欢迎页只在首次运行出现（3.63 起没有「启动时显示欢迎页」开关）
            App.ShowWelcome = false;
            App.SaveSettings();
        }
        catch { }

        try { MessageBus.Publish(new AppMessages.NavigateTo("Main")); }
        catch { }
    }

    // ================================================================
    // 目录选择
    // ================================================================

    private void BtnPickPoolDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择卡池文件夹", Multiselect = false };
            if (dialog.ShowDialog() != true) return;

            string folder = (dialog.FolderName ?? "").Trim();
            if (folder.Length == 0) return;
            if (!App.IsValidDirectoryPath(folder))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "所选目录无效，请换一个。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }
            AddPoolDir(folder);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Window.GetWindow(this), "选择卡池目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnCreatePoolDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(App.BaseDir, "SelectBox");
            Directory.CreateDirectory(dir);
            AddPoolDir(dir);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Window.GetWindow(this), "创建默认卡池目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddPoolDir(string dir)
    {
        try
        {
            if (App.SearchDirectories == null) App.SearchDirectories = new List<string>();
            if (!App.SearchDirectories.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                App.SearchDirectories.Add(dir);

            // 去掉「并不存在的默认目录」，避免主界面/选项里出现一个假路径
            App.SearchDirectories = App.SearchDirectories
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            App.SaveSettings();
            LoadState();
            try { MessageBus.Publish(new AppMessages.PoolListChanged()); } catch { }
        }
        catch { }
    }

    // -------------------- 卡池目录：文本框可直接编辑 --------------------

    private void TxtPoolDirBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyPoolDirFromBox();
    }

    private void TxtPoolDirBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 只在这一页可见时应用，避免翻页 / 切窗口时误触发提示
        if (PanelWelcomePools == null || PanelWelcomePools.Visibility != Visibility.Visible) return;
        ApplyPoolDirFromBox();
    }

    private void ApplyPoolDirFromBox()
    {
        if (_loading) return;

        try
        {
            string text = (TxtPoolDirBox.Text ?? "").Trim().Trim('"');
            var existing = GetExistingPoolDirs();

            if (text.Length == 0)
            {
                // 清空输入不删已有目录，恢复显示即可
                LoadState();
                return;
            }

            if (existing.Count > 0 && string.Equals(existing[0], text, StringComparison.OrdinalIgnoreCase))
                return;   // 没改

            if (!App.IsValidDirectoryPath(text))
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "这个路径不能用，请选择一个文件夹路径（例如 D:\\SelectBox）。", "路径无效",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                LoadState();
                return;
            }

            Directory.CreateDirectory(text);

            // 已存在同一个目录（大小写 / 末尾斜杠可能不同）就把它提到最前，
            // 否则作为新的第一个目录加入；其它目录保留，避免误删用户的卡池。
            var list = (App.SearchDirectories ?? new List<string>()).ToList();
            string normalized = text.TrimEnd('\\', '/');
            int index = list.FindIndex(d =>
                string.Equals((d ?? "").Trim().TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                string hit = list[index];
                list.RemoveAt(index);
                list.Insert(0, hit);
            }
            else
            {
                list.Insert(0, text);
            }

            App.SearchDirectories = list
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            App.SaveSettings();
            LoadState();
            try { MessageBus.Publish(new AppMessages.PoolListChanged()); } catch { }
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Window.GetWindow(this), "设置卡池目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadState();
        }
    }

    private void BtnPickResDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择资源文件夹", Multiselect = false };
            string current = (App.ResourcesDir ?? "").Trim();
            if (current.Length > 0 && Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog() != true) return;

            string folder = (dialog.FolderName ?? "").Trim();
            if (folder.Length == 0) return;
            if (!App.IsValidDirectoryPath(folder))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "所选目录无效，请换一个。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return;
            }

            Directory.CreateDirectory(folder);
            App.ResourcesDir = folder;
            App.SaveSettings();
            LoadState();
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Window.GetWindow(this), "选择资源目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>3.74.0：和上一页的「默认位置创建」一致，直接在程序目录下建 Resources 并设为资源文件夹。</summary>
    private void BtnCreateResDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(App.BaseDir, "Resources");
            Directory.CreateDirectory(dir);
            App.ResourcesDir = dir;
            App.SaveSettings();
            LoadState();
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Window.GetWindow(this), "创建默认资源目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // -------------------- 资源目录：文本框可直接编辑 --------------------

    private void TxtResDirBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyResDirFromBox();
    }

    private void TxtResDirBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // 记下进入输入框时的内容：用来区分「只是点了一下」和「真的清空了」
        _resDirOnFocus = TxtResDirBox.Text ?? "";
    }

    private void TxtResDirBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 只在这一页可见时应用，避免翻页 / 切窗口时误触发提示
        if (PanelWelcomeResources == null || PanelWelcomeResources.Visibility != Visibility.Visible) return;
        ApplyResDirFromBox();
    }

    private void ApplyResDirFromBox()
    {
        if (_loading) return;

        try
        {
            string text = (TxtResDirBox.Text ?? "").Trim().Trim('"');
            string current = (App.ResourcesDir ?? "").Trim();

            if (text.Length == 0)
            {
                // 3.74.0：只是点了一下输入框（进来时就是空的）再点空白，不能把默认路径填回去
                if ((_resDirOnFocus ?? "").Trim().Length == 0) return;
                // 3.74.1：用户把原来的路径全选删掉了 —— 清空资源目录设置并保持输入框为空，
                // 不要再用旧值（或默认值）填回去
                if (current.Length > 0)
                {
                    App.ResourcesDir = "";
                    App.SaveSettings();
                }
                LoadState();
                return;
            }

            if (string.Equals(text, current, StringComparison.OrdinalIgnoreCase)) return;

            if (!App.IsValidDirectoryPath(text))
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "这个路径不能用，请选择一个文件夹路径（例如 D:\\Resources）。", "路径无效",
                    MessageBoxButton.OK, MessageBoxImage.Exclamation);
                TxtResDirBox.Text = current;
                return;
            }

            Directory.CreateDirectory(text);
            App.ResourcesDir = text;
            App.SaveSettings();
            LoadState();
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(Window.GetWindow(this), "设置资源目录失败：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================================================================
    // 性能
    // ================================================================

    private void ModeStandardCard_Click(object sender, MouseButtonEventArgs e)
    {
        try { e.Handled = true; } catch { }
        if (_loading) return;
        ApplyMode(low: false);
    }

    private void ModeLowCard_Click(object sender, MouseButtonEventArgs e)
    {
        try { e.Handled = true; } catch { }
        if (_loading) return;
        ApplyMode(low: true);
    }

    /// <summary>两个模式卡片二选一，并立即保存。</summary>
    private void ApplyMode(bool low)
    {
        try
        {
            _loading = true;
            _lowEndSelected = low;
            UpdateModeCards(low);

            App.LowEndMode = low;
            App.LowEndModeExplicitlySet = true;
            App.SaveSettings();
        }
        catch { }
        finally { _loading = false; }
    }

    private void ComboRenderPreference_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        try
        {
            App.RenderPreference = ComboRenderPreference.SelectedIndex == 1
                ? App.RenderPreferenceKind.Fast
                : App.RenderPreferenceKind.Smooth;
            App.SaveSettings();
        }
        catch { }
    }

    // ================================================================
    // 主界面布局（第 5 页）
    // ================================================================

    private bool _layoutDetailed;

    private void LayoutCompactCard_Click(object sender, MouseButtonEventArgs e)
    {
        try { e.Handled = true; } catch { }
        if (_loading) return;
        ApplyLayoutMode(compact: true);
    }

    private void LayoutDetailedCard_Click(object sender, MouseButtonEventArgs e)
    {
        try { e.Handled = true; } catch { }
        if (_loading) return;
        ApplyLayoutMode(compact: false);
    }

    /// <summary>两张卡片二选一，立即保存并通知主界面重新排版。</summary>
    private void ApplyLayoutMode(bool compact)
    {
        try
        {
            _loading = true;
            _layoutDetailed = !compact;
            UpdateLayoutCards();

            App.MainPageLayout = compact
                ? App.MainPageLayoutKind.Compact
                : App.MainPageLayoutKind.Detailed;
            App.SaveSettings();
            MessageBus.Publish(new AppMessages.UiOptionsChanged());
        }
        catch { }
        finally { _loading = false; }
    }

    /// <summary>布局卡片：选中的描边用强调色并显示蓝色图标；未选中的不显示图标。</summary>
    private void UpdateLayoutCards()
    {
        try
        {
            Brush accent = TryFindResource("Ui.AccentFill") as Brush;
            Brush normal = TryFindResource("Bd_E5E7EB") as Brush;
            bool detailed = _layoutDetailed;

            if (LayoutCompactCard != null) LayoutCompactCard.BorderBrush = detailed ? normal : accent;
            if (LayoutDetailedCard != null) LayoutDetailedCard.BorderBrush = detailed ? accent : normal;

            if (TxtCompactLayoutIcon != null)
                TxtCompactLayoutIcon.Visibility = detailed ? Visibility.Collapsed : Visibility.Visible;
            if (TxtDetailedLayoutIcon != null)
                TxtDetailedLayoutIcon.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;

            if (TxtLayoutHint != null)
                TxtLayoutHint.Text = detailed
                    ? "详细：底部直接显示「不重复抽取」开关、抽取次数、重置与本次抽取详情。"
                    : "简洁：状态信息收进「抽取详情」面板，底部只留「不重复抽取」开关。";
        }
        catch { }
    }

    // ================================================================
    // IPageAnimation（简单淡入，跟随项目统一的页面动画约定）
    // ================================================================

    public void PlayEnterAnimation(Action onComplete)
    {
        PlayFadeIn();
        try { onComplete?.Invoke(); } catch { }
    }

    public void PlayExitAnimation(Action onComplete)
    {
        try { onComplete?.Invoke(); } catch { }
    }

    public void PlayEnterAsReverseAnimation(Action onComplete)
    {
        PlayFadeIn();
        try { onComplete?.Invoke(); } catch { }
    }

    private void PlayFadeIn()
    {
        try
        {
            LoadState();
            ApplyStepInstant(_step);

            // 基值保持 1：动画结束、或别处清除动画时都不会回落到 0（否则整页会消失）
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (App.IsAnimationDisabled) return;

            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase(),
                FillBehavior = FillBehavior.HoldEnd
            });
        }
        catch { }
    }
}
