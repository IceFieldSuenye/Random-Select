using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    public partial class AdvancedGachaPage_UC : UserControl, IConfirmLeave, IPageAnimation
    {
        private Dictionary<string, List<string>> _categoryMap = new Dictionary<string, List<string>>();
        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);
        private string _poolDir = "";
        private bool _isLoading = true;
        private bool _hasSavedSettings;
        private bool _hasCustomColors = false;
        private const string SUPPORTED_PRESET = "崩坏：星穹铁道";
        private const string BA_PRESET = "蔚蓝档案（旧）";
        private const string HSR_WARP_DEFAULT = "角色活动跃迁";
        private const int UpListDisplayThreshold = 2;
        private const string NoStudentOption = "（无学生）";

        private List<string> _fourStarUpObjects = new List<string>();
        private List<string> _baMainUp3Stars = new List<string>();
        private List<string> _baSubUp3Stars = new List<string>();

        public GachaSettings Settings { get; private set; } = new GachaSettings();

        private string _stateSnapshotAtOpen = "";
        private string _stateSnapshotAtSave = "";
        private string _snapshotBeforeSub = null;

        private Action<AppMessages.OpenAdvancedGacha> _onOpenAdvancedGacha;
        private Action<AppMessages.MultiSelectFinished> _onMultiSelectFinished;
        private Action<AppMessages.CelebrationUpFinished> _onCelebrationUpFinished;

        public AdvancedGachaPage_UC()
        {
            InitializeComponent();
            _onOpenAdvancedGacha = OnOpenAdvancedGacha;
            _onMultiSelectFinished = OnMultiSelectFinished;
            _onCelebrationUpFinished = OnCelebrationUpFinished;
            MessageBus.Subscribe(_onOpenAdvancedGacha);
            MessageBus.Subscribe(_onMultiSelectFinished);
            MessageBus.Subscribe(_onCelebrationUpFinished);

            // 页面与两个规则面板都按可用宽度居中，并限制最大宽度：
            // 面板宽度固定后，内部的 * 列自然等宽，所有控件宽度一致。
            SizeChanged += (s, e) => UpdateLayoutWidths();
        }

        /// <summary>
        /// 内容区宽度：页面最多 1040、规则面板最多 960（= 200+260+40+200+260），
        /// 窗口不够宽时按可用宽度收缩，避免被裁掉。
        /// </summary>
        private void UpdateLayoutWidths()
        {
            try
            {
                double available = ActualWidth;
                if (available <= 0) return;

                if (PageRoot != null)
                    PageRoot.Width = Math.Max(320, Math.Min(1040, available - 40));

                double panelWidth = Math.Max(300, Math.Min(960, available - 80));
                if (SettingsPanel != null) SettingsPanel.Width = panelWidth;
                if (SettingsPanelBA != null) SettingsPanelBA.Width = panelWidth;

                // 顶部一行（启用开关 + 预设下拉）与规则面板同宽：
                // 这样预设下拉框的右边缘正好对齐「下方控件最右侧」。
                if (PresetHeader != null) PresetHeader.Width = panelWidth;

                // 同一列的还有：提示文字、底部说明、底部按钮 —— 都要与规则面板同宽，
                // 否则会贴到页面最右侧/最左侧，看起来像「没对齐」。
                if (FooterHintBar != null) FooterHintBar.Width = panelWidth;
                if (BottomBar != null) BottomBar.Width = panelWidth;
            }
            catch { }
        }

        public void PlayEnterAnimation(Action onComplete) => SlideInAnimationHelper.PlaySlideIn(this, 60, 0, onComplete);
        public void PlayExitAnimation(Action onComplete) => SlideInAnimationHelper.PlaySlideOut(this, 60, 0, onComplete);

        public string UnsavedLabel => "高级抽卡规则";

        public bool HasUnsavedChanges()
        {
            if (string.IsNullOrEmpty(_stateSnapshotAtOpen)) return false;
            string current = BuildStateSnapshot();
            return current != _stateSnapshotAtOpen && current != _stateSnapshotAtSave;
        }

        public bool CanLeave()
        {
            if (!HasUnsavedChanges()) return true;
            var choice = CustomMessageBox.ShowUnsavedChanges(Window.GetWindow(this),
                "高级抽卡规则有未保存的修改。\n\n是否保存修改并离开？");

            switch (choice)
            {
                case UnsavedChoice.Cancel: return false;
                case UnsavedChoice.Discard: return true;
                case UnsavedChoice.Keep:
                    bool saved = TrySaveAndValidate();
                    if (!saved) return false;
                    MarkSaved();
                    MessageBus.Publish(new AppMessages.AdvancedGachaFinished(_poolDir, true, Settings));
                    return true;
            }
            return false;
        }

        private string BuildStateSnapshot()
        {
            try
            {
                string preset = (ComboPreset?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                string warp = (ComboWarpType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

                return string.Format(
                    "En={0}|Ps={1}|Wp={2}|FS={3}|FSU={4}|F4S={5}|F4U={6}|P5N={7}|P5U={8}|P4U={9}|P4N={10}|SPS={11}|SPI={12}|F4P={13}|F5P={14}|HC={15}|BAPT={16}|BA3={17}|BA2={18}|BAU3={19}|BAU2={20}|BAMU={21}|BASU={22}|BA3P={23}|BA2P={24}|BAU3P={25}|BASU3P={26}|BAU2P={27}|BA2Pity={28}|HCBA={29}",
                    ChkEnable?.IsChecked == true ? "1" : "0",
                    preset, warp,
                    ComboFiveStar?.SelectedItem?.ToString() ?? "",
                    ComboFiveStarUp?.SelectedItem?.ToString() ?? "",
                    ComboFourStar?.SelectedItem?.ToString() ?? "",
                    string.Join(",", _fourStarUpObjects),
                    TxtFiveStarNonUpProb?.Text ?? "", TxtFiveStarUpProb?.Text ?? "",
                    TxtFourStarUpProb?.Text ?? "", TxtFourStarNonUpProb?.Text ?? "",
                    TxtSoftPityStart?.Text ?? "", TxtSoftPityIncrease?.Text ?? "",
                    TxtFourStarPity?.Text ?? "", TxtFiveStarPity?.Text ?? "",
                    ChkHighlightColors?.IsChecked == true ? "1" : "0",
                    ComboBAPoolType?.SelectedIndex.ToString() ?? "",
                    ComboBAThreeStar?.SelectedItem?.ToString() ?? "",
                    ComboBATwoStar?.SelectedItem?.ToString() ?? "",
                    ComboBAUp3Star?.SelectedItem?.ToString() ?? "",
                    GetSelectedUp2Star() ?? "",
                    string.Join(",", _baMainUp3Stars), string.Join(",", _baSubUp3Stars),
                    TxtBAThreeStarProb?.Text ?? "", TxtBATwoStarProb?.Text ?? "",
                    TxtBAUp3StarProb?.Text ?? "", TxtBASubUp3StarProb?.Text ?? "",
                    TxtBAUp2StarProb?.Text ?? "", TxtBA2StarPity?.Text ?? "",
                    ChkHighlightColorsBA?.IsChecked == true ? "1" : "0");
            }
            catch { return ""; }
        }

        private void ResetSnapshots()
        {
            string s = BuildStateSnapshot();
            _stateSnapshotAtOpen = s;
            _stateSnapshotAtSave = s;
            _snapshotBeforeSub = null;
        }

        private void MarkSaved() => _stateSnapshotAtSave = BuildStateSnapshot();

        private void PrepareNavigateToSubPage()
        {
            _snapshotBeforeSub = _stateSnapshotAtOpen;
            _stateSnapshotAtOpen = BuildStateSnapshot();
        }

        private void RestoreSnapshotAfterSubPage()
        {
            if (_snapshotBeforeSub != null)
            {
                _stateSnapshotAtOpen = _snapshotBeforeSub;
                _snapshotBeforeSub = null;
            }
        }

        private bool TrySaveGachaSettings()
        {
            try
            {
                if (string.IsNullOrEmpty(_poolDir)) return false;
                if (!Directory.Exists(_poolDir)) Directory.CreateDirectory(_poolDir);
                string file = Path.Combine(_poolDir, "gacha_settings.ini");
                GachaSettings.SaveSettingsToFile(Settings, file);
                return true;
            }
            catch { return false; }
        }

        private void OnOpenAdvancedGacha(AppMessages.OpenAdvancedGacha msg)
        {
            if (msg == null) return;
            Dispatcher.Invoke(new Action(() =>
            {
                // ★ 先藏起来再灌数据：页面是缓存的实例，直接切过来会先闪一帧上一个卡池的旧内容
                if (PageRoot != null) PageRoot.Opacity = 0;

                _categoryMap = msg.CategoryMap ?? new Dictionary<string, List<string>>();
                _poolDir = msg.PoolDir ?? "";
                Settings = msg.InitialSettings ?? new GachaSettings();
                _hasSavedSettings = (msg.InitialSettings != null && msg.InitialSettings.Enabled);

                // ★ 接收是否有自定义颜色（用于互斥判断）
                _hasCustomColors = msg.HasCustomColors;

                ReinitializeUI();

                // 数据与布局都就绪后再显示，避免旧内容闪一下
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { if (PageRoot != null) PageRoot.Opacity = 1; } catch { }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }));
        }

        private void OnMultiSelectFinished(AppMessages.MultiSelectFinished msg)
        {
            if (msg == null) return;
            Dispatcher.Invoke(new Action(() =>
            {
                RestoreSnapshotAfterSubPage();
                if (!msg.Ok) return;
                _fourStarUpObjects = (msg.SelectedItems ?? new List<string>()).ToList();
                UpdateFourStarUpDisplay();
            }));
        }

        private void OnCelebrationUpFinished(AppMessages.CelebrationUpFinished msg)
        {
            if (msg == null) return;
            Dispatcher.Invoke(new Action(() =>
            {
                RestoreSnapshotAfterSubPage();
                if (!msg.Ok) return;

                _baMainUp3Stars = string.IsNullOrEmpty(msg.MainUp) ? new List<string>() : new List<string> { msg.MainUp };
                _baSubUp3Stars = (msg.SubUps ?? new List<string>()).Where(x => !_baMainUp3Stars.Contains(x)).Distinct().ToList();
                UpdateBACelebrationDisplay();

                if (_baSubUp3Stars.Count == 0 && TxtBASubUp3StarProb != null)
                {
                    bool o = _isLoading;
                    _isLoading = true;
                    TxtBASubUp3StarProb.Text = "";
                    _isLoading = o;
                }
            }));
        }

        private void ReinitializeUI()
        {
            _isLoading = true;
            var categories = _categoryMap.Keys.ToList();
            ComboFiveStar.ItemsSource = categories.ToList();
            ComboFourStar.ItemsSource = categories.ToList();
            ComboBAThreeStar.ItemsSource = categories.ToList();
            ComboBATwoStar.ItemsSource = categories.ToList();

            if (ComboPreset.Items.Count > 0) ComboPreset.SelectedIndex = 0;
            if (ComboWarpType.Items.Count > 0) ComboWarpType.SelectedIndex = 0;
            if (ComboBAPoolType.Items.Count > 0) ComboBAPoolType.SelectedIndex = 0;

            ApplySettingsToUI();
            _isLoading = false;
            UpdateUIState();
            ApplyBAPoolTypeUI();
            UpdatePityNText();
            UpdateHSRThreeStarProb();
            ApplyColorExclusion();
            ResetSnapshots();
        }

        private bool PoolHasPersonalizationColors()
        {
            return _hasCustomColors;
        }

        private void ApplyColorExclusion()
        {
            bool hasColors = PoolHasPersonalizationColors();

            if (ChkHighlightColors != null)
            {
                ChkHighlightColors.IsEnabled = !hasColors;
                if (hasColors) ChkHighlightColors.IsChecked = false;
                ChkHighlightColors.ToolTip = hasColors
                    ? "该卡池已使用自定义颜色，无法启用高亮颜色"
                    : null;
            }

            if (ChkHighlightColorsBA != null)
            {
                ChkHighlightColorsBA.IsEnabled = !hasColors;
                if (hasColors) ChkHighlightColorsBA.IsChecked = false;
                ChkHighlightColorsBA.ToolTip = hasColors
                    ? "该卡池已使用自定义颜色，无法启用高亮颜色"
                    : null;
            }
        }

        private void ShowDeveloping(bool show)
        {
            if (DevelopingPanel == null) return;
            DevelopingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                // 和其它页面用同一个转圈图标（\uF16A），禁用动画时也照常转
                if (DevelopingSpinner != null)
                {
                    if (show) DevelopingSpinner.Start();
                    else DevelopingSpinner.Stop();
                }
            }
            catch { }
        }

        private void RebuildUp2StarComboBox(string category, string initialSelection)
        {
            if (ComboBAUp2Star == null) return;
            ComboBAUp2Star.Items.Clear();
            // 不再放「(无学生)」占位项：未选择时按钮显示「点击选择」，与 UP3 一致
            if (!string.IsNullOrEmpty(category) && _categoryMap != null && _categoryMap.ContainsKey(category))
            {
                foreach (var name in _categoryMap[category])
                    ComboBAUp2Star.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }
            if (!string.IsNullOrEmpty(initialSelection))
            {
                foreach (ComboBoxItem item in ComboBAUp2Star.Items)
                {
                    if ((item.Tag as string) == initialSelection) { ComboBAUp2Star.SelectedItem = item; return; }
                }
            }
            ComboBAUp2Star.SelectedIndex = -1;
        }

        private string GetSelectedUp2Star()
        {
            if (ComboBAUp2Star?.SelectedItem is ComboBoxItem item) return item.Tag as string;
            return null;
        }

        private void SyncUp2StarProbEnabled()
        {
            if (TxtBAUp2StarProb == null) return;
            bool hasUp2 = !string.IsNullOrEmpty(GetSelectedUp2Star());
            TxtBAUp2StarProb.IsEnabled = hasUp2;
        }

        private void ApplySettingsToUI()
        {
            ChkEnable.IsChecked = Settings.Enabled;

            if (_hasSavedSettings)
            {
                if (!string.IsNullOrEmpty(Settings.PresetName))
                {
                    foreach (ComboBoxItem item in ComboPreset.Items)
                        if (item.Content.ToString() == Settings.PresetName) { ComboPreset.SelectedItem = item; break; }
                }
                if (!string.IsNullOrEmpty(Settings.WarpType))
                {
                    foreach (ComboBoxItem item in ComboWarpType.Items)
                        if (item.Content.ToString() == Settings.WarpType) { ComboWarpType.SelectedItem = item; break; }
                }

                try
                {
                    if (!string.IsNullOrEmpty(Settings.FiveStarCategory) && _categoryMap.ContainsKey(Settings.FiveStarCategory))
                    {
                        ComboFiveStar.SelectedItem = Settings.FiveStarCategory;
                        ComboFiveStarUp.IsEnabled = true;
                        ComboFiveStarUp.ItemsSource = _categoryMap[Settings.FiveStarCategory];
                        if (!string.IsNullOrEmpty(Settings.FiveStarUpObject))
                            ComboFiveStarUp.SelectedItem = Settings.FiveStarUpObject;
                    }
                    if (!string.IsNullOrEmpty(Settings.FourStarCategory) && _categoryMap.ContainsKey(Settings.FourStarCategory))
                    {
                        ComboFourStar.SelectedItem = Settings.FourStarCategory;
                        _fourStarUpObjects = ParseUpObjectsString(Settings.FourStarUpObjects);
                        UpdateFourStarUpDisplay();
                    }
                    TxtFiveStarNonUpProb.Text = Settings.FiveStarNonUpProb.ToString("F3");
                    TxtFiveStarUpProb.Text = Settings.FiveStarUpProb.ToString("F3");
                    TxtFourStarUpProb.Text = Settings.FourStarUpProb.ToString("F3");
                    TxtFourStarNonUpProb.Text = Settings.FourStarNonUpProb.ToString("F3");
                    TxtSoftPityStart.Text = Settings.SoftPityStart.ToString();
                    TxtSoftPityIncrease.Text = Settings.SoftPityIncrease.ToString("F2");
                    TxtFourStarPity.Text = Settings.FourStarPity.ToString();
                    TxtFiveStarPity.Text = Settings.FiveStarPity.ToString();
                    UpdateHSRThreeStarProb();

                    if (ChkHighlightColors != null) ChkHighlightColors.IsChecked = Settings.EnableHighlightColors;

                    ComboBAPoolType.SelectedIndex = GetBAPoolTypeIndex(Settings.BAPoolType);

                    if (!string.IsNullOrEmpty(Settings.BA3StarCategory) && _categoryMap.ContainsKey(Settings.BA3StarCategory))
                    {
                        ComboBAThreeStar.SelectedItem = Settings.BA3StarCategory;
                        ComboBAUp3Star.ItemsSource = _categoryMap[Settings.BA3StarCategory];
                        if (!string.IsNullOrEmpty(Settings.BAUp3Star)) ComboBAUp3Star.SelectedItem = Settings.BAUp3Star;
                    }
                    if (!string.IsNullOrEmpty(Settings.BA2StarCategory) && _categoryMap.ContainsKey(Settings.BA2StarCategory))
                    {
                        ComboBATwoStar.SelectedItem = Settings.BA2StarCategory;
                        RebuildUp2StarComboBox(Settings.BA2StarCategory, Settings.BAUp2Star);
                    }
                    TxtBAThreeStarProb.Text = Settings.BA3StarProb.ToString("0.####");
                    TxtBATwoStarProb.Text = Settings.BA2StarProb.ToString("0.####");
                    TxtBA2StarPity.Text = Settings.BA2StarPity.ToString();
                    TxtBAUp3StarProb.Text = Settings.BAUp3StarProb.ToString("0.####");
                    TxtBASubUp3StarProb.Text = Settings.BASubUp3StarProb.ToString("0.####");
                    TxtBAUp2StarProb.Text = !string.IsNullOrEmpty(Settings.BAUp2Star) ? Settings.BAUp2StarProb.ToString("0.####") : "";

                    _baMainUp3Stars = ParseUpObjectsString(Settings.BAMainUp3Stars).Take(1).ToList();
                    _baSubUp3Stars = ParseUpObjectsString(Settings.BASubUp3Stars);
                    if (_baMainUp3Stars.Count > 0) _baSubUp3Stars = _baSubUp3Stars.Where(x => x != _baMainUp3Stars[0]).ToList();

                    UpdateBACelebrationDisplay();
                    UpdateBAOneStarProb();
                    UpdatePityNText();
                    SyncUp2StarProbEnabled();

                    if (ChkHighlightColorsBA != null) ChkHighlightColorsBA.IsChecked = Settings.EnableHighlightColorsBA;
                }
                catch { }
            }
            else
            {
                if (ComboBAPoolType.Items.Count > 0) ComboBAPoolType.SelectedIndex = 0;
                UpdateBAOneStarProb();
                UpdatePityNText();
            }
        }

        private int GetBAPoolTypeIndex(string type)
        {
            if (string.IsNullOrEmpty(type)) return 0;
            switch (type) { case "限时招募": return 1; case "庆典招募": return 2; default: return 0; }
        }

        private string GetBAPoolTypeFromIndex(int index)
        {
            switch (index) { case 1: return "限时招募"; case 2: return "庆典招募"; default: return "常驻招募"; }
        }

        private List<string> ParseUpObjectsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        private static double TryParseProbLoose(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            bool hasPercent = s.EndsWith("%");
            if (hasPercent) s = s.TrimEnd('%').Trim();
            if (!double.TryParse(s, out double v)) return 0;
            return hasPercent ? v / 100.0 : v;
        }

        private static double TryParseHSRPercent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.EndsWith("%")) s = s.TrimEnd('%').Trim();
            if (!double.TryParse(s, out double v)) return 0;
            return v;
        }

        private void UpdateBAOneStarProb()
        {
            if (TxtBAOneStarProb == null) return;
            string threeText = TxtBAThreeStarProb?.Text ?? "";
            string twoText = TxtBATwoStarProb?.Text ?? "";
            if (string.IsNullOrWhiteSpace(threeText) && string.IsNullOrWhiteSpace(twoText)) { TxtBAOneStarProb.Text = "-"; return; }
            double three = TryParseProbLoose(threeText);
            double two = TryParseProbLoose(twoText);
            double one = 1.0 - three - two;
            if (one < 0) one = 0;
            TxtBAOneStarProb.Text = one.ToString("0.####");
        }

        private void UpdateHSRThreeStarProb()
        {
            if (TxtThreeStarProb == null) return;
            double p5n = TryParseHSRPercent(TxtFiveStarNonUpProb?.Text ?? "");
            double p5u = TryParseHSRPercent(TxtFiveStarUpProb?.Text ?? "");
            double p4u = TryParseHSRPercent(TxtFourStarUpProb?.Text ?? "");
            double p4n = TryParseHSRPercent(TxtFourStarNonUpProb?.Text ?? "");
            double three = 100.0 - p5n - p5u - p4u - p4n;
            if (three < 0) three = 0;
            TxtThreeStarProb.Text = three.ToString("F3");
        }

        private void UpdatePityNText()
        {
            if (RunPityN == null) return;

            // 只有填入有效的抽数才显示「每 N 次抽取必得 2星或以上学生」的提示
            string txt = (TxtBA2StarPity?.Text ?? "").Trim();
            bool valid = int.TryParse(txt, out int n) && n >= 1 && n <= 9999;

            if (TxtPityHint != null)
                TxtPityHint.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;

            if (RunPityN != null) RunPityN.Text = valid ? n.ToString() : "N";
        }

        private string FormatUpList(string prefix, List<string> list)
        {
            if (list == null || list.Count == 0) return "";
            if (list.Count <= UpListDisplayThreshold) return prefix + ":" + string.Join(",", list);
            return prefix + ":" + list[0] + " 等" + list.Count + "名学生";
        }

        private void UpdateFourStarUpDisplay()
        {
            if (TxtFourStarUpDisplay == null) return;
            if (_fourStarUpObjects.Count == 0)
            {
                TxtFourStarUpDisplay.Text = "点击选择";
                IconFourStarUp.Visibility = Visibility.Visible;
            }
            else
            {
                TxtFourStarUpDisplay.Text = string.Join(", ", _fourStarUpObjects);
                IconFourStarUp.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateBACelebrationDisplay()
        {
            if (TxtBACelebrationUp3Display == null || IconBACelebrationUp3 == null) return;
            if (_baMainUp3Stars.Count == 0 && _baSubUp3Stars.Count == 0)
            {
                TxtBACelebrationUp3Display.Text = "点击选择";
                IconBACelebrationUp3.Visibility = Visibility.Visible;
            }
            else
            {
                var parts = new List<string>();
                if (_baMainUp3Stars.Count > 0) parts.Add("主UP:" + _baMainUp3Stars[0]);
                if (_baSubUp3Stars.Count > 0) parts.Add(FormatUpList("次UP", _baSubUp3Stars));
                TxtBACelebrationUp3Display.Text = string.Join(" | ", parts);
                IconBACelebrationUp3.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateUIState()
        {
            bool enabled = ChkEnable.IsChecked == true;
            ComboPreset.IsEnabled = enabled;
            if (BtnDefault != null) BtnDefault.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

            string preset = (ComboPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SUPPORTED_PRESET;

            if (!enabled)
            {
                if (WarpTypePanel != null) WarpTypePanel.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsPanelBA.Visibility = Visibility.Collapsed;
                ShowDeveloping(false);
                TxtFooterHint.Visibility = Visibility.Collapsed;
            }
            else if (preset == SUPPORTED_PRESET)
            {
                if (WarpTypePanel != null) WarpTypePanel.Visibility = Visibility.Visible;
                SettingsPanelBA.Visibility = Visibility.Collapsed;
                string warpType = (ComboWarpType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? HSR_WARP_DEFAULT;
                if (warpType == HSR_WARP_DEFAULT)
                {
                    SettingsPanel.Visibility = Visibility.Visible;
                    ShowDeveloping(false);
                    TxtFooterHint.Visibility = Visibility.Visible;
                }
                else
                {
                    SettingsPanel.Visibility = Visibility.Collapsed;
                    ShowDeveloping(true);
                    TxtFooterHint.Visibility = Visibility.Collapsed;
                }
            }
            else if (preset == BA_PRESET)
            {
                if (WarpTypePanel != null) WarpTypePanel.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsPanelBA.Visibility = Visibility.Visible;
                ShowDeveloping(false);
                TxtFooterHint.Visibility = Visibility.Visible;
                ApplyBAPoolTypeUI();
            }
            else
            {
                if (WarpTypePanel != null) WarpTypePanel.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Collapsed;
                SettingsPanelBA.Visibility = Visibility.Collapsed;
                ShowDeveloping(true);
                TxtFooterHint.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyBAPoolTypeUI()
        {
            if (ComboBAPoolType == null) return;
            string poolType = GetBAPoolTypeFromIndex(ComboBAPoolType.SelectedIndex);

            if (poolType == "常驻招募")
            {
                if (LblBAUp3Star != null) LblBAUp3Star.Visibility = Visibility.Collapsed;
                if (ComboBAUp3Star != null) ComboBAUp3Star.Visibility = Visibility.Collapsed;
                if (BtnBACelebrationUp3 != null) BtnBACelebrationUp3.Visibility = Visibility.Collapsed;
                if (LblBAUp3Prob != null) LblBAUp3Prob.Visibility = Visibility.Collapsed;
                if (TxtBAUp3StarProb != null) TxtBAUp3StarProb.Visibility = Visibility.Collapsed;
                if (LblBASubUp3Prob != null) LblBASubUp3Prob.Visibility = Visibility.Collapsed;
                if (TxtBASubUp3StarProb != null) TxtBASubUp3StarProb.Visibility = Visibility.Collapsed;
                if (LblBAUp2Star != null) LblBAUp2Star.Visibility = Visibility.Collapsed;
                if (ComboBAUp2Star != null) ComboBAUp2Star.Visibility = Visibility.Collapsed;
                if (LblBAUp2Prob != null) LblBAUp2Prob.Visibility = Visibility.Collapsed;
                if (TxtBAUp2StarProb != null) TxtBAUp2StarProb.Visibility = Visibility.Collapsed;
            }
            else if (poolType == "限时招募")
            {
                if (LblBAUp3Star != null) LblBAUp3Star.Visibility = Visibility.Visible;
                if (ComboBAUp3Star != null) ComboBAUp3Star.Visibility = Visibility.Visible;
                if (BtnBACelebrationUp3 != null) BtnBACelebrationUp3.Visibility = Visibility.Collapsed;
                if (LblBAUp3Prob != null) { LblBAUp3Prob.Visibility = Visibility.Visible; LblBAUp3Prob.Text = "UP3星角色概率："; }
                if (TxtBAUp3StarProb != null) TxtBAUp3StarProb.Visibility = Visibility.Visible;
                if (LblBASubUp3Prob != null) LblBASubUp3Prob.Visibility = Visibility.Collapsed;
                if (TxtBASubUp3StarProb != null) TxtBASubUp3StarProb.Visibility = Visibility.Collapsed;
                if (LblBAUp2Star != null) LblBAUp2Star.Visibility = Visibility.Visible;
                if (ComboBAUp2Star != null) ComboBAUp2Star.Visibility = Visibility.Visible;
                if (LblBAUp2Prob != null) LblBAUp2Prob.Visibility = Visibility.Visible;
                if (TxtBAUp2StarProb != null) { TxtBAUp2StarProb.Visibility = Visibility.Visible; SyncUp2StarProbEnabled(); }
            }
            else
            {
                if (LblBAUp3Star != null) LblBAUp3Star.Visibility = Visibility.Visible;
                if (ComboBAUp3Star != null) ComboBAUp3Star.Visibility = Visibility.Collapsed;
                if (BtnBACelebrationUp3 != null) BtnBACelebrationUp3.Visibility = Visibility.Visible;
                if (LblBAUp3Prob != null) { LblBAUp3Prob.Visibility = Visibility.Visible; LblBAUp3Prob.Text = "主UP3星角色概率："; }
                if (TxtBAUp3StarProb != null) TxtBAUp3StarProb.Visibility = Visibility.Visible;
                if (LblBASubUp3Prob != null) LblBASubUp3Prob.Visibility = Visibility.Visible;
                if (TxtBASubUp3StarProb != null) TxtBASubUp3StarProb.Visibility = Visibility.Visible;
                if (LblBAUp2Star != null) LblBAUp2Star.Visibility = Visibility.Collapsed;
                if (ComboBAUp2Star != null) ComboBAUp2Star.Visibility = Visibility.Collapsed;
                if (LblBAUp2Prob != null) LblBAUp2Prob.Visibility = Visibility.Collapsed;
                if (TxtBAUp2StarProb != null) TxtBAUp2StarProb.Visibility = Visibility.Collapsed;
            }
        }

        private void ChkEnable_Click(object sender, RoutedEventArgs e) { if (_isLoading) return; UpdateUIState(); }
        private void ComboPreset_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_isLoading) return; UpdateUIState(); }
        private void ComboWarpType_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_isLoading) return; UpdateUIState(); }
        private void ComboBAPoolType_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_isLoading) return; ApplyBAPoolTypeUI(); }

        private void ComboBAThreeStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboBAUp3Star == null) return;
            string selected = ComboBAThreeStar.SelectedItem as string;
            string oldUp = ComboBAUp3Star.SelectedItem as string;
            ComboBAUp3Star.ItemsSource = null;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected)) return;
            ComboBAUp3Star.ItemsSource = _categoryMap[selected];
            if (!string.IsNullOrEmpty(oldUp) && ComboBAUp3Star.Items.Contains(oldUp))
                ComboBAUp3Star.SelectedItem = oldUp;
            else
                ComboBAUp3Star.SelectedIndex = -1;
        }

        private void ComboBATwoStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboBAUp2Star == null) return;
            string selected = ComboBATwoStar.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
            { ComboBAUp2Star.Items.Clear(); return; }
            RebuildUp2StarComboBox(selected, null);
        }

        private void ComboFiveStarUp_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_isLoading) return;

            string category = ComboFiveStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择 5星角色分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var items = _categoryMap[category];
            if (items == null || items.Count == 0) return;

            var dlg = new ItemSelectDialog("选择 UP5星角色", items,
                ComboFiveStarUp?.SelectedItem as string, false,
                "请选择 1 名 UP5星角色。") { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedItem))
                ComboFiveStarUp.SelectedItem = dlg.SelectedItem;
        }

        private void ComboBAUp3Star_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_isLoading) return;

            string category = ComboBAThreeStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择 3星学生分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var items = _categoryMap[category];
            if (items == null || items.Count == 0) return;

            var dlg = new ItemSelectDialog("选择 UP3星学生", items,
                ComboBAUp3Star?.SelectedItem as string, false,
                "请选择 1 名 UP3星学生。") { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedItem))
                ComboBAUp3Star.SelectedItem = dlg.SelectedItem;
        }

        private void ComboBAUp2Star_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_isLoading) return;

            string category = ComboBATwoStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择 2星学生分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var items = _categoryMap[category];
            if (items == null || items.Count == 0) return;

            var dlg = new ItemSelectDialog("选择 UP2星学生", items,
                GetSelectedUp2Star(), true,
                "可选择 1 名 UP2星学生，或点「清除」不设置。") { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() != true) return;

            if (dlg.ClearRequested)
            {
                ComboBAUp2Star.SelectedIndex = -1;
                return;
            }

            foreach (ComboBoxItem item in ComboBAUp2Star.Items)
            {
                if (string.Equals(item.Tag as string, dlg.SelectedItem, StringComparison.Ordinal))
                {
                    ComboBAUp2Star.SelectedItem = item;
                    break;
                }
            }
        }

        private void ComboBAUp2Star_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = GetSelectedUp2Star();
            bool hasUp2 = !string.IsNullOrEmpty(selected);
            if (TxtBAUp2StarProb != null) TxtBAUp2StarProb.IsEnabled = hasUp2;
            if (_isLoading) return;
            bool oldLoading = _isLoading;
            _isLoading = true;
            try
            {
                if (TxtBAUp2StarProb != null)
                {
                    if (!hasUp2) TxtBAUp2StarProb.Text = "";
                    else if (string.IsNullOrWhiteSpace(TxtBAUp2StarProb.Text)) TxtBAUp2StarProb.Text = "0.03";
                }
            }
            finally { _isLoading = oldLoading; }
        }

        private void TxtBAProb_TextChanged(object sender, TextChangedEventArgs e) { if (_isLoading) return; UpdateBAOneStarProb(); }
        private void TxtBA2StarPity_TextChanged(object sender, TextChangedEventArgs e) { if (_isLoading) return; UpdatePityNText(); }
        private void TxtHSRProb_TextChanged(object sender, TextChangedEventArgs e) { if (_isLoading) return; UpdateHSRThreeStarProb(); }
        private void ChkHighlightColors_Changed(object sender, RoutedEventArgs e) { }

        private void BtnBACelebrationUp3_Click(object sender, RoutedEventArgs e)
        {
            string category = ComboBAThreeStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择 3星学生分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var allItems = _categoryMap[category];
            if (allItems == null || allItems.Count < 2)
            {
                CustomMessageBox.Show(Window.GetWindow(this), $"“{category}”下至少需要 2 个对象才能选择主/次UP。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _baMainUp3Stars = _baMainUp3Stars.Where(x => allItems.Contains(x)).Take(1).ToList();
            _baSubUp3Stars = _baSubUp3Stars.Where(x => allItems.Contains(x)).ToList();
            if (_baMainUp3Stars.Count > 0) _baSubUp3Stars = _baSubUp3Stars.Where(x => x != _baMainUp3Stars[0]).ToList();

            string currentMain = _baMainUp3Stars.FirstOrDefault();
            PrepareNavigateToSubPage();

            // 弹窗选择（重做版：不再复用旧页面）
            var dlg = new CelebrationUpDialog(allItems, currentMain, _baSubUp3Stars)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();
        }

        private void ComboFiveStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboFiveStarUp == null) return;
            string selected = ComboFiveStar.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
            { ComboFiveStarUp.IsEnabled = false; ComboFiveStarUp.ItemsSource = null; return; }
            string oldUp = ComboFiveStarUp.SelectedItem as string;
            ComboFiveStarUp.IsEnabled = true;
            ComboFiveStarUp.ItemsSource = _categoryMap[selected];
            if (!string.IsNullOrEmpty(oldUp) && ComboFiveStarUp.Items.Contains(oldUp))
                ComboFiveStarUp.SelectedItem = oldUp;
            else
                ComboFiveStarUp.SelectedIndex = -1;
        }

        private void ComboFourStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            string selected = ComboFourStar.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
            { _fourStarUpObjects.Clear(); UpdateFourStarUpDisplay(); return; }
            _fourStarUpObjects.Clear();
            UpdateFourStarUpDisplay();
        }

        private void BtnFourStarUp_Click(object sender, RoutedEventArgs e)
        {
            string category = ComboFourStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先选择 4星角色分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var items = _categoryMap[category];
            int maxSel = Math.Max(1, items.Count - 1);
            PrepareNavigateToSubPage();

            // 弹窗选择（重做版：不再复用旧页面）
            var dlg = new MultiSelectDialog(items, _fourStarUpObjects, "选择UP4星角色", maxSel)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();
        }

        private void BtnDefault_Click(object sender, RoutedEventArgs e)
        {
            string preset = (ComboPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SUPPORTED_PRESET;
            if (preset == BA_PRESET)
            {
                string poolType = GetBAPoolTypeFromIndex(ComboBAPoolType?.SelectedIndex ?? 0);
                bool oldLoading = _isLoading; _isLoading = true;
                try
                {
                    if (poolType == "庆典招募")
                    {
                        TxtBAThreeStarProb.Text = "0.06"; TxtBATwoStarProb.Text = "0.185";
                        TxtBAUp3StarProb.Text = "0.007";
                        TxtBASubUp3StarProb.Text = (_baSubUp3Stars != null && _baSubUp3Stars.Count > 0) ? "0.003" : "";
                        TxtBAUp2StarProb.Text = ""; TxtBA2StarPity.Text = "10";
                    }
                    else if (poolType == "限时招募")
                    {
                        TxtBAThreeStarProb.Text = "0.03"; TxtBATwoStarProb.Text = "0.185";
                        TxtBAUp3StarProb.Text = "0.007"; TxtBASubUp3StarProb.Text = "";
                        string up2Selected = GetSelectedUp2Star();
                        if (!string.IsNullOrEmpty(up2Selected)) { TxtBAUp2StarProb.Text = "0.03"; TxtBAUp2StarProb.IsEnabled = true; }
                        else { TxtBAUp2StarProb.Text = ""; TxtBAUp2StarProb.IsEnabled = false; }
                        TxtBA2StarPity.Text = "10";
                    }
                    else
                    {
                        TxtBAThreeStarProb.Text = "0.03"; TxtBATwoStarProb.Text = "0.185";
                        TxtBAUp3StarProb.Text = ""; TxtBASubUp3StarProb.Text = "";
                        TxtBAUp2StarProb.Text = ""; TxtBA2StarPity.Text = "10";
                    }
                }
                finally { _isLoading = oldLoading; }
                UpdateBAOneStarProb(); UpdatePityNText(); return;
            }
            TxtFiveStarNonUpProb.Text = "0.300"; TxtFiveStarUpProb.Text = "0.300";
            TxtFourStarUpProb.Text = "2.550"; TxtFourStarNonUpProb.Text = "2.550";
            TxtSoftPityStart.Text = "74"; TxtSoftPityIncrease.Text = "0.06";
            TxtFourStarPity.Text = "10"; TxtFiveStarPity.Text = "90";
            UpdateHSRThreeStarProb();
        }

        private bool TryParsePercent(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            text = text.Trim(); bool hasPercent = text.EndsWith("%"); if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            result = val;
            if (result < 0 || result > 100) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须在 0~100 之间！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            return true;
        }

        private bool TryParseDecimalPercent(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            text = text.Trim(); bool hasPercent = text.EndsWith("%"); if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            result = hasPercent ? val / 100.0 : val;
            if (result < 0 || result > 1.0) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须在 0~100% 之间！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            return true;
        }

        private bool TryParseBAProb(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            text = text.Trim(); bool hasPercent = text.EndsWith("%"); if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            result = hasPercent ? val / 100.0 : val;
            if (result < 0 || result > 1.0) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须在 0~1 之间（例如 0.03 表示 3%）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            return true;
        }

        private bool TryParseBAProbAllowEmpty(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) { result = 0; return true; }
            text = text.Trim(); bool hasPercent = text.EndsWith("%"); if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            result = hasPercent ? val / 100.0 : val;
            if (result < 0 || result > 1.0) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须在 0~1 之间（例如 0.03 表示 3%）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            return true;
        }

        private bool TryParsePositiveInt(string text, string fieldName, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (!int.TryParse(text.Trim(), out int val)) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须是有效的整数！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (val <= 0 || val > 9999) { CustomMessageBox.Show(Window.GetWindow(this), $"“{fieldName}”必须在 1~9999 之间！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            result = val; return true;
        }

        private void CloseWithResult(bool saved)
        {
            if (saved) MarkSaved();
            MessageBus.Publish(new AppMessages.AdvancedGachaFinished(_poolDir, saved, Settings));
            MessageBus.Publish(new AppMessages.NavigateTo("Pool"));
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

        private bool TrySaveAndValidate()
        {
            string preset = (ComboPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SUPPORTED_PRESET;
            if (preset == BA_PRESET) return HandleBAOk(preset);
            if (preset != SUPPORTED_PRESET)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "当前预设暂未开放。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return HandleHSROk(preset);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            bool saved = TrySaveAndValidate();
            if (!saved) return;
            CloseWithResult(true);
        }

        private bool HandleHSROk(string preset)
        {
            bool enabled = ChkEnable.IsChecked == true;

            if (!enabled)
            {
                Settings.Enabled = false;
                Settings.PresetName = preset;
                Settings.WarpType = (ComboWarpType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? HSR_WARP_DEFAULT;
                Settings.EnableHighlightColors = ChkHighlightColors?.IsChecked == true;
                TrySaveGachaSettings();
                return true;
            }

            string warpType = (ComboWarpType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? HSR_WARP_DEFAULT;
            if (warpType != HSR_WARP_DEFAULT)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "当前跃迁类型暂未开放。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string fiveStarCategory = ComboFiveStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(fiveStarCategory)) { CustomMessageBox.Show(Window.GetWindow(this), "请选择“5星角色分类”。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            string fourStarCategory = ComboFourStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(fourStarCategory)) { CustomMessageBox.Show(Window.GetWindow(this), "请选择“4星角色分类”。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (fiveStarCategory == fourStarCategory) { CustomMessageBox.Show(Window.GetWindow(this), "5星角色分类和4星角色分类不能相同！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            int fiveStarCount = _categoryMap.TryGetValue(fiveStarCategory, out var fsList) && fsList != null ? fsList.Count : 0;
            if (fiveStarCount < 2) { CustomMessageBox.Show(Window.GetWindow(this), $"“5星角色分类”下至少需要 2 个对象（当前 {fiveStarCount} 个）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            int fourStarCount = _categoryMap.TryGetValue(fourStarCategory, out var f4List) && f4List != null ? f4List.Count : 0;
            if (fourStarCount < 2) { CustomMessageBox.Show(Window.GetWindow(this), $"“4星角色分类”下至少需要 2 个对象（当前 {fourStarCount} 个）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            bool has3StarCategory = _categoryMap.Any(kvp =>
                kvp.Key != fiveStarCategory && kvp.Key != fourStarCategory && kvp.Value != null && kvp.Value.Count > 0);
            if (!has3StarCategory) { CustomMessageBox.Show(Window.GetWindow(this), "卡池中不存在可用的“3星”分类，无法启用高级规则。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            string fiveStarUpObject = ComboFiveStarUp?.SelectedItem?.ToString() ?? "";
            if (_fourStarUpObjects.Count < 1) { CustomMessageBox.Show(Window.GetWindow(this), "请至少选择 1 个 UP4星角色。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (_fourStarUpObjects.Count > fourStarCount - 1) { CustomMessageBox.Show(Window.GetWindow(this), $"UP4星角色最多只能选择 {fourStarCount - 1} 个！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (!string.IsNullOrEmpty(fiveStarUpObject) && _fourStarUpObjects.Contains(fiveStarUpObject))
            { CustomMessageBox.Show(Window.GetWindow(this), "UP5星角色不能同时作为UP4星角色！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            if (!TryParsePercent(TxtFiveStarNonUpProb.Text, "5星非UP基础概率", out double p5n)) return false;
            if (!TryParsePercent(TxtFiveStarUpProb.Text, "5星UP基础概率", out double p5u)) return false;
            if (!TryParsePercent(TxtFourStarUpProb.Text, "4星UP基础概率", out double p4u)) return false;
            if (!TryParsePercent(TxtFourStarNonUpProb.Text, "4星非UP基础概率", out double p4n)) return false;

            double p3 = 100.0 - p5n - p5u - p4u - p4n;
            if (p3 < -0.001) { CustomMessageBox.Show(Window.GetWindow(this), "四项概率之和不能超过 100%！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (p3 < 0) p3 = 0;

            if (!TryParsePositiveInt(TxtSoftPityStart.Text, "概率提升起始抽数", out int softPityStart)) return false;
            if (!TryParseDecimalPercent(TxtSoftPityIncrease.Text, "概率提升值", out double softPityIncrease)) return false;
            if (!TryParsePositiveInt(TxtFourStarPity.Text, "4星保底抽数", out int fourStarPity)) return false;
            if (!TryParsePositiveInt(TxtFiveStarPity.Text, "5星保底抽数", out int fiveStarPity)) return false;
            if (fiveStarPity <= softPityStart) { CustomMessageBox.Show(Window.GetWindow(this), "5星保底抽数必须大于概率提升起始抽数！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            Settings.Enabled = true;
            Settings.PresetName = preset;
            Settings.WarpType = warpType;
            Settings.FiveStarCategory = fiveStarCategory;
            Settings.FiveStarUpObject = fiveStarUpObject;
            Settings.FourStarCategory = fourStarCategory;
            Settings.FourStarUpObjects = string.Join(",", _fourStarUpObjects);
            Settings.FiveStarNonUpProb = p5n;
            Settings.FiveStarUpProb = p5u;
            Settings.FourStarUpProb = p4u;
            Settings.FourStarNonUpProb = p4n;
            Settings.ThreeStarProb = p3;
            Settings.SoftPityStart = softPityStart;
            Settings.SoftPityIncrease = softPityIncrease;
            Settings.FourStarPity = fourStarPity;
            Settings.FiveStarPity = fiveStarPity;
            Settings.EnableHighlightColors = ChkHighlightColors?.IsChecked == true;

            TrySaveGachaSettings();
            return true;
        }

        private bool HandleBAOk(string preset)
        {
            bool enabled = ChkEnable.IsChecked == true;

            if (!enabled)
            {
                Settings.Enabled = false;
                Settings.PresetName = preset;
                Settings.EnableHighlightColorsBA = ChkHighlightColorsBA?.IsChecked == true;
                TrySaveGachaSettings();
                return true;
            }

            string poolType = GetBAPoolTypeFromIndex(ComboBAPoolType?.SelectedIndex ?? 0);

            string threeStarCat = ComboBAThreeStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(threeStarCat)) { CustomMessageBox.Show(Window.GetWindow(this), "请选择“3星学生”分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            string twoStarCat = ComboBATwoStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(twoStarCat)) { CustomMessageBox.Show(Window.GetWindow(this), "请选择“2星学生”分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (threeStarCat == twoStarCat) { CustomMessageBox.Show(Window.GetWindow(this), "3星学生分类和2星学生分类不能相同！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            if (_categoryMap == null ||
                !_categoryMap.TryGetValue(threeStarCat, out var threeList) || threeList == null ||
                !_categoryMap.TryGetValue(twoStarCat, out var twoList) || twoList == null)
            { CustomMessageBox.Show(Window.GetWindow(this), "无法读取所选分类下的对象，请检查卡池内容。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            int threeStarCount = threeList.Count;
            int twoStarCount = twoList.Count;

            if (threeStarCount < 2) { CustomMessageBox.Show(Window.GetWindow(this), $"“{threeStarCat}”下至少需要 2 个对象。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (twoStarCount < 2) { CustomMessageBox.Show(Window.GetWindow(this), $"“{twoStarCat}”下至少需要 2 个对象。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            bool has1Star = _categoryMap.Any(kvp =>
                kvp.Key != threeStarCat && kvp.Key != twoStarCat && kvp.Value != null && kvp.Value.Count > 0);
            if (!has1Star) { CustomMessageBox.Show(Window.GetWindow(this), "卡池中不存在可用的“1星”分类，无法启用高级规则。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            if (!TryParseBAProb(TxtBAThreeStarProb.Text, "3星学生总概率", out double threeStarProb)) return false;
            if (!TryParseBAProb(TxtBATwoStarProb.Text, "2星学生总概率", out double twoStarProb)) return false;
            if (threeStarProb + twoStarProb > 1.0 + 1e-9) { CustomMessageBox.Show(Window.GetWindow(this), "3星 + 2星 总概率不能超过 1（100%）！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (!TryParsePositiveInt(TxtBA2StarPity.Text, "2星保底抽数", out int pity)) return false;

            string up3Star = "", up2Star = "", mainUp3 = "", subUp3 = "";
            double up3Prob = 0, subUp3Prob = 0, up2Prob = 0;

            if (poolType == "限时招募")
            {
                up3Star = ComboBAUp3Star?.SelectedItem?.ToString() ?? "";
                if (string.IsNullOrEmpty(up3Star)) { CustomMessageBox.Show(Window.GetWindow(this), "请选择 UP3星学生。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                if (!threeList.Contains(up3Star)) { CustomMessageBox.Show(Window.GetWindow(this), "UP3星学生必须属于 3星学生分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                if (!TryParseBAProb(TxtBAUp3StarProb.Text, "UP3星角色概率", out up3Prob)) return false;
                if (up3Prob > threeStarProb + 1e-9) { CustomMessageBox.Show(Window.GetWindow(this), "UP3星角色概率不能超过 3星学生总概率。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

                up2Star = GetSelectedUp2Star() ?? "";
                if (!string.IsNullOrEmpty(up2Star))
                {
                    if (!twoList.Contains(up2Star)) { CustomMessageBox.Show(Window.GetWindow(this), "UP2星学生必须属于 2星学生分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                    if (!TryParseBAProb(TxtBAUp2StarProb.Text, "UP2星角色概率", out up2Prob)) return false;
                    if (up2Prob > twoStarProb + 1e-9) { CustomMessageBox.Show(Window.GetWindow(this), "UP2星角色概率不能超过 2星学生总概率。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(TxtBAUp2StarProb.Text)) { CustomMessageBox.Show(Window.GetWindow(this), "未选择 UP2星学生时，“UP2星角色概率”必须为空。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                    up2Prob = 0;
                }
            }
            else if (poolType == "庆典招募")
            {
                if (_baMainUp3Stars.Count < 1) { CustomMessageBox.Show(Window.GetWindow(this), "庆典招募必须选择 1 个主UP3星学生。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                if (_baMainUp3Stars.Count > 1) { CustomMessageBox.Show(Window.GetWindow(this), "主UP3星学生只能选择 1 个！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

                foreach (var up in _baMainUp3Stars.Concat(_baSubUp3Stars))
                {
                    if (!threeList.Contains(up)) { CustomMessageBox.Show(Window.GetWindow(this), $"UP3星学生“{up}”不属于 3星分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                }

                var overlap = _baMainUp3Stars.Intersect(_baSubUp3Stars).ToList();
                if (overlap.Count > 0) { CustomMessageBox.Show(Window.GetWindow(this), "主UP 与 次UP 不能选择同一个学生：\n\n" + string.Join(", ", overlap), "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

                if (_baSubUp3Stars.Count > threeStarCount - 2) { CustomMessageBox.Show(Window.GetWindow(this), $"次UP 最多选择 {threeStarCount - 2} 个。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                if (_baMainUp3Stars.Count + _baSubUp3Stars.Count > threeStarCount - 1) { CustomMessageBox.Show(Window.GetWindow(this), "主UP + 次UP 数量不能超过（3星总数 - 1）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

                if (!TryParseBAProb(TxtBAUp3StarProb.Text, "主UP3星角色概率", out up3Prob)) return false;
                if (_baSubUp3Stars.Count > 0)
                {
                    if (!TryParseBAProbAllowEmpty(TxtBASubUp3StarProb.Text, "次UP3星角色概率", out subUp3Prob)) return false;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(TxtBASubUp3StarProb.Text)) { CustomMessageBox.Show(Window.GetWindow(this), "未选择次UP学生时，“次UP3星角色概率”必须为空。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
                    subUp3Prob = 0;
                }

                if (up3Prob + subUp3Prob > threeStarProb + 1e-9) { CustomMessageBox.Show(Window.GetWindow(this), "主UP + 次UP 概率之和不能超过 3星学生总概率。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

                mainUp3 = string.Join(",", _baMainUp3Stars);
                subUp3 = string.Join(",", _baSubUp3Stars);
            }
            else { up3Prob = 0; subUp3Prob = 0; up2Prob = 0; }

            Settings.Enabled = true;
            Settings.PresetName = preset;
            Settings.BAPoolType = poolType;
            Settings.BA3StarCategory = threeStarCat;
            Settings.BA2StarCategory = twoStarCat;
            Settings.BA3StarProb = threeStarProb;
            Settings.BA2StarProb = twoStarProb;
            Settings.BAUp3Star = up3Star;
            Settings.BAUp2Star = up2Star;
            Settings.BAMainUp3Stars = mainUp3;
            Settings.BASubUp3Stars = subUp3;
            Settings.BA2StarPity = pity;
            Settings.BAUp3StarProb = up3Prob;
            Settings.BASubUp3StarProb = subUp3Prob;
            Settings.BAUp2StarProb = up2Prob;
            Settings.EnableHighlightColorsBA = ChkHighlightColorsBA?.IsChecked == true;

            TrySaveGachaSettings();
            return true;
        }
    }
}