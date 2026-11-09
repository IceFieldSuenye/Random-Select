using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RandomDrawer.Messaging;

namespace RandomDrawer
{
    public class PersonalizationRow : INotifyPropertyChanged
    {
        public string ObjectName { get; set; } = "";
        public Dictionary<string, string> OtherValues { get; set; } = new Dictionary<string, string>();

        private string _colorHex = "";
        private string _avatarFile = "";
        private string _illustrationFile = "";
        private string _soundFile = "";

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                _colorHex = value ?? "";
                OnPropertyChanged(nameof(ColorHex));
                OnPropertyChanged(nameof(ColorPreviewBrush));
            }
        }

        public Brush ColorPreviewBrush
        {
            get
            {
                if (string.IsNullOrEmpty(_colorHex)) return Brushes.Transparent;
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(_colorHex)); }
                catch { return Brushes.Transparent; }
            }
        }

        public string AvatarFile
        {
            get => _avatarFile;
            set
            {
                _avatarFile = value ?? "";
                OnPropertyChanged(nameof(AvatarFile));
                OnPropertyChanged(nameof(AvatarDisplay));
                OnPropertyChanged(nameof(AvatarButtonIcon));
                OnPropertyChanged(nameof(AvatarDeleteVisibility));
            }
        }

        public string IllustrationFile
        {
            get => _illustrationFile;
            set
            {
                _illustrationFile = value ?? "";
                OnPropertyChanged(nameof(IllustrationFile));
                OnPropertyChanged(nameof(IllustrationDisplay));
                OnPropertyChanged(nameof(IllustrationButtonIcon));
                OnPropertyChanged(nameof(IllustrationDeleteVisibility));
            }
        }

        public string SoundFile
        {
            get => _soundFile;
            set
            {
                _soundFile = value ?? "";
                OnPropertyChanged(nameof(SoundFile));
                OnPropertyChanged(nameof(SoundDisplay));
                OnPropertyChanged(nameof(SoundButtonIcon));
                OnPropertyChanged(nameof(SoundDeleteVisibility));
            }
        }

        public string AvatarDisplay => string.IsNullOrEmpty(_avatarFile) ? "选择头像..." : Path.GetFileName(_avatarFile);
        public string IllustrationDisplay => string.IsNullOrEmpty(_illustrationFile) ? "选择立绘..." : Path.GetFileName(_illustrationFile);
        public string SoundDisplay => string.IsNullOrEmpty(_soundFile) ? "选择音效..." : Path.GetFileName(_soundFile);

        // 图标绑定到 TextBlock.Text 上，兼容层无法直接改写绑定值，因此在数据源侧先解析
        public string AvatarButtonIcon => IconSupport.Resolve(string.IsNullOrEmpty(_avatarFile) ? "\uE8B0" : "\uE70F");
        public string IllustrationButtonIcon => IconSupport.Resolve(string.IsNullOrEmpty(_illustrationFile) ? "\uEB9F" : "\uE70F");
        public string SoundButtonIcon => IconSupport.Resolve(string.IsNullOrEmpty(_soundFile) ? "\uE8D6" : "\uE70F");

        public Visibility AvatarDeleteVisibility => string.IsNullOrEmpty(_avatarFile) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility IllustrationDeleteVisibility => string.IsNullOrEmpty(_illustrationFile) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SoundDeleteVisibility => string.IsNullOrEmpty(_soundFile) ? Visibility.Collapsed : Visibility.Visible;

        public string this[string key] => OtherValues.TryGetValue(key, out var v) ? v : "";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class PersonalizationPage_UC : UserControl, IPageAnimation, IConfirmLeave, IHandleEscape
    {
        private readonly ObservableCollection<PersonalizationRow> _rows = new ObservableCollection<PersonalizationRow>();
        private ICollectionView _rowView;
        private string _objectSearchText = "";
        private PersonalizationSettings _settings = new PersonalizationSettings();
        private PersonalizationSettings _draftSettings = new PersonalizationSettings();
        private PersonalizationSettings _initialSnapshot = new PersonalizationSettings();
        private PersonalizationRow _selectedRow = null;
        private string _poolDir = "";
        private bool _initialized = false;
        private bool _discardConfirmed = false;

        private PersonalizationRow _colorTargetRow = null;
        private string _pendingColorHex = "";
        private bool _isUpdatingColorUi = false;
        private bool _editingBackgroundColor = false;

        private DispatcherTimer _previewGifTimer;
        private GifBitmapDecoder _previewGifDecoder;
        private int _previewGifFrameIndex = 0;

        private DispatcherTimer _avatarGifTimer;
        private GifBitmapDecoder _avatarGifDecoder;
        private int _avatarGifFrameIndex = 0;

        private Action<AppMessages.OpenPersonalization> _onOpen;

        private static readonly TimeSpan DrawDuration = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan PauseAfterDraw = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(450);
        private const double StartScale = 6.0;
        private const double EndScale = 1.0;
        private const double ImageSlideRatio = 0.05;

        private const double PopupEstimatedWidth = 384;
        private const double PopupEstimatedHeight = 440;

        public PersonalizationPage_UC()
        {
            InitializeComponent();
            TableItems.ItemsSource = _rows;
            _rowView = CollectionViewSource.GetDefaultView(_rows);
            _rowView.Filter = FilterPersonalizationRow;
            // ★ 名称过长时自动换行，行高随内容增加，避免被固定行高截断
            TableItems.RowHeight = double.NaN;
            TableItems.MinRowHeight = 42;

            // ★ 单元格左右内边距缩小，让「调整颜色 / 选择头像 / 选择立绘」在较窄列中也能完整显示
            try
            {
                var cellStyle = new Style(typeof(DataGridCell), TableItems.CellStyle);
                cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
                TableItems.CellStyle = cellStyle;

                var headerStyle = new Style(typeof(DataGridColumnHeader), TableItems.ColumnHeaderStyle);
                headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
                TableItems.ColumnHeaderStyle = headerStyle;
            }
            catch { }

            _onOpen = OnOpen;
            MessageBus.Subscribe(_onOpen);
        }

        public void PlayEnterAnimation(Action onComplete) => SlideInAnimationHelper.PlaySlideIn(this, 60, 0, onComplete);
        public void PlayExitAnimation(Action onComplete)
        {
            StopPreviewGifPlayback();
            StopAvatarGifPlayback();
            try { PreviewRealImage.Source = null; } catch { }
            try { PreviewSilhouetteMask.ImageSource = null; } catch { }
            try { PreviewAvatar.Source = null; } catch { }
            SlideInAnimationHelper.PlaySlideOut(this, 60, 0, onComplete);
        }
        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);

        public string UnsavedLabel => "个性化抽取对象";

        public bool HasUnsavedChanges()
        {
            if (!_initialized) return false;
            try
            {
                var cur = CollectSettingsFromUI();
                string a = System.Text.Json.JsonSerializer.Serialize(cur);
                string b = System.Text.Json.JsonSerializer.Serialize(_initialSnapshot);
                return a != b;
            }
            catch { return false; }
        }

        public bool CanLeave()
        {
            if (_discardConfirmed) return true;
            if (!HasUnsavedChanges()) return true;

            var r = CustomMessageBox.Show(Window.GetWindow(this),
                "您已修改了个性化设置，确定要放弃并返回吗？",
                "确认放弃", MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "放弃", noText: "取消", yesIcon: "\uE926");
            if (r == MessageBoxResult.Yes) _discardConfirmed = true;
            return r == MessageBoxResult.Yes;
        }

        public bool TryHandleEscape() { BtnCancelInternal(); return true; }

        private void OnOpen(AppMessages.OpenPersonalization msg)
        {
            if (msg == null) return;
            Dispatcher.Invoke(new Action(() => LoadFrom(msg)));
        }

        private void ClearPreviewElements()
        {
            StopPreviewGifPlayback();
            StopAvatarGifPlayback();

            try { PreviewRealImage.BeginAnimation(OpacityProperty, null); } catch { }
            try { PreviewSilhouette.BeginAnimation(OpacityProperty, null); } catch { }
            try { PreviewTextFallback.BeginAnimation(OpacityProperty, null); } catch { }
            try { PreviewImageTranslate.BeginAnimation(TranslateTransform.XProperty, null); } catch { }
            try { PreviewSilhouetteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); } catch { }
            try { PreviewSilhouetteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); } catch { }

            PreviewRealImage.Source = null;
            PreviewRealImage.Opacity = 0;
            PreviewRealImage.Visibility = Visibility.Collapsed;

            PreviewSilhouetteMask.ImageSource = null;
            PreviewSilhouette.Visibility = Visibility.Collapsed;
            PreviewSilhouette.Opacity = 1;

            PreviewSilhouetteScale.ScaleX = EndScale;
            PreviewSilhouetteScale.ScaleY = EndScale;
            PreviewImageTranslate.X = 0;

            PreviewAvatar.Source = null;
            PreviewAvatar.Visibility = Visibility.Collapsed;
            PreviewAvatarFallback.Visibility = Visibility.Visible;

            PreviewTextName.Text = "";
            PreviewTextOthers.Items.Clear();
            PreviewTextFallback.Visibility = Visibility.Collapsed;
            PreviewTextFallback.Opacity = 1;

            PreviewEmptyHint.Visibility = Visibility.Visible;
        }

        // ★ FIX：统一解析资源路径
        //   · 相对路径 → 拼接到 _poolDir
        //   · 绝对路径且文件存在 → 直接用
        //   · 绝对路径失效（例如卡池重命名后）→ 在卡池目录下同名文件回退查找
        //     搜索顺序：illustrations / avatars / sounds / backgrounds / 根目录
        private string ResolveAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                // A. 绝对路径
                if (Path.IsPathRooted(path))
                {
                    if (File.Exists(path)) return path;

                    // ★ 兼容"卡池重命名后绝对路径失效"：尝试在卡池目录下同名文件回退查找
                    if (!string.IsNullOrEmpty(_poolDir))
                    {
                        string fileName = Path.GetFileName(path);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            string[] subDirs = { "illustrations", "avatars", "sounds", "backgrounds", "" };
                            foreach (var sub in subDirs)
                            {
                                string candidate = string.IsNullOrEmpty(sub)
                                    ? Path.Combine(_poolDir, fileName)
                                    : Path.Combine(_poolDir, sub, fileName);
                                if (File.Exists(candidate)) return candidate;
                            }
                        }
                    }
                    // 找不到回退文件 → 原样返回（调用方通常会 File.Exists 二次检查）
                    return path;
                }

                // B. 相对路径
                if (!string.IsNullOrEmpty(_poolDir))
                {
                    string combined = Path.Combine(_poolDir, path);
                    if (File.Exists(combined)) return combined;
                }
                return path;
            }
            catch { return path; }
        }

        private void LoadFrom(AppMessages.OpenPersonalization msg)
        {
            _selectedRow = null;
            ClearPreviewElements();

            _discardConfirmed = false;
            _poolDir = msg.PoolDir ?? "";

            _settings = msg.InitialSettings?.Clone() ?? new PersonalizationSettings();

            // ★ 修复：强制将所有传进来的相对路径展开为绝对路径，避免后续加载失败
            if (_settings.Items != null)
            {
                foreach (var item in _settings.Items)
                {
                    if (item == null) continue;
                    item.AvatarFile = ResolveAssetPath(item.AvatarFile);
                    item.IllustrationFile = ResolveAssetPath(item.IllustrationFile);
                    item.SoundFile = ResolveAssetPath(item.SoundFile);
                }
            }

            _draftSettings = _settings.Clone();
            _initialSnapshot = _settings.Clone();

            while (TableItems.Columns.Count > 5)
                TableItems.Columns.RemoveAt(TableItems.Columns.Count - 1);

            foreach (var header in msg.OtherColumnHeaders)
            {
                var col = new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding($"[{header}]"),
                    IsReadOnly = true,
                    Width = new DataGridLength(90),
                    MinWidth = 70
                };
                TableItems.Columns.Add(col);
            }

            // ★ 所有列按比例铺满表格宽度，避免横向滚动条上方出现空白

            _rows.Clear();
            _objectSearchText = "";
            if (SearchObjects != null) SearchObjects.SetTextWithoutSearch("");
            try { _rowView?.Refresh(); } catch { }
            foreach (var name in msg.ObjectNames)
            {
                var row = new PersonalizationRow { ObjectName = name };
                if (msg.OtherValues.TryGetValue(name, out var kv))
                {
                    foreach (var pair in kv) row.OtherValues[pair.Key] = pair.Value;
                }

                var item = _settings.Items.FirstOrDefault(x => string.Equals(x.ObjectName, name, StringComparison.Ordinal));
                if (item != null)
                {
                    row.ColorHex = item.ColorHex ?? "";
                    row.AvatarFile = item.AvatarFile ?? "";
                    row.IllustrationFile = item.IllustrationFile ?? "";
                    row.SoundFile = item.SoundFile ?? "";
                }
                _rows.Add(row);
            }

            _initialized = false;

            ComboSoundMode.SelectedIndex = (int)_settings.SoundMode;
            ComboAnimationMode.SelectedIndex = (int)_settings.AnimationMode;
            ComboBackgroundMode.SelectedIndex = ModeToIndex(_settings.BackgroundMode);

            if (!string.IsNullOrEmpty(_settings.GlobalSoundFile))
                TxtGlobalSoundFile.Text = Path.GetFileName(_settings.GlobalSoundFile);
            else TxtGlobalSoundFile.Text = "";

            UpdateSoundModeUI();
            UpdateBackgroundModeUI();
            ApplyPreviewBackground();
            UpdatePreview();

            _initialized = true;
            _initialSnapshot = CollectSettingsFromUI();
        }

        private PersonalizationSettings CollectSettingsFromUI()
        {
            var s = new PersonalizationSettings
            {
                SoundMode = (SoundMode)Math.Max(0, ComboSoundMode.SelectedIndex),
                AnimationMode = (AnimationMode)Math.Max(0, ComboAnimationMode.SelectedIndex),
                BackgroundMode = IndexToMode(ComboBackgroundMode.SelectedIndex),
                GlobalSoundFile = _draftSettings.GlobalSoundFile ?? "",
                BackgroundColor = _draftSettings.BackgroundColor ?? "#F0F0F0",
                BackgroundImage = _draftSettings.BackgroundImage ?? ""
            };

            s.Items.Clear();
            foreach (var row in _rows)
            {
                bool hasAny = !string.IsNullOrEmpty(row.ColorHex)
                           || !string.IsNullOrEmpty(row.AvatarFile)
                           || !string.IsNullOrEmpty(row.IllustrationFile)
                           || !string.IsNullOrEmpty(row.SoundFile);
                if (!hasAny) continue;

                s.Items.Add(new PersonalizationItem
                {
                    ObjectName = row.ObjectName,
                    ColorHex = row.ColorHex ?? "",
                    AvatarFile = row.AvatarFile ?? "",
                    IllustrationFile = row.IllustrationFile ?? "",
                    SoundFile = row.SoundFile ?? ""
                });
            }
            return s;
        }

        private bool PoolHasHighlightColors()
        {
            try
            {
                if (string.IsNullOrEmpty(_poolDir)) return false;
                string gachaFile = Path.Combine(_poolDir, "gacha_settings.ini");
                if (!File.Exists(gachaFile)) return false;
                var gs = GachaSettings.LoadSettingsFromFile(gachaFile);
                if (gs == null || !gs.Enabled) return false;
                return gs.EnableHighlightColors || gs.EnableHighlightColorsBA;
            }
            catch { return false; }
        }

        private static readonly string[] CommonColors = new[]
        {
            "#000000", "#212121", "#424242", "#616161", "#757575", "#9E9E9E", "#BDBDBD", "#FFFFFF",
            "#D32F2F", "#E53935", "#EF5350", "#F44336", "#E91E63", "#EC407A", "#F06292", "#F8BBD0",
            "#7B1FA2", "#9C27B0", "#AB47BC", "#CE93D8", "#5E35B1", "#673AB7", "#7E57C2", "#B39DDB",
            "#1976D2", "#2196F3", "#42A5F5", "#90CAF9", "#0288D1", "#03A9F4", "#4FC3F7", "#B3E5FC",
            "#00796B", "#009688", "#26A69A", "#80CBC4", "#388E3C", "#4CAF50", "#66BB6A", "#A5D6A7",
            "#F57C00", "#FF9800", "#FFA726", "#FFCC80", "#FBC02D", "#FFEB3B", "#FFF176", "#FFF9C4",
            "#5D4037", "#795548", "#8D6E63", "#BCAAA4", "#D4AF37", "#FFD700", "#FFC107", "#FFECB3"
        };

        private void SelectColor_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;

            if (PoolHasHighlightColors())
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "该卡池已启用高级设置中的「更醒目的颜色」，无法使用自定义颜色。\n\n" +
                    "请先在「高级」设置中关闭「更醒目的颜色」。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new ColorPickerDialog("选择抽取对象颜色", row.ColorHex, true)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true) return;

            row.ColorHex = dialog.Cleared ? "" : dialog.SelectedHex;
            TableItems.SelectedItem = row;
            _selectedRow = row;
            UpdatePreview();
        }

        /// <summary>创建常用颜色色块（只创建一次）。</summary>
        private void EnsureItemColorGrid()
        {
            if (ItemColorGrid.Children.Count > 0) return;

            foreach (var hex in CommonColors)
            {
                Color c;
                try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { continue; }

                var btn = new Button
                {
                    Style = TryFindResource("ColorChipStyle") as Style,
                    Width = 34,
                    Height = 28,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(c),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderThickness = new Thickness(1),
                    Tag = hex,
                    ToolTip = hex
                };
                btn.Click += CommonColorBtn_Click;
                ItemColorGrid.Children.Add(btn);
            }
        }

        /// <summary>为「抽取对象背景」选择纯色。</summary>
        private void ShowBackgroundColorPicker()
        {
            string initial = _draftSettings?.BackgroundColor ?? "";
            var dialog = new ColorPickerDialog("选择背景颜色", initial, true)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true) return;

            if (_draftSettings == null) return;
            if (dialog.Cleared)
            {
                ComboBackgroundMode.SelectedIndex = 0;
                UpdateBackgroundModeUI();
                ApplyPreviewBackground();
                return;
            }

            _draftSettings.BackgroundColor = dialog.SelectedHex;
            _draftSettings.BackgroundMode = BackgroundMode.Color;
            UpdateBackgroundModeUI();
            ApplyPreviewBackground();
        }

        private CustomPopupPlacement[] ColorPopupPlacementCallback(Size popupSize, Size targetSize, Point offset)
        {
            var workArea = SystemParameters.WorkArea;
            double screenLeft = workArea.Left;
            double screenTop = workArea.Top;
            double screenRight = workArea.Right;
            double screenBottom = workArea.Bottom;

            Point screenPos = new Point(0, 0);
            try
            {
                if (ItemColorPopup.PlacementTarget is UIElement target)
                    screenPos = target.PointToScreen(new Point(0, 0));
            }
            catch { }

            double pw = popupSize.Width > 0 ? popupSize.Width : PopupEstimatedWidth;
            double ph = popupSize.Height > 0 ? popupSize.Height : PopupEstimatedHeight;

            double y = targetSize.Height + 2;

            if (screenPos.Y + targetSize.Height + ph > screenBottom - 8)
            {
                y = -ph - 2;
            }

            if (screenPos.Y + y < screenTop + 8)
            {
                y = screenTop + 8 - screenPos.Y;
            }

            double x = 0;
            if (screenPos.X + pw > screenRight - 8)
            {
                x = targetSize.Width - pw;
            }
            if (screenPos.X + x < screenLeft + 8)
            {
                x = screenLeft + 8 - screenPos.X;
            }

            return new CustomPopupPlacement[]
            {
                new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)
            };
        }

        private void CommonColorBtn_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            string hex = btn.Tag as string;
            if (string.IsNullOrEmpty(hex)) return;

            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { return; }

            _isUpdatingColorUi = true;
            try
            {
                SliderR.Value = c.R;
                SliderG.Value = c.G;
                SliderB.Value = c.B;
                TxtR.Text = c.R.ToString();
                TxtG.Text = c.G.ToString();
                TxtB.Text = c.B.ToString();
                _pendingColorHex = hex;
                UpdateItemColorPreview();
            }
            finally { _isUpdatingColorUi = false; }
        }

        private void SliderRgb_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingColorUi) return;
            _isUpdatingColorUi = true;
            try
            {
                TxtR.Text = ((int)SliderR.Value).ToString();
                TxtG.Text = ((int)SliderG.Value).ToString();
                TxtB.Text = ((int)SliderB.Value).ToString();
                _pendingColorHex = "";
                UpdateItemColorPreview();
            }
            finally { _isUpdatingColorUi = false; }
        }

        private void TxtRgb_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingColorUi) return;
            if (TxtR == null || TxtG == null || TxtB == null) return;

            _isUpdatingColorUi = true;
            try
            {
                if (int.TryParse(TxtR.Text, out int r)) SliderR.Value = Math.Max(0, Math.Min(255, r));
                if (int.TryParse(TxtG.Text, out int g)) SliderG.Value = Math.Max(0, Math.Min(255, g));
                if (int.TryParse(TxtB.Text, out int b)) SliderB.Value = Math.Max(0, Math.Min(255, b));
                _pendingColorHex = "";
                UpdateItemColorPreview();
            }
            finally { _isUpdatingColorUi = false; }
        }

        private void UpdateItemColorPreview()
        {
            try
            {
                var color = Color.FromRgb((byte)SliderR.Value, (byte)SliderG.Value, (byte)SliderB.Value);
                if (ItemColorPreview != null)
                    ItemColorPreview.Background = new SolidColorBrush(color);
            }
            catch { }
        }

        private void ApplyItemColor_Click(object sender, RoutedEventArgs e)
        {
            var color = Color.FromRgb((byte)SliderR.Value, (byte)SliderG.Value, (byte)SliderB.Value);
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            if (_editingBackgroundColor)
            {
                if (_draftSettings != null)
                {
                    _draftSettings.BackgroundColor = hex;
                    _draftSettings.BackgroundMode = BackgroundMode.Color;
                }
                ItemColorPopup.IsOpen = false;
                _editingBackgroundColor = false;
                _pendingColorHex = "";
                UpdateBackgroundModeUI();
                ApplyPreviewBackground();
                return;
            }

            if (_colorTargetRow == null) { ItemColorPopup.IsOpen = false; return; }

            _colorTargetRow.ColorHex = hex;
            ItemColorPopup.IsOpen = false;
            _colorTargetRow = null;
            _pendingColorHex = "";

            if (ReferenceEquals(_colorTargetRow, _selectedRow)) UpdatePreview();
        }

        private void ClearItemColor_Click(object sender, RoutedEventArgs e)
        {
            if (_editingBackgroundColor)
            {
                ItemColorPopup.IsOpen = false;
                _editingBackgroundColor = false;
                _pendingColorHex = "";
                // 「清除」= 恢复默认背景（跟随主题）
                ComboBackgroundMode.SelectedIndex = 0;
                UpdateBackgroundModeUI();
                ApplyPreviewBackground();
                return;
            }

            if (_colorTargetRow != null) _colorTargetRow.ColorHex = "";
            ItemColorPopup.IsOpen = false;
            _colorTargetRow = null;
            _pendingColorHex = "";

            if (ReferenceEquals(_colorTargetRow, _selectedRow)) UpdatePreview();
        }

        private void CancelItemColor_Click(object sender, RoutedEventArgs e)
        {
            ItemColorPopup.IsOpen = false;
            _editingBackgroundColor = false;
            _colorTargetRow = null;
            _pendingColorHex = "";
        }

        private void ResetAllColors_Click(object sender, RoutedEventArgs e)
        {
            if (_rows.Count == 0) return;

            bool hasAny = _rows.Any(r => !string.IsNullOrEmpty(r.ColorHex));
            if (!hasAny)
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "当前没有对象设置了自定义颜色。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = CustomMessageBox.Show(Window.GetWindow(this),
                "确定要清除所有对象的自定义颜色吗？",
                "确认清除", MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "清除", noText: "取消", yesIcon: "\uE738");
            if (r != MessageBoxResult.Yes) return;

            foreach (var row in _rows) row.ColorHex = "";
            UpdatePreview();
        }

        private void ResetAllAvatars_Click(object sender, RoutedEventArgs e)
        {
            ResetAllAssets("头像", row => !string.IsNullOrEmpty(row.AvatarFile), row => row.AvatarFile = "");
        }

        private void ResetAllIllustrations_Click(object sender, RoutedEventArgs e)
        {
            ResetAllAssets("立绘", row => !string.IsNullOrEmpty(row.IllustrationFile), row => row.IllustrationFile = "");
        }

        private void ResetAllSounds_Click(object sender, RoutedEventArgs e)
        {
            ResetAllAssets("音效", row => !string.IsNullOrEmpty(row.SoundFile), row => row.SoundFile = "");
        }

        private void ResetAllAssets(string label, Func<PersonalizationRow, bool> hasValue, Action<PersonalizationRow> clear)
        {
            if (_rows.Count == 0) return;
            if (!_rows.Any(hasValue))
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    $"当前没有对象设置了{label}。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = CustomMessageBox.Show(Window.GetWindow(this),
                $"确定要清除所有对象的{label}吗？",
                "确认清除", MessageBoxButton.YesNo, MessageBoxImage.Question,
                yesText: "清除", noText: "取消", yesIcon: "\uE738");
            if (r != MessageBoxResult.Yes) return;

            foreach (var row in _rows) clear(row);
            UpdatePreview();
        }

        private void UpdateSoundModeUI()
        {
            int idx = ComboSoundMode.SelectedIndex;
            bool showGlobal = idx == 1;
            bool showPerItem = idx == 2;

            BtnSelectGlobalSound.Visibility = showGlobal ? Visibility.Visible : Visibility.Collapsed;

            bool hasGlobalFile = !string.IsNullOrEmpty(_draftSettings.GlobalSoundFile);
            TxtGlobalSoundFile.Visibility = (showGlobal && hasGlobalFile) ? Visibility.Visible : Visibility.Collapsed;
            ColSound.Visibility = showPerItem ? Visibility.Visible : Visibility.Collapsed;
        }

        private static int ModeToIndex(BackgroundMode mode)
        {
            if (mode == BackgroundMode.Color) return 1;
            if (mode == BackgroundMode.Image) return 2;
            return 0;
        }

        private static BackgroundMode IndexToMode(int index)
        {
            if (index == 1) return BackgroundMode.Color;
            if (index == 2) return BackgroundMode.Image;
            return BackgroundMode.Default;
        }

        private void UpdateBackgroundModeUI()
        {
            int index = ComboBackgroundMode?.SelectedIndex ?? 0;
            bool isDefault = index == 0;
            bool isColor = index == 1;

            BtnBgText.Text = isDefault ? "跟随主题" : (isColor ? "调整颜色" : "浏览...");
            BtnBgIcon.Text = isDefault ? "\uE81C" : (isColor ? "\uE790" : "\uE8E5");
            BtnBackgroundAction.IsEnabled = !isDefault;
            BtnBackgroundAction.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ComboSoundMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            UpdateSoundModeUI();
        }

        private void ComboBackgroundMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            UpdateBackgroundModeUI();

            var newMode = IndexToMode(ComboBackgroundMode.SelectedIndex);
            _draftSettings.BackgroundMode = newMode;

            // 选择「纯色」时，若还没有颜色则给一个初始灰色，保证能立即预览并继续调色
            if (newMode == BackgroundMode.Color && string.IsNullOrEmpty(_draftSettings.BackgroundColor))
                _draftSettings.BackgroundColor = "#DCE9F7";

            ApplyPreviewBackground();
        }

        private void BtnSelectGlobalSound_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择音效文件",
                Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.flac;*.aac;*.ogg|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _draftSettings.GlobalSoundFile = dlg.FileName;
                TxtGlobalSoundFile.Text = Path.GetFileName(dlg.FileName);
                TxtGlobalSoundFile.Visibility = Visibility.Visible;
            }
        }

        private void BtnBackgroundAction_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBackgroundMode.SelectedIndex == 1) ShowBackgroundColorPicker();
            else if (ComboBackgroundMode.SelectedIndex == 2) BrowseBackgroundImage();
        }

        private void BrowseBackgroundImage()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
            if (!valid.Contains(ext))
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    $"不支持的文件类型：{ext}", "不支持的格式",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _draftSettings.BackgroundImage = dlg.FileName;
            // ★ 修复：强制同步模式为图片，并立即刷新
            _draftSettings.BackgroundMode = BackgroundMode.Image;
            ApplyPreviewBackground();
        }

        private void ShowColorPicker()
        {
            if (ColorPickerPopup.IsOpen) { ColorPickerPopup.IsOpen = false; return; }
            if (ColorGrid.Children.Count == 0) BuildColorGrid();

            ColorPickerPopup.PlacementTarget = BtnBackgroundAction;
            ColorPickerPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ColorPickerPopup.HorizontalOffset = 0;
            ColorPickerPopup.VerticalOffset = 4;
            ColorPickerPopup.IsOpen = true;
        }

        private void BuildColorGrid()
        {
            string[] colors = {
                "#F0F0F0", "#FFFFFF", "#E0E0E0", "#BDBDBD", "#9E9E9E", "#757575", "#424242", "#000000",
                "#FFCDD2", "#F8BBD0", "#E1BEE7", "#D1C4E9", "#C5CAE9", "#BBDEFB", "#B3E5FC", "#B2EBF2",
                "#FFECB3", "#FFE0B2", "#FFCCBC", "#DCEDC8", "#C8E6C9", "#B2DFDB", "#B3E5FC", "#D1C4E9",
                "#FFEBEE", "#FCE4EC", "#F3E5F5", "#EDE7F6", "#E8EAF6", "#E3F2FD", "#E1F5FE", "#E0F7FA"
            };

            foreach (var c in colors)
            {
                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(c); }
                catch { continue; }

                var btn = new Button
                {
                    Style = TryFindResource("ColorChipStyle") as Style,
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(2),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderThickness = new Thickness(1),
                    Tag = c,
                    ToolTip = c
                };
                btn.Click += ColorBtn_Click;
                ColorGrid.Children.Add(btn);
            }
        }

        private void ColorBtn_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;
            string hex = btn.Tag as string;
            if (string.IsNullOrEmpty(hex)) return;

            _draftSettings.BackgroundColor = hex;
            _draftSettings.BackgroundMode = BackgroundMode.Color;

            // 同步下拉框为「纯色」
            if (ComboBackgroundMode != null && ComboBackgroundMode.SelectedIndex != 1)
                ComboBackgroundMode.SelectedIndex = 1;

            UpdateBackgroundModeUI();
            ApplyPreviewBackground();
            ColorPickerPopup.IsOpen = false;
        }

        private void ApplyPreviewBackground()
        {
            if (_draftSettings.EffectiveBackgroundMode == BackgroundMode.Default)
            {
                PreviewTopArea.SetResourceReference(Panel.BackgroundProperty, "Bg_F0F0F0");
                return;
            }

            if (_draftSettings.EffectiveBackgroundMode == BackgroundMode.Color)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(_draftSettings.BackgroundColor);
                    PreviewTopArea.Background = new SolidColorBrush(color);
                }
                catch { PreviewTopArea.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)); }
            }
            else
            {
                string bgPath = ResolveAssetPath(_draftSettings.BackgroundImage);
                if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
                {
                    var bmp = LoadBitmapSafe(bgPath);
                    if (bmp != null)
                    {
                        PreviewTopArea.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                        return;
                    }
                }
                PreviewTopArea.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            }
        }

        private void TableItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedRow = TableItems.SelectedItem as PersonalizationRow;
            UpdatePreview();
        }

        private bool FilterPersonalizationRow(object item)
        {
            if (!(item is PersonalizationRow row)) return true;
            string q = (_objectSearchText ?? "").Trim();
            if (q.Length == 0) return true;
            return (row.ObjectName ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void SearchObjects_SearchChanged(string text)
        {
            _objectSearchText = text ?? "";
            try { _rowView?.Refresh(); } catch { }
        }

        private void UpdatePreview()
        {
            ResetPreviewStateOnly();

            if (_selectedRow == null)
            {
                StopPreviewGifPlayback();
                StopAvatarGifPlayback();
                PreviewEmptyHint.Visibility = Visibility.Visible;
                PreviewTextFallback.Visibility = Visibility.Collapsed;
                PreviewAvatar.Source = null;
                PreviewAvatar.Visibility = Visibility.Collapsed;
                PreviewAvatarFallback.Visibility = Visibility.Collapsed;
                PreviewRealImage.Source = null;
                PreviewRealImage.Visibility = Visibility.Collapsed;
                PreviewRealImage.Opacity = 0;
                SetAvatarPanelVisible(false);
                UpdatePreviewInteraction(false, PoolHasAnyMedia());
                return;
            }

            PreviewEmptyHint.Visibility = Visibility.Collapsed;
            UpdatePreviewText(_selectedRow);

            string avatarPath = ResolveAssetPath(_selectedRow.AvatarFile);
            bool hasAvatar = !string.IsNullOrEmpty(avatarPath) && File.Exists(avatarPath);
            string illustPath = ResolveAssetPath(_selectedRow.IllustrationFile);
            bool hasIllustration = !string.IsNullOrEmpty(illustPath) && File.Exists(illustPath);
            bool hasMedia = hasAvatar || hasIllustration;

            // 没有头像时不显示头像预览区
            SetAvatarPanelVisible(hasAvatar);
            if (hasAvatar) UpdatePreviewAvatar(_selectedRow);

            if (hasIllustration)
            {
                ApplyIllustrationToPreview(illustPath);
                PreviewRealImage.Opacity = 1;
                PreviewRealImage.Visibility = Visibility.Visible;
            }
            else
            {
                StopPreviewGifPlayback();
                PreviewRealImage.Source = null;
                PreviewRealImage.Opacity = 0;
                PreviewRealImage.Visibility = Visibility.Collapsed;
                PreviewSilhouetteMask.ImageSource = null;
            }

            // 有头像/立绘：文字浮在预览左上角；都没有：文字居中显示
            ShowPreviewText(!hasMedia);
            UpdatePreviewInteraction(hasIllustration, PoolHasAnyMedia());
        }

        private void SetAvatarPanelVisible(bool visible)
        {
            if (AvatarRow != null) AvatarRow.Height = visible ? new GridLength(140) : new GridLength(0);
            if (AvatarPreviewPanel != null)
                AvatarPreviewPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePreviewInteraction(bool canPreview, bool poolHasMedia)
        {
            // 「播放」预览需要当前对象有立绘；动画是卡池级设置，只要有任意对象带头像/立绘即可用
            if (BtnPreview != null) BtnPreview.IsEnabled = canPreview;
            if (ComboAnimationMode != null) ComboAnimationMode.IsEnabled = poolHasMedia;
        }

        /// <summary>卡池中是否至少有一个对象设置了头像或立绘。</summary>
        private bool PoolHasAnyMedia()
        {
            if (_rows == null) return false;
            foreach (var r in _rows)
            {
                if (r == null) continue;
                string avatar = ResolveAssetPath(r.AvatarFile);
                if (!string.IsNullOrEmpty(avatar) && File.Exists(avatar)) return true;
                string illust = ResolveAssetPath(r.IllustrationFile);
                if (!string.IsNullOrEmpty(illust) && File.Exists(illust)) return true;
            }
            return false;
        }

        private void ShowPreviewText(bool centered)
        {
            PreviewTextFallback.Visibility = Visibility.Visible;
            PreviewTextFallback.Opacity = 1;
            PreviewTextFallback.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            PreviewTextFallback.VerticalAlignment = centered ? VerticalAlignment.Center : VerticalAlignment.Top;
            PreviewTextFallback.Margin = centered ? new Thickness(24) : new Thickness(20);
            PreviewTextFallback.MaxWidth = centered ? 560 : 520;

            PreviewTextName.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
            PreviewTextName.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            PreviewTextName.FontSize = centered ? 34 : 28;

            Brush brush = centered ? ThemeManager.GetBrush("Fg_333333") : Brushes.White;
            if (!string.IsNullOrEmpty(_selectedRow?.ColorHex))
            {
                try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selectedRow.ColorHex)); }
                catch { }
            }

            PreviewTextName.Foreground = brush;
            PreviewTextName.Effect = centered ? null : new DropShadowEffect
            {
                Color = Colors.Black,
                ShadowDepth = 0,
                BlurRadius = 3,
                Opacity = 0.7
            };

            PreviewTextOthers.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            foreach (var item in PreviewTextOthers.Items)
            {
                if (!(item is TextBlock tb)) continue;
                tb.Foreground = brush;
                tb.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
                tb.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                tb.Effect = centered ? null : new DropShadowEffect
                {
                    Color = Colors.Black,
                    ShadowDepth = 0,
                    BlurRadius = 2,
                    Opacity = 0.7
                };
            }
        }

        private void BtnAnimationHelp_Click(object sender, RoutedEventArgs e)
        {
            CustomMessageBox.Show(Window.GetWindow(this),
                "抽取动画说明：\n\n" +
                "· 无动画：抽取后直接显示结果。\n" +
                "· 部分：只播放简化动画。\n" +
                "· 完整：播放完整的抽取动画。\n\n" +
                "只有卡池中至少一个抽取对象设置了头像或立绘时，动画才可用。\n" +
                "请先在左侧为任意对象添加头像或立绘，再回来设置。",
                "动画帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void UpdatePreviewText(PersonalizationRow row)
        {
            if (row == null) return;
            PreviewTextName.Text = row.ObjectName;

            Brush nameBrush = Brushes.White;
            if (!string.IsNullOrEmpty(row.ColorHex))
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(row.ColorHex);
                    nameBrush = new SolidColorBrush(c);
                }
                catch { }
            }
            PreviewTextName.Foreground = nameBrush;

            PreviewTextOthers.Items.Clear();
            foreach (var kv in row.OtherValues)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                var tb = new TextBlock
                {
                    Text = $"{kv.Key}：{kv.Value}",
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    FontSize = 16,
                    // ★ 修复：跟随当前对象名称的颜色
                    Foreground = nameBrush,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                tb.Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    ShadowDepth = 0,
                    BlurRadius = 2,
                    Opacity = 0.7
                };
                PreviewTextOthers.Items.Add(tb);
            }
        }

        private void UpdatePreviewAvatar(PersonalizationRow row)
        {
            string avatarPath = ResolveAssetPath(row?.AvatarFile);
            if (string.IsNullOrEmpty(avatarPath) || !File.Exists(avatarPath))
            {
                StopAvatarGifPlayback();
                PreviewAvatar.Source = null;
                PreviewAvatar.Visibility = Visibility.Collapsed;
                PreviewAvatarFallback.Visibility = Visibility.Visible;
                return;
            }

            ApplyAvatarToPreview(avatarPath);
        }

        private void ApplyAvatarToPreview(string path)
        {
            StopAvatarGifPlayback();
            PreviewAvatar.Source = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                PreviewAvatar.Visibility = Visibility.Collapsed;
                PreviewAvatarFallback.Visibility = Visibility.Visible;
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gif")
            {
                try
                {
                    // ★ 使用 FileStream 读取，避免 URI 转义问题
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        _avatarGifDecoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    }

                    if (_avatarGifDecoder.Frames.Count > 0)
                    {
                        _avatarGifFrameIndex = 0;
                        var first = _avatarGifDecoder.Frames[0];
                        PreviewAvatar.Source = first;
                        PreviewAvatar.Visibility = Visibility.Visible;
                        PreviewAvatarFallback.Visibility = Visibility.Collapsed;

                        if (_avatarGifDecoder.Frames.Count > 1)
                        {
                            _avatarGifTimer = new DispatcherTimer { Interval = GetGifFrameDelay(first) };
                            _avatarGifTimer.Tick += AvatarGifTimer_Tick;
                            _avatarGifTimer.Start();
                        }
                        return;
                    }
                }
                catch { }
            }

            var bmp = LoadBitmapSafe(path);
            if (bmp != null)
            {
                PreviewAvatar.Source = bmp;
                PreviewAvatar.Visibility = Visibility.Visible;
                PreviewAvatarFallback.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewAvatar.Source = null;
                PreviewAvatar.Visibility = Visibility.Collapsed;
                PreviewAvatarFallback.Visibility = Visibility.Visible;
            }
        }

        private void AvatarGifTimer_Tick(object sender, EventArgs e)
        {
            if (_avatarGifDecoder == null || _avatarGifDecoder.Frames.Count == 0) return;

            _avatarGifFrameIndex = (_avatarGifFrameIndex + 1) % _avatarGifDecoder.Frames.Count;
            var frame = _avatarGifDecoder.Frames[_avatarGifFrameIndex];

            try { PreviewAvatar.Source = frame; }
            catch { }

            if (_avatarGifTimer != null)
                _avatarGifTimer.Interval = GetGifFrameDelay(frame);
        }

        private void StopAvatarGifPlayback()
        {
            if (_avatarGifTimer != null)
            {
                _avatarGifTimer.Stop();
                _avatarGifTimer.Tick -= AvatarGifTimer_Tick;
                _avatarGifTimer = null;
            }
            _avatarGifDecoder = null;
            _avatarGifFrameIndex = 0;
        }

        private void ResetPreviewStateOnly()
        {
            PreviewSilhouetteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PreviewSilhouetteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PreviewImageTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            PreviewSilhouette.BeginAnimation(OpacityProperty, null);
            PreviewRealImage.BeginAnimation(OpacityProperty, null);
            PreviewTextFallback.BeginAnimation(OpacityProperty, null);

            PreviewSilhouetteScale.ScaleX = EndScale;
            PreviewSilhouetteScale.ScaleY = EndScale;
            PreviewImageTranslate.X = 0;
            PreviewSilhouette.Opacity = 1;
            PreviewSilhouette.Visibility = Visibility.Collapsed;
            PreviewRealImage.Opacity = 0;
            PreviewTextFallback.Opacity = 1;
        }

        private void ApplyIllustrationToPreview(string path)
        {
            StopPreviewGifPlayback();
            PreviewRealImage.Source = null;
            PreviewSilhouetteMask.ImageSource = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gif")
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        _previewGifDecoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    }

                    if (_previewGifDecoder.Frames.Count > 0)
                    {
                        _previewGifFrameIndex = 0;
                        var first = _previewGifDecoder.Frames[0];
                        PreviewRealImage.Source = first;
                        PreviewSilhouetteMask.ImageSource = first;

                        if (_previewGifDecoder.Frames.Count > 1)
                        {
                            _previewGifTimer = new DispatcherTimer { Interval = GetGifFrameDelay(first) };
                            _previewGifTimer.Tick += PreviewGifTimer_Tick;
                            _previewGifTimer.Start();
                        }
                        return;
                    }
                }
                catch { }
            }

            var bmp = LoadBitmapSafe(path);
            PreviewRealImage.Source = bmp;
            PreviewSilhouetteMask.ImageSource = bmp;
        }

        private void PreviewGifTimer_Tick(object sender, EventArgs e)
        {
            if (_previewGifDecoder == null || _previewGifDecoder.Frames.Count == 0) return;

            _previewGifFrameIndex = (_previewGifFrameIndex + 1) % _previewGifDecoder.Frames.Count;
            var frame = _previewGifDecoder.Frames[_previewGifFrameIndex];

            try
            {
                PreviewRealImage.Source = frame;
                PreviewSilhouetteMask.ImageSource = frame;
            }
            catch { }

            if (_previewGifTimer != null)
                _previewGifTimer.Interval = GetGifFrameDelay(frame);
        }

        private void StopPreviewGifPlayback()
        {
            if (_previewGifTimer != null)
            {
                _previewGifTimer.Stop();
                _previewGifTimer.Tick -= PreviewGifTimer_Tick;
                _previewGifTimer = null;
            }
            _previewGifDecoder = null;
            _previewGifFrameIndex = 0;
        }

        private static TimeSpan GetGifFrameDelay(BitmapFrame frame)
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

        // ★ 终极兜底：用 FileStream 和 System.Drawing 双重解析图片
        private static BitmapImage LoadBitmapSafe(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            // 1. 优先尝试原生 FileStream（绕开 Uri 转义问题）
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { }

            // 2. 如果 WPF 原生解析失败（如某些顽固的 BMP 或损坏格式），使用 System.Drawing 重新编码为 PNG 读取
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var img = System.Drawing.Image.FromStream(fs))
                    {
                        using (var ms = new MemoryStream())
                        {
                            img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            ms.Position = 0;
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                            bmp.Freeze();
                            return bmp;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private void SelectAvatar_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;
            string path = SelectImageFile("选择头像图片");
            if (!string.IsNullOrEmpty(path))
            {
                row.AvatarFile = path;
                TableItems.SelectedItem = row;
                _selectedRow = row;
                UpdatePreview();
            }
        }

        private void SelectIllustration_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;
            string path = SelectImageFile("选择立绘图片");
            if (!string.IsNullOrEmpty(path))
            {
                row.IllustrationFile = path;
                TableItems.SelectedItem = row;
                _selectedRow = row;
                UpdatePreview();
            }
        }

        private void SelectSound_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;
            var dlg = new OpenFileDialog
            {
                Title = "选择音效文件",
                Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.flac;*.aac;*.ogg|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true) row.SoundFile = dlg.FileName;
        }

        private void DeleteAvatar_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;
            row.AvatarFile = "";
            TableItems.SelectedItem = row;
            _selectedRow = row;
            UpdatePreview();
        }

        private void DeleteIllustration_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;
            row.IllustrationFile = "";
            TableItems.SelectedItem = row;
            _selectedRow = row;
            UpdatePreview();
        }

        private void DeleteSound_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.DataContext as PersonalizationRow;
            if (row == null) return;
            row.SoundFile = "";
        }

        private string SelectImageFile(string title)
        {
            var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return null;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
            if (!valid.Contains(ext))
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    $"不支持的文件类型：{ext}", "不支持的格式",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return dlg.FileName;
        }

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedRow == null)
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "请先在左侧表格中选择一个抽取对象。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string illustPath = ResolveAssetPath(_selectedRow.IllustrationFile);
            bool hasIllustration = !string.IsNullOrEmpty(illustPath) && File.Exists(illustPath);
            if (!hasIllustration) { UpdatePreview(); return; }

            int mode = ComboAnimationMode.SelectedIndex;
            if (mode == 0) { UpdatePreview(); return; }

            var rowBeforePlay = _selectedRow;
            await PlayPreviewAnimationAsync(mode);
            if (!ReferenceEquals(rowBeforePlay, _selectedRow)) UpdatePreview();
        }

        private async Task PlayPreviewAnimationAsync(int mode)
        {
            if (_selectedRow == null) return;

            string path = ResolveAssetPath(_selectedRow.IllustrationFile);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            ResetPreviewStateOnly();
            ApplyIllustrationToPreview(path);

            PreviewRealImage.Visibility = Visibility.Visible;
            PreviewTextFallback.Visibility = Visibility.Visible;
            PreviewTextFallback.Opacity = 1;

            if (mode == 2)
            {
                PreviewSilhouette.Visibility = Visibility.Visible;
                PreviewSilhouetteScale.ScaleX = StartScale;
                PreviewSilhouetteScale.ScaleY = StartScale;

                var tcs = new TaskCompletionSource<bool>();
                var shrinkX = new DoubleAnimation(StartScale, EndScale, DrawDuration)
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                var shrinkY = new DoubleAnimation(StartScale, EndScale, DrawDuration)
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };

                shrinkX.Completed += (s, e) => tcs.TrySetResult(true);
                PreviewSilhouetteScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkX);
                PreviewSilhouetteScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkY);

                await tcs.Task;
                await Task.Delay(PauseAfterDraw);

                double slideDist = PreviewTopArea.ActualWidth * ImageSlideRatio;
                var morphEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

                var slideAnim = new DoubleAnimation(0, slideDist, MorphDuration) { EasingFunction = morphEase };
                PreviewImageTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnim);

                var fadeOut = new DoubleAnimation(1, 0, MorphDuration) { EasingFunction = morphEase };
                PreviewSilhouette.BeginAnimation(OpacityProperty, fadeOut);

                var fadeIn = new DoubleAnimation(0, 1, MorphDuration) { EasingFunction = morphEase };
                PreviewRealImage.BeginAnimation(OpacityProperty, fadeIn);
            }
            else
            {
                PreviewSilhouette.Visibility = Visibility.Collapsed;
                double startX = -PreviewTopArea.ActualWidth * 0.35;
                PreviewImageTranslate.X = startX;
                PreviewRealImage.Opacity = 0;

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var slideAnim = new DoubleAnimation(startX, 0, MorphDuration) { EasingFunction = ease };
                PreviewImageTranslate.BeginAnimation(TranslateTransform.XProperty, slideAnim);

                var fadeIn = new DoubleAnimation(0, 1, MorphDuration) { EasingFunction = ease };
                PreviewRealImage.BeginAnimation(OpacityProperty, fadeIn);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = CollectSettingsFromUI();
                if (draft.BackgroundMode == BackgroundMode.Image)
                {
                    string bgPath = ResolveAssetPath(draft.BackgroundImage);
                    bool hasBg = !string.IsNullOrEmpty(bgPath) && File.Exists(bgPath);
                    if (!hasBg)
                    {
                        var r = CustomMessageBox.Show(Window.GetWindow(this),
                            "您选择了“图片”背景，但尚未指定背景图片。\n\n是否改为默认背景？",
                            "未选择背景图片", MessageBoxButton.YesNo, MessageBoxImage.Question, yesText: "改为默认", noText: "取消", yesIcon: "\uE72C");
                        if (r == MessageBoxResult.Yes)
                        {
                            draft.BackgroundMode = BackgroundMode.Default;
                            draft.BackgroundImage = "";
                            _draftSettings.BackgroundMode = BackgroundMode.Default;
                            _draftSettings.BackgroundImage = "";
                        }
                        else return;
                    }
                }

                _discardConfirmed = true;
                MessageBus.Publish(new AppMessages.PersonalizationFinished(true, draft));
                MessageBus.Publish(new AppMessages.NavigateBack());
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(Window.GetWindow(this),
                    "保存失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => BtnCancelInternal();

        private void BtnCancelInternal()
        {
            if (!CanLeave()) return;
            _discardConfirmed = true;
            MessageBus.Publish(new AppMessages.PersonalizationFinished(false, null));
            MessageBus.Publish(new AppMessages.NavigateBack());
        }
    }
}