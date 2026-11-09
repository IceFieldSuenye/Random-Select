using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    public partial class NewPoolPage_UC : UserControl, IConfirmLeave, IPageAnimation
    {
        private string _selectedCoverPath = "";
        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);
        private string _editPoolName = null;
        private string _poolAbsoluteDir = "";
        private bool _isSyncing = false;
        private bool _isCoverChanged = false;
        private bool _coverRemoved = false;
        private string _selectedOverlayPath = "";
        private bool _coverBlurBackground;

        public string CurrentTitle { get; private set; } = "卡池管理";

        private string _advancedGachaTargetDir = "";
        private string _snapshotBeforeAdvanced = null;

        private string _stateSnapshotAtOpen = "";
        private string _stateSnapshotAtSave = "";

        private PersonalizationSettings _personalizationSettings = new PersonalizationSettings();
        private string _personalizationTargetDir = "";
        private string _snapshotBeforePersonalization = null;

        private static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg" };

        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".jfif" };

        private Action<AppMessages.OpenPoolEditor> _onOpenPoolEditor;
        private Action<AppMessages.AdvancedGachaFinished> _onAdvancedGachaFinished;
        private Action<AppMessages.PersonalizationFinished> _onPersonalizationFinished;
        private Action<AppMessages.UiOptionsChanged> _onUiOptionsChanged;

        private Action<AppMessages.CoverEditorFinished> _onCoverEditorFinished;

        public NewPoolPage_UC()
        {
            InitializeComponent();

            _onCoverEditorFinished = OnCoverEditorFinished;
            MessageBus.Subscribe(_onCoverEditorFinished);
            PreviewGrid.AutoGeneratingColumn += PreviewGrid_AutoGeneratingColumn;
            ResetForNew();

            _onOpenPoolEditor = OnOpenPoolEditor;
            _onAdvancedGachaFinished = OnAdvancedGachaFinished;
            _onPersonalizationFinished = OnPersonalizationFinished;
            _onUiOptionsChanged = OnUiOptionsChanged;

            MessageBus.Subscribe(_onOpenPoolEditor);
            MessageBus.Subscribe(_onAdvancedGachaFinished);
            MessageBus.Subscribe(_onPersonalizationFinished);
            MessageBus.Subscribe(_onUiOptionsChanged);

            Loaded += (s, e) => ApplyHideOptions();
        }

        /// <summary>是否从主界面右键菜单进入：决定进出动画方向。</summary>
        private bool _fromMainPage = true;

        // 导航栏由 MainWindow 负责从上到下滑入；
        // 主界面进入时页面内容从下往上淡入，其它入口（选项-卡池-编辑）沿用原来的横向滑入。
        public void PlayEnterAnimation(Action onComplete)
        {
            if (_fromMainPage) SlideInAnimationHelper.PlaySlideIn(this, 0, 60, onComplete);
            else SlideInAnimationHelper.PlaySlideIn(this, 60, 0, onComplete);
        }

        public void PlayExitAnimation(Action onComplete)
        {
            if (_fromMainPage) SlideInAnimationHelper.PlaySlideOut(this, 0, 60, onComplete);
            else SlideInAnimationHelper.PlaySlideOut(this, 60, 0, onComplete);
        }

        public string UnsavedLabel => CurrentTitle;

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
                "当前卡池有未保存的修改。\n\n是否放弃修改并离开？");
            return choice == UnsavedChoice.Discard;
        }

        private string BuildStateSnapshot()
        {
            string personalizationFingerprint = "";
            try { personalizationFingerprint = System.Text.Json.JsonSerializer.Serialize(_personalizationSettings); }
            catch { }

            return string.Format("Name={0}|Save={1}|Cover={2}|Removed={3}|Lock={4}|Text={5}|Pers={6}",
                PoolNameBox?.Text ?? "",
                SaveLocationBox?.Text ?? "",
                _selectedCoverPath ?? "",
                _coverRemoved ? "1" : "0",
                (ChkLock?.IsChecked == true) ? "1" : "0",
                InputBox?.Text ?? "",
                personalizationFingerprint);
        }

        private void ResetSnapshots()
        {
            string s = BuildStateSnapshot();
            _stateSnapshotAtOpen = s;
            _stateSnapshotAtSave = s;
            _snapshotBeforeAdvanced = null;
            _snapshotBeforePersonalization = null;
        }

        private void MarkSaved() => _stateSnapshotAtSave = BuildStateSnapshot();

        private void OnOpenPoolEditor(AppMessages.OpenPoolEditor msg)
        {
            if (msg == null) return;
            _fromMainPage = msg.FromMainPage;

            // ★ 安全兜底：只要目标是「加密卡池」，编辑页自己也要确认密码。
            //   主页 / 选项页验证通过时会把 PasswordVerified 置为 true，因此不会二次弹窗；
            //   将来若新增入口却忘记加密码校验，这里会兜住，避免直接看到明文名单。
            if (!string.IsNullOrEmpty(msg.PoolDir) && !msg.PasswordVerified
                && PoolFileOps.IsPoolLocked(msg.PoolDir))
            {
                string lockedName = Path.GetFileName(msg.PoolDir);
                var guard = new PasswordDialog($"修改锁定的卡池“{lockedName}”需要验证密码")
                {
                    Owner = Window.GetWindow(this)
                };

                if (guard.ShowDialog() != true) return;
                if (!CryptoUtils.VerifyPassword(lockedName, guard.Password))
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "密码错误！", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            Dispatcher.Invoke(new Action(() =>
            {
                if (string.IsNullOrEmpty(msg.PoolDir)) ResetForNew();
                else LoadForEdit(msg.PoolDir);
                MessageBus.Publish(new AppMessages.NavigateTo("Pool"));
            }));
        }

        private void OnAdvancedGachaFinished(AppMessages.AdvancedGachaFinished msg)
        {
            if (msg == null) return;
            bool matches = PathEquals(msg.PoolDir, _advancedGachaTargetDir) || PathEquals(msg.PoolDir, _poolAbsoluteDir);
            if (!matches) return;

            Dispatcher.Invoke(new Action(() =>
            {
                _advancedGachaTargetDir = "";
                if (_snapshotBeforeAdvanced != null)
                {
                    _stateSnapshotAtOpen = _snapshotBeforeAdvanced;
                    _snapshotBeforeAdvanced = null;
                }

                if (!msg.Saved) return;
                if (string.IsNullOrEmpty(_poolAbsoluteDir) && !string.IsNullOrEmpty(msg.PoolDir))
                    _poolAbsoluteDir = msg.PoolDir;

                string settingsFile = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                var afterSave = GachaSettings.LoadSettingsFromFile(settingsFile);
                if (afterSave != null && afterSave.Enabled)
                {
                    RemoveProbabilityColumnSilently();
                    UpdateActiveStrategyDisplay();
                    _isSyncing = true;
                    ChkProbability.IsChecked = true;
                    _isSyncing = false;
                    if (App.IsAdvancedVisible)
                        BtnAdvanced.Visibility = Visibility.Visible;
                }
                else
                {
                    TxtActiveStrategy.Visibility = Visibility.Collapsed;
                    BtnAdvanced.Visibility = Visibility.Collapsed;
                    _isSyncing = true;
                    ChkProbability.IsChecked = false;
                    _isSyncing = false;
                }
            }));
        }

        private void OnPersonalizationFinished(AppMessages.PersonalizationFinished msg)
        {
            if (msg == null) return;
            Dispatcher.Invoke(new Action(() =>
            {
                if (_snapshotBeforePersonalization != null)
                {
                    _stateSnapshotAtOpen = _snapshotBeforePersonalization;
                    _snapshotBeforePersonalization = null;
                }
                _personalizationTargetDir = "";
                if (!msg.Ok) return;
                _personalizationSettings = msg.Settings ?? new PersonalizationSettings();
                UpdatePersonalizationButtonDisplay();
            }));
        }

        private void OnUiOptionsChanged(AppMessages.UiOptionsChanged msg)
        {
            Dispatcher.Invoke(new Action(() => ApplyHideOptions()));
        }

        private void ResetForNew()
        {
            _editPoolName = null;
            _poolAbsoluteDir = "";
            _advancedGachaTargetDir = "";
            _snapshotBeforeAdvanced = null;
            _personalizationTargetDir = "";
            _snapshotBeforePersonalization = null;
            _selectedCoverPath = "";
            _isCoverChanged = false;
            _coverRemoved = false;
            _isSyncing = false;

            CurrentTitle = "新建卡池";
            _personalizationSettings = new PersonalizationSettings();

            PoolNameBox.Text = "";
            TxtCoverPath.Text = "未选择文件";
            UpdateCoverButtonText();
            ChkLock.IsChecked = false;
            TxtActiveStrategy.Visibility = Visibility.Collapsed;
            BtnAdvanced.Visibility = Visibility.Collapsed;

            // ★ 只使用现有配置路径作为默认保存位置，不在这里创建任何目录；
            //   真正创建发生在用户点击「创建」按钮时。
            string defaultParent = null;
            try
            {
                defaultParent = App.SearchDirectories.FirstOrDefault(d => App.IsValidDirectoryPath(d));
            }
            catch { }

            if (string.IsNullOrEmpty(defaultParent))
                defaultParent = Path.Combine(App.BaseDir, "SelectBox");
            SaveLocationBox.Text = defaultParent;

            _isSyncing = true;
            ChkProbability.IsChecked = false;
            _isSyncing = false;

            InputBox.Text = "抽取对象 分类\n示例1 1\n示例2 2";
            InputBox_TextChanged(null, null);

            PopulateSaveLocationCombo();
            UpdateRemoveCoverButtonVisibility();
            UpdatePersonalizationButtonDisplay();
            ApplyHideOptions();
            ResetSnapshots();
        }

        private void LoadForEdit(string poolDir)
        {
            _editPoolName = Path.GetFileName(poolDir);
            _poolAbsoluteDir = poolDir;
            _advancedGachaTargetDir = "";
            _snapshotBeforeAdvanced = null;
            _personalizationTargetDir = "";
            _snapshotBeforePersonalization = null;
            _selectedCoverPath = "";
            _isCoverChanged = false;
            _coverRemoved = false;
            _isSyncing = false;

            CurrentTitle = "修改卡池";
            PoolNameBox.Text = _editPoolName;

            string parentDir = Path.GetDirectoryName(poolDir);
            if (App.IsValidDirectoryPath(parentDir)) SaveLocationBox.Text = parentDir;
            else
            {
                string defaultParent = App.SearchDirectories.FirstOrDefault(d =>
                    App.IsValidDirectoryPath(d));
                if (string.IsNullOrEmpty(defaultParent)) defaultParent = Path.Combine(App.BaseDir, "SelectBox");
                SaveLocationBox.Text = defaultParent;
            }

            try { if (PoolArchive.NeedsUnpack(poolDir)) PoolArchive.UnpackFromArchive(poolDir); } catch { }

            bool hasLockFile = File.Exists(Path.Combine(_poolAbsoluteDir, "data.lock"));
            string txtFile = Path.Combine(_poolAbsoluteDir, "data.txt");

            if (File.Exists(txtFile))
            {
                try { InputBox.Text = CryptoUtils.ReadAllTextWithEncoding(txtFile); } catch { }
                ChkLock.IsChecked = hasLockFile;
            }
            else if (hasLockFile)
            {
                try { InputBox.Text = CryptoUtils.DecryptString(File.ReadAllText(Path.Combine(_poolAbsoluteDir, "data.lock"))); }
                catch { InputBox.Text = ""; }
                ChkLock.IsChecked = true;
            }
            else
            {
                ChkLock.IsChecked = false;
                InputBox.Text = "";
            }

            try
            {
                string persFile = Path.Combine(_poolAbsoluteDir, "personalization.json");
                if (File.Exists(persFile))
                {
                    var loaded = PersonalizationSettings.Load(persFile);
                    _personalizationSettings = PoolArchive.ExpandAssetPaths(loaded, _poolAbsoluteDir);
                }
                else _personalizationSettings = new PersonalizationSettings();
            }
            catch { _personalizationSettings = new PersonalizationSettings(); }

            bool hasStrategy = HasActiveGachaStrategy();
            bool hasProbColumn = HasProbabilityColumn();

            if (hasStrategy || hasProbColumn)
            {
                _isSyncing = true;
                ChkProbability.IsChecked = true;
                _isSyncing = false;
                if (App.IsAdvancedVisible)
                    BtnAdvanced.Visibility = Visibility.Visible;
                if (hasStrategy) UpdateActiveStrategyDisplay();
            }
            else
            {
                _isSyncing = true;
                ChkProbability.IsChecked = false;
                _isSyncing = false;
                BtnAdvanced.Visibility = Visibility.Collapsed;
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
            }

            InputBox_TextChanged(null, null);

            TxtCoverPath.Text = "未选择文件";
            UpdateCoverButtonText();
            try
            {
                var covers = PoolCover.GetCoverFiles(_poolAbsoluteDir);
                if (covers.Length > 0)
                {
                    _selectedCoverPath = covers[0];
                    TxtCoverPath.Text = Path.GetFileName(_selectedCoverPath);
                    UpdateCoverButtonText();
                }
            }
            catch { }

            PopulateSaveLocationCombo();
            UpdateRemoveCoverButtonVisibility();
            UpdatePersonalizationButtonDisplay();
            ApplyHideOptions();
            ResetSnapshots();
        }

        private void ApplyHideOptions()
        {
            if (!App.IsProbabilityVisible)
            {
                ChkProbability.Visibility = Visibility.Collapsed;
                // ★ 修复：不再强制勾选概率，也不再重建文本加入概率列
                // 以保持底层数据逻辑和用户原样数据不被篡改，防止未知的校验报错
            }
            else
            {
                ChkProbability.Visibility = Visibility.Visible;
            }

            if (!App.IsAdvancedVisible)
            {
                BtnAdvanced.Visibility = Visibility.Collapsed;
            }
            else if (ChkProbability.IsChecked == true)
            {
                BtnAdvanced.Visibility = Visibility.Visible;
            }
        }

        private void PopulateSaveLocationCombo()
        {
            try
            {
                string currentText = SaveLocationBox.Text;
                SaveLocationBox.Items.Clear();
                foreach (var dir in App.SearchDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    if (!App.IsValidDirectoryPath(dir)) continue;
                    SaveLocationBox.Items.Add(dir);
                }
                SaveLocationBox.Text = currentText;
            }
            catch { }
        }

        private void UpdateRemoveCoverButtonVisibility()
        {
            if (BtnRemoveCover == null) return;
            bool hasCover;
            if (_coverRemoved) hasCover = false;
            else if (!string.IsNullOrEmpty(_selectedCoverPath) && File.Exists(_selectedCoverPath)) hasCover = true;
            else if (!string.IsNullOrEmpty(_poolAbsoluteDir) && Directory.Exists(_poolAbsoluteDir))
            {
                try { hasCover = PoolCover.GetCoverFiles(_poolAbsoluteDir).Length > 0; }
                catch { hasCover = false; }
            }
            else hasCover = false;
            BtnRemoveCover.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RemoveCover_Click(object sender, RoutedEventArgs e)
        {
            var r = CustomMessageBox.Show(Window.GetWindow(this),
                "确定要删除当前封面吗？\n\n删除后请点击“创建/确定”按钮以保存修改。",
                "确认删除封面", MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "删除", noText: "取消", yesIcon: "\uE74D");
            if (r != MessageBoxResult.Yes) return;

            _selectedCoverPath = "";
            _coverRemoved = true;
            _isCoverChanged = true;
            TxtCoverPath.Text = "未选择文件";
            UpdateCoverButtonText();
            UpdateRemoveCoverButtonVisibility();
        }

        private bool HasPersonalizationContent()
        {
            var s = _personalizationSettings;
            if (s == null) return false;

            if (s.SoundMode != SoundMode.None) return true;
            if (s.AnimationMode != AnimationMode.Full) return true;
            if (s.EffectiveBackgroundMode != BackgroundMode.Default) return true;
            if (!string.IsNullOrEmpty(s.BackgroundImage)) return true;
            if (!string.IsNullOrEmpty(s.GlobalSoundFile) && s.SoundMode == SoundMode.Global) return true;
            if (s.Items != null && s.Items.Count > 0) return true;
            return false;
        }

        private void UpdatePersonalizationButtonDisplay()
        {
            if (TxtPersonalizationDisplay == null || TxtPersonalizationIcon == null) return;
            int count = _personalizationSettings?.Items?.Count(i => i != null &&
                        (!string.IsNullOrEmpty(i.ColorHex)
                         || !string.IsNullOrEmpty(i.AvatarFile)
                         || !string.IsNullOrEmpty(i.IllustrationFile)
                         || !string.IsNullOrEmpty(i.SoundFile))) ?? 0;
            bool hasAnyConfig = _personalizationSettings != null &&
                (_personalizationSettings.SoundMode != SoundMode.None
                 || _personalizationSettings.EffectiveBackgroundMode != BackgroundMode.Default
                 || !string.IsNullOrEmpty(_personalizationSettings.BackgroundImage)
                 || _personalizationSettings.AnimationMode != AnimationMode.Full
                 || count > 0);

            if (hasAnyConfig)
            {
                TxtPersonalizationDisplay.Text = count > 0 ? $"已自定义（{count} 项）" : "已自定义";
                TxtPersonalizationIcon.Text = "\uE73E";
            }
            else
            {
                TxtPersonalizationDisplay.Text = "自定义...";
                TxtPersonalizationIcon.Text = "\uE8B0";
            }
        }

        private void BtnPersonalization_Click(object sender, RoutedEventArgs e)
        {
            var dtView = PreviewGrid.ItemsSource as DataView;
            if (dtView == null || dtView.Table.Rows.Count == 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "抽取对象不能为空！请先在下方输入并预览数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DataTable dt = dtView.Table;
            if (dt.Columns.Count == 0) return;
            string objectColumn = dt.Columns[0].ColumnName;

            var objectNames = new List<string>();
            foreach (DataRow row in dt.Rows)
            {
                string name = (row[objectColumn]?.ToString() ?? "").Trim();
                if (!string.IsNullOrEmpty(name) && !objectNames.Contains(name))
                    objectNames.Add(name);
            }

            if (objectNames.Count == 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "抽取对象不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var otherHeaders = new List<string>();
            for (int i = 1; i < dt.Columns.Count; i++)
            {
                string h = dt.Columns[i].ColumnName;
                if (string.Equals(h, "概率", StringComparison.Ordinal)) continue;
                otherHeaders.Add(h);
            }

            var otherValues = new Dictionary<string, Dictionary<string, string>>();
            foreach (DataRow row in dt.Rows)
            {
                string name = (row[objectColumn]?.ToString() ?? "").Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!otherValues.ContainsKey(name)) otherValues[name] = new Dictionary<string, string>();
                for (int i = 1; i < dt.Columns.Count; i++)
                {
                    string h = dt.Columns[i].ColumnName;
                    if (string.Equals(h, "概率", StringComparison.Ordinal)) continue;
                    otherValues[name][h] = (row[h]?.ToString() ?? "").Trim();
                }
            }

            string targetPoolDir = _poolAbsoluteDir;
            if (string.IsNullOrEmpty(targetPoolDir))
            {
                string poolName = (PoolNameBox.Text ?? "").Trim();
                string saveLocation = (SaveLocationBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(poolName) && !string.IsNullOrEmpty(saveLocation) && App.IsValidDirectoryPath(saveLocation))
                    targetPoolDir = Path.Combine(EnsureSelectBoxSuffix(saveLocation), poolName);
                else targetPoolDir = Path.Combine(App.AppDataDir, "_temp_personalization");
            }

            _personalizationTargetDir = targetPoolDir;
            _snapshotBeforePersonalization = _stateSnapshotAtOpen;
            _stateSnapshotAtOpen = BuildStateSnapshot();

            MessageBus.Publish(new AppMessages.OpenPersonalization(
                objectNames, otherHeaders, otherValues,
                _personalizationSettings?.Clone() ?? new PersonalizationSettings(),
                targetPoolDir));
            MessageBus.Publish(new AppMessages.NavigateTo("Personalization"));
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "";
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        private static bool PathEquals(string a, string b) => string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

        private static string EnsureSelectBoxSuffix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += Path.DirectorySeparatorChar;
                if (string.IsNullOrEmpty(trimmed)) return path;
                string leaf = Path.GetFileName(trimmed);
                if (string.Equals(leaf, "SelectBox", StringComparison.OrdinalIgnoreCase)) return trimmed;
                return Path.Combine(trimmed, "SelectBox");
            }
            catch { return path; }
        }

        private static void EnsureSearchDirectory(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!App.IsValidDirectoryPath(dir)) return;
            try
            {
                string normalized = NormalizePath(dir);
                foreach (var existing in App.SearchDirectories)
                    if (PathEquals(existing, normalized)) return;
                App.SearchDirectories.Add(dir);
                App.SaveSettings();
            }
            catch { }
        }

        private static bool TryMoveDirectory(string sourceDir, string destDir, out string errorMsg)
        {
            errorMsg = null;
            try { Directory.Move(sourceDir, destDir); return true; } catch { }
            bool copyOk = false;
            try { CopyDirectoryRecursive(sourceDir, destDir); copyOk = true; }
            catch (Exception ex)
            {
                try { if (Directory.Exists(destDir)) Directory.Delete(destDir, true); } catch { }
                errorMsg = "移动卡池失败，请检查磁盘空间或目录权限。\n\n详细信息：" + ex.Message;
                return false;
            }
            if (copyOk) { try { Directory.Delete(sourceDir, true); } catch { } }
            return true;
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
            foreach (var subDir in Directory.GetDirectories(sourceDir))
                CopyDirectoryRecursive(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }

        private void BrowseSaveLocation_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "选择卡池的保存位置", Multiselect = false };
            string cur = (SaveLocationBox.Text ?? "").Trim();
            if (Directory.Exists(cur)) { try { dlg.InitialDirectory = cur; } catch { } }
            if (dlg.ShowDialog() != true) return;

            string newDir = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(newDir)) return;
            newDir = EnsureSelectBoxSuffix(newDir);

            if (!App.IsValidDirectoryPath(newDir))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "所选目录无效。\n\n" + newDir, "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!App.IsDirectoryWritable(newDir))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "所选目录不可写，请重新选择。\n\n" + newDir, "目录不可写", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SaveLocationBox.Text = newDir;
        }

        private bool HasActiveGachaStrategy()
        {
            try
            {
                if (string.IsNullOrEmpty(_poolAbsoluteDir)) return false;
                string file = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                if (!File.Exists(file)) return false;
                var s = GachaSettings.LoadSettingsFromFile(file);
                return s != null && s.Enabled;
            }
            catch { return false; }
        }

        private void UpdateActiveStrategyDisplay()
        {
            try
            {
                if (string.IsNullOrEmpty(_poolAbsoluteDir)) return;
                string file = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                if (File.Exists(file))
                {
                    var s = GachaSettings.LoadSettingsFromFile(file);
                    if (s != null && s.Enabled)
                    {
                        TxtActiveStrategy.Text = $"已启用抽卡规则：{s.PresetName}";
                        TxtActiveStrategy.Visibility = Visibility.Visible;
                        return;
                    }
                }
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
            }
            catch { TxtActiveStrategy.Visibility = Visibility.Collapsed; }
        }

        private bool HasProbabilityColumn()
        {
            var lines = InputBox.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;
            var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
            return firstLineParts.Contains("概率");
        }

        private Dictionary<string, List<string>> BuildCategoryMap()
        {
            var map = new Dictionary<string, List<string>>();
            var dtView = PreviewGrid.ItemsSource as DataView;
            if (dtView == null) return map;
            DataTable dt = dtView.Table;
            if (!dt.Columns.Contains("分类")) return map;
            string objectColumn = dt.Columns[0].ColumnName;
            foreach (DataRow row in dt.Rows)
            {
                string category = row["分类"]?.ToString() ?? "";
                string obj = row[objectColumn]?.ToString() ?? "";
                if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(obj)) continue;
                if (!map.ContainsKey(category)) map[category] = new List<string>();
                if (!map[category].Contains(obj)) map[category].Add(obj);
            }
            return map;
        }

        private string LoadOriginalPoolContent()
        {
            try
            {
                if (string.IsNullOrEmpty(_poolAbsoluteDir)) return null;
                string lockFile = Path.Combine(_poolAbsoluteDir, "data.lock");
                string txtFile = Path.Combine(_poolAbsoluteDir, "data.txt");
                if (File.Exists(lockFile)) return CryptoUtils.DecryptString(File.ReadAllText(lockFile));
                else if (File.Exists(txtFile)) return CryptoUtils.ReadAllTextWithEncoding(txtFile);
            }
            catch { }
            return null;
        }

        private string NormalizeContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        }

        private bool HasPoolContentChanged()
        {
            string original = LoadOriginalPoolContent();
            if (original == null) return false;
            return NormalizeContent(StripProbColumn(original)) != NormalizeContent(StripProbColumn(InputBox.Text));
        }

        private string StripProbColumn(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            try
            {
                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
                if (lines.Count == 0) return content;
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                int probIndex = Array.IndexOf(firstLineParts, "概率");
                if (probIndex < 0) return content;
                var newLines = new List<string>();
                for (int i = 0; i < lines.Count; i++)
                {
                    var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (probIndex >= 0 && probIndex < parts.Count) parts.RemoveAt(probIndex);
                    newLines.Add(string.Join(" ", parts));
                }
                return string.Join("\n", newLines);
            }
            catch { return content; }
        }

        private static string NormalizeObjectName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string s = value.Normalize(NormalizationForm.FormC).Trim();
            return s.Replace("\u200B", "").Replace("\uFEFF", "");
        }

        /// <summary>
        /// 检查表格内容里有没有会让数据错位的符号（|、Tab、空格、全角空格、换行）。
        /// 卡池数据用空格分隔列、历史记录用 | 和 Tab 分隔字段，这些符号出现在内容里会导致
        /// 历史记录、表格错位或整行显示异常，所以在保存前拦下来。
        /// </summary>
        private bool ValidateIllegalSymbols(DataTable dt)
        {
            if (PoolTextRules.TryBuildIssueReport(dt, out string report)) return true;

            CustomMessageBox.Show(Window.GetWindow(this), report,
                "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool ValidateDuplicateObjects(DataTable dt)
        {
            if (dt == null || dt.Columns.Count == 0) return true;
            string objectColumn = dt.Columns[0].ColumnName;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();
            foreach (DataRow row in dt.Rows)
            {
                string obj = NormalizeObjectName(row[objectColumn]?.ToString());
                if (string.IsNullOrEmpty(obj)) continue;
                if (!seen.Add(obj)) { if (!duplicates.Contains(obj)) duplicates.Add(obj); }
            }
            if (duplicates.Count == 0) return true;

            // 完整列出所有重复对象，避免用户反复查找
            string listText = string.Join("\n", duplicates.Select(x => "  • " + x));

            CustomMessageBox.Show(Window.GetWindow(this),
                $"第一列“{objectColumn}”中存在重复的对象，请修改后再保存：\n\n" + listText,
                "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool ValidateCategoryColumn(DataTable dt)
        {
            if (dt == null || !dt.Columns.Contains("分类")) return true;
            string objectColumn = dt.Columns[0].ColumnName;
            var missing = new List<string>();
            foreach (DataRow row in dt.Rows)
            {
                string cat = (row["分类"]?.ToString() ?? "").Trim();
                if (!string.IsNullOrEmpty(cat)) continue;
                string obj = (row[objectColumn]?.ToString() ?? "").Trim();
                if (string.IsNullOrEmpty(obj)) continue;
                missing.Add(obj);
            }
            if (missing.Count == 0) return true;

            string listText = string.Join("\n", missing.Select(x => "  • " + x));

            var r = CustomMessageBox.Show(Window.GetWindow(this),
                "以下对象未填写“分类”列，启用高级规则时将被舍弃：\n\n" + listText + "\n\n是否仍要继续保存？",
                "提示", MessageBoxButton.YesNo, MessageBoxImage.Warning, yesText: "继续保存", noText: "取消", yesIcon: "\uE73E");
            return r == MessageBoxResult.Yes;
        }

        private bool ValidateAdvancedSettingsAfterEdit(Dictionary<string, List<string>> categoryMap)
        {
            string gachaFile = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
            if (!File.Exists(gachaFile)) return true;
            var settings = GachaSettings.LoadSettingsFromFile(gachaFile);
            if (settings == null || !settings.Enabled) return true;
            if (!HasPoolContentChanged()) return true;

            string reason = GetAdvancedSettingsInvalidReason(settings, categoryMap);
            if (reason != null)
            {
                var r = CustomMessageBox.Show(Window.GetWindow(this),
                    "卡池内容已修改，现有高级规则不再适用：\n\n  " + reason + "\n\n是否重置高级规则？",
                    "高级规则失效", MessageBoxButton.YesNo, MessageBoxImage.Warning, yesText: "重置规则", noText: "取消", yesIcon: "\uE72C");
                if (r != MessageBoxResult.Yes) return false;

                try { File.Delete(gachaFile); } catch { }
                try
                {
                    string stateFile = Path.Combine(_poolAbsoluteDir, "gacha_state.ini");
                    if (File.Exists(stateFile)) File.Delete(stateFile);
                }
                catch { }

                TxtActiveStrategy.Visibility = Visibility.Collapsed;
                BtnAdvanced.Visibility = Visibility.Collapsed;
                _isSyncing = true;
                ChkProbability.IsChecked = false;
                _isSyncing = false;
                return true;
            }

            var r2 = CustomMessageBox.Show(Window.GetWindow(this),
                "卡池内容已被修改。由于当前卡池已启用高级规则，原有规则配置可能与新数据不匹配。\n\n是否仍要保存当前修改？",
                "高级规则提醒", MessageBoxButton.YesNo, MessageBoxImage.Warning, yesText: "继续保存", noText: "取消", yesIcon: "\uE73E");
            return r2 == MessageBoxResult.Yes;
        }

        private string GetAdvancedSettingsInvalidReason(GachaSettings settings, Dictionary<string, List<string>> categoryMap)
        {
            if (settings == null) return null;
            if (settings.PresetName == "蔚蓝档案（旧）") return GetBAInvalidReason(settings, categoryMap);
            return GetHSRInvalidReason(settings, categoryMap);
        }

        private string GetHSRInvalidReason(GachaSettings settings, Dictionary<string, List<string>> categoryMap)
        {
            if (string.IsNullOrEmpty(settings.FiveStarCategory) || !categoryMap.ContainsKey(settings.FiveStarCategory))
                return $"5星角色分类“{settings.FiveStarCategory}”不存在或已被清空。";
            if (categoryMap[settings.FiveStarCategory].Count < 2)
                return $"5星角色分类“{settings.FiveStarCategory}”下的对象少于 2 个。";
            if (!string.IsNullOrEmpty(settings.FiveStarUpObject) && !categoryMap[settings.FiveStarCategory].Contains(settings.FiveStarUpObject))
                return $"UP5星角色“{settings.FiveStarUpObject}”已不存在。";
            if (string.IsNullOrEmpty(settings.FourStarCategory) || !categoryMap.ContainsKey(settings.FourStarCategory))
                return $"4星角色分类“{settings.FourStarCategory}”不存在或已被清空。";
            if (categoryMap[settings.FourStarCategory].Count < 2)
                return $"4星角色分类“{settings.FourStarCategory}”下的对象少于 2 个。";

            var up4 = (settings.FourStarUpObjects ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            foreach (var up in up4)
                if (!categoryMap[settings.FourStarCategory].Contains(up))
                    return $"UP4星角色“{up}”已不存在。";
            if (up4.Count > categoryMap[settings.FourStarCategory].Count - 1)
                return $"UP4星角色数量超过了当前4星分类下的对象总数 - 1。";

            bool has3Star = categoryMap.Any(kvp =>
                kvp.Key != settings.FiveStarCategory && kvp.Key != settings.FourStarCategory && kvp.Value.Count > 0);
            if (!has3Star) return "卡池中不存在可用的“3星”分类。";
            return null;
        }

        private string GetBAInvalidReason(GachaSettings settings, Dictionary<string, List<string>> categoryMap)
        {
            if (string.IsNullOrEmpty(settings.BA3StarCategory) || !categoryMap.ContainsKey(settings.BA3StarCategory))
                return $"3星学生分类“{settings.BA3StarCategory}”不存在或已被清空。";
            if (categoryMap[settings.BA3StarCategory].Count < 2)
                return $"3星学生分类“{settings.BA3StarCategory}”下的对象少于 2 个。";
            if (string.IsNullOrEmpty(settings.BA2StarCategory) || !categoryMap.ContainsKey(settings.BA2StarCategory))
                return $"2星学生分类“{settings.BA2StarCategory}”不存在或已被清空。";
            if (categoryMap[settings.BA2StarCategory].Count < 2)
                return $"2星学生分类“{settings.BA2StarCategory}”下的对象少于 2 个。";

            var threeList = categoryMap[settings.BA3StarCategory];
            if (settings.BAPoolType == "限时招募")
            {
                if (!string.IsNullOrEmpty(settings.BAUp3Star) && !threeList.Contains(settings.BAUp3Star))
                    return $"UP3星学生“{settings.BAUp3Star}”已不存在。";
            }
            else if (settings.BAPoolType == "庆典招募")
            {
                var mainUps = (settings.BAMainUp3Stars ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                var subUps = (settings.BASubUp3Stars ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                foreach (var up in mainUps.Concat(subUps))
                    if (!threeList.Contains(up)) return $"UP3星学生“{up}”已不存在。";
            }

            bool has1Star = categoryMap.Any(kvp =>
                kvp.Key != settings.BA3StarCategory && kvp.Key != settings.BA2StarCategory && kvp.Value.Count > 0);
            if (!has1Star) return "卡池中不存在可用的“1星”分类。";
            return null;
        }

        private void BtnAdvanced_Click(object sender, RoutedEventArgs e)
        {
            if (!App.IsAdvancedVisible) return;

            var map = BuildCategoryMap();
            if (map.Count == 0)
            {
                // ★ 不再用页面里的常驻小提示，改成点击时校验 + 弹窗，并直接提供「立即添加」
                var choice = CustomMessageBox.ShowCustom(Window.GetWindow(this),
                    "使用高级抽卡规则需要一个「分类」列（用来区分 5 星 / 4 星等）。\n\n" +
                    "是否立即为当前卡池添加「分类」列？添加后请为每一行填写分类再点「高级」。",
                    "缺少“分类”列", "立即添加", "\uE710", "取消", "\uE711", MessageBoxImage.Warning);

                if (choice != MessageBoxResult.Yes) return;
                if (!AddCategoryColumnToInput()) return;

                map = BuildCategoryMap();
                if (map.Count == 0)
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "已添加「分类」列，请为每一行填写分类（例如 5、4、3）后再点「高级」。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string targetPoolDir = _poolAbsoluteDir;
            if (string.IsNullOrEmpty(targetPoolDir))
            {
                string poolName = (PoolNameBox.Text ?? "").Trim();
                string saveLocation = (SaveLocationBox.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(poolName) && !string.IsNullOrEmpty(saveLocation) && App.IsValidDirectoryPath(saveLocation))
                    targetPoolDir = Path.Combine(EnsureSelectBoxSuffix(saveLocation), poolName);
                else targetPoolDir = Path.Combine(App.AppDataDir, "_temp_gacha");
            }

            _advancedGachaTargetDir = targetPoolDir;
            _snapshotBeforeAdvanced = _stateSnapshotAtOpen;
            _stateSnapshotAtOpen = BuildStateSnapshot();

            string settingsFile = Path.Combine(targetPoolDir, "gacha_settings.ini");
            var initial = File.Exists(settingsFile) ? GachaSettings.LoadSettingsFromFile(settingsFile) : null;

            bool hasColors = _personalizationSettings != null && _personalizationSettings.Items != null &&
                             _personalizationSettings.Items.Any(x => !string.IsNullOrEmpty(x.ColorHex));

            MessageBus.Publish(new AppMessages.OpenAdvancedGacha(targetPoolDir, map, initial, hasColors));
            MessageBus.Publish(new AppMessages.NavigateTo("AdvancedGacha"));
        }

        private void RemoveProbabilityColumnSilently()
        {
            try
            {
                var lines = InputBox.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (lines.Count == 0) return;
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                if (!firstLineParts.Contains("概率")) return;
                int probIndex = Array.IndexOf(firstLineParts, "概率");
                var newLines = new List<string>();
                for (int i = 0; i < lines.Count; i++)
                {
                    var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (probIndex >= 0 && probIndex < parts.Count) parts.RemoveAt(probIndex);
                    newLines.Add(string.Join(" ", parts));
                }
                _isSyncing = true;
                InputBox.Text = string.Join(Environment.NewLine, newLines);
                _isSyncing = false;
                InputBox_TextChanged(null, null);
            }
            catch { }
        }

        /// <summary>「添加封面 / 修改封面」：打开封面编辑页（带页面切换动画）。</summary>
        private void EditCover_Click(object sender, RoutedEventArgs e)
        {
            MessageBus.Publish(new AppMessages.OpenCoverEditor(_poolAbsoluteDir ?? "", _selectedCoverPath ?? ""));
            MessageBus.Publish(new AppMessages.NavigateTo("CoverEditor"));
        }

        private void OnCoverEditorFinished(AppMessages.CoverEditorFinished msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (msg == null || !msg.Saved || string.IsNullOrEmpty(msg.CoverPath)) return;

                _selectedCoverPath = msg.CoverPath;
                _selectedOverlayPath = msg.CoverPath;   // 批注覆盖层（透明 PNG）
                _coverBlurBackground = msg.BlurBackground;
                _coverRemoved = false;
                _isCoverChanged = true;
                TxtCoverPath.Text = Path.GetFileName(_selectedCoverPath);
                UpdateCoverButtonText();
                UpdateCoverButtonText();
                MessageBus.Publish(new AppMessages.NavigateBack());
            }));
        }

        /// <summary>按当前是否已有封面切换按钮文案：添加封面 / 修改封面。</summary>
        private void UpdateCoverButtonText()
        {
            try
            {
                bool hasCover = !_coverRemoved && !string.IsNullOrEmpty(_selectedCoverPath) && File.Exists(_selectedCoverPath);
                if (TxtEditCoverText != null) TxtEditCoverText.Text = hasCover ? "修改封面" : "添加封面";
                if (BtnRemoveCover != null)
                    BtnRemoveCover.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }
        private void SelectCover_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|所有文件|*.*",
                FilterIndex = 1
            };
            if (ofd.ShowDialog() == true)
            {
                string ext = Path.GetExtension(ofd.FileName).ToLowerInvariant();
                if (!VideoExts.Contains(ext) && !ImageExts.Contains(ext))
                {
                    CustomMessageBox.Show(Window.GetWindow(this), $"不支持的文件类型：{ext}\n\n封面未被修改。", "不支持的格式", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _selectedCoverPath = ofd.FileName;
                TxtCoverPath.Text = Path.GetFileName(_selectedCoverPath);
                UpdateCoverButtonText();
                _isCoverChanged = true;
                _coverRemoved = false;
                UpdateRemoveCoverButtonVisibility();
            }
        }

        private void SelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "文本文件|*.txt|所有文件|*.*" };
            if (ofd.ShowDialog() == true)
            {
                try { InputBox.Text = CryptoUtils.ReadAllTextWithEncoding(ofd.FileName); }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "读取文件失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ChkProbability_Checked(object sender, RoutedEventArgs e)
        {
            if (App.IsAdvancedVisible)
                BtnAdvanced.Visibility = Visibility.Visible;
            if (_isSyncing) return;
            RebuildTextWithProbability(false);
        }

        private void ChkProbability_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isSyncing) return;

            if (HasActiveGachaStrategy())
            {
                _isSyncing = true;
                ChkProbability.IsChecked = true;
                _isSyncing = false;
                CustomMessageBox.Show(Window.GetWindow(this),
                    "当前卡池已启用高级抽卡规则。\n\n如需禁用，请点击“高级”按钮进入高级设置界面，取消勾选“启用”后点击确定。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!HasProbabilityColumn())
            {
                BtnAdvanced.Visibility = Visibility.Collapsed;
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
                return;
            }

            var result = CustomMessageBox.Show(Window.GetWindow(this), "是否同时删除‘概率’列？", "提示",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "删除", noText: "保留", yesIcon: "\uE74D", noIcon: "\uE73E");
            if (result == MessageBoxResult.Yes)
            {
                RebuildTextWithProbability(true);
                BtnAdvanced.Visibility = Visibility.Collapsed;
                TxtActiveStrategy.Visibility = Visibility.Collapsed;
            }
            else
            {
                _isSyncing = true;
                ChkProbability.IsChecked = true;
                _isSyncing = false;
                if (App.IsAdvancedVisible)
                    BtnAdvanced.Visibility = Visibility.Visible;
            }
        }

        private void RebuildTextWithProbability(bool removeProbColumn)
        {
            if (_isSyncing) return;
            var lines = InputBox.Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count == 0) return;
            bool textChanged = false;

            if (ChkProbability.IsChecked == true)
            {
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                if (!firstLineParts.Contains("概率")) { lines[0] = lines[0].TrimEnd() + " 概率"; textChanged = true; }
            }
            else if (removeProbColumn)
            {
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                if (firstLineParts.Contains("概率"))
                {
                    var newLines = new List<string>();
                    int probIndex = Array.IndexOf(firstLineParts, "概率");
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        if (probIndex >= 0 && probIndex < parts.Count) parts.RemoveAt(probIndex);
                        newLines.Add(string.Join(" ", parts));
                    }
                    lines = newLines;
                    textChanged = true;
                }
            }

            if (textChanged)
            {
                _isSyncing = true;
                InputBox.Text = string.Join(Environment.NewLine, lines);
                _isSyncing = false;
                InputBox_TextChanged(null, null);
            }
        }

        /// <summary>
        /// 在输入框里补一列「分类」，每行默认填 1。
        /// 只在「点击高级但还没有分类列」时调用，改完立即重建预览表格。
        /// </summary>
        private bool AddCategoryColumnToInput()
        {
            try
            {
                var lines = (InputBox.Text ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (lines.Count == 0)
                {
                    // 完全空的卡池：给一个带表头的最小结构
                    InputBox.Text = "抽取对象 分类";
                    InputBox_TextChanged(null, null);
                    return true;
                }

                var sb = new StringBuilder();
                sb.Append(lines[0].TrimEnd()).Append(" 分类");
                for (int i = 1; i < lines.Count; i++)
                {
                    string line = lines[i].TrimEnd();
                    if (line.Length == 0) continue;
                    sb.Append("\r\n").Append(line);
                }

                InputBox.Text = sb.ToString();
                InputBox_TextChanged(null, null);
                return true;
            }
            catch { return false; }
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncing) return;
            _isSyncing = true;
            try
            {
                string raw = InputBox.Text;
                var lines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).ToList();
                DataTable dt = new DataTable();
                if (lines.Count == 0) { PreviewGrid.ItemsSource = null; return; }
                var firstLineParts = lines[0].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                int columnCount = firstLineParts.Length;
                if (columnCount == 0) { PreviewGrid.ItemsSource = null; return; }

                for (int i = 0; i < columnCount; i++)
                {
                    string header = firstLineParts[i];
                    if (string.IsNullOrEmpty(header)) header = $"列{i + 1}";
                    if (dt.Columns.Contains(header)) header = $"{header}_{i}";
                    dt.Columns.Add(header, typeof(string));
                }

                for (int i = 1; i < lines.Count; i++)
                {
                    var parts = lines[i].Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    DataRow row = dt.NewRow();
                    for (int j = 0; j < columnCount; j++) row[j] = j < parts.Length ? parts[j] : "";
                    dt.Rows.Add(row);
                }
                PreviewGrid.ItemsSource = dt.DefaultView;
                Dispatcher.BeginInvoke(new Action(ResetPreviewGridColumnWidths), DispatcherPriority.Background);
            }
            catch { }
            finally { _isSyncing = false; }
        }

        private void PreviewGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (_isSyncing) return;
            if (e.EditAction != DataGridEditAction.Commit) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isSyncing) return;
                _isSyncing = true;
                try
                {
                    try { PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true); } catch { }
                    try { PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }
                    var dtView = PreviewGrid.ItemsSource as DataView;
                    if (dtView == null) return;
                    foreach (DataRowView drv in dtView) { if (drv.IsEdit) drv.EndEdit(); }

                    DataTable dt = dtView.Table;
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine(string.Join(" ", dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName)));
                    foreach (DataRow row in dt.Rows)
                        sb.AppendLine(string.Join(" ", row.ItemArray.Select(i => i?.ToString() ?? "")));

                    string newText = sb.ToString().Trim();
                    if (!string.Equals(InputBox.Text, newText, StringComparison.Ordinal))
                        InputBox.Text = newText;
                    ResetPreviewGridColumnWidths();
                }
                catch { }
                finally { _isSyncing = false; }
            }), DispatcherPriority.Background);
        }

        private void PreviewGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            try
            {
                e.Column.MinWidth = 80;
                e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
            }
            catch { }
        }

        private void ResetPreviewGridColumnWidths()
        {
            try
            {
                if (PreviewGrid == null || PreviewGrid.Columns == null) return;
                foreach (var column in PreviewGrid.Columns)
                {
                    column.Width = new DataGridLength(0);
                    column.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
                }
            }
            catch { }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => MessageBus.Publish(new AppMessages.NavigateBack());

        private bool TryParseProbabilityValue(string raw, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) { value = 0; return true; }
            string s = raw.Trim();
            bool hasPercent = s.EndsWith("%");
            if (hasPercent) s = s.TrimEnd('%').Trim();
            if (!double.TryParse(s, out double v)) return false;
            if (hasPercent) v = v / 100.0;
            value = v;
            return true;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string poolName = (PoolNameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(poolName))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请输入卡池名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (poolName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "名称包含非法符号，请修改后再保存。\n\n以下符号不能使用：\n" + PoolTextRules.InvalidFileNameCharsText,
                    "名称不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 先提交正在编辑的单元格，确保查重与保存使用的是用户刚输入的名称
            try { PreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true); } catch { }
            try { PreviewGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch { }

            var dtView = PreviewGrid.ItemsSource as DataView;
            if (dtView == null || dtView.Table.Rows.Count == 0)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "抽取对象不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string saveLocation = (SaveLocationBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(saveLocation))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请选择保存位置！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            saveLocation = EnsureSelectBoxSuffix(saveLocation);
            if (!App.IsValidDirectoryPath(saveLocation))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "保存位置无效：\n\n" + saveLocation, "保存位置无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!App.IsDirectoryWritable(saveLocation))
            {
                CustomMessageBox.Show(Window.GetWindow(this), "保存位置不可写：\n\n" + saveLocation, "保存位置不可写", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!ValidateIllegalSymbols(dtView.Table)) return;

            if (!ValidateDuplicateObjects(dtView.Table)) return;

            if (ChkProbability.IsChecked == true && !HasActiveGachaStrategy() && HasProbabilityColumn() && dtView.Table.Columns.Contains("概率"))
            {
                DataTable dt = dtView.Table;
                double sum = 0;
                foreach (DataRow row in dt.Rows)
                {
                    string probStr = row["概率"]?.ToString() ?? "";
                    if (!TryParseProbabilityValue(probStr, out double prob))
                    {
                        CustomMessageBox.Show(Window.GetWindow(this), $"概率值“{probStr}”不是有效的数字！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    sum += prob;
                }
                if (Math.Abs(sum - 1.0) > 0.001)
                {
                    CustomMessageBox.Show(Window.GetWindow(this), $"概率之和必须等于 1（或 100%），当前概率之和为：{sum}！", "校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            if (!ValidateCategoryColumn(dtView.Table)) return;

            if (_editPoolName != null)
            {
                var categoryMap = BuildCategoryMap();
                if (!ValidateAdvancedSettingsAfterEdit(categoryMap)) return;
            }

            bool isLocked = ChkLock.IsChecked == true;
            string rawText = InputBox.Text.Trim();
            string finalPoolDir = Path.Combine(saveLocation, poolName);

            string renamedPassword = null;
            if (_editPoolName != null && isLocked && !PathEquals(_poolAbsoluteDir, finalPoolDir))
            {
                string originalLockFile = Path.Combine(_poolAbsoluteDir, "data.lock");
                if (File.Exists(originalLockFile))
                {
                    var pwd = new PasswordDialog($"卡池已重命名为“{poolName}”，请输入原密码以保留锁定状态") { Owner = Window.GetWindow(this) };
                    if (pwd.ShowDialog() != true) return;
                    if (!CryptoUtils.VerifyPassword(_editPoolName, pwd.Password))
                    {
                        CustomMessageBox.Show(Window.GetWindow(this), "密码错误！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    renamedPassword = pwd.Password;
                }
            }

            bool createdPoolDir = false;
            if (_editPoolName == null)
            {
                bool dirExists = Directory.Exists(finalPoolDir);
                bool hasDataFile = File.Exists(Path.Combine(finalPoolDir, "data.lock"))
                                || File.Exists(Path.Combine(finalPoolDir, "data.zip"))
                                || File.Exists(Path.Combine(finalPoolDir, "data.txt"));
                if (dirExists && hasDataFile)
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "该位置已存在相同名称的卡池！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                try
                {
                    if (!dirExists)
                    {
                        Directory.CreateDirectory(finalPoolDir);
                        createdPoolDir = true;
                    }
                    _poolAbsoluteDir = finalPoolDir;

                    // 首次创建卡池时自动生成所在目录，并加入卡池扫描目录
                    try
                    {
                        Directory.CreateDirectory(saveLocation);
                        if (!App.SearchDirectories.Contains(saveLocation))
                        {
                            App.SearchDirectories.Add(saveLocation);
                            App.SaveSettings();
                        }
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "创建卡池目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                if (!PathEquals(_poolAbsoluteDir, finalPoolDir))
                {
                    if (Directory.Exists(finalPoolDir))
                    {
                        CustomMessageBox.Show(Window.GetWindow(this), "目标位置已存在相同名称的卡池！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    string oldDir = _poolAbsoluteDir;
                    string moveError;
                    if (!TryMoveDirectory(_poolAbsoluteDir, finalPoolDir, out moveError))
                    {
                        CustomMessageBox.Show(Window.GetWindow(this), "移动卡池失败：\n\n" + moveError, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    _poolAbsoluteDir = finalPoolDir;

                    // ★ 修复：重命名/移动后，更新个性化设置中的绝对路径，防止后续无法找到文件
                    UpdatePersonalizationPathsAfterMove(oldDir, finalPoolDir);
                }
            }

            bool wasLocked = File.Exists(Path.Combine(_poolAbsoluteDir, "data.lock"));

            try
            {
                if (_coverRemoved)
                {
                    try { PoolCover.RemoveCover(_poolAbsoluteDir); } catch { }
                }
                else if (_isCoverChanged && !string.IsNullOrEmpty(_selectedCoverPath) && File.Exists(_selectedCoverPath))
                {
                    string ext = Path.GetExtension(_selectedCoverPath).ToLowerInvariant();
                    if (VideoExts.Contains(ext) || ImageExts.Contains(ext))
                    {
                        try { PoolCover.SaveCover(_poolAbsoluteDir, _selectedCoverPath); } catch { }
                        try { PoolCover.SaveOverlay(_poolAbsoluteDir, _selectedOverlayPath); } catch { }
                        try { PoolCover.SetBlurBackground(_poolAbsoluteDir, _coverBlurBackground); } catch { }
                    }
                }

                if (isLocked)
                {
                    if (!wasLocked)
                    {
                        // ★ 先要密码，再写入锁定数据：取消或密码为空时不能留下"有锁无密码"的卡池
                        var pwdDialog = new PasswordDialog("设置锁定密码", true) { Owner = Window.GetWindow(this) };

                        if (pwdDialog.ShowDialog() != true)
                        {
                            PrepareAbortLockedPool(finalPoolDir, createdPoolDir);
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(pwdDialog.Password))
                        {
                            PrepareAbortLockedPool(finalPoolDir, createdPoolDir);
                            CustomMessageBox.Show(Window.GetWindow(this), "密码不能为空，已取消创建。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        // ★ 密码确认后才清理旧数据，取消密码不能破坏现有卡池
                        if (!CryptoUtils.SavePasswordHash(poolName, pwdDialog.Password))
                        {
                            // 注册表写不进去（权限策略等）时绝不能继续，否则会生成
                            // 「有 data.lock 但没有密码」的卡池 —— 之后既打不开也改不了。
                            PrepareAbortLockedPool(finalPoolDir, createdPoolDir);
                            CustomMessageBox.Show(Window.GetWindow(this),
                                "无法保存密码（系统可能限制了注册表写入）。\n\n为避免生成「有锁但没有密码」的卡池，已取消本次操作。",
                                "设置密码失败", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        if (!string.IsNullOrEmpty(pwdDialog.SecretQuestion))
                        {
                            try
                            {
                                string qaContent = pwdDialog.SecretQuestion + "\u0001" + pwdDialog.SecretAnswer;
                                File.WriteAllText(Path.Combine(_poolAbsoluteDir, "secret_qa.lock"), CryptoUtils.EncryptString(qaContent));
                            }
                            catch { }
                        }
                    }
                    else if (!string.IsNullOrEmpty(renamedPassword))
                    {
                        try
                        {
                            CryptoUtils.SavePasswordHash(poolName, renamedPassword);
                            CryptoUtils.DeletePasswordHash(_editPoolName);
                        }
                        catch { }
                    }

                    // 已锁定卡池直接保存新内容前清理旧数据
                    PoolArchive.DeleteArchives(_poolAbsoluteDir);
                    File.WriteAllText(Path.Combine(_poolAbsoluteDir, "data.lock"),
                        CryptoUtils.EncryptString(rawText));
                }
                else
                {
                    PoolArchive.DeleteArchives(_poolAbsoluteDir);
                    File.WriteAllText(Path.Combine(_poolAbsoluteDir, "data.txt"),
                        rawText, new UTF8Encoding(false));

                    try { CryptoUtils.DeletePasswordHash(poolName); } catch { }
                    if (_editPoolName != null && !PathEquals(_editPoolName, poolName))
                    { try { CryptoUtils.DeletePasswordHash(_editPoolName); } catch { } }

                    try
                    {
                        string qaFile = Path.Combine(_poolAbsoluteDir, "secret_qa.lock");
                        if (File.Exists(qaFile)) File.Delete(qaFile);
                    }
                    catch { }

                    // 解除锁定：清理残留的锁文件
                    try
                    {
                        string lockFile = Path.Combine(_poolAbsoluteDir, "data.lock");
                        if (File.Exists(lockFile)) File.Delete(lockFile);
                    }
                    catch { }
                }

                if (ChkProbability.IsChecked != true && !HasActiveGachaStrategy())
                {
                    string gachaFile = Path.Combine(_poolAbsoluteDir, "gacha_settings.ini");
                    if (File.Exists(gachaFile)) File.Delete(gachaFile);
                }

                SavePersonalizationFileToDir(dtView.Table);

                EnsureSearchDirectory(saveLocation);
                MarkSaved();

                App.LastSelectedPoolDir = _poolAbsoluteDir;
                App.LastSelectedPoolName = poolName;
                App.SaveSettings();

                RuntimeLog.Info($"{(_editPoolName == null ? "创建卡池" : "修改卡池")}成功：{poolName}，目录={finalPoolDir}，条目={dtView.Table.Rows.Count}");
                CustomMessageBox.Show(Window.GetWindow(this),
                    _editPoolName == null ? $"卡池“{poolName}”创建成功！" : "修改成功！",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                MessageBus.Publish(new AppMessages.PoolListChanged());
                MessageBus.Publish(new AppMessages.NavigateBack());
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, $"保存卡池失败：{poolName}");
                CustomMessageBox.Show(Window.GetWindow(this), "保存卡池失败：\n\n" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 取消「设置锁定密码」时的回滚：取消勾选并把刚创建的空目录删掉，
        /// 避免出现「有锁文件但没有密码」的卡池。
        /// </summary>
        private void PrepareAbortLockedPool(string poolDir, bool createdPoolDir)
        {
            try
            {
                if (ChkLock != null) ChkLock.IsChecked = false;
            }
            catch { }

            // ★ 只删除本次新建的卡池目录；编辑现有卡池取消密码时绝不能删除原数据
            if (!createdPoolDir) return;

            try
            {
                if (!string.IsNullOrEmpty(poolDir) && Directory.Exists(poolDir))
                {
                    Directory.Delete(poolDir, true);
                }
            }
            catch { }
        }
        private void UpdatePersonalizationPathsAfterMove(string oldDir, string newDir)
        {
            if (_personalizationSettings == null || string.IsNullOrEmpty(oldDir) || string.IsNullOrEmpty(newDir)) return;

            string oldNormalized = NormalizePath(oldDir);
            string newNormalized = NormalizePath(newDir);

            Func<string, string> fixPath = (path) =>
            {
                if (string.IsNullOrEmpty(path)) return path;
                string full = NormalizePath(path);
                if (full.StartsWith(oldNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = full.Substring(oldNormalized.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return Path.Combine(newNormalized, relative);
                }
                return path;
            };

            _personalizationSettings.GlobalSoundFile = fixPath(_personalizationSettings.GlobalSoundFile);
            _personalizationSettings.BackgroundImage = fixPath(_personalizationSettings.BackgroundImage);

            if (_personalizationSettings.Items != null)
            {
                foreach (var item in _personalizationSettings.Items)
                {
                    if (item == null) continue;
                    item.AvatarFile = fixPath(item.AvatarFile);
                    item.IllustrationFile = fixPath(item.IllustrationFile);
                    item.SoundFile = fixPath(item.SoundFile);
                }
            }
        }

        private void SavePersonalizationFileToDir(DataTable dt)
        {
            try
            {
                if (string.IsNullOrEmpty(_poolAbsoluteDir)) return;
                if (!Directory.Exists(_poolAbsoluteDir)) Directory.CreateDirectory(_poolAbsoluteDir);

                string persFile = Path.Combine(_poolAbsoluteDir, "personalization.json");
                var normalized = PoolArchive.NormalizeAssetPaths(_personalizationSettings, _poolAbsoluteDir);

                var validNames = new HashSet<string>(StringComparer.Ordinal);
                if (dt != null && dt.Columns.Count > 0)
                {
                    string objectColumn = dt.Columns[0].ColumnName;
                    foreach (DataRow row in dt.Rows)
                    {
                        string name = (row[objectColumn]?.ToString() ?? "").Trim();
                        if (!string.IsNullOrEmpty(name)) validNames.Add(name);
                    }
                }

                var orphanedItems = new List<PersonalizationItem>();
                if (normalized.Items != null)
                {
                    foreach (var item in normalized.Items)
                    {
                        if (item == null) continue;
                        if (!validNames.Contains(item.ObjectName ?? ""))
                            orphanedItems.Add(item);
                    }
                }

                if (normalized.Items != null)
                {
                    normalized.Items = normalized.Items
                        .Where(x => x != null && validNames.Contains(x.ObjectName ?? ""))
                        .ToList();
                }

                var stillReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (normalized.Items != null)
                {
                    foreach (var it in normalized.Items)
                    {
                        if (it == null) continue;
                        if (!string.IsNullOrEmpty(it.AvatarFile)) stillReferenced.Add(it.AvatarFile);
                        if (!string.IsNullOrEmpty(it.IllustrationFile)) stillReferenced.Add(it.IllustrationFile);
                        if (!string.IsNullOrEmpty(it.SoundFile)) stillReferenced.Add(it.SoundFile);
                    }
                }
                if (!string.IsNullOrEmpty(normalized.GlobalSoundFile)) stillReferenced.Add(normalized.GlobalSoundFile);
                if (!string.IsNullOrEmpty(normalized.BackgroundImage)) stillReferenced.Add(normalized.BackgroundImage);

                foreach (var orphan in orphanedItems)
                {
                    if (orphan == null) continue;
                    TryDeleteAssetFile(orphan.AvatarFile, stillReferenced);
                    TryDeleteAssetFile(orphan.IllustrationFile, stillReferenced);
                    TryDeleteAssetFile(orphan.SoundFile, stillReferenced);
                }

                bool hasContent =
                    normalized.SoundMode != SoundMode.None
                    || normalized.AnimationMode != AnimationMode.Full
                    || normalized.EffectiveBackgroundMode != BackgroundMode.Default
                    || !string.IsNullOrEmpty(normalized.BackgroundImage)
                    || (!string.IsNullOrEmpty(normalized.GlobalSoundFile) && normalized.SoundMode == SoundMode.Global)
                    || (normalized.Items != null && normalized.Items.Count > 0);

                if (!hasContent)
                {
                    if (File.Exists(persFile)) { try { File.Delete(persFile); } catch { } }
                    return;
                }

                PersonalizationSettings.Save(normalized, persFile);
            }
            catch { }
        }

        private void TryDeleteAssetFile(string relPath, HashSet<string> stillReferenced)
        {
            if (string.IsNullOrEmpty(relPath)) return;
            if (stillReferenced != null && stillReferenced.Contains(relPath)) return;
            try
            {
                string fullPath = Path.Combine(_poolAbsoluteDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    File.SetAttributes(fullPath, FileAttributes.Normal);
                    File.Delete(fullPath);
                }
            }
            catch { }
        }
    }
}

