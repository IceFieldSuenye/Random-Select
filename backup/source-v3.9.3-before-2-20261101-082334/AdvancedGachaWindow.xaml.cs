using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace RandomDrawer
{
    public partial class AdvancedGachaWindow : Window
    {
        private Dictionary<string, List<string>> _categoryMap;
        private string _poolDir;
        private bool _isLoading = true;
        private bool _hasSavedSettings;
        private const string SUPPORTED_PRESET = "崩坏：星穹铁道";
        private const string BA_PRESET = "蔚蓝档案（旧）";
        private const string HSR_WARP_DEFAULT = "角色活动跃迁";
        private const int UpListDisplayThreshold = 2;

        private const string NoStudentOption = "（无学生）";

        private List<string> _fourStarUpObjects = new List<string>();
        private List<string> _baMainUp3Stars = new List<string>();
        private List<string> _baSubUp3Stars = new List<string>();

        private Storyboard _syncStoryboard;

        public class GachaSettings
        {
            public bool Enabled { get; set; } = false;
            public string PresetName { get; set; } = "崩坏：星穹铁道";

            // HSR
            public string WarpType { get; set; } = "角色活动跃迁";
            public string FiveStarCategory { get; set; } = "";
            public string FiveStarUpObject { get; set; } = "";
            public string FourStarCategory { get; set; } = "";
            public string FourStarUpObjects { get; set; } = "";
            public double FiveStarNonUpProb { get; set; } = 0.300;
            public double FiveStarUpProb { get; set; } = 0.300;
            public double FourStarUpProb { get; set; } = 2.550;
            public double FourStarNonUpProb { get; set; } = 2.550;
            public double ThreeStarProb { get; set; } = 94.300;
            public int SoftPityStart { get; set; } = 74;
            public double SoftPityIncrease { get; set; } = 0.06;
            public int FourStarPity { get; set; } = 10;
            public int FiveStarPity { get; set; } = 90;

            // 旧字段，兼容历史 ini 文件
            public bool EnableStarColor { get; set; } = false;

            // ★ 新增：更醒目的颜色（HSR）
            public bool EnableHighlightColors { get; set; } = false;

            // BA
            public string BAPoolType { get; set; } = "常驻招募";
            public string BA3StarCategory { get; set; } = "";
            public string BA2StarCategory { get; set; } = "";
            public double BA3StarProb { get; set; } = 0.03;
            public double BA2StarProb { get; set; } = 0.185;
            public string BAUp3Star { get; set; } = "";
            public string BAUp2Star { get; set; } = "";
            public string BAMainUp3Stars { get; set; } = "";
            public string BASubUp3Stars { get; set; } = "";
            public int BA2StarPity { get; set; } = 10;

            public double BAUp3StarProb { get; set; } = 0.007;
            public double BASubUp3StarProb { get; set; } = 0.003;
            public double BAUp2StarProb { get; set; } = 0.03;

            // ★ 新增：更醒目的颜色（BA）
            public bool EnableHighlightColorsBA { get; set; } = false;
        }

        public GachaSettings Settings { get; private set; }

        public AdvancedGachaWindow(GachaSettings initialSettings, Dictionary<string, List<string>> categoryMap, string poolDir)
        {
            InitializeComponent();

            _categoryMap = categoryMap ?? new Dictionary<string, List<string>>();
            _poolDir = poolDir;
            Settings = initialSettings ?? new GachaSettings();
            _hasSavedSettings = (initialSettings != null && initialSettings.Enabled);

            var categories = _categoryMap.Keys.ToList();
            ComboFiveStar.ItemsSource = categories.ToList();
            ComboFourStar.ItemsSource = categories.ToList();
            ComboBAThreeStar.ItemsSource = categories.ToList();
            ComboBATwoStar.ItemsSource = categories.ToList();

            if (ComboPreset.Items.Count > 0) ComboPreset.SelectedIndex = 0;
            if (ComboWarpType.Items.Count > 0) ComboWarpType.SelectedIndex = 0;

            ApplySettingsToUI();
            _isLoading = false;
            UpdateUIState();
            ApplyBAPoolTypeUI();
            UpdatePityNText();
            UpdateHSRThreeStarProb();
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _syncStoryboard?.Stop(this); } catch { }
            base.OnClosed(e);
        }

        private void ShowDeveloping(bool show)
        {
            if (DevelopingPanel == null) return;
            DevelopingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            try
            {
                if (_syncStoryboard == null)
                    _syncStoryboard = (Storyboard)FindResource("SyncRingStoryboard");

                if (show)
                {
                    try { _syncStoryboard.Stop(this); } catch { }
                    _syncStoryboard.Begin(this, true);
                }
                else
                {
                    _syncStoryboard.Stop(this);
                }
            }
            catch { }
        }

        private void RebuildUp2StarComboBox(string category, string initialSelection)
        {
            if (ComboBAUp2Star == null) return;

            ComboBAUp2Star.Items.Clear();
            ComboBAUp2Star.Items.Add(new ComboBoxItem { Content = NoStudentOption, Tag = null });

            if (!string.IsNullOrEmpty(category) && _categoryMap != null && _categoryMap.ContainsKey(category))
            {
                foreach (var name in _categoryMap[category])
                    ComboBAUp2Star.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            }

            if (!string.IsNullOrEmpty(initialSelection))
            {
                foreach (ComboBoxItem item in ComboBAUp2Star.Items)
                {
                    if ((item.Tag as string) == initialSelection)
                    {
                        ComboBAUp2Star.SelectedItem = item;
                        return;
                    }
                }
            }

            ComboBAUp2Star.SelectedIndex = 0;
        }

        private string GetSelectedUp2Star()
        {
            if (ComboBAUp2Star?.SelectedItem is ComboBoxItem item)
                return item.Tag as string;
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
                    {
                        if (item.Content.ToString() == Settings.PresetName)
                        {
                            ComboPreset.SelectedItem = item;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(Settings.WarpType))
                {
                    foreach (ComboBoxItem item in ComboWarpType.Items)
                    {
                        if (item.Content.ToString() == Settings.WarpType)
                        {
                            ComboWarpType.SelectedItem = item;
                            break;
                        }
                    }
                }

                try
                {
                    // HSR
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

                    // ★ 恢复 HSR 高亮复选框
                    if (ChkHighlightColors != null)
                        ChkHighlightColors.IsChecked = Settings.EnableHighlightColors;

                    // BA
                    ComboBAPoolType.SelectedIndex = GetBAPoolTypeIndex(Settings.BAPoolType);

                    if (!string.IsNullOrEmpty(Settings.BA3StarCategory) && _categoryMap.ContainsKey(Settings.BA3StarCategory))
                    {
                        ComboBAThreeStar.SelectedItem = Settings.BA3StarCategory;
                        ComboBAUp3Star.ItemsSource = _categoryMap[Settings.BA3StarCategory];
                        if (!string.IsNullOrEmpty(Settings.BAUp3Star))
                            ComboBAUp3Star.SelectedItem = Settings.BAUp3Star;
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

                    if (!string.IsNullOrEmpty(Settings.BAUp2Star))
                        TxtBAUp2StarProb.Text = Settings.BAUp2StarProb.ToString("0.####");
                    else
                        TxtBAUp2StarProb.Text = "";

                    _baMainUp3Stars = ParseUpObjectsString(Settings.BAMainUp3Stars).Take(1).ToList();
                    _baSubUp3Stars = ParseUpObjectsString(Settings.BASubUp3Stars);
                    if (_baMainUp3Stars.Count > 0)
                        _baSubUp3Stars = _baSubUp3Stars.Where(x => x != _baMainUp3Stars[0]).ToList();

                    UpdateBACelebrationDisplay();
                    UpdateBAOneStarProb();
                    UpdatePityNText();
                    SyncUp2StarProbEnabled();

                    // ★ 恢复 BA 高亮复选框
                    if (ChkHighlightColorsBA != null)
                        ChkHighlightColorsBA.IsChecked = Settings.EnableHighlightColorsBA;
                }
                catch { }
            }
            else
            {
                if (ComboBAPoolType.Items.Count > 0)
                    ComboBAPoolType.SelectedIndex = 0;
                UpdateBAOneStarProb();
                UpdatePityNText();
            }
        }

        private int GetBAPoolTypeIndex(string type)
        {
            if (string.IsNullOrEmpty(type)) return 0;
            switch (type)
            {
                case "限时招募": return 1;
                case "庆典招募": return 2;
                default: return 0;
            }
        }

        private string GetBAPoolTypeFromIndex(int index)
        {
            switch (index)
            {
                case 1: return "限时招募";
                case 2: return "庆典招募";
                default: return "常驻招募";
            }
        }

        private List<string> ParseUpObjectsString(string s)
        {
            if (string.IsNullOrEmpty(s)) return new List<string>();
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
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

            if (string.IsNullOrWhiteSpace(threeText) && string.IsNullOrWhiteSpace(twoText))
            {
                TxtBAOneStarProb.Text = "-";
                return;
            }

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

            string txt = TxtBA2StarPity?.Text ?? "";
            if (string.IsNullOrWhiteSpace(txt))
                RunPityN.Text = "N";
            else
                RunPityN.Text = txt.Trim();
        }

        private string FormatUpList(string prefix, List<string> list)
        {
            if (list == null || list.Count == 0) return "";
            if (list.Count <= UpListDisplayThreshold)
                return prefix + ":" + string.Join(",", list);
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
                if (_baMainUp3Stars.Count > 0)
                    parts.Add("主UP:" + _baMainUp3Stars[0]);
                if (_baSubUp3Stars.Count > 0)
                    parts.Add(FormatUpList("次UP", _baSubUp3Stars));
                TxtBACelebrationUp3Display.Text = string.Join(" | ", parts);
                IconBACelebrationUp3.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateUIState()
        {
            bool enabled = ChkEnable.IsChecked == true;
            ComboPreset.IsEnabled = enabled;

            if (BtnDefault != null)
                BtnDefault.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

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
                if (LblBAUp3Prob != null)
                {
                    LblBAUp3Prob.Visibility = Visibility.Visible;
                    LblBAUp3Prob.Text = "UP3星角色概率：";
                }
                if (TxtBAUp3StarProb != null) TxtBAUp3StarProb.Visibility = Visibility.Visible;
                if (LblBASubUp3Prob != null) LblBASubUp3Prob.Visibility = Visibility.Collapsed;
                if (TxtBASubUp3StarProb != null) TxtBASubUp3StarProb.Visibility = Visibility.Collapsed;
                if (LblBAUp2Star != null) LblBAUp2Star.Visibility = Visibility.Visible;
                if (ComboBAUp2Star != null) ComboBAUp2Star.Visibility = Visibility.Visible;
                if (LblBAUp2Prob != null) LblBAUp2Prob.Visibility = Visibility.Visible;
                if (TxtBAUp2StarProb != null)
                {
                    TxtBAUp2StarProb.Visibility = Visibility.Visible;
                    SyncUp2StarProbEnabled();
                }
            }
            else
            {
                if (LblBAUp3Star != null) LblBAUp3Star.Visibility = Visibility.Visible;
                if (ComboBAUp3Star != null) ComboBAUp3Star.Visibility = Visibility.Collapsed;
                if (BtnBACelebrationUp3 != null) BtnBACelebrationUp3.Visibility = Visibility.Visible;
                if (LblBAUp3Prob != null)
                {
                    LblBAUp3Prob.Visibility = Visibility.Visible;
                    LblBAUp3Prob.Text = "主UP3星角色概率：";
                }
                if (TxtBAUp3StarProb != null) TxtBAUp3StarProb.Visibility = Visibility.Visible;
                if (LblBASubUp3Prob != null) LblBASubUp3Prob.Visibility = Visibility.Visible;
                if (TxtBASubUp3StarProb != null) TxtBASubUp3StarProb.Visibility = Visibility.Visible;
                if (LblBAUp2Star != null) LblBAUp2Star.Visibility = Visibility.Collapsed;
                if (ComboBAUp2Star != null) ComboBAUp2Star.Visibility = Visibility.Collapsed;
                if (LblBAUp2Prob != null) LblBAUp2Prob.Visibility = Visibility.Collapsed;
                if (TxtBAUp2StarProb != null) TxtBAUp2StarProb.Visibility = Visibility.Collapsed;
            }
        }

        // ================================================================
        // 事件
        // ================================================================
        private void ChkEnable_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            UpdateUIState();
        }

        private void ComboPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            UpdateUIState();
        }

        private void ComboWarpType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            UpdateUIState();
        }

        private void ComboBAPoolType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            ApplyBAPoolTypeUI();
        }

        private void ComboBAThreeStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboBAUp3Star == null) return;

            string selected = ComboBAThreeStar.SelectedItem as string;
            ComboBAUp3Star.ItemsSource = null;

            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
                return;

            ComboBAUp3Star.ItemsSource = _categoryMap[selected];
            if (ComboBAUp3Star.Items.Count > 0)
                ComboBAUp3Star.SelectedIndex = 0;
        }

        private void ComboBATwoStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboBAUp2Star == null) return;

            string selected = ComboBATwoStar.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
            {
                ComboBAUp2Star.Items.Clear();
                return;
            }

            RebuildUp2StarComboBox(selected, null);
        }

        private void ComboBAUp2Star_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = GetSelectedUp2Star();
            bool hasUp2 = !string.IsNullOrEmpty(selected);

            if (TxtBAUp2StarProb != null)
                TxtBAUp2StarProb.IsEnabled = hasUp2;

            if (_isLoading) return;

            bool oldLoading = _isLoading;
            _isLoading = true;
            try
            {
                if (TxtBAUp2StarProb != null)
                {
                    if (!hasUp2)
                        TxtBAUp2StarProb.Text = "";
                    else if (string.IsNullOrWhiteSpace(TxtBAUp2StarProb.Text))
                        TxtBAUp2StarProb.Text = "0.03";
                }
            }
            finally
            {
                _isLoading = oldLoading;
            }
        }

        private void TxtBAProb_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            UpdateBAOneStarProb();
        }

        private void TxtBA2StarPity_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            UpdatePityNText();
        }

        private void TxtHSRProb_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            UpdateHSRThreeStarProb();
        }

        // ★ 更醒目的颜色复选框变化（无需实时反应，保存时统一读取）
        private void ChkHighlightColors_Changed(object sender, RoutedEventArgs e)
        {
            // 空实现即可，避免设计器报错
        }

        private void BtnBACelebrationUp3_Click(object sender, RoutedEventArgs e)
        {
            string category = ComboBAThreeStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(this, "请先选择 3星学生分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var allItems = _categoryMap[category];
            if (allItems == null || allItems.Count < 2)
            {
                CustomMessageBox.Show(this, $"“{category}”下至少需要 2 个对象才能选择主/次UP。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _baMainUp3Stars = _baMainUp3Stars.Where(x => allItems.Contains(x)).Take(1).ToList();
            _baSubUp3Stars = _baSubUp3Stars.Where(x => allItems.Contains(x)).ToList();
            if (_baMainUp3Stars.Count > 0)
                _baSubUp3Stars = _baSubUp3Stars.Where(x => x != _baMainUp3Stars[0]).ToList();

            string currentMain = _baMainUp3Stars.FirstOrDefault();
            var selector = new CelebrationUpSelectorWindow(allItems, currentMain, _baSubUp3Stars) { Owner = this };

            if (selector.ShowDialog() == true)
            {
                if (!string.IsNullOrEmpty(selector.MainUp))
                    _baMainUp3Stars = new List<string> { selector.MainUp };
                else
                    _baMainUp3Stars = new List<string>();

                _baSubUp3Stars = selector.SubUps ?? new List<string>();
                _baSubUp3Stars = _baSubUp3Stars.Where(x => !_baMainUp3Stars.Contains(x)).Distinct().ToList();

                UpdateBACelebrationDisplay();

                if (_baSubUp3Stars.Count == 0 && TxtBASubUp3StarProb != null)
                {
                    bool oldLoading = _isLoading;
                    _isLoading = true;
                    TxtBASubUp3StarProb.Text = "";
                    _isLoading = oldLoading;
                }
            }
        }

        private void ComboFiveStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (ComboFiveStarUp == null) return;

            string selected = ComboFiveStar.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
            {
                ComboFiveStarUp.IsEnabled = false;
                ComboFiveStarUp.ItemsSource = null;
                return;
            }
            ComboFiveStarUp.IsEnabled = true;
            ComboFiveStarUp.ItemsSource = _categoryMap[selected];
            if (ComboFiveStarUp.Items.Count > 0) ComboFiveStarUp.SelectedIndex = 0;
        }

        private void ComboFourStar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            string selected = ComboFourStar.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || _categoryMap == null || !_categoryMap.ContainsKey(selected))
            {
                _fourStarUpObjects.Clear();
                UpdateFourStarUpDisplay();
                return;
            }
            _fourStarUpObjects.Clear();
            UpdateFourStarUpDisplay();
        }

        private void BtnFourStarUp_Click(object sender, RoutedEventArgs e)
        {
            string category = ComboFourStar?.SelectedItem as string;
            if (string.IsNullOrEmpty(category) || _categoryMap == null || !_categoryMap.ContainsKey(category))
            {
                CustomMessageBox.Show(this, "请先选择 4星角色分类。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var items = _categoryMap[category];
            var win = new MultiSelectWindow(items, _fourStarUpObjects, "选择UP4星角色") { Owner = this };
            if (win.ShowDialog() == true && win.SelectedItems != null)
            {
                _fourStarUpObjects = win.SelectedItems.ToList();
                UpdateFourStarUpDisplay();
            }
        }

        private void BtnDefault_Click(object sender, RoutedEventArgs e)
        {
            string preset = (ComboPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SUPPORTED_PRESET;

            if (preset == BA_PRESET)
            {
                string poolType = GetBAPoolTypeFromIndex(ComboBAPoolType?.SelectedIndex ?? 0);

                bool oldLoading = _isLoading;
                _isLoading = true;
                try
                {
                    if (poolType == "庆典招募")
                    {
                        TxtBAThreeStarProb.Text = "0.06";
                        TxtBATwoStarProb.Text = "0.185";
                        TxtBAUp3StarProb.Text = "0.007";
                        if (_baSubUp3Stars != null && _baSubUp3Stars.Count > 0)
                            TxtBASubUp3StarProb.Text = "0.003";
                        else
                            TxtBASubUp3StarProb.Text = "";
                        TxtBAUp2StarProb.Text = "";
                        TxtBA2StarPity.Text = "10";
                    }
                    else if (poolType == "限时招募")
                    {
                        TxtBAThreeStarProb.Text = "0.03";
                        TxtBATwoStarProb.Text = "0.185";
                        TxtBAUp3StarProb.Text = "0.007";
                        TxtBASubUp3StarProb.Text = "";

                        string up2Selected = GetSelectedUp2Star();
                        if (!string.IsNullOrEmpty(up2Selected))
                        {
                            TxtBAUp2StarProb.Text = "0.03";
                            TxtBAUp2StarProb.IsEnabled = true;
                        }
                        else
                        {
                            TxtBAUp2StarProb.Text = "";
                            TxtBAUp2StarProb.IsEnabled = false;
                        }

                        TxtBA2StarPity.Text = "10";
                    }
                    else
                    {
                        TxtBAThreeStarProb.Text = "0.03";
                        TxtBATwoStarProb.Text = "0.185";
                        TxtBAUp3StarProb.Text = "";
                        TxtBASubUp3StarProb.Text = "";
                        TxtBAUp2StarProb.Text = "";
                        TxtBA2StarPity.Text = "10";
                    }
                }
                finally { _isLoading = oldLoading; }

                UpdateBAOneStarProb();
                UpdatePityNText();
                return;
            }

            TxtFiveStarNonUpProb.Text = "0.300";
            TxtFiveStarUpProb.Text = "0.300";
            TxtFourStarUpProb.Text = "2.550";
            TxtFourStarNonUpProb.Text = "2.550";
            TxtSoftPityStart.Text = "74";
            TxtSoftPityIncrease.Text = "0.06";
            TxtFourStarPity.Text = "10";
            TxtFiveStarPity.Text = "90";
            UpdateHSRThreeStarProb();
        }

        // ================================================================
        // 校验工具
        // ================================================================
        private bool TryParsePercent(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            text = text.Trim();
            bool hasPercent = text.EndsWith("%");
            if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            result = val;
            if (result < 0 || result > 100)
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须在 0~100 之间！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private bool TryParseDecimalPercent(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            text = text.Trim();
            bool hasPercent = text.EndsWith("%");
            if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            result = hasPercent ? val / 100.0 : val;
            if (result < 0 || result > 1.0)
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须在 0~100% 之间！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private bool TryParseBAProb(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            text = text.Trim();
            bool hasPercent = text.EndsWith("%");
            if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            result = hasPercent ? val / 100.0 : val;
            if (result < 0 || result > 1.0)
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须在 0~1 之间（例如 0.03 表示 3%）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private bool TryParseBAProbAllowEmpty(string text, string fieldName, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text)) { result = 0; return true; }
            text = text.Trim();
            bool hasPercent = text.EndsWith("%");
            if (hasPercent) text = text.TrimEnd('%').Trim();
            if (!double.TryParse(text, out double val))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            result = hasPercent ? val / 100.0 : val;
            if (result < 0 || result > 1.0)
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须在 0~1 之间（例如 0.03 表示 3%）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private bool TryParsePositiveInt(string text, string fieldName, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”不能为空！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!int.TryParse(text.Trim(), out int val))
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须是有效的整数！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (val <= 0 || val > 9999)
            {
                CustomMessageBox.Show(this, $"“{fieldName}”必须在 1~9999 之间！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            result = val;
            return true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string preset = (ComboPreset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SUPPORTED_PRESET;

            if (preset == BA_PRESET) { HandleBAOk(preset); return; }

            if (preset != SUPPORTED_PRESET)
            {
                CustomMessageBox.Show(this, "当前预设暂未开放，请选择“崩坏：星穹铁道”或“蔚蓝档案（旧）”。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            HandleHSROk(preset);
        }

        private void HandleHSROk(string preset)
        {
            bool enabled = ChkEnable.IsChecked == true;

            if (!enabled)
            {
                Settings.Enabled = false;
                Settings.PresetName = preset;
                Settings.WarpType = (ComboWarpType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? HSR_WARP_DEFAULT;
                Settings.EnableHighlightColors = ChkHighlightColors?.IsChecked == true;
                try { SaveSettingsToFile(Settings, Path.Combine(_poolDir, "gacha_settings.ini")); } catch { }
                DialogResult = true;
                return;
            }

            string warpType = (ComboWarpType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? HSR_WARP_DEFAULT;
            if (warpType != HSR_WARP_DEFAULT)
            {
                CustomMessageBox.Show(this, "当前跃迁类型暂未开放，请选择“角色活动跃迁”。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fiveStarCategory = ComboFiveStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(fiveStarCategory))
            {
                CustomMessageBox.Show(this, "请选择“5星角色分类”。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fourStarCategory = ComboFourStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(fourStarCategory))
            {
                CustomMessageBox.Show(this, "请选择“4星角色分类”。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (fiveStarCategory == fourStarCategory)
            {
                CustomMessageBox.Show(this, "5星角色分类和4星角色分类不能相同！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int fiveStarCount = _categoryMap.TryGetValue(fiveStarCategory, out var fsList) && fsList != null ? fsList.Count : 0;
            if (fiveStarCount < 2)
            {
                CustomMessageBox.Show(this, $"“5星角色分类”下至少需要 2 个对象（当前 {fiveStarCount} 个）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int fourStarCount = _categoryMap.TryGetValue(fourStarCategory, out var f4List) && f4List != null ? f4List.Count : 0;
            if (fourStarCount < 2)
            {
                CustomMessageBox.Show(this, $"“4星角色分类”下至少需要 2 个对象（当前 {fourStarCount} 个）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool has3StarCategory = _categoryMap.Any(kvp =>
                kvp.Key != fiveStarCategory &&
                kvp.Key != fourStarCategory &&
                kvp.Value != null && kvp.Value.Count > 0);

            if (!has3StarCategory)
            {
                CustomMessageBox.Show(this, "卡池中不存在可用的“3星”分类，无法启用高级规则。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string fiveStarUpObject = ComboFiveStarUp?.SelectedItem?.ToString() ?? "";

            if (_fourStarUpObjects.Count < 1)
            {
                CustomMessageBox.Show(this, "请至少选择 1 个 UP4星角色。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_fourStarUpObjects.Count > fourStarCount - 1)
            {
                CustomMessageBox.Show(this, $"UP4星角色最多只能选择 {fourStarCount - 1} 个！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(fiveStarUpObject) && _fourStarUpObjects.Contains(fiveStarUpObject))
            {
                CustomMessageBox.Show(this, "UP5星角色不能同时作为UP4星角色！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParsePercent(TxtFiveStarNonUpProb.Text, "5星非UP基础概率", out double p5n)) return;
            if (!TryParsePercent(TxtFiveStarUpProb.Text, "5星UP基础概率", out double p5u)) return;
            if (!TryParsePercent(TxtFourStarUpProb.Text, "4星UP基础概率", out double p4u)) return;
            if (!TryParsePercent(TxtFourStarNonUpProb.Text, "4星非UP基础概率", out double p4n)) return;

            double p3 = 100.0 - p5n - p5u - p4u - p4n;
            if (p3 < -0.001)
            {
                CustomMessageBox.Show(this, "5星非UP + 5星UP + 4星UP + 4星非UP 的概率之和不能超过 100%！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (p3 < 0) p3 = 0;

            if (!TryParsePositiveInt(TxtSoftPityStart.Text, "概率提升起始抽数", out int softPityStart)) return;
            if (!TryParseDecimalPercent(TxtSoftPityIncrease.Text, "概率提升值", out double softPityIncrease)) return;
            if (!TryParsePositiveInt(TxtFourStarPity.Text, "4星保底抽数", out int fourStarPity)) return;
            if (!TryParsePositiveInt(TxtFiveStarPity.Text, "5星保底抽数", out int fiveStarPity)) return;

            if (fiveStarPity <= softPityStart)
            {
                CustomMessageBox.Show(this, "5星保底抽数必须大于概率提升起始抽数！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

            try { SaveSettingsToFile(Settings, Path.Combine(_poolDir, "gacha_settings.ini")); } catch { }
            DialogResult = true;
        }

        private void HandleBAOk(string preset)
        {
            bool enabled = ChkEnable.IsChecked == true;

            if (!enabled)
            {
                Settings.Enabled = false;
                Settings.PresetName = preset;
                Settings.EnableHighlightColorsBA = ChkHighlightColorsBA?.IsChecked == true;
                try { SaveSettingsToFile(Settings, Path.Combine(_poolDir, "gacha_settings.ini")); } catch { }
                DialogResult = true;
                return;
            }

            string poolType = GetBAPoolTypeFromIndex(ComboBAPoolType?.SelectedIndex ?? 0);

            string threeStarCat = ComboBAThreeStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(threeStarCat))
            {
                CustomMessageBox.Show(this, "请选择“3星学生”分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string twoStarCat = ComboBATwoStar?.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(twoStarCat))
            {
                CustomMessageBox.Show(this, "请选择“2星学生”分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (threeStarCat == twoStarCat)
            {
                CustomMessageBox.Show(this, "3星学生分类和2星学生分类不能相同！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_categoryMap == null ||
                !_categoryMap.TryGetValue(threeStarCat, out var threeList) || threeList == null ||
                !_categoryMap.TryGetValue(twoStarCat, out var twoList) || twoList == null)
            {
                CustomMessageBox.Show(this, "无法读取所选分类下的对象，请检查卡池内容。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int threeStarCount = threeList.Count;
            int twoStarCount = twoList.Count;

            if (threeStarCount < 2)
            {
                CustomMessageBox.Show(this, $"“{threeStarCat}”下至少需要 2 个对象。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (twoStarCount < 2)
            {
                CustomMessageBox.Show(this, $"“{twoStarCat}”下至少需要 2 个对象。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool has1Star = _categoryMap.Any(kvp =>
                kvp.Key != threeStarCat &&
                kvp.Key != twoStarCat &&
                kvp.Value != null && kvp.Value.Count > 0);
            if (!has1Star)
            {
                CustomMessageBox.Show(this, "卡池中不存在可用的“1星”分类，无法启用高级规则。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseBAProb(TxtBAThreeStarProb.Text, "3星学生总概率", out double threeStarProb)) return;
            if (!TryParseBAProb(TxtBATwoStarProb.Text, "2星学生总概率", out double twoStarProb)) return;

            if (threeStarProb + twoStarProb > 1.0 + 1e-9)
            {
                CustomMessageBox.Show(this, "3星学生总概率 + 2星学生总概率 不能超过 1（100%）！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParsePositiveInt(TxtBA2StarPity.Text, "2星保底抽数", out int pity)) return;

            string up3Star = "";
            string up2Star = "";
            string mainUp3 = "";
            string subUp3 = "";

            double up3Prob = 0;
            double subUp3Prob = 0;
            double up2Prob = 0;

            if (poolType == "限时招募")
            {
                up3Star = ComboBAUp3Star?.SelectedItem?.ToString() ?? "";
                if (string.IsNullOrEmpty(up3Star))
                {
                    CustomMessageBox.Show(this, "请选择 UP3星学生。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!threeList.Contains(up3Star))
                {
                    CustomMessageBox.Show(this, "UP3星学生必须属于 3星学生分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!TryParseBAProb(TxtBAUp3StarProb.Text, "UP3星角色概率", out up3Prob)) return;
                if (up3Prob > threeStarProb + 1e-9)
                {
                    CustomMessageBox.Show(this, $"UP3星角色概率（{up3Prob:0.####}）不能超过 3星学生总概率（{threeStarProb:0.####}）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                up2Star = GetSelectedUp2Star() ?? "";

                if (!string.IsNullOrEmpty(up2Star))
                {
                    if (!twoList.Contains(up2Star))
                    {
                        CustomMessageBox.Show(this, "UP2星学生必须属于 2星学生分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (!TryParseBAProb(TxtBAUp2StarProb.Text, "UP2星角色概率", out up2Prob)) return;
                    if (up2Prob > twoStarProb + 1e-9)
                    {
                        CustomMessageBox.Show(this, $"UP2星角色概率（{up2Prob:0.####}）不能超过 2星学生总概率（{twoStarProb:0.####}）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(TxtBAUp2StarProb.Text))
                    {
                        CustomMessageBox.Show(this, "未选择 UP2星学生时，“UP2星角色概率”必须为空。\n\n请先选择一个 UP2星学生，或清空该输入框后再保存。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    up2Prob = 0;
                }
            }
            else if (poolType == "庆典招募")
            {
                if (_baMainUp3Stars.Count < 1)
                {
                    CustomMessageBox.Show(this, "庆典招募必须选择 1 个主UP3星学生。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (_baMainUp3Stars.Count > 1)
                {
                    CustomMessageBox.Show(this, "主UP3星学生只能选择 1 个！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var up in _baMainUp3Stars.Concat(_baSubUp3Stars))
                {
                    if (!threeList.Contains(up))
                    {
                        CustomMessageBox.Show(this, $"UP3星学生“{up}”不属于 3星分类。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                var overlap = _baMainUp3Stars.Intersect(_baSubUp3Stars).ToList();
                if (overlap.Count > 0)
                {
                    CustomMessageBox.Show(this, "主UP 与 次UP 不能选择同一个学生：\n\n" + string.Join(", ", overlap), "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_baSubUp3Stars.Count > threeStarCount - 2)
                {
                    CustomMessageBox.Show(this, $"次UP 最多选择 {threeStarCount - 2} 个。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_baMainUp3Stars.Count + _baSubUp3Stars.Count > threeStarCount - 1)
                {
                    CustomMessageBox.Show(this, "主UP + 次UP 数量不能超过（3星总数 - 1）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!TryParseBAProb(TxtBAUp3StarProb.Text, "主UP3星角色概率", out up3Prob)) return;

                if (_baSubUp3Stars.Count > 0)
                {
                    if (!TryParseBAProbAllowEmpty(TxtBASubUp3StarProb.Text, "次UP3星角色概率", out subUp3Prob)) return;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(TxtBASubUp3StarProb.Text))
                    {
                        CustomMessageBox.Show(this, "未选择次UP学生时，“次UP3星角色概率”必须为空。\n\n请先选择至少 1 个次UP学生，或清空该输入框后再保存。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    subUp3Prob = 0;
                }

                if (up3Prob + subUp3Prob > threeStarProb + 1e-9)
                {
                    CustomMessageBox.Show(this, $"主UP3星角色概率 + 次UP3星角色概率（{up3Prob + subUp3Prob:0.####}）不能超过 3星学生总概率（{threeStarProb:0.####}）。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                mainUp3 = string.Join(",", _baMainUp3Stars);
                subUp3 = string.Join(",", _baSubUp3Stars);
            }
            else
            {
                up3Prob = 0;
                subUp3Prob = 0;
                up2Prob = 0;
            }

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

            // ★ 保存高亮复选状态
            Settings.EnableHighlightColorsBA = ChkHighlightColorsBA?.IsChecked == true;

            try { SaveSettingsToFile(Settings, Path.Combine(_poolDir, "gacha_settings.ini")); } catch { }
            DialogResult = true;
        }

        // ================================================================
        // 读写 ini
        // ================================================================
        public static GachaSettings LoadSettingsFromFile(string path)
        {
            var settings = new GachaSettings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return settings;

            try
            {
                settings.Enabled = IniHelper.ReadValue("Gacha", "Enabled", "0", path) == "1";
                settings.PresetName = IniHelper.ReadValue("Gacha", "PresetName", "崩坏：星穹铁道", path);
                settings.WarpType = IniHelper.ReadValue("Gacha", "WarpType", "角色活动跃迁", path);
                settings.FiveStarCategory = IniHelper.ReadValue("Gacha", "FiveStarCategory", "", path);
                settings.FiveStarUpObject = IniHelper.ReadValue("Gacha", "FiveStarUpObject", "", path);
                settings.FourStarCategory = IniHelper.ReadValue("Gacha", "FourStarCategory", "", path);
                settings.FourStarUpObjects = IniHelper.ReadValue("Gacha", "FourStarUpObjects", "", path);
                double.TryParse(IniHelper.ReadValue("Gacha", "FiveStarNonUpProb", "0.300", path), out double a); settings.FiveStarNonUpProb = a;
                double.TryParse(IniHelper.ReadValue("Gacha", "FiveStarUpProb", "0.300", path), out double b); settings.FiveStarUpProb = b;
                double.TryParse(IniHelper.ReadValue("Gacha", "FourStarUpProb", "2.550", path), out double c); settings.FourStarUpProb = c;
                double.TryParse(IniHelper.ReadValue("Gacha", "FourStarNonUpProb", "2.550", path), out double d); settings.FourStarNonUpProb = d;
                double.TryParse(IniHelper.ReadValue("Gacha", "ThreeStarProb", "94.300", path), out double e); settings.ThreeStarProb = e;
                int.TryParse(IniHelper.ReadValue("Gacha", "SoftPityStart", "74", path), out int f); settings.SoftPityStart = f;
                double.TryParse(IniHelper.ReadValue("Gacha", "SoftPityIncrease", "0.06", path), out double g); settings.SoftPityIncrease = g;
                int.TryParse(IniHelper.ReadValue("Gacha", "FourStarPity", "10", path), out int h); settings.FourStarPity = h;
                int.TryParse(IniHelper.ReadValue("Gacha", "FiveStarPity", "90", path), out int i); settings.FiveStarPity = i;
                settings.EnableStarColor = IniHelper.ReadValue("Gacha", "EnableStarColor", "0", path) == "1";

                // ★ 读取 HSR 高亮
                settings.EnableHighlightColors = IniHelper.ReadValue("Gacha", "EnableHighlightColors", "0", path) == "1";

                settings.BAPoolType = IniHelper.ReadValue("GachaBA", "PoolType", "常驻招募", path);
                settings.BA3StarCategory = IniHelper.ReadValue("GachaBA", "ThreeStarCategory", "", path);
                settings.BA2StarCategory = IniHelper.ReadValue("GachaBA", "TwoStarCategory", "", path);
                double.TryParse(IniHelper.ReadValue("GachaBA", "ThreeStarProb", "0.03", path), out double ba3); settings.BA3StarProb = ba3;
                double.TryParse(IniHelper.ReadValue("GachaBA", "TwoStarProb", "0.185", path), out double ba2); settings.BA2StarProb = ba2;
                settings.BAUp3Star = IniHelper.ReadValue("GachaBA", "Up3Star", "", path);
                settings.BAUp2Star = IniHelper.ReadValue("GachaBA", "Up2Star", "", path);
                settings.BAMainUp3Stars = IniHelper.ReadValue("GachaBA", "MainUp3Stars", "", path);
                settings.BASubUp3Stars = IniHelper.ReadValue("GachaBA", "SubUp3Stars", "", path);
                int.TryParse(IniHelper.ReadValue("GachaBA", "TwoStarPity", "10", path), out int bap); settings.BA2StarPity = bap;

                double.TryParse(IniHelper.ReadValue("GachaBA", "Up3StarProb", "0.007", path), out double u3); settings.BAUp3StarProb = u3;
                double.TryParse(IniHelper.ReadValue("GachaBA", "SubUp3StarProb", "0.003", path), out double su3); settings.BASubUp3StarProb = su3;
                double.TryParse(IniHelper.ReadValue("GachaBA", "Up2StarProb", "0.03", path), out double u2); settings.BAUp2StarProb = u2;

                // ★ 读取 BA 高亮
                settings.EnableHighlightColorsBA = IniHelper.ReadValue("GachaBA", "EnableHighlightColors", "0", path) == "1";

                if (settings.BA3StarProb > 1.0) settings.BA3StarProb /= 100.0;
                if (settings.BA2StarProb > 1.0) settings.BA2StarProb /= 100.0;

                if (ContainsGarbledText(settings.PresetName) ||
                    ContainsGarbledText(settings.FiveStarCategory) ||
                    ContainsGarbledText(settings.FourStarCategory) ||
                    ContainsGarbledText(settings.BA3StarCategory) ||
                    ContainsGarbledText(settings.BA2StarCategory))
                {
                    settings.Enabled = false;
                }
            }
            catch { }
            return settings;
        }

        private static bool ContainsGarbledText(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c == '?' || c == '\uFFFD') return true;
            }
            return false;
        }

        public static void SaveSettingsToFile(GachaSettings settings, string path)
        {
            if (settings == null || string.IsNullOrEmpty(path)) return;
            try
            {
                IniHelper.WriteValue("Gacha", "Enabled", settings.Enabled ? "1" : "0", path);
                IniHelper.WriteValue("Gacha", "PresetName", settings.PresetName ?? "", path);
                IniHelper.WriteValue("Gacha", "WarpType", settings.WarpType ?? "角色活动跃迁", path);
                IniHelper.WriteValue("Gacha", "FiveStarCategory", settings.FiveStarCategory ?? "", path);
                IniHelper.WriteValue("Gacha", "FiveStarUpObject", settings.FiveStarUpObject ?? "", path);
                IniHelper.WriteValue("Gacha", "FourStarCategory", settings.FourStarCategory ?? "", path);
                IniHelper.WriteValue("Gacha", "FourStarUpObjects", settings.FourStarUpObjects ?? "", path);
                IniHelper.WriteValue("Gacha", "FiveStarNonUpProb", settings.FiveStarNonUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "FiveStarUpProb", settings.FiveStarUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "FourStarUpProb", settings.FourStarUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "FourStarNonUpProb", settings.FourStarNonUpProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "ThreeStarProb", settings.ThreeStarProb.ToString(), path);
                IniHelper.WriteValue("Gacha", "SoftPityStart", settings.SoftPityStart.ToString(), path);
                IniHelper.WriteValue("Gacha", "SoftPityIncrease", settings.SoftPityIncrease.ToString(), path);
                IniHelper.WriteValue("Gacha", "FourStarPity", settings.FourStarPity.ToString(), path);
                IniHelper.WriteValue("Gacha", "FiveStarPity", settings.FiveStarPity.ToString(), path);
                IniHelper.WriteValue("Gacha", "EnableStarColor", settings.EnableStarColor ? "1" : "0", path);

                // ★ 保存 HSR 高亮
                IniHelper.WriteValue("Gacha", "EnableHighlightColors", settings.EnableHighlightColors ? "1" : "0", path);

                IniHelper.WriteValue("GachaBA", "PoolType", settings.BAPoolType ?? "常驻招募", path);
                IniHelper.WriteValue("GachaBA", "ThreeStarCategory", settings.BA3StarCategory ?? "", path);
                IniHelper.WriteValue("GachaBA", "TwoStarCategory", settings.BA2StarCategory ?? "", path);
                IniHelper.WriteValue("GachaBA", "ThreeStarProb", settings.BA3StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "TwoStarProb", settings.BA2StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "Up3Star", settings.BAUp3Star ?? "", path);
                IniHelper.WriteValue("GachaBA", "Up2Star", settings.BAUp2Star ?? "", path);
                IniHelper.WriteValue("GachaBA", "MainUp3Stars", settings.BAMainUp3Stars ?? "", path);
                IniHelper.WriteValue("GachaBA", "SubUp3Stars", settings.BASubUp3Stars ?? "", path);
                IniHelper.WriteValue("GachaBA", "TwoStarPity", settings.BA2StarPity.ToString(), path);

                IniHelper.WriteValue("GachaBA", "Up3StarProb", settings.BAUp3StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "SubUp3StarProb", settings.BASubUp3StarProb.ToString(), path);
                IniHelper.WriteValue("GachaBA", "Up2StarProb", settings.BAUp2StarProb.ToString(), path);

                // ★ 保存 BA 高亮
                IniHelper.WriteValue("GachaBA", "EnableHighlightColors", settings.EnableHighlightColorsBA ? "1" : "0", path);
            }
            catch { }
        }
    }
}