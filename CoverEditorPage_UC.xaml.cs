using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using RandomDrawer.Messaging;
using Microsoft.Win32;

namespace RandomDrawer
{
    /// <summary>
    /// 卡池封面编辑页：导入图片 / GIF / 视频，在封面上加文字（颜色、字号、粗斜体、下划线、删除线、荧光笔）
    /// 或随手涂画（画笔 / 荧光笔 / 橡皮擦），保存成带透明通道的批注层（原封面文件不动）。
    ///
    /// 3.28：文本框去掉右下角小方块，改成「鼠标靠近虚线框四边 / 四角出现缩放光标、拖动改大小」；
    ///       工具栏加缩放（10%~400%，Ctrl+滚轮），整个编辑区一起缩放并可滚动。
    /// </summary>
    public partial class CoverEditorPage_UC : UserControl, IPageAnimation, IConfirmLeave
    {
        /// <summary>3.40：None = 当前没有工具（「样式」页签下不选中任何工具，画布也不能画 / 不能编辑文字）。
        /// 3.42：Select = 框选工具（框住批注后可复制 / 剪切 / 删除 / 拖动移动）。</summary>
        private enum ToolMode { None, Text, Pen, Highlight, Eraser, Select }

        private sealed class TextItem
        {
            public RichTextBox Box = null!;
            /// <summary>3.41：文本框的旋转角度（度数，正数顺时针）。</summary>
            public double Angle;
        }

        [Flags]
        private enum ResizeEdge
        {
            None = 0,
            Left = 1,
            Right = 2,
            Top = 4,
            Bottom = 8
        }

        private readonly List<TextItem> _texts = new List<TextItem>();
        /// <summary>
        /// 一步可撤销的编辑：Undo 与 Redo 都显式给出来。
        /// 之前两个栈存的都是「同一个动作」，恢复时把撤销动作又执行了一遍（比如删除再删除），
        /// 所以「恢复」在文字 / 笔画上看起来没反应。
        /// </summary>
        private sealed class EditAction
        {
            public Action Undo = () => { };
            public Action Redo = () => { };
        }

        private readonly Stack<EditAction> _undo = new Stack<EditAction>();
        private readonly Stack<EditAction> _redo = new Stack<EditAction>();
        private readonly List<HintBubble> _toolHints = new List<HintBubble>();

        private ToolMode _mode = ToolMode.Text;
        private Color _textColor = Color.FromRgb(0x33, 0x33, 0x33);
        private Color _highlightColor = Color.FromRgb(0xFF, 0xF7, 0xB2);
        /// <summary>3.39：文字页签荧光笔的样式 —— false = 背景高亮，true = 彩色下划线。</summary>
        private bool _textHighlightUnderline;
        private Color _penColor = Color.FromRgb(0xE5, 0x39, 0x35);
        private double _fontSize = 32;
        /// <summary>3.35：文字字体（功能区「字体」下拉框）；新建文本框与选中文字都用它。</summary>
        private string _fontFamily = "Microsoft YaHei";
        private double _penWidth = 6;
        private TextItem? _activeText;
        /// <summary>最近一次编辑过的文本框：固定之后再点颜色 / 字号 / 样式按钮仍然作用于它。</summary>
        private TextItem? _lastTextItem;

        // 3.35：页签（text / draw / style）；默认「文字」页签
        private string _ribbonTab = "text";

        private string _coverPath = "";
        private string _poolDir = "";
        /// <summary>卡池里已经存在的批注层；保存时与本次批注合并，避免二次编辑丢字。</summary>
        private string _existingOverlayPath = "";
        private bool _hasMedia;
        private bool _isVideo;
        /// <summary>3.34：圆角封面开关（从页面底部的复选框改成顶部「样式」功能区的圆角 / 方角按钮）。</summary>
        private bool _roundCover;
        private double _coverAspect = 16.0 / 9.0;

        // 缩放
        private const double MinZoom = 10;
        private const double MaxZoom = 400;
        private const double ZoomStep = 10;
        private double _zoom = 100;
        /// <summary>防止「代码设置滑块值 → ValueChanged → 再 SetZoom」的递归。</summary>
        private bool _zoomSync;

        // 画图式虚线框（激活文本框时显示）：黑色加粗虚线 + 8 个小方块控制点
        private System.Windows.Shapes.Rectangle? _activeFrame;
        private readonly List<System.Windows.Shapes.Rectangle> _frameHandles =
            new List<System.Windows.Shapes.Rectangle>();
        private const double FrameHandleSize = 7;

        // 3.41：文本框上方的圆形旋转按钮
        private const double RotateHandleSize = 22;
        private System.Windows.Controls.Border? _rotateHandle;
        private TextItem? _rotatingItem;
        private Point _rotateCenter;
        private double _rotateStartMouseAngle;
        private double _rotateStartItemAngle;

        // 拖动缩放
        private const double EdgeGrip = 6;
        private TextItem? _resizingItem;
        private ResizeEdge _resizeEdge = ResizeEdge.None;
        private Point _resizeStart;
        private double _resizeLeft, _resizeTop, _resizeWidth, _resizeHeight;

        // 拖出新文本框
        private bool _draggingSelection;
        private Point _dragStart;
        private System.Windows.Shapes.Rectangle? _selectionRect;

        // ================================================================
        // 3.43：「选择」工具 —— 直接在批注位图上框选、复制 / 剪切 / 删除 / 拖动移动
        // ================================================================

        /// <summary>选框（粗虚线，2 px）。坐标是舞台 DIP。</summary>
        private System.Windows.Shapes.Rectangle? _selectFrame;
        private bool _hasSelRect;
        private Rect _selRect;
        private bool _selectDragging;
        private Point _selectDragStart;
        /// <summary>正在拖动选框里的像素。</summary>
        private bool _selectMoving;
        private Point _selectMoveStart;
        private Rect _selectMoveRect;
        /// <summary>拖动开始时的整张位图（每帧先恢复它，再画提起的那块）。</summary>
        private byte[]? _moveWholeSnapshot;
        /// <summary>被「提起」的选区像素（Pbgra32）。</summary>
        private byte[]? _moveBlock;
        private Int32Rect _moveBlockRect;

        /// <summary>内部剪贴板：复制 / 剪切下来的选区位图。</summary>
        private BitmapSource? _clipImage;

        // 橡皮擦：正方形，边长 = 「粗细」；带一个跟随鼠标的方框提示
        private bool _erasing;
        private System.Windows.Shapes.Rectangle? _eraserFrame;

        // 3.33：批注栅格化（与画图 / PS 的橡皮擦一致）
        // 已固定的文字、笔迹、旧批注层会先合成为一张位图，橡皮擦直接擦位图的方形像素。
        private sealed class EraseSnapshot
        {
            public Int32Rect Rect;
            public byte[] Pixels = Array.Empty<byte>();
        }

        private readonly List<EraseSnapshot> _eraseBatch = new List<EraseSnapshot>();
        private WriteableBitmap? _rasterBitmap;

        // 按住文本框上边界中间拖动位置（四向箭头）
        private TextItem? _movingItem;
        private Point _moveStart;
        private double _moveLeft, _moveTop;

        private Action<AppMessages.OpenCoverEditor> _onOpen;

        // 是否有未保存的修改（返回时弹「保存 / 不保存 / 取消」）
        private bool _dirty;
        private bool _loading;

        /// <summary>3.38：当前是在编辑内置默认封面（卡池没有自己的封面文件）。</summary>
        private bool _editingDefaultCover;

        // ================================================================
        // 3.36：取色器 + 媒体进度（视频 / GIF 可以暂停后从画面取色）
        // ================================================================

        private bool _pickingColor;
        /// <summary>媒体是否处于暂停（视频 / GIF）。</summary>
        private bool _mediaPaused;
        /// <summary>进入取色前媒体是否在播放：退出取色时据此决定要不要恢复播放。</summary>
        private bool _mediaWasPlaying;
        /// <summary>代码更新进度条时不要反过来 Seek 媒体。</summary>
        private bool _mediaProgressSync;

        /// <summary>编辑页里的 GIF 解码器（只在 .gif 封面时非空）；逐帧播放，方便暂停后取色。</summary>
        private GifBitmapDecoder? _gifDecoder;
        private int _gifFrameIndex;
        private DispatcherTimer? _gifTimer;
        /// <summary>视频播放进度刷新（视频没有 PositionChanged 事件）。</summary>
        private DispatcherTimer? _mediaProgressTimer;

        // ================================================================
        // 3.36：选区记忆
        // 点击功能区按钮 / 下拉框会让 RichTextBox 失焦，有的情况下 Selection 会被收成空；
        // 以前这种情况下 ApplyToSelection 会退化成「整个文本框」，所以改字体 / 颜色 / 粗斜体
        // 都变成了整框生效。这里记住最近一次有效选区，失焦后仍按那一段应用格式。
        // ================================================================

        private TextPointer? _styleSelStart;
        private TextPointer? _styleSelEnd;
        private RichTextBox? _styleSelBox;
        private bool _applyingStyle;

        public CoverEditorPage_UC()
        {
            InitializeComponent();

            BuildFontSizeItems();
            BuildFontFamilyItems();
            ComboPenSize.SelectedIndex = 2;   // 默认粗细 6
            UpdatePaintColorSwatch();

            _onOpen = msg => Dispatcher.BeginInvoke(new Action(() => LoadFrom(msg)));
            MessageBus.Subscribe(_onOpen);

            // 3.32：颜色面板（内嵌浮层）
            if (ColorPopup != null) ColorPopup.Closed += ColorPopup_Closed;
            if (ColorPanel != null)
            {
                ColorPanel.ColorChanged += ColorPanel_ColorChanged;
                // 3.64：面板有了「取消 / 确定」——取消（含右上角 ×）把颜色还原成打开面板之前的样子
                ColorPanel.Confirmed += () => { try { if (ColorPopup != null) ColorPopup.IsOpen = false; } catch { } };
                ColorPanel.Cancelled += CancelColorPanel;
                ColorPanel.CloseRequested += CancelColorPanel;
            }

            StageScroll.SizeChanged += (s, e) => LayoutStage();

            // 画笔 / 荧光笔写下的每一笔都进撤销栈
            InkLayer.StrokeCollected += OnStrokeCollected;

            EnsureActiveFrame();
            EnsureEraserFrame();
            AttachToolHints();
            ApplyMode();
            // 3.35：默认打开「文字」页签
            ShowRibbon("text", animate: false);
            // 3.37：布局完成后才知道页签宽度，这时再算一次指示条位置
            Loaded += (s, e) => { try { UpdateTabIndicator(false); } catch { } };
            BringTextLayerToFront();
            SetZoom(100);

            // 橡皮擦：在 Preview 阶段接管。视频（MediaElement）会把鼠标事件吃掉，
            // 之前「橡皮擦完全没反应」就是点击落在了 VideoPreview 上。
            try
            {
                Stage.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                    new MouseButtonEventHandler(Stage_PreviewMouseDown), true);
            }
            catch { }

            // 3.36：离开页面时把 GIF / 进度条计时器停掉（页面被移出可视化树就没人 Tick 了）
            Unloaded += (s, e) =>
            {
                try { EndColorPick(false); } catch { }
                try { StopGifPlayback(); } catch { }
                try { StopMediaProgressTimer(); } catch { }
            };
        }

        // 进场 / 退场方向：与「历史记录」页保持一致
        // （从主界面进入时从下方 60 DIP 滑入、420 ms；从其它页面进入时从右侧滑入）
        private double _lastEnterFromX;
        private double _lastEnterFromY;

        public void PlayEnterAnimation(Action onComplete)
        {
            double fromX = 0, fromY = 0;
            bool fromMain = NavigationContext.CurrentSource == PageEnterSource.Main;

            if (fromMain) fromY = 60;
            else fromX = 60;

            _lastEnterFromX = fromX;
            _lastEnterFromY = fromY;

            if (fromMain)
                SlideInAnimationHelper.PlaySlideIn(this, fromX, fromY, TimeSpan.FromMilliseconds(420), onComplete);
            else
                SlideInAnimationHelper.PlaySlideIn(this, fromX, fromY, onComplete);
        }

        public void PlayEnterAsReverseAnimation(Action onComplete) => PlayEnterAnimation(onComplete);

        // 3.42：返回主界面时统一「整个页面向下淡出」
        public void PlayExitAnimation(Action onComplete) =>
            SlideInAnimationHelper.PlaySlideOut(this, 0, 60, onComplete);

        // ================================================================
        // 离开确认（有修改时：保存 / 不保存 / 取消）
        // ================================================================

        public string UnsavedLabel => "编辑卡池封面";

        public bool HasUnsavedChanges() => _dirty;

        public bool CanLeave()
        {
            if (!_dirty) return true;

            var choice = CustomMessageBox.ShowSaveChanges(Window.GetWindow(this),
                "封面有未保存的修改。\n\n是否保存后再离开？");

            switch (choice)
            {
                case SaveChoice.Save:
                    return SaveCover(false);
                case SaveChoice.Discard:
                    return true;
                default:
                    return false;
            }
        }

        private void MarkDirty()
        {
            if (!_loading) _dirty = true;
        }

        // ================================================================
        // 载入
        // ================================================================

        private void LoadFrom(AppMessages.OpenCoverEditor msg)
        {
            if (msg == null) return;

            _loading = true;
            _poolDir = msg.PoolDir ?? "";
            _coverPath = msg.CoverPath ?? "";
            _existingOverlayPath = "";
            try { if (!string.IsNullOrEmpty(_poolDir)) _existingOverlayPath = PoolCover.GetOverlayPath(_poolDir); } catch { }
            try
            {
                RuntimeLog.Info("封面编辑页：打开"
                    + "，卡池=" + (string.IsNullOrEmpty(_poolDir) ? "-" : Path.GetFileName(_poolDir.TrimEnd('\\', '/')))
                    + "，封面=" + (string.IsNullOrEmpty(_coverPath) ? "无" : Path.GetFileName(_coverPath))
                    + "，已有批注=" + (File.Exists(_existingOverlayPath) ? "是" : "否"));
            }
            catch { }
            ClearTexts();
            InkLayer.Strokes.Clear();
            _undo.Clear();
            _redo.Clear();
            _rasterBitmap = null;
            _eraseBatch.Clear();
            if (RasterLayer != null)
            {
                RasterLayer.Source = null;
                RasterLayer.Visibility = Visibility.Collapsed;
            }
            SetZoom(100);
            _mode = ToolMode.Text;
            _lastTextItem = null;
            ApplyMode();
            // 3.35：每次打开编辑页都回到「文字」页签
            ShowRibbon("text", animate: false);

            // 回显当前卡池的封面样式（编辑已有卡池时不能每次打开都变回方角）
            _roundCover = !string.IsNullOrEmpty(_poolDir) && PoolCover.IsRoundCover(_poolDir);
            UpdateStyleButtons();
            if (!string.IsNullOrEmpty(_coverPath) && File.Exists(_coverPath))
            {
                EnterEditMode();
                try { LoadMedia(_coverPath); }
                catch (Exception ex)
                {
                    RuntimeLog.Error(ex, "封面编辑页：加载封面失败");
                    ShowEmpty();
                }
            }
            else if (!string.IsNullOrEmpty(_poolDir) && PoolCover.HasDefaultCoverEdit(_poolDir))
            {
                // 3.38：这个卡池之前点过「编辑默认封面」→ 直接进入编辑模式，以内置默认封面为底图
                EnterEditMode();
                LoadDefaultCover();
            }
            else
            {
                ShowEmpty();
            }

            UpdateImportButtonText();
            // 3.33：把卡池里已有的批注层显示出来，再次编辑时能看到上次的文字 / 涂画
            LoadExistingOverlay();
            // 3.48：编辑默认封面时，把主界面那行卡池名也作为可编辑位图放进批注层
            EnsureDefaultCoverNameRaster();
            ApplyRoundPreview();

            _loading = false;
            _dirty = false;
        }

        /// <summary>加载卡池已有的透明批注层（显示在底图之上，可被橡皮擦按像素擦除）。</summary>
        private void LoadExistingOverlay()
        {
            try
            {
                if (RasterLayer == null) return;
                _rasterBitmap = null;
                RasterLayer.Source = null;
                RasterLayer.Visibility = Visibility.Collapsed;

                if (string.IsNullOrEmpty(_existingOverlayPath) || !File.Exists(_existingOverlayPath)) return;

                BitmapSource? bmp = ImageHelper.LoadBitmapSized(_existingOverlayPath, 0);
                if (bmp == null)
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(_existingOverlayPath, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    bmp = image;
                }

                RasterLayer.Source = bmp;
                RasterLayer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：加载已有批注层失败");
            }
        }

        /// <summary>3.38：没有封面时的空状态 —— 收起工具栏 / 预览区 / 缩放条，只显示白色空状态页。</summary>
        private void ShowEmpty()
        {
            _hasMedia = false;
            _isVideo = false;
            _editingDefaultCover = false;
            BaseImage.Source = null;
            BaseImage.Visibility = Visibility.Collapsed;
            VideoPreview.Visibility = Visibility.Collapsed;
            try { VideoPreview.Stop(); VideoPreview.Close(); } catch { }
            try { StopGifPlayback(); } catch { }
            try { EndColorPick(false); } catch { }
            try { StopMediaProgressTimer(); } catch { }
            EmptyHint.Visibility = Visibility.Collapsed;

            try
            {
                if (EmptyCoverPanel != null) EmptyCoverPanel.Visibility = Visibility.Visible;
                if (RibbonArea != null) RibbonArea.Visibility = Visibility.Collapsed;
                if (BottomArea != null) BottomArea.Visibility = Visibility.Collapsed;
                if (StageArea != null) StageArea.Visibility = Visibility.Collapsed;
            }
            catch { }

            UpdateImportButtonText();
        }

        /// <summary>3.38：切到正常编辑模式（显示工具栏 / 预览区 / 缩放条）。</summary>
        private void EnterEditMode()
        {
            try
            {
                if (EmptyCoverPanel != null) EmptyCoverPanel.Visibility = Visibility.Collapsed;
                if (RibbonArea != null) RibbonArea.Visibility = Visibility.Visible;
                if (BottomArea != null) BottomArea.Visibility = Visibility.Visible;
                if (StageArea != null) StageArea.Visibility = Visibility.Visible;
            }
            catch { }
            if (EmptyHint != null) EmptyHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 3.38：编辑内置默认封面 —— 把默认封面图当作底图载入，批注层照常保存到卡池目录
        /// （卡池依然没有自己的封面文件，主界面显示「默认封面 + 批注」）。
        /// </summary>
        private void LoadDefaultCover()
        {
            try
            {
                var bmp = EmbeddedImageLoader.Load("Assets/default_cover.jpg");
                if (bmp == null) { ShowEmpty(); return; }

                try { StopGifPlayback(); } catch { }
                try { VideoPreview.Stop(); VideoPreview.Close(); } catch { }
                VideoPreview.Visibility = Visibility.Collapsed;
                try { StopMediaProgressTimer(); } catch { }

                _hasMedia = true;
                _isVideo = false;
                _editingDefaultCover = true;
                BaseImage.Source = bmp;
                BaseImage.Visibility = Visibility.Visible;
                _coverAspect = bmp.PixelWidth / (double)Math.Max(1, bmp.PixelHeight);

                UpdateImportButtonText();
                LayoutStage();
                UpdateMediaBarVisibility();
                Dispatcher.BeginInvoke(new Action(() => { try { LayoutStage(); } catch { } }),
                    DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：加载默认封面失败");
                ShowEmpty();
            }
        }

        /// <summary>
        /// 3.48：编辑内置默认封面时，把主界面居中显示的那行「卡池名」也画进批注位图。
        /// 这样名称与涂画一样能被橡皮擦擦掉、被选择工具框选 / 移动 / 删除。
        /// 已有批注层（上次保存过）时不重复添加。
        /// </summary>
        private void EnsureDefaultCoverNameRaster()
        {
            try
            {
                if (!_editingDefaultCover || RasterLayer == null) return;
                if (RasterLayer.Source != null) return;   // 已有批注层：名称已经在里面
                if (string.IsNullOrEmpty(_poolDir)) return;

                var baseBmp = BaseImage?.Source as BitmapSource;
                if (baseBmp == null) return;

                string poolName = Path.GetFileName(_poolDir.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(poolName)) return;

                var overlay = BuildDefaultCoverNameOverlay(baseBmp, poolName);
                if (overlay == null) return;

                _rasterBitmap = new WriteableBitmap(overlay);
                RasterLayer.Source = _rasterBitmap;
                RasterLayer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：生成默认封面名称位图失败");
            }
        }

        /// <summary>把卡池名按主界面的样式（居中、加粗、白色阴影）渲染成与默认封面同尺寸的透明位图。</summary>
        private static BitmapSource BuildDefaultCoverNameOverlay(BitmapSource baseBmp, string poolName)
        {
            try
            {
                int w = Math.Max(16, baseBmp.PixelWidth);
                int h = Math.Max(16, baseBmp.PixelHeight);

                // 缩放到封面显示尺寸后，字号与主界面那行 32 DIP 的名称接近
                double fontSize = Math.Max(14.0, w * 0.04);
                var text = new TextBlock
                {
                    Text = poolName,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = w * 0.75,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Effect = new DropShadowEffect
                    {
                        Color = Colors.White,
                        BlurRadius = Math.Max(4.0, w * 0.008),
                        ShadowDepth = 0,
                        Opacity = 0.9
                    }
                };

                var host = new Grid { Width = w, Height = h };
                host.Children.Add(text);
                host.Measure(new Size(w, h));
                host.Arrange(new Rect(0, 0, w, h));
                host.UpdateLayout();

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        /// <summary>3.38：空状态页的「编辑默认封面」—— 进入编辑模式并记住这个选择。</summary>
        private void BtnEditDefaultCover_Click(object sender, RoutedEventArgs e)        {
            if (!string.IsNullOrEmpty(_poolDir))
            {
                try { PoolCover.SetDefaultCoverEdit(_poolDir, true); } catch { }
            }

            EnterEditMode();
            LoadDefaultCover();
            EnsureDefaultCoverNameRaster();
            ShowRibbon("text", animate: false);
            SetZoom(100);
        }

        private void LoadMedia(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

            _hasMedia = true;
            EmptyHint.Visibility = Visibility.Collapsed;
            try { StopGifPlayback(); } catch { }
            _mediaPaused = false;

            if (videoExts.Contains(ext))
            {
                _isVideo = true;
                BaseImage.Source = null;
                BaseImage.Visibility = Visibility.Collapsed;
                VideoPreview.Visibility = Visibility.Visible;
                try
                {
                    VideoPreview.Source = new Uri(path, UriKind.Absolute);
                    VideoPreview.Play();
                }
                catch { }
                StartMediaProgressTimer();
            }
            else if (string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                // 3.36：GIF 在编辑页里也逐帧播放（以前只显示首帧），这样才可以「暂停 → 取色」
                _isVideo = false;
                try { VideoPreview.Stop(); VideoPreview.Close(); } catch { }
                VideoPreview.Visibility = Visibility.Collapsed;
                try { StopMediaProgressTimer(); } catch { }

                var gif = ImageHelper.LoadGifFromFile(path);
                if (gif != null && gif.Frames.Count > 0)
                {
                    _gifDecoder = gif;
                    _gifFrameIndex = 0;
                    BaseImage.Source = gif.Frames[0];
                    BaseImage.Visibility = Visibility.Visible;
                    _coverAspect = gif.Frames[0].PixelWidth / (double)Math.Max(1, gif.Frames[0].PixelHeight);
                    StartGifTimer();
                }
                else
                {
                    var fallback = new BitmapImage();
                    fallback.BeginInit();
                    fallback.CacheOption = BitmapCacheOption.OnLoad;
                    fallback.UriSource = new Uri(path, UriKind.Absolute);
                    fallback.EndInit();
                    fallback.Freeze();

                    BaseImage.Source = fallback;
                    BaseImage.Visibility = Visibility.Visible;
                    _coverAspect = fallback.PixelWidth / (double)Math.Max(1, fallback.PixelHeight);
                }
            }
            else
            {
                _isVideo = false;
                try { VideoPreview.Stop(); VideoPreview.Close(); } catch { }
                VideoPreview.Visibility = Visibility.Collapsed;
                try { StopGifPlayback(); } catch { }
                try { StopMediaProgressTimer(); } catch { }

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                BaseImage.Source = bmp;
                BaseImage.Visibility = Visibility.Visible;
                _coverAspect = bmp.PixelWidth / (double)Math.Max(1, bmp.PixelHeight);
            }

            UpdateImportButtonText();
            LayoutStage();
            UpdateMediaBarVisibility();
        }

        // ================================================================
        // 3.36：GIF 逐帧播放 / 媒体进度
        // ================================================================

        private void StartGifTimer()
        {
            try { if (_gifTimer != null) { _gifTimer.Stop(); _gifTimer.Tick -= GifTimer_Tick; _gifTimer = null; } } catch { }
            if (_gifDecoder == null || _gifDecoder.Frames.Count <= 1) return;

            _gifTimer = new DispatcherTimer { Interval = GetGifFrameDelay(_gifDecoder.Frames[_gifFrameIndex]) };
            _gifTimer.Tick += GifTimer_Tick;
            _gifTimer.Start();
        }

        private void GifTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_gifDecoder == null || _gifDecoder.Frames.Count == 0) return;
                _gifFrameIndex = (_gifFrameIndex + 1) % _gifDecoder.Frames.Count;
                ApplyCurrentGifFrame();
                if (_gifTimer != null && _gifDecoder.Frames.Count > 0)
                    _gifTimer.Interval = GetGifFrameDelay(_gifDecoder.Frames[_gifFrameIndex]);
                UpdateMediaProgress();
            }
            catch { }
        }

        private void ApplyCurrentGifFrame()
        {
            try
            {
                if (_gifDecoder == null) return;
                if (_gifFrameIndex < 0 || _gifFrameIndex >= _gifDecoder.Frames.Count) _gifFrameIndex = 0;
                BaseImage.Source = _gifDecoder.Frames[_gifFrameIndex];
                BaseImage.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void StopGifPlayback()
        {
            try
            {
                if (_gifTimer != null)
                {
                    _gifTimer.Stop();
                    _gifTimer.Tick -= GifTimer_Tick;
                    _gifTimer = null;
                }
            }
            catch { }
            _gifDecoder = null;
            _gifFrameIndex = 0;
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

        private void StartMediaProgressTimer()
        {
            if (_mediaProgressTimer != null) return;
            _mediaProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _mediaProgressTimer.Tick += (s, e) => UpdateMediaProgress();
            _mediaProgressTimer.Start();
        }

        private void StopMediaProgressTimer()
        {
            try
            {
                if (_mediaProgressTimer != null) { _mediaProgressTimer.Stop(); _mediaProgressTimer = null; }
            }
            catch { }
        }

        /// <summary>当前封面有没有可拖动的播放进度（视频 / 多帧 GIF）。</summary>
        private bool HasMediaProgress => _isVideo || (_gifDecoder != null && _gifDecoder.Frames.Count > 1);

        /// <summary>3.37：视频 / GIF 的进度条始终显示（不再只在取色时出现）。</summary>
        private void UpdateMediaBarVisibility()
        {
            try
            {
                if (MediaBarCard == null) return;
                MediaBarCard.Visibility = HasMediaProgress ? Visibility.Visible : Visibility.Collapsed;
                if (MediaBarCard.Visibility == Visibility.Visible)
                {
                    UpdateMediaPlayPauseVisual();
                    UpdateMediaProgress();
                }
            }
            catch { }
        }

        private void UpdateMediaProgress()
        {
            try
            {
                if (SlMediaProgress == null) return;

                _mediaProgressSync = true;
                if (_isVideo)
                {
                    TimeSpan pos = TimeSpan.Zero, total = TimeSpan.Zero;
                    try { pos = VideoPreview.Position; } catch { }
                    try { total = VideoPreview.NaturalDuration.HasTimeSpan ? VideoPreview.NaturalDuration.TimeSpan : TimeSpan.Zero; } catch { }

                    double value = total.TotalMilliseconds > 1
                        ? Math.Max(0, Math.Min(100, pos.TotalMilliseconds / total.TotalMilliseconds * 100))
                        : 0;
                    SlMediaProgress.Value = value;
                    if (TxtMediaTime != null) TxtMediaTime.Text = FormatMediaTime(pos) + " / " + FormatMediaTime(total);
                }
                else if (_gifDecoder != null && _gifDecoder.Frames.Count > 1)
                {
                    double value = _gifFrameIndex * 100.0 / (_gifDecoder.Frames.Count - 1);
                    SlMediaProgress.Value = Math.Max(0, Math.Min(100, value));
                    if (TxtMediaTime != null)
                        TxtMediaTime.Text = (_gifFrameIndex + 1) + " / " + _gifDecoder.Frames.Count + " 帧";
                }
                else
                {
                    SlMediaProgress.Value = 0;
                    if (TxtMediaTime != null) TxtMediaTime.Text = "0:00 / 0:00";
                }
            }
            catch { }
            finally { _mediaProgressSync = false; }
        }

        private static string FormatMediaTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1) return ((int)t.TotalHours) + ":" + t.ToString("mm\\:ss");
            return t.ToString("m\\:ss");
        }

        /// <summary>进度条拖动 / 点击轨道：把媒体定位到对应位置。</summary>
        private void SlMediaProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaProgressSync) return;
            try
            {
                double pct = Math.Max(0, Math.Min(100, e.NewValue));

                if (_isVideo)
                {
                    TimeSpan total;
                    try { total = VideoPreview.NaturalDuration.HasTimeSpan ? VideoPreview.NaturalDuration.TimeSpan : TimeSpan.Zero; }
                    catch { total = TimeSpan.Zero; }
                    if (total.TotalMilliseconds > 1)
                    {
                        try { VideoPreview.Position = TimeSpan.FromMilliseconds(total.TotalMilliseconds * pct / 100.0); } catch { }
                    }
                }
                else if (_gifDecoder != null && _gifDecoder.Frames.Count > 1)
                {
                    _gifFrameIndex = (int)Math.Round(pct / 100.0 * (_gifDecoder.Frames.Count - 1));
                    ApplyCurrentGifFrame();
                }
                UpdateMediaProgress();
            }
            catch { }
        }

        private void BtnMediaPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPaused) ResumeMedia();
            else PauseMedia();
            UpdateMediaPlayPauseVisual();
            UpdateMediaProgress();
        }

        private void PauseMedia()
        {
            try
            {
                if (_isVideo) VideoPreview.Pause();
                else if (_gifTimer != null) _gifTimer.Stop();
            }
            catch { }
            _mediaPaused = true;
            UpdateMediaPlayPauseVisual();
        }

        private void ResumeMedia()
        {
            try
            {
                if (_isVideo) VideoPreview.Play();
                else if (_gifDecoder != null && _gifDecoder.Frames.Count > 1) StartGifTimer();
            }
            catch { }
            _mediaPaused = false;
            UpdateMediaPlayPauseVisual();
        }

        private void UpdateMediaPlayPauseVisual()
        {
            try
            {
                if (TxtMediaPlayPause == null) return;
                // E768 = 播放，E769 = 暂停
                TxtMediaPlayPause.Text = _mediaPaused ? "\uE768" : "\uE769";
            }
            catch { }
        }

        private void LayoutStage()
        {
            if (!_hasMedia) return;
            if (StageScroll == null) return;

            // 用 ScrollViewer 自身尺寸算「适应窗口」的大小：滚动条出现/消失不会反过来改基准尺寸，
            // 否则缩放时会出现「算小 → 无滚动条 → 变大 → 有滚动条」的抖动。
            double hostW = StageScroll.ActualWidth;
            double hostH = StageScroll.ActualHeight;
            if (hostW < 8 || hostH < 8) return;

            double availW = Math.Max(80, hostW - 24);
            double availH = Math.Max(80, hostH - 24);

            double w = availW, h = availW / _coverAspect;
            if (h > availH) { h = availH; w = availH * _coverAspect; }

            Stage.Width = Math.Round(w);
            Stage.Height = Math.Round(h);

            foreach (var layer in new FrameworkElement[] { InkLayer, TextLayer, SelectionLayer })
            {
                layer.Width = Stage.Width;
                layer.Height = Stage.Height;
            }
        }

        /// <summary>按钮文案：未导入「导入封面」，已导入「替换封面」。</summary>
        private void UpdateImportButtonText()
        {
            try
            {
                if (TxtImportText != null) TxtImportText.Text = _hasMedia ? "替换封面" : "导入封面";
            }
            catch { }
        }

        // ================================================================
        // 按钮
        // ================================================================

        // 3.35：「取消」按钮移除（保存移到左上角；返回时由 CanLeave 统一提示保存）

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择卡池封面",
                Filter = "支持的媒体文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|视频文件|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v;*.webm|所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            _coverPath = dlg.FileName;
            _editingDefaultCover = false;
            EnterEditMode();
            ClearTexts();
            InkLayer.Strokes.Clear();
            _undo.Clear();
            _redo.Clear();
            SetZoom(100);
            // 换了新封面就是重新开始：旧批注层是给上一张图画的，不能再合并进来
            _existingOverlayPath = "";
            LoadMedia(_coverPath);
            MarkDirty();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveCover(true);

        /// <summary>
        /// 保存封面：把底图与批注层分别发给调用方。返回 true 表示保存成功。
        /// navigateBack = false 时由调用方（CanLeave）继续导航，避免连发两次 NavigateBack。
        /// </summary>
        private bool SaveCover(bool navigateBack)
        {
            if (!_hasMedia)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先导入封面图片、GIF 或视频。",
                    "还没有封面", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            try
            {
                CommitActiveText();

                // 视频 / GIF 也支持批注：视频的批注层按视频尺寸合成，播放时叠在画面上
                string outPath = RenderOverlay();
                if (string.IsNullOrEmpty(outPath))
                {
                    CustomMessageBox.Show(Window.GetWindow(this), "保存批注图层失败。",
                        "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                _dirty = false;
                try
                {
                    RuntimeLog.Info("封面编辑页：保存完成"
                        + "，卡池=" + (string.IsNullOrEmpty(_poolDir) ? "-" : Path.GetFileName(_poolDir.TrimEnd('\\', '/')))
                        + "，圆角=" + (_roundCover ? "是" : "否")
                        + "，批注层=已写入");
                }
                catch { }
                // 3.31：模糊背景改为「选项 → 常规 → 外观」里的全局设置，封面编辑页不再带这个开关
                MessageBus.Publish(new AppMessages.CoverEditorFinished(_coverPath, true,
                    false, _roundCover, outPath, _poolDir, useDefaultCover: _editingDefaultCover));

                if (navigateBack) MessageBus.Publish(new AppMessages.NavigateBack());
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "保存封面批注失败");
                CustomMessageBox.Show(Window.GetWindow(this), "保存封面失败：" + ex.Message,
                    "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>只渲染批注层（透明 PNG），原封面文件不动。</summary>
        private string RenderOverlay()
        {
            CommitActiveText();

            // 3.33：旧批注已经显示在 RasterLayer 里，Stage 渲染一次就是「旧批注 + 本次新内容」，
            // 不再额外合并旧 PNG（以前每次保存都会把旧批注重叠一次，看起来越存越错位）。
            BitmapSource? frame = RenderStageBitmap();
            if (frame == null) return "";

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));

            string name = "cover-overlay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
            foreach (string dir in CandidateDirs())
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, name);
                    using (var fs = File.Create(file)) encoder.Save(fs);
                    _existingOverlayPath = "";
                    return file;
                }
                catch { }
            }
            return "";
        }

        /// <summary>
        /// 把 Stage 上的批注（旧批注位图 + 矢量文字 / 笔迹）渲染成一张透明位图。
        /// 底图 / 视频 / 选区框 / 空状态提示都会被隐藏，不受预览缩放与圆角裁剪影响。
        /// </summary>
        private BitmapSource? RenderStageBitmap()
        {
            var baseVis = BaseImage.Visibility;
            var vidVis = VideoPreview.Visibility;
            var hintVis = EmptyHint.Visibility;
            var selVis = SelectionLayer.Visibility;
            BaseImage.Visibility = Visibility.Collapsed;
            VideoPreview.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Collapsed;
            SelectionLayer.Visibility = Visibility.Collapsed;

            var savedClip = Stage.Clip;
            double savedScaleX = ZoomScale.ScaleX;
            double savedScaleY = ZoomScale.ScaleY;
            var savedHA = Stage.HorizontalAlignment;
            var savedVA = Stage.VerticalAlignment;

            try
            {
                Stage.Clip = null;
                ZoomScale.ScaleX = 1;
                ZoomScale.ScaleY = 1;

                // 3.33：Stage 在 ScrollViewer 里是居中的，直接 Render 会带上父容器的偏移，
                // 保存的批注层整体错位。渲染期间临时把它贴到 ZoomHost 左上角，渲染完恢复。
                Stage.HorizontalAlignment = HorizontalAlignment.Left;
                Stage.VerticalAlignment = VerticalAlignment.Top;
                Stage.UpdateLayout();

                int w = Math.Max(16, (int)Math.Round(Stage.ActualWidth));
                int h = Math.Max(16, (int)Math.Round(Stage.ActualHeight));

                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(Stage);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
            finally
            {
                Stage.Clip = savedClip;
                ZoomScale.ScaleX = savedScaleX;
                ZoomScale.ScaleY = savedScaleY;
                Stage.HorizontalAlignment = savedHA;
                Stage.VerticalAlignment = savedVA;
                BaseImage.Visibility = baseVis;
                VideoPreview.Visibility = vidVis;
                EmptyHint.Visibility = hintVis;
                SelectionLayer.Visibility = selVis;
                try { Stage.UpdateLayout(); } catch { }
            }
        }

        /// <summary>
        /// 3.33：把当前所有批注烘焙进 RasterLayer 位图（切换橡皮擦时调用）。
        /// 之后橡皮擦按方形区域直接改像素，和画图 / PS 一致；矢量层清空，避免同一内容画两遍。
        ///
        /// 3.34：栅格化本身也记进撤销栈（recordUndo）。以前这里会直接把撤销 / 恢复两个栈清空，
        /// 于是「切到橡皮擦之后按撤销 / 恢复完全没反应」；现在撤销会把文字 / 笔迹原样放回来。
        /// </summary>
        private void FlattenVectorsIntoRaster(bool recordUndo)
        {
            try
            {
                CommitActiveText();

                bool hasVectors = InkLayer.Strokes.Count > 0 || TextLayer.Children.Count > 0;
                if (!hasVectors)
                {
                    // 3.40.2：上次保存的批注只存在于栅格层（BitmapImage 不可写），这时
                    // _rasterBitmap 一直是 null，橡皮擦按像素擦时直接 return，看起来
                    // 「上一次修改的内容完全擦不掉」。这里把它换成可写的同尺寸位图。
                    EnsureRasterWritable();
                    return;
                }

                var frame = RenderStageBitmap();
                if (frame == null) return;

                var prevSource = RasterLayer.Source;
                var prevVisibility = RasterLayer.Visibility;

                var baked = new WriteableBitmap(frame);
                var strokes = InkLayer.Strokes.ToList();
                var texts = _texts.ToList();

                _rasterBitmap = baked;
                RasterLayer.Source = baked;
                RasterLayer.Visibility = Visibility.Visible;

                InkLayer.Strokes.Clear();
                foreach (var it in texts) { try { TextLayer.Children.Remove(it.Box); } catch { } }
                _texts.Clear();
                _lastTextItem = null;
                _activeText = null;
                _eraseBatch.Clear();

                if (!recordUndo) return;

                _undo.Push(new EditAction
                {
                    Undo = () =>
                    {
                        // 撤销「栅格化」：位图收起，文字 / 笔迹原样放回（仍保持已固定状态）
                        try
                        {
                            _rasterBitmap = null;
                            RasterLayer.Source = prevSource;
                            RasterLayer.Visibility = (prevVisibility == Visibility.Visible && prevSource != null)
                                ? Visibility.Visible : Visibility.Collapsed;
                            foreach (var stroke in strokes)
                                if (!InkLayer.Strokes.Contains(stroke)) InkLayer.Strokes.Add(stroke);
                            foreach (var text in texts) RestoreTextItem(text);
                        }
                        catch { }
                    },
                    Redo = () =>
                    {
                        // 重新栅格化：复用同一张位图，之后「擦除」的撤销 / 恢复还写在这个对象上
                        try
                        {
                            _rasterBitmap = baked;
                            RasterLayer.Source = baked;
                            RasterLayer.Visibility = Visibility.Visible;
                            InkLayer.Strokes.Clear();
                            foreach (var text in texts) { try { TextLayer.Children.Remove(text.Box); } catch { } }
                            _texts.Clear();
                            _lastTextItem = null;
                            _activeText = null;
                        }
                        catch { }
                    }
                });
                _redo.Clear();
                MarkDirty();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：批注栅格化失败");
            }
        }

        /// <summary>
        /// 3.40.2：把栅格层里已存在的批注（上次保存的透明 PNG）换成可写的 WriteableBitmap。
        /// 打开编辑页时批注层是只读的 BitmapImage、_rasterBitmap 为空，橡皮擦会认为
        /// 「没有可擦的内容」而完全不动作。内容不变，所以这里不记撤销。
        /// </summary>
        private void EnsureRasterWritable()
        {
            if (_rasterBitmap != null) return;
            if (RasterLayer == null) return;

            try
            {
                if (RasterLayer.Visibility != Visibility.Visible) return;
                var src = RasterLayer.Source as BitmapSource;
                if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return;

                // 统一成 Pbgra32（与 RenderStageBitmap 的产物一致）：擦除写入全 0 就是全透明
                BitmapSource prepared = src;
                if (src.Format != PixelFormats.Pbgra32)
                {
                    var converted = new FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = src;
                    converted.DestinationFormat = PixelFormats.Pbgra32;
                    converted.EndInit();
                    prepared = converted;
                }

                var writable = new WriteableBitmap(prepared);
                _rasterBitmap = writable;
                RasterLayer.Source = writable;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：批注层转可写失败");
            }
        }

        private List<string> CandidateDirs()
        {
            var dirs = new List<string>();
            // 先写程序数据目录 / 临时目录：避免把临时 PNG 丢进卡池目录（会被当成新封面）
            try { dirs.Add(Path.Combine(App.AppDataDir, "cover-edits")); } catch { }
            try { dirs.Add(Path.Combine(Path.GetTempPath(), "RadomSelectCover")); } catch { }
            try { if (!string.IsNullOrEmpty(_coverPath)) { string dir = Path.GetDirectoryName(_coverPath) ?? ""; if (dir.Length > 0) dirs.Add(dir); } } catch { }
            try { if (!string.IsNullOrEmpty(_poolDir) && Directory.Exists(_poolDir)) dirs.Add(_poolDir); } catch { }
            return dirs;
        }

        // ================================================================
        // 工具切换（始终有且仅有一个工具处于激活态）
        // ================================================================

        private void BtnTextTool_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) EndColorPick(true);
            _mode = ToolMode.Text;
            ApplyMode();
            EnsureRibbonForMode();
        }

        private void BtnPen_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) EndColorPick(true);
            _mode = ToolMode.Pen;
            ApplyMode();
            EnsureRibbonForMode();
        }

        private void BtnEraser_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) EndColorPick(true);
            _mode = ToolMode.Eraser;
            ApplyMode();
            EnsureRibbonForMode();
        }

        private void BtnHighlight_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) EndColorPick(true);

            // 有选中文字 → 给选中文字加荧光
            var item = TargetTextItem();
            bool hasTextTarget = item != null
                && (HasTextSelection(item.Box)
                    || (ReferenceEquals(_styleSelBox, item.Box) && IsRememberedRangeValid(item.Box)));

            if (hasTextTarget)
            {
                ApplyTextHighlight(_highlightColor, _textHighlightUnderline);
                return;
            }

            // 3.37：文字页签的荧光笔只负责「给文字加荧光」，不能当画笔在封面上自由涂抹
            if (_ribbonTab == "text")
            {
                try
                {
                    ToastNotification.ShowWarning(Window.GetWindow(this), "请先在文本框里选中要加荧光的文字");
                }
                catch { }
                return;
            }

            _mode = ToolMode.Highlight;
            ApplyMode();
            EnsureRibbonForMode();
        }

        /// <summary>颜色按钮：作用于当前绘图工具（画笔 / 荧光笔各自记忆颜色）。</summary>
        private void BtnPaintColor_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) EndColorPick(true);
            bool highlight = _mode == ToolMode.Highlight;
            OpenColorPanel(BtnPaintColor, highlight ? ColorTarget.Highlight : ColorTarget.Pen);
        }

        /// <summary>颜色方块跟着当前工具走：画笔显示画笔色，荧光笔显示荧光色。</summary>
        private void UpdatePaintColorSwatch()
        {
            try
            {
                if (PaintColorSwatch == null) return;
                var color = _mode == ToolMode.Highlight ? _highlightColor : _penColor;
                PaintColorSwatch.Background = new SolidColorBrush(color);
            }
            catch { }
        }

        // ================================================================
        // 3.36：取色器（吸管）
        // 从封面画面取色：图片 / GIF 按当前帧的像素取，视频按屏幕上的实际画面取
        // （视频走 MediaElement 渲染，拿不到 BitmapSource，所以读屏幕像素）。
        // 视频 / GIF 会先暂停并显示进度条，定位到想要的那一帧再取。
        // ================================================================

        private void BtnEyedropper_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) { EndColorPick(true); return; }
            StartColorPick();
        }

        private void StartColorPick()
        {
            if (!_hasMedia)
            {
                CustomMessageBox.Show(Window.GetWindow(this), "请先导入封面图片、GIF 或视频。",
                    "还没有封面", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 橡皮擦模式下不能取色（点一下会把画面擦掉），先切回画笔
            if (_mode == ToolMode.Eraser || _mode == ToolMode.None) _mode = ToolMode.Pen;

            _pickingColor = true;
            ApplyMode();                       // 3.38：取色器独占选中
            try { Stage.Cursor = Cursors.Cross; } catch { }

            // 视频 / GIF：暂停在某一帧上，取色才稳定（进度条同时出现，方便定位）
            _mediaWasPlaying = HasMediaProgress && !_mediaPaused;
            if (HasMediaProgress) PauseMedia();
            UpdateMediaBarVisibility();
        }

        private void EndColorPick(bool restorePlayback)
        {
            if (!_pickingColor) return;

            _pickingColor = false;
            ApplyMode();                       // 3.38：把高亮与光标还给当前工具
            UpdateMediaBarVisibility();

            if (restorePlayback && _mediaWasPlaying) ResumeMedia();
        }

        private void PickColorAt(Point stagePoint)
        {
            try
            {
                if (Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0) return;
                if (stagePoint.X < 0 || stagePoint.Y < 0
                    || stagePoint.X > Stage.ActualWidth || stagePoint.Y > Stage.ActualHeight) return;

                // 3.40：先取「当前编辑内容」（文字 / 荧光 / 笔迹 / 已栅格化的批注），
                // 没点到内容时再取封面画面（图片按位图取、视频按屏幕取）。
                Color? color = PickContentColor(stagePoint)
                               ?? (_isVideo ? PickScreenColor(stagePoint) : PickBitmapColor(stagePoint));
                if (color == null) return;

                ApplyPickedColor(color.Value);
                EndColorPick(true);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：取色失败");
            }
        }

        /// <summary>
        /// 3.40：取「当前编辑内容」的颜色 —— 已经写在封面上的文字、荧光、笔迹、栅格化的旧批注。
        /// 以前取色器只能取封面画面，想把自己刚写的红字颜色拿来画笔画不出来。
        /// </summary>
        private Color? PickContentColor(Point stagePoint)
        {
            bool textOnTop = Panel.GetZIndex(TextLayer) >= Panel.GetZIndex(InkLayer);

            if (textOnTop)
            {
                var text = PickTextColorAt(stagePoint);
                if (text != null) return text;
                var ink = PickInkColorAt(stagePoint);
                if (ink != null) return ink;
            }
            else
            {
                var ink = PickInkColorAt(stagePoint);
                if (ink != null) return ink;
                var text = PickTextColorAt(stagePoint);
                if (text != null) return text;
            }

            return PickRasterColorAt(stagePoint);
        }

        /// <summary>文字层：点在字符上取文字颜色；点在荧光 / 高亮底色上取底色。</summary>
        private Color? PickTextColorAt(Point stagePoint)
        {
            foreach (var item in _texts)
            {
                var box = item.Box;
                if (box == null || !box.IsVisible) continue;

                Point local;
                try { local = Stage.TranslatePoint(stagePoint, box); }
                catch { continue; }
                if (local.X < 0 || local.Y < 0 || local.X > box.ActualWidth || local.Y > box.ActualHeight) continue;

                TextPointer? pos = null;
                try { pos = box.GetPositionFromPoint(local, false); } catch { }
                if (pos == null) continue;

                try
                {
                    var next = pos.GetPositionAtOffset(1, LogicalDirection.Forward);
                    if (next == null) continue;
                    var range = new TextRange(pos, next);

                    var fg = range.GetPropertyValue(TextElement.ForegroundProperty) as SolidColorBrush;
                    var bg = range.GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;

                    // 文字之间的空白处（例如荧光条的空隙）取荧光底色
                    string ch = range.Text ?? "";
                    bool onGlyph = ch.Length > 0 && !char.IsWhiteSpace(ch[0]);
                    if (!onGlyph && bg != null && bg.Color.A > 0x20)
                        return Color.FromRgb(bg.Color.R, bg.Color.G, bg.Color.B);

                    if (fg != null)
                        return Color.FromRgb(fg.Color.R, fg.Color.G, fg.Color.B);

                    if (bg != null && bg.Color.A > 0x20)
                        return Color.FromRgb(bg.Color.R, bg.Color.G, bg.Color.B);
                }
                catch { }
            }

            return null;
        }

        /// <summary>笔迹层：点中某条笔迹时取它的颜色（荧光笔的半透明色取 RGB，不带 alpha）。</summary>
        private Color? PickInkColorAt(Point stagePoint)
        {
            try
            {
                if (InkLayer == null) return null;
                if (InkLayer.Strokes.Count == 0) return null;

                Point local = Stage.TranslatePoint(stagePoint, InkLayer);
                var hits = InkLayer.Strokes.HitTest(local, 3.0);
                if (hits == null || hits.Count == 0) return null;

                Color color = hits[hits.Count - 1].DrawingAttributes.Color;
                return Color.FromRgb(color.R, color.G, color.B);
            }
            catch { return null; }
        }

        /// <summary>栅格层：橡皮擦烘焙后的旧批注也属于「当前编辑内容」。</summary>
        private Color? PickRasterColorAt(Point stagePoint)
        {
            try
            {
                if (RasterLayer == null) return null;
                if (RasterLayer.Visibility != Visibility.Visible) return null;
                // 3.40.2：刚打开编辑页时栅格层是上次保存的只读批注，_rasterBitmap 为空，
                // 直接读 RasterLayer.Source 才能取到它的颜色。
                BitmapSource? source = _rasterBitmap ?? RasterLayer.Source as BitmapSource;
                return ReadBitmapPixel(source, RasterLayer, stagePoint, skipTransparent: true);
            }
            catch { return null; }
        }

        /// <summary>按 Stage 坐标从一张铺满 Stage 的位图上读一个像素。</summary>
        private static Color? ReadBitmapPixel(BitmapSource? source, FrameworkElement target, Point stagePoint,
                                              bool skipTransparent)
        {
            try
            {
                if (source == null || target == null) return null;
                if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return null;
                if (target.ActualWidth <= 0 || target.ActualHeight <= 0) return null;

                double sx = source.PixelWidth / target.ActualWidth;
                double sy = source.PixelHeight / target.ActualHeight;

                int x = Math.Max(0, Math.Min(source.PixelWidth - 1, (int)(stagePoint.X * sx)));
                int y = Math.Max(0, Math.Min(source.PixelHeight - 1, (int)(stagePoint.Y * sy)));

                BitmapSource read = source;
                if (source.Format != PixelFormats.Bgra32)
                {
                    var converted = new FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = source;
                    converted.DestinationFormat = PixelFormats.Bgra32;
                    converted.EndInit();
                    converted.Freeze();
                    read = converted;
                }

                var px = new byte[4];
                read.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                if (skipTransparent && px[3] < 8) return null;
                return Color.FromRgb(px[2], px[1], px[0]);
            }
            catch { return null; }
        }

        /// <summary>图片 / GIF：直接从当前帧像素取色（比读屏幕准，不受窗口边框影响）。</summary>
        private Color? PickBitmapColor(Point stagePoint)
        {
            return ReadBitmapPixel(BaseImage.Source as BitmapSource, Stage, stagePoint, skipTransparent: false);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int x, int y);

        /// <summary>视频：读屏幕像素（MediaElement 的画面不在 WPF 的位图层里，RenderTargetBitmap 抓不到）。</summary>
        private Color? PickScreenColor(Point stagePoint)
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                Point screen = Stage.PointToScreen(stagePoint);
                hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return null;

                uint v = GetPixel(hdc, (int)Math.Round(screen.X), (int)Math.Round(screen.Y));
                if (v == 0xFFFFFFFF) return null;

                byte r = (byte)(v & 0xFF);
                byte g = (byte)((v >> 8) & 0xFF);
                byte b = (byte)((v >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
            catch { return null; }
            finally
            {
                if (hdc != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, hdc); } catch { } }
            }
        }

        private void ApplyPickedColor(Color color)
        {
            if (_mode == ToolMode.Highlight)
            {
                _highlightColor = color;
            }
            else
            {
                _penColor = color;
                if (_mode != ToolMode.Pen)
                {
                    _mode = ToolMode.Pen;
                    ApplyMode();
                }
            }

            UpdatePaintColorSwatch();
            ApplyDrawingAttributes();
            MarkDirty();
        }

        private void ComboPenSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 每个下拉项就是一条粗细不同的水平直线，直接读它自己的 Thickness
            if (ComboPenSize.SelectedItem is PenWidthItem item && item.Thickness > 0)
                _penWidth = item.Thickness;

            UpdatePenSizePreview();
            ApplyDrawingAttributes();
        }

        /// <summary>收起状态下显示当前粗细的那条线（下拉项本身不显示文字）。</summary>
        private void UpdatePenSizePreview()
        {
            try
            {
                if (PenSizePreview == null) return;
                double thickness = Math.Max(2, _penWidth);
                PenSizePreview.Height = thickness;
                PenSizePreview.RadiusX = thickness / 2.0;
                PenSizePreview.RadiusY = thickness / 2.0;
            }
            catch { }
        }

        private void ApplyDrawingAttributes()
        {
            if (InkLayer == null) return;

            var da = InkLayer.DefaultDrawingAttributes;
            bool highlight = _mode == ToolMode.Highlight;
            da.Color = highlight
                ? Color.FromArgb(0x80, _highlightColor.R, _highlightColor.G, _highlightColor.B)
                : _penColor;
            da.Width = highlight ? Math.Max(14, _penWidth * 2.4) : _penWidth;
            da.Height = da.Width;
            da.IsHighlighter = false;
        }

        // ================================================================
        // 功能区页签（3.35：WPS 风格）
        // 左侧常驻工具组（文本框 / 画笔 / 荧光笔 / 橡皮擦），右侧页签只切换属性区：
        //   文字 → 字体 / 字号 / 颜色 / 粗斜体；
        //   绘图 → 画笔颜色 / 粗细；
        //   样式 → 圆角 / 方角。
        // 荧光笔与橡皮擦属于常驻工具组，两个页签下都能用（切换页签不会丢掉工具）。
        // ================================================================

        /// <summary>程序化切换页签时避免 RadioButton 的 Checked 再回调一次。</summary>
        private bool _ribbonSync;

        private void RibbonTab_Checked(object sender, RoutedEventArgs e)
        {
            if (_ribbonSync) return;
            if (ReferenceEquals(sender, TabText)) ShowRibbon("text");
            else if (ReferenceEquals(sender, TabDraw)) ShowRibbon("draw");
            else if (ReferenceEquals(sender, TabStyle)) ShowRibbon("style");
        }

        /// <summary>3.38：页签切换的代号 —— 快速连点页签时只让最后一次的动画收尾生效。</summary>
        private int _ribbonSwitchToken;

        private void ShowRibbon(string tab, bool animate = true)
        {
            if (tab != "draw" && tab != "style") tab = "text";
            bool changed = _ribbonTab != tab;
            string oldTab = _ribbonTab;
            _ribbonTab = tab;

            try
            {
                _ribbonSync = true;
                if (TabText != null) TabText.IsChecked = tab == "text";
                if (TabDraw != null) TabDraw.IsChecked = tab == "draw";
                if (TabStyle != null) TabStyle.IsChecked = tab == "style";
            }
            catch { }
            finally { _ribbonSync = false; }

            UpdatePaintColorVisibility();
            AdjustModeForTab(tab);
            UpdateTabIndicator(changed && animate);

            var newPanel = PanelForTab(tab);
            bool fade = changed && animate && !App.IsAnimationDisabled;

            if (!fade)
            {
                UpdateToolVisibility(tab);
                ApplyRibbonInstant(newPanel);
                return;
            }

            int token = ++_ribbonSwitchToken;
            var oldPanel = PanelForTab(oldTab);

            // 旧内容（属性区 + 左侧工具组）先快速淡出
            bool anyVisible = false;
            if (oldPanel != null && oldPanel.Visibility == Visibility.Visible)
            {
                anyVisible = true;
                FadeOutRibbonPart(oldPanel);
            }
            if (ToolGroupRoot != null && ToolGroupRoot.Visibility == Visibility.Visible)
            {
                anyVisible = true;
                FadeOutRibbonPart(ToolGroupRoot);
            }

            if (!anyVisible)
            {
                FinishRibbonSwitch(token, tab, newPanel);
                return;
            }

            // 60 ms 后统一收尾（用计时器而不是依赖 Completed，避免新旧动画互相打断）
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += (s, e) =>
            {
                try { timer.Stop(); } catch { }
                FinishRibbonSwitch(token, tab, newPanel);
            };
            timer.Start();
        }

        private void FinishRibbonSwitch(int token, string tab, FrameworkElement? newPanel)
        {
            if (token != _ribbonSwitchToken) return;

            try
            {
                UpdateToolVisibility(tab);
                ApplyRibbonInstant(newPanel);

                // 新内容从左侧淡入；左侧工具组一起入场（3.38）
                AnimateRibbonPartIn(newPanel, TimeSpan.FromMilliseconds(40));
                if (ToolGroupRoot != null && ToolGroupRoot.Visibility == Visibility.Visible)
                    AnimateRibbonPartIn(ToolGroupRoot, TimeSpan.FromMilliseconds(20));
            }
            catch { }
        }

        private static void FadeOutRibbonPart(FrameworkElement part)
        {
            try
            {
                part.IsHitTestVisible = false;
                var anim = new DoubleAnimation(part.Opacity, 0, TimeSpan.FromMilliseconds(60))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (s, e) =>
                {
                    try
                    {
                        part.BeginAnimation(UIElement.OpacityProperty, null);
                        part.Opacity = 0;
                        part.IsHitTestVisible = true;
                    }
                    catch { }
                };
                part.BeginAnimation(UIElement.OpacityProperty, anim);
            }
            catch { }
        }

        private static void AnimateRibbonPartIn(FrameworkElement? part, TimeSpan begin)
        {
            if (part == null) return;

            try
            {
                part.Visibility = Visibility.Visible;
                part.IsHitTestVisible = true;
                part.Opacity = 0;

                var tt = EnsurePanelShift(part);
                tt.X = -12;
                tt.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(140))
                    {
                        BeginTime = begin,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                part.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = begin });
            }
            catch { }
        }

        /// <summary>无动画地把功能区切到目标状态（其它面板收起、目标面板显示）。</summary>
        private void ApplyRibbonInstant(FrameworkElement? newPanel)
        {
            try
            {
                foreach (var panel in new[] { PanelTextTools, PanelDrawTools, PanelStyleTools })
                {
                    if (ReferenceEquals(panel, newPanel)) continue;
                    panel.BeginAnimation(UIElement.OpacityProperty, null);
                    panel.Opacity = 0;
                    panel.Visibility = Visibility.Collapsed;
                    ResetPanelShift(panel);
                }

                if (newPanel != null)
                {
                    newPanel.BeginAnimation(UIElement.OpacityProperty, null);
                    newPanel.Visibility = Visibility.Visible;
                    newPanel.Opacity = 1;
                    ResetPanelShift(newPanel);
                    Panel.SetZIndex(newPanel, 0);
                }

                if (ToolGroupRoot != null)
                {
                    ToolGroupRoot.BeginAnimation(UIElement.OpacityProperty, null);
                    ToolGroupRoot.Opacity = 1;
                    ResetPanelShift(ToolGroupRoot);
                }
            }
            catch { }
        }

        private FrameworkElement? PanelForTab(string tab)
        {
            if (tab == "draw") return PanelDrawTools;
            if (tab == "style") return PanelStyleTools;
            return PanelTextTools;
        }

        /// <summary>
        /// 3.37：页签指示条跟随选中项平滑滑动（参考选项页导航指示条），
        /// 横线只盖在文字下方，颜色是灰色。
        /// </summary>
        private void UpdateTabIndicator(bool animate)
        {
            try
            {
                if (TabIndicator == null || TabStrip == null) return;

                RadioButton? target = _ribbonTab == "draw" ? TabDraw
                                    : _ribbonTab == "style" ? TabStyle
                                    : TabText;
                if (target == null) return;

                double padding = 12;   // 与 RibbonTabButton 的 Padding 一致
                double width = Math.Max(8, target.ActualWidth - padding * 2);
                double x = 0;
                try
                {
                    x = target.TranslatePoint(new Point(padding, 0), TabStrip).X;
                }
                catch { }

                if (TabIndicator.RenderTransform is not TranslateTransform tt)
                {
                    tt = new TranslateTransform();
                    TabIndicator.RenderTransform = tt;
                }

                if (!animate || App.IsAnimationDisabled)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    TabIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
                    TabIndicator.Width = width;
                    tt.X = x;
                    return;
                }

                TabIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
                TabIndicator.Width = width;

                tt.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(tt.X, x, TimeSpan.FromMilliseconds(220))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            catch { }
        }

        private static TranslateTransform EnsurePanelShift(FrameworkElement panel)
        {
            if (panel.RenderTransform is TranslateTransform tt) return tt;
            tt = new TranslateTransform();
            panel.RenderTransform = tt;
            return tt;
        }

        private static void ResetPanelShift(FrameworkElement panel)
        {
            try
            {
                if (panel.RenderTransform is TranslateTransform tt)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.X = 0;
                }
            }
            catch { }
        }

        /// <summary>3.36：工具按钮按页签显示 —— 文字页签没有笔 / 橡皮擦、绘图页签没有文本框。</summary>
        private void UpdateToolVisibility(string tab)
        {
            try
            {
                bool text = tab == "text";
                bool draw = tab == "draw";

                if (ToolGroupRoot != null)
                    ToolGroupRoot.Visibility = (text || draw) ? Visibility.Visible : Visibility.Collapsed;

                if (BtnTextTool != null)
                    BtnTextTool.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
                // 3.42：「选择」在文字 / 绘图两个页签都有，位置在最左侧
                if (BtnSelectTool != null)
                    BtnSelectTool.Visibility = (text || draw) ? Visibility.Visible : Visibility.Collapsed;
                if (BtnHighlight != null)
                    BtnHighlight.Visibility = (text || draw) ? Visibility.Visible : Visibility.Collapsed;
                // 3.39：荧光样式下拉只在文字页签出现（绘图页签的荧光笔是自由涂抹）
                if (BtnHighlightMenu != null)
                    BtnHighlightMenu.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
                if (BtnPen != null)
                    BtnPen.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
                if (BtnEraser != null)
                    BtnEraser.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;
                if (BtnEyedropper != null)
                    BtnEyedropper.Visibility = draw ? Visibility.Visible : Visibility.Collapsed;

                // 3.40.1：荧光笔在文字页签是「给文字加荧光」，绘图页签是「在封面上涂抹」
                if (_highlightHint != null)
                    _highlightHint.Text = text ? "荧光笔：给选中的文字加荧光" : "荧光笔：在封面上涂抹";

                // 离开绘图页签时退出取色（不然光标还停在十字上）
                if (!draw) EndColorPick(true);
            }
            catch { }
        }

        /// <summary>3.36：切页签后把当前工具修正到该页签可用的模式。</summary>
        private void AdjustModeForTab(string tab)
        {
            try
            {
                if (tab == "text" && _mode != ToolMode.Text)
                {
                    // 3.37：文字页签只有「文本框」这一种自由工具 —— 荧光笔在这里只给文字上色，
                    // 所以从别的页签带着画笔 / 荧光笔 / 橡皮擦过来时统一切回文本框。
                    _mode = ToolMode.Text;
                    ApplyMode();
                }
                else if (tab == "draw" && (_mode == ToolMode.Text || _mode == ToolMode.None || _mode == ToolMode.Select))
                {
                    _mode = ToolMode.Pen;
                    ApplyMode();
                }
                else if (tab == "style" && _mode != ToolMode.None)
                {
                    // 3.40：样式页签没有工具。以前切过来只是把左侧工具组收起来，_mode 还留着，
                    // 画布照样能画 / 能编辑文字，看起来「上一个页签的工具还能用」。
                    _mode = ToolMode.None;
                    ApplyMode();
                }
            }
            catch { }
        }

        /// <summary>
        /// 3.39：给选中文字加荧光。两种样式 —— 背景高亮（半透明色块）或彩色下划线。
        /// </summary>
        private void ApplyTextHighlight(Color color, bool underline)
        {
            if (TargetTextItem() == null) return;
            try
            {
                if (underline)
                {
                    var pen = new Pen(new SolidColorBrush(color), 3);
                    var decorations = new TextDecorationCollection
                    {
                        new TextDecoration { Location = TextDecorationLocation.Underline, Pen = pen }
                    };
                    ApplyToSelection(Inline.TextDecorationsProperty, decorations);
                }
                else
                {
                    ApplyToSelection(TextElement.BackgroundProperty,
                        new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B)));
                }
            }
            catch { }
        }

        /// <summary>3.39：荧光笔样式下拉（高亮 / 下划线 × 黄绿粉蓝）。</summary>
        private void BtnHighlightMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menu = BuildHighlightMenu();
                menu.PlacementTarget = BtnHighlightMenu;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：打开荧光样式菜单失败");
            }
        }

        /// <summary>
        /// 3.41：构建荧光笔样式下拉。抽成方法，测试可以直接检查菜单项
        /// （是否还有分隔线、选中的颜色后面有没有勾）。
        /// </summary>
        private ContextMenu BuildHighlightMenu()
        {
            var menu = new ContextMenu();

            bool modern = App.UseModernContextMenu;
            if (TryFindResource(modern ? "Win11ContextMenuStyle" : "LegacyContextMenuStyle") is Style menuStyle)
                menu.Style = menuStyle;
            var itemStyle = TryFindResource(modern ? "Win11MenuItemStyle" : "LegacyMenuItemStyle") as Style;

            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0xFF, 0xF1, 0x8A), false, "高亮 · 黄色");
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0xB9, 0xF2, 0xB0), false, "高亮 · 绿色");
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0xFF, 0xC9, 0xD7), false, "高亮 · 粉色");
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0xBC, 0xE1, 0xFF), false, "高亮 · 蓝色");
            // 3.41：高亮组与下划线组之间不再画分隔线
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0xF2, 0xB8, 0x00), true, "下划线 · 黄色");
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0x2E, 0xA0, 0x43), true, "下划线 · 绿色");
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0xE0, 0x4A, 0x8B), true, "下划线 · 粉色");
            AddHighlightMenuItem(menu, itemStyle, Color.FromRgb(0x1E, 0x7F, 0xD8), true, "下划线 · 蓝色");

            return menu;
        }

        /// <summary>3.40.1：鼠标停在荧光样式箭头上时，左侧荧光笔图标一起变深色（像整组按钮在悬浮）。</summary>
        private void BtnHighlightMenu_MouseEnter(object sender, MouseEventArgs e)
        {
            try { HighlightSwatch?.SetResourceReference(TextBlock.ForegroundProperty, "Fg_000000"); } catch { }
        }

        private void BtnHighlightMenu_MouseLeave(object sender, MouseEventArgs e)
        {
            try { HighlightSwatch?.ClearValue(TextBlock.ForegroundProperty); } catch { }
        }

        private void AddHighlightMenuItem(ContextMenu menu, Style? itemStyle, Color color, bool underline, string text)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 3.41：当前选中的那一项在文字后面显示勾图标（\uE8FB）
            bool isCurrent = _textHighlightUnderline == underline && _highlightColor == color;
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            if (isCurrent)
            {
                var iconFont = TryFindResource("AppIconFont") as FontFamily
                               ?? new FontFamily("Segoe Fluent Icons");
                header.Children.Add(new TextBlock
                {
                    Text = "\uE8FB",
                    FontFamily = iconFont,
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var item = new MenuItem
            {
                Header = header,
                Icon = swatch
            };
            if (itemStyle != null) item.Style = itemStyle;

            item.Click += (s, args) =>
            {
                _highlightColor = color;
                _textHighlightUnderline = underline;
                UpdatePaintColorSwatch();
                ApplyTextHighlight(color, underline);
            };

            menu.Items.Add(item);
        }

        /// <summary>橡皮擦只用「粗细」：绘图页签里的颜色按钮在这种情况下没有意义。</summary>
        private void UpdatePaintColorVisibility()
        {
            try
            {
                if (BtnPaintColor != null)
                    BtnPaintColor.Visibility = _mode == ToolMode.Eraser ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }

        /// <summary>点左侧工具时：若当前停在「样式」页签，切回该工具对应的属性页。</summary>
        private void EnsureRibbonForMode()
        {
            if (_ribbonTab != "style") return;
            ShowRibbon(_mode == ToolMode.Text ? "text" : "draw");
        }

        private void ApplyMode()
        {
            UpdateToolVisuals();

            UpdatePaintColorSwatch();

            bool textMode = _mode == ToolMode.Text;
            bool eraserMode = _mode == ToolMode.Eraser;
            // 3.40：只有画笔 / 荧光笔才让 InkCanvas 接收鼠标；样式页签（None）下画布不落笔。
            bool drawMode = _mode == ToolMode.Pen || _mode == ToolMode.Highlight;

            // 橡皮擦由页面自己处理（要按方形擦到文字），所以不让 InkCanvas 抢点击。
            // 3.39：取色模式下 InkCanvas 也必须让开 —— 它是 Stage 的子元素，
            // 会先把鼠标按下当成「开始画一笔」，取色器看起来就还是画笔。
            InkLayer.IsHitTestVisible = !_pickingColor && drawMode;
            InkLayer.EditingMode = InkCanvasEditingMode.Ink;
            ApplyDrawingAttributes();

            // 3.31：橡皮擦不再用十字光标（看起来像凭空多了一个「+」），统一箭头 + 跟随鼠标的方框
            Stage.Cursor = _pickingColor ? Cursors.Cross
                         : textMode ? Cursors.IBeam : Cursors.Arrow;

            if (eraserMode) UpdateEraserFrame(Mouse.GetPosition(Stage));
            else HideEraserFrame();

            TextLayer.IsHitTestVisible = textMode && !_pickingColor;
            if (!textMode) CommitActiveText();

            // 3.35：绘图页签里，橡皮擦没有「颜色」概念
            UpdatePaintColorVisibility();

            // 3.33：切到橡皮擦时先把当前批注烘焙成位图，之后按方形区域擦像素
            if (eraserMode) FlattenVectorsIntoRaster(recordUndo: true);

            // 3.42：选择工具需要键盘焦点（Ctrl+C / Delete / 方向键）；切到别的工具时清掉选框
            if (_mode == ToolMode.Select)
            {
                // 3.43：选择工具直接改位图 —— 先把文字 / 笔迹烘焙进可写位图（可撤销）
                if (!_pickingColor) EnsureSelectionRaster();
                try { Stage.Focus(); } catch { }
            }
            else
            {
                ClearSelection();
            }
        }

        private void UpdateToolVisuals()
        {
            // 3.38：取色模式下只点亮取色器。
            // 以前取色器的选中态和 _mode（仍是画笔）各画各的，会出现两个工具同时高亮；
            // 这时点画布又被取色逻辑接管，看起来「完全没法用」。
            bool picking = _pickingColor;

            SetToggle(BtnTextTool, !picking && _mode == ToolMode.Text);
            SetToggle(BtnPen, !picking && _mode == ToolMode.Pen);
            SetToggle(BtnEraser, !picking && _mode == ToolMode.Eraser);
            SetToggle(BtnHighlight, !picking && _mode == ToolMode.Highlight);
            if (BtnSelectTool != null) SetToggle(BtnSelectTool, !picking && _mode == ToolMode.Select);
            if (BtnEyedropper != null) SetToggle(BtnEyedropper, picking);
        }

        private static void SetToggle(Button button, bool on)
        {
            if (button == null) return;
            if (on)
            {
                button.SetResourceReference(Control.BackgroundProperty, "Bg_E5F3FF");
                button.SetResourceReference(Control.BorderBrushProperty, "Fg_0078D7");
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
            }
        }

        // ================================================================
        // 字号
        // ================================================================

        private void BuildFontSizeItems()
        {
            ComboFontSize.Items.Clear();
            for (int size = 12; size <= 200; size += 4)
                ComboFontSize.Items.Add(size.ToString());
            ComboFontSize.Text = ((int)_fontSize).ToString();
        }

        // ================================================================
        // 字体（3.35）
        // 3.40：显示字体的中文名（没有中文名的显示英文名）；下拉里每项用该字体本身显示，
        // 只有符号 / 图标字体（Wingdings、MS Outlook、MDL2 这类）改用界面字体渲染名字，
        // 否则名字会变成「◆ □□□□□□」这种看不懂的图案。选中后按真实字体名（Source）应用。
        // ================================================================

        private sealed class FontChoice
        {
            public FontFamily Family { get; init; } = new FontFamily("Microsoft YaHei");
            public string Name { get; init; } = "";
            /// <summary>下拉项名称用哪套字体渲染：一般是该字体本身，符号字体回退界面字体。</summary>
            public FontFamily DisplayFamily { get; init; } = new FontFamily("Microsoft YaHei");

            /// <summary>ComboBox 收起时没有套 ItemTemplate，直接显示 ToString()。</summary>
            public override string ToString() => Name;
        }

        /// <summary>符号 / 图标字体的名字：用自身渲染只会看到图标和方框。</summary>
        private static readonly HashSet<string> IconFontSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "symbol", "wingdings", "wingdings 2", "wingdings 3", "webdings", "marlett",
            "ms outlook", "mt extra", "segoe mdl2 assets", "segoe fluent icons",
            "ms reference specialty", "bookshelf symbol 7"
        };

        private static readonly FontFamily UiFontFamily = new FontFamily("Microsoft YaHei");

        private static bool IsIconFont(FontFamily family)
        {
            string source = (family?.Source ?? "").Trim();
            return source.Length > 0 && IconFontSources.Contains(source);
        }

        /// <summary>字体的中文名（zh-cn / zh-hans / zh-hant / zh-tw / ja-jp / en-us 依次回退）。</summary>
        private static string FontDisplayName(FontFamily family)
        {
            try
            {
                foreach (var tag in new[] { "zh-cn", "zh-hans", "zh-hant", "zh-tw", "zh-hk", "ja-jp", "en-us" })
                {
                    if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(tag), out string? name)
                        && !string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }

                foreach (var pair in family.FamilyNames)
                {
                    if (pair.Key.IetfLanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        return pair.Value;
                    }
                }
            }
            catch { }

            return family.Source ?? "";
        }

        private void BuildFontFamilyItems()
        {
            try
            {
                var choices = Fonts.SystemFontFamilies
                    .Where(f => f != null && !string.IsNullOrEmpty(f.Source) && !f.Source.StartsWith("#"))
                    .Select(f => new FontChoice
                    {
                        Family = f,
                        Name = FontDisplayName(f),
                        DisplayFamily = IsIconFont(f) ? UiFontFamily : f
                    })
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                ComboFontFamily.ItemsSource = choices;

                var current = choices.FirstOrDefault(c =>
                                  string.Equals(c.Family.Source, _fontFamily, StringComparison.OrdinalIgnoreCase))
                              ?? choices.FirstOrDefault(c =>
                                  string.Equals(c.Family.Source, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase))
                              ?? choices.FirstOrDefault();
                if (current != null) ComboFontFamily.SelectedItem = current;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：枚举系统字体失败");
            }
        }

        private void ComboFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboFontFamily.SelectedItem is not FontChoice choice) return;
            string name = choice.Family.Source;
            if (string.IsNullOrEmpty(name) || string.Equals(name, _fontFamily, StringComparison.Ordinal)) return;

            _fontFamily = name;
            ApplyToSelection(TextElement.FontFamilyProperty, choice.Family);
        }

        private void ApplyFontSizeToSelection()
        {
            if (TargetTextItem() == null) return;
            ApplyToSelection(TextElement.FontSizeProperty, _fontSize);
        }

        /// <summary>字体名非法（被删掉的字体等）时回退到微软雅黑，避免 RichTextBox 抛异常。</summary>
        private static FontFamily SafeFontFamily(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return new FontFamily("Microsoft YaHei");
                return new FontFamily(name);
            }
            catch
            {
                return new FontFamily("Microsoft YaHei");
            }
        }

        private void ComboFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboFontSize.SelectedItem is string s && double.TryParse(s, out double size))
            {
                _fontSize = Math.Max(6, Math.Min(400, size));
                ApplyFontSizeToSelection();
            }
        }

        private void ComboFontSize_LostFocus(object sender, RoutedEventArgs e) => CommitFontSizeText();

        private void ComboFontSize_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitFontSizeText(); e.Handled = true; }
        }

        private void CommitFontSizeText()
        {
            if (double.TryParse((ComboFontSize.Text ?? "").Trim(), out double size))
            {
                _fontSize = Math.Max(6, Math.Min(400, size));
                ApplyFontSizeToSelection();
            }
            else
            {
                ComboFontSize.Text = ((int)_fontSize).ToString();
            }
        }

        // ================================================================
        // 文本样式（只作用于选中的文字）
        // ================================================================

        // ================================================================
        // 颜色面板（3.32）
        // 不弹独立对话框：点「颜色」在按钮下方弹出内嵌面板（带指向按钮的小三角），
        // 拖动色块 / 选常用色 / 输十六进制都立即生效；点面板外自动收起。
        // ================================================================

        private enum ColorTarget { Text, Pen, Highlight }

        private ColorTarget _colorTarget = ColorTarget.Text;
        private TextItem? _colorTargetText;
        private TextPointer? _colorSelStart;
        private TextPointer? _colorSelEnd;
        private Button? _colorPopupButton;
        /// <summary>3.39：这次打开颜色面板时的锚点按钮（Closed 不清空，供尖角对齐使用）。</summary>
        private Button? _colorPopupAnchor;
        /// <summary>3.64：打开颜色面板之前的颜色，点「取消 / ×」时还原。</summary>
        private Color _colorBeforePanel;
        private DateTime _colorPopupClosedUtc;
        private Button? _colorPopupClosedButton;
        private HintBubble? _textColorHint;
        private HintBubble? _paintColorHint;
        /// <summary>3.40.1：荧光笔的提示语按页签切换（文字页签加荧光 / 绘图页签涂抹）。</summary>
        private HintBubble? _highlightHint;

        private void BtnTextColor_Click(object sender, RoutedEventArgs e)
        {
            OpenColorPanel(BtnTextColor, ColorTarget.Text);
        }

        /// <summary>打开颜色面板（同一个按钮再点一次 = 收起，靠 Popup 的 Closed 时间戳判断）。</summary>
        private void OpenColorPanel(Button target, ColorTarget kind)
        {
            try
            {
                if (target == null || ColorPanel == null || ColorPopup == null) return;

                // 点同一个按钮时，Popup 会先因为 StaysOpen=False 关闭并触发 Closed；
                // 这里的 Click 紧随其后，不能立刻又打开。
                if (ReferenceEquals(_colorPopupClosedButton, target)
                    && (DateTime.UtcNow - _colorPopupClosedUtc).TotalMilliseconds < 350)
                {
                    _colorPopupClosedButton = null;
                    return;
                }

                _colorTarget = kind;
                _colorTargetText = kind == ColorTarget.Text ? TargetTextItem() : null;
                _colorSelStart = null;
                _colorSelEnd = null;
                if (kind == ColorTarget.Text && _colorTargetText != null)
                {
                    try
                    {
                        _colorSelStart = _colorTargetText.Box.Selection.Start;
                        _colorSelEnd = _colorTargetText.Box.Selection.End;
                    }
                    catch { }
                }

                Color current = kind == ColorTarget.Text ? _textColor
                              : kind == ColorTarget.Highlight ? _highlightColor
                              : _penColor;
                _colorBeforePanel = current;
                // 3.33：文字 / 画笔共用一个「颜色」面板（作用对象由入口决定）
                ColorPanel.SetColor(current, "颜色");

                // 3.39：外层为阴影留了 20 DIP 左边距，这里一并扣除；
                // 尖角的最终位置在 Opened 里按真实屏幕坐标校正（Popup 可能被屏幕边缘挤动）。
                _colorPopupButton = target;
                _colorPopupAnchor = target;
                ColorPopup.PlacementTarget = target;
                ColorPopup.HorizontalOffset = target.ActualWidth / 2 - 31 - 20;
                ColorPopup.VerticalOffset = 0;
                ColorPopup.IsOpen = true;

                if (kind == ColorTarget.Text) _textColorHint?.Hide();
                else _paintColorHint?.Hide();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：打开颜色面板失败");
            }
        }

        /// <summary>3.39：按打开后的真实位置把尖角对准被点击的按钮。</summary>
        private void ColorPopup_Opened(object sender, EventArgs e)
        {
            // Opened 触发时内容可能还没完成布局（ActualWidth 为 0），延后到布局阶段再校正
            Dispatcher.BeginInvoke(new Action(AlignColorPopupArrow), DispatcherPriority.Loaded);
        }

        private void AlignColorPopupArrow()
        {
            try
            {
                var target = _colorPopupAnchor;
                var card = ColorCard;
                if (target == null || card == null || ColorPopupArrow == null) return;
                if (card.ActualWidth <= 0) return;

                Point buttonCenter = target.PointToScreen(new Point(target.ActualWidth / 2, 0));
                Point origin = card.PointToScreen(new Point(0, 0));

                // 3.64：PointToScreen 返回的是物理像素，而 Margin 用的是 DIP。
                // 系统缩放 125% / 150% 时两边单位不一致，尖角会偏离按钮 —— 这里先换算回 DIP。
                Matrix toDip = Matrix.Identity;
                try
                {
                    var source = PresentationSource.FromVisual(card);
                    if (source?.CompositionTarget != null) toDip = source.CompositionTarget.TransformFromDevice;
                }
                catch { }
                Point offset = toDip.Transform(new Point(buttonCenter.X - origin.X, buttonCenter.Y - origin.Y));

                double x = offset.X - 7;   // 三角宽 14，中心对准按钮中心
                double max = Math.Max(14, card.ActualWidth - 28);
                x = Math.Max(14, Math.Min(max, x));

                ColorPopupArrow.Margin = new Thickness(x, -7, 0, 0);
            }
            catch { }
        }

        /// <summary>3.64：点「取消 / ×」——把颜色还原成打开面板之前的样子，然后收起面板。</summary>
        private void CancelColorPanel()
        {
            try
            {
                Color previous = _colorBeforePanel;
                if (ColorPopup != null) ColorPopup.IsOpen = false;
                ColorPanel_ColorChanged(previous);
                try { ColorPanel?.SetColor(previous, null); } catch { }
            }
            catch { }
        }

        private void ColorPopup_Closed(object sender, EventArgs e)
        {
            _colorPopupClosedUtc = DateTime.UtcNow;
            _colorPopupClosedButton = _colorPopupButton;
            _colorPopupButton = null;
        }

        /// <summary>面板里颜色一变就应用：文字改当前 / 最近文本框，画笔 / 荧光笔记住各自颜色。</summary>
        private void ColorPanel_ColorChanged(Color color)
        {
            try
            {
                switch (_colorTarget)
                {
                    case ColorTarget.Text:
                        _textColor = color;
                        if (TextColorSwatch != null) TextColorSwatch.Background = new SolidColorBrush(color);

                        // 打开面板会让文本框失焦，选区可能被清掉：恢复后再应用
                        if (_colorTargetText != null && _colorSelStart != null && _colorSelEnd != null
                            && _texts.Contains(_colorTargetText))
                        {
                            try { _colorTargetText.Box.Selection.Select(_colorSelStart, _colorSelEnd); } catch { }
                        }
                        ApplyTextColor();
                        break;

                    case ColorTarget.Highlight:
                        _highlightColor = color;
                        UpdatePaintColorSwatch();
                        ApplyDrawingAttributes();
                        MarkDirty();
                        break;

                    default:
                        _penColor = color;
                        UpdatePaintColorSwatch();
                        ApplyDrawingAttributes();
                        MarkDirty();
                        break;
                }
            }
            catch { }
        }

        /// <summary>
        /// 应用文字颜色：有选中就只改选中部分；没有选中则同时改整段和文本框默认色
        /// （否则「先选颜色、再开始输入」看起来像没生效）。
        /// 3.36：有选区时不再设置 <see cref="RichTextBox.Foreground"/> —— 那是整框默认色，
        /// 会把没选中的文字一起染色（「改颜色却改了整个文本框」就是这么来的）。
        /// </summary>
        private void ApplyTextColor()
        {
            var item = TargetTextItem();
            if (item == null) return;

            try
            {
                var brush = new SolidColorBrush(_textColor);

                bool wholeBox = !HasTextSelection(item.Box)
                                && !(ReferenceEquals(_styleSelBox, item.Box) && IsRememberedRangeValid(item.Box));
                if (wholeBox) item.Box.Foreground = brush;

                ApplyToSelection(TextElement.ForegroundProperty, brush);
                MarkDirty();
            }
            catch { }
        }

        /// <summary>当前作用目标：活动文本框；没有活动文本框时用最近编辑过的那个（固定后仍能改色 / 改样式）。</summary>
        private TextItem? TargetTextItem()
        {
            if (_activeText != null) return _activeText;
            if (_lastTextItem != null && _texts.Contains(_lastTextItem)) return _lastTextItem;
            return null;
        }

        private void BtnBold_Click(object sender, RoutedEventArgs e) => ToggleTextStyle(TextElement.FontWeightProperty, FontWeights.Bold, FontWeights.Normal);
        private void BtnItalic_Click(object sender, RoutedEventArgs e) => ToggleTextStyle(TextElement.FontStyleProperty, FontStyles.Italic, FontStyles.Normal);

        private void BtnUnderline_Click(object sender, RoutedEventArgs e) =>
            ToggleDecoration(TextDecorationLocation.Underline);

        private void BtnStrike_Click(object sender, RoutedEventArgs e) =>
            ToggleDecoration(TextDecorationLocation.Strikethrough);

        /// <summary>
        /// 3.36：下划线 / 删除线各自独立切换（以前用集合相等比较，
        /// 取到的永远是新建的集合，结果只能加不能取消，而且会互相覆盖）。
        /// </summary>
        private void ToggleDecoration(TextDecorationLocation location)
        {
            if (TargetTextItem() == null) return;

            try
            {
                var current = GetSelectionValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
                bool has = false;
                var result = new TextDecorationCollection();

                if (current != null)
                {
                    foreach (var d in current)
                    {
                        try
                        {
                            if (d.Location == location) { has = true; continue; }
                            result.Add(d.Clone());
                        }
                        catch { }
                    }
                }

                if (!has) result.Add(new TextDecoration { Location = location });
                ApplyToSelection(Inline.TextDecorationsProperty, result);
            }
            catch { }
        }

        private void ToggleTextStyle(DependencyProperty property, object on, object off)
        {
            if (TargetTextItem() == null) return;
            bool currently = Equals(GetSelectionValue(property), on);
            ApplyToSelection(property, currently ? off : on);
        }

        private object? GetSelectionValue(DependencyProperty property)
        {
            try
            {
                var item = TargetTextItem();
                if (item == null) return null;
                return BuildTargetRange(item.Box).GetPropertyValue(property);
            }
            catch { return null; }
        }

        private static bool HasTextSelection(RichTextBox box)
        {
            try { return box != null && !box.Selection.IsEmpty; } catch { return false; }
        }

        private void ApplyToSelection(DependencyProperty property, object value)
        {
            var item = TargetTextItem();
            if (item == null) return;

            try
            {
                var range = BuildTargetRange(item.Box);

                // 应用格式本身会触发 SelectionChanged，别让它把记住的选区覆盖掉
                _applyingStyle = true;
                try { range.ApplyPropertyValue(property, value); }
                finally { _applyingStyle = false; }

                MarkDirty();
            }
            catch { }
        }

        /// <summary>
        /// 3.36：格式作用范围 —— 优先当前选区；选区因为失焦被收掉时退回「最近一次有效选区」；
        /// 都没有才作用于整个文本框。以前少了中间这一步，改字体 / 颜色会把整框都改掉。
        /// </summary>
        private TextRange BuildTargetRange(RichTextBox box)
        {
            try
            {
                if (HasTextSelection(box))
                    return new TextRange(box.Selection.Start, box.Selection.End);

                if (ReferenceEquals(_styleSelBox, box) && IsRememberedRangeValid(box))
                    return new TextRange(_styleSelStart!, _styleSelEnd!);
            }
            catch { }

            return new TextRange(box.Document.ContentStart, box.Document.ContentEnd);
        }

        private bool IsRememberedRangeValid(RichTextBox box)
        {
            try
            {
                if (_styleSelStart == null || _styleSelEnd == null) return false;
                if (!_styleSelStart.IsInSameDocument(box.Document.ContentStart)) return false;
                if (!_styleSelEnd.IsInSameDocument(box.Document.ContentStart)) return false;
                return _styleSelStart.CompareTo(_styleSelEnd) < 0;
            }
            catch { return false; }
        }

        private void RememberTextSelection(TextItem item)
        {
            if (_applyingStyle) return;

            try
            {
                var box = item.Box;
                if (box.Selection.IsEmpty) return;

                _styleSelBox = box;
                _styleSelStart = box.Selection.Start;
                _styleSelEnd = box.Selection.End;
            }
            catch { }
        }

        private void ClearRememberedSelection()
        {
            _styleSelBox = null;
            _styleSelStart = null;
            _styleSelEnd = null;
        }

        // ================================================================
        // 3.43：选择工具（直接操作批注位图）
        // 进入选择工具时先把文字 / 笔迹烘焙进 RasterLayer，之后框选、复制、剪切、删除、
        // 拖动移动全部按像素处理 —— 这样上一轮保存的批注（本来就是位图）也能改。
        // ================================================================

        private static Color SelectAccent => Color.FromRgb(0x00, 0x78, 0xD7);

        private void BtnSelectTool_Click(object sender, RoutedEventArgs e)
        {
            if (_pickingColor) EndColorPick(true);
            if (_mode != ToolMode.Select) ClearSelection();
            _mode = ToolMode.Select;
            CommitActiveText();
            ApplyMode();
            EnsureRibbonForMode();
            try { Stage.Focus(); Keyboard.Focus(Stage); } catch { }
        }

        private bool HasSelection
            => _hasSelRect && _selRect.Width >= 1 && _selRect.Height >= 1;

        /// <summary>让批注层变成可写的位图；文字 / 笔迹先烘焙进去（可撤销）。</summary>
        private void EnsureSelectionRaster()
        {
            try
            {
                if (_rasterBitmap == null) FlattenVectorsIntoRaster(recordUndo: true);
                EnsureRasterWritable();
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：准备批注位图失败"); }
        }

        private System.Windows.Shapes.Rectangle EnsureSelectFrame()
        {
            if (_selectFrame != null) return _selectFrame;

            _selectFrame = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(SelectAccent),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection(new double[] { 4, 3 }),
                Fill = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x78, 0xD7)),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_selectFrame, 8);
            SelectionLayer.Children.Add(_selectFrame);
            return _selectFrame;
        }

        private void ShowSelectFrame(Rect rect)
        {
            try
            {
                var frame = EnsureSelectFrame();
                Canvas.SetLeft(frame, rect.X);
                Canvas.SetTop(frame, rect.Y);
                frame.Width = Math.Max(1, rect.Width);
                frame.Height = Math.Max(1, rect.Height);
                frame.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void ClearSelection()
        {
            _hasSelRect = false;
            _selRect = Rect.Empty;
            _selectDragging = false;
            _selectMoving = false;
            _moveWholeSnapshot = null;
            _moveBlock = null;

            try
            {
                if (_selectFrame != null) _selectFrame.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // ---------------------------------------------------------------
        // 选区 ↔ 像素
        // ---------------------------------------------------------------

        private static Int32Rect ToPixelRect(Rect dip, int bw, int bh, double stageW, double stageH)
        {
            if (bw <= 0 || bh <= 0 || stageW <= 0 || stageH <= 0 || dip.IsEmpty) return new Int32Rect(0, 0, 0, 0);

            int x = (int)Math.Round(dip.X * bw / stageW);
            int y = (int)Math.Round(dip.Y * bh / stageH);
            int w = (int)Math.Round(dip.Width * bw / stageW);
            int h = (int)Math.Round(dip.Height * bh / stageH);

            x = Math.Max(0, Math.Min(bw - 1, x));
            y = Math.Max(0, Math.Min(bh - 1, y));
            w = Math.Max(1, Math.Min(w, bw - x));
            h = Math.Max(1, Math.Min(h, bh - y));
            return new Int32Rect(x, y, w, h);
        }

        private Int32Rect SelectionPixelRect()
        {
            if (_rasterBitmap == null) return new Int32Rect(0, 0, 0, 0);
            return ToPixelRect(_selRect, _rasterBitmap.PixelWidth, _rasterBitmap.PixelHeight,
                               Math.Max(1, Stage.ActualWidth), Math.Max(1, Stage.ActualHeight));
        }

        private byte[]? CaptureAllPixels()
        {
            if (_rasterBitmap == null) return null;
            int bw = _rasterBitmap.PixelWidth, bh = _rasterBitmap.PixelHeight;
            if (bw <= 0 || bh <= 0) return null;
            var buf = new byte[bw * bh * 4];
            try { _rasterBitmap.CopyPixels(buf, bw * 4, 0); } catch { return null; }
            return buf;
        }

        private void RestoreAllPixels(byte[] buf)
        {
            if (_rasterBitmap == null || buf == null) return;
            int bw = _rasterBitmap.PixelWidth, bh = _rasterBitmap.PixelHeight;
            try { _rasterBitmap.WritePixels(new Int32Rect(0, 0, bw, bh), buf, bw * 4, 0); } catch { }
        }

        private byte[]? CaptureRegion(Int32Rect rect)
        {
            if (_rasterBitmap == null || rect.Width <= 0 || rect.Height <= 0) return null;
            int stride = rect.Width * 4;
            var buf = new byte[stride * rect.Height];
            try { _rasterBitmap.CopyPixels(rect, buf, stride, 0); } catch { return null; }
            return buf;
        }

        private void RestoreRegion(Int32Rect rect, byte[]? buf)
        {
            if (_rasterBitmap == null || buf == null || rect.Width <= 0 || rect.Height <= 0) return;
            try { _rasterBitmap.WritePixels(rect, buf, rect.Width * 4, 0); } catch { }
        }

        private void ClearRegion(Int32Rect rect)
        {
            if (_rasterBitmap == null || rect.Width <= 0 || rect.Height <= 0) return;
            var clear = new byte[rect.Width * rect.Height * 4];
            try { _rasterBitmap.WritePixels(rect, clear, rect.Width * 4, 0); } catch { }
        }

        /// <summary>从整张快照里裁一小块（撤销「移动」时还原受影响区域用）。</summary>
        private static byte[]? CropFromWhole(byte[]? whole, int bw, Int32Rect rect)
        {
            if (whole == null || rect.Width <= 0 || rect.Height <= 0) return null;
            int stride = bw * 4;
            int w = rect.Width * 4;
            var buf = new byte[w * rect.Height];
            for (int y = 0; y < rect.Height; y++)
                Buffer.BlockCopy(whole, (rect.Y + y) * stride + rect.X * 4, buf, y * w, w);
            return buf;
        }

        /// <summary>把一张位图（Pbgra32）叠到批注位图的 (dx,dy) 处。</summary>
        private void BlendBitmapAt(BitmapSource? img, int dx, int dy)
        {
            if (_rasterBitmap == null || img == null) return;

            try
            {
                BitmapSource src = img;
                if (src.Format != PixelFormats.Pbgra32)
                {
                    var converted = new FormatConvertedBitmap();
                    converted.BeginInit();
                    converted.Source = src;
                    converted.DestinationFormat = PixelFormats.Pbgra32;
                    converted.EndInit();
                    src = converted;
                }

                int bw = _rasterBitmap.PixelWidth, bh = _rasterBitmap.PixelHeight;
                int iw = src.PixelWidth, ih = src.PixelHeight;
                if (iw <= 0 || ih <= 0) return;

                int x0 = Math.Max(0, dx), y0 = Math.Max(0, dy);
                int x1 = Math.Min(bw, dx + iw), y1 = Math.Min(bh, dy + ih);
                if (x1 <= x0 || y1 <= y0) return;

                int w = x1 - x0, h = y1 - y0;
                int stride = w * 4;

                var dst = new byte[stride * h];
                _rasterBitmap.CopyPixels(new Int32Rect(x0, y0, w, h), dst, stride, 0);

                var spx = new byte[iw * ih * 4];
                src.CopyPixels(spx, iw * 4, 0);

                for (int y = 0; y < h; y++)
                {
                    int sy = y0 + y - dy;
                    for (int x = 0; x < w; x++)
                    {
                        int sx = x0 + x - dx;
                        int si = (sy * iw + sx) * 4;
                        int di = (y * w + x) * 4;

                        int sa = spx[si + 3];
                        if (sa == 0) continue;

                        if (sa == 255)
                        {
                            dst[di] = spx[si];
                            dst[di + 1] = spx[si + 1];
                            dst[di + 2] = spx[si + 2];
                            dst[di + 3] = 255;
                            continue;
                        }

                        int ia = 255 - sa;
                        dst[di] = (byte)Math.Min(255, spx[si] + dst[di] * ia / 255);
                        dst[di + 1] = (byte)Math.Min(255, spx[si + 1] + dst[di + 1] * ia / 255);
                        dst[di + 2] = (byte)Math.Min(255, spx[si + 2] + dst[di + 2] * ia / 255);
                        dst[di + 3] = (byte)Math.Min(255, sa + dst[di + 3] * ia / 255);
                    }
                }

                _rasterBitmap.WritePixels(new Int32Rect(x0, y0, w, h), dst, stride, 0);
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：叠加位图失败"); }
        }

        private void BlendBlockAt(byte[]? block, Int32Rect blockRect, int dx, int dy)
        {
            if (_rasterBitmap == null || block == null || blockRect.Width <= 0 || blockRect.Height <= 0) return;
            try
            {
                var bmp = BitmapSource.Create(blockRect.Width, blockRect.Height, 96, 96,
                    PixelFormats.Pbgra32, null, block, blockRect.Width * 4);
                bmp.Freeze();
                BlendBitmapAt(bmp, dx, dy);
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：绘制选区块失败"); }
        }

        private BitmapSource? CropSelection()
        {
            if (_rasterBitmap == null || !HasSelection) return null;
            try
            {
                var rect = SelectionPixelRect();
                if (rect.Width <= 0 || rect.Height <= 0) return null;

                // ★ 不要用 CroppedBitmap(_rasterBitmap)：对它 Freeze() 会顺着 Source 把
                //   可写的 WriteableBitmap 一起冻住，之后删除 / 粘贴全部写不进去。
                //   这里先拷出像素，再独立建一张位图。
                var pixels = CaptureRegion(rect);
                if (pixels == null) return null;

                var cropped = BitmapSource.Create(rect.Width, rect.Height, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, rect.Width * 4);
                cropped.Freeze();
                return cropped;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------
        // 拖动移动
        // ---------------------------------------------------------------

        private void BeginSelectMove(Point p)
        {
            if (_rasterBitmap == null || !HasSelection) return;

            _selectMoving = true;
            _selectMoveStart = p;
            _selectMoveRect = _selRect;

            _moveWholeSnapshot = CaptureAllPixels();
            var rect = SelectionPixelRect();
            _moveBlock = CaptureRegion(rect);
            _moveBlockRect = rect;

            // 先「提起」这块像素（原位置变透明），拖动时再画到目标位置
            ClearRegion(rect);

            try { Stage.CaptureMouse(); } catch { }
        }

        private void MoveSelectionBy(double dx, double dy)
        {
            if (!_selectMoving || _moveWholeSnapshot == null || _moveBlock == null || _rasterBitmap == null) return;

            try
            {
                RestoreAllPixels(_moveWholeSnapshot);
                ClearRegion(_moveBlockRect);

                int px = (int)Math.Round(dx * (_rasterBitmap.PixelWidth / Math.Max(1, Stage.ActualWidth)));
                int py = (int)Math.Round(dy * (_rasterBitmap.PixelHeight / Math.Max(1, Stage.ActualHeight)));
                BlendBlockAt(_moveBlock, _moveBlockRect, _moveBlockRect.X + px, _moveBlockRect.Y + py);

                var moved = new Rect(_selectMoveRect.X + dx, _selectMoveRect.Y + dy,
                                     _selectMoveRect.Width, _selectMoveRect.Height);
                _selRect = moved;
                _hasSelRect = true;
                ShowSelectFrame(moved);
            }
            catch { }
        }

        private void EndSelectMove()
        {
            _selectMoving = false;
            try { Stage.ReleaseMouseCapture(); } catch { }

            if (_rasterBitmap == null || _moveWholeSnapshot == null || _moveBlock == null)
            {
                _moveWholeSnapshot = null;
                _moveBlock = null;
                return;
            }

            try
            {
                int bw = _rasterBitmap.PixelWidth, bh = _rasterBitmap.PixelHeight;
                double stageW = Math.Max(1, Stage.ActualWidth);
                double stageH = Math.Max(1, Stage.ActualHeight);

                var startDip = _selectMoveRect;
                var endDip = _selRect;

                var startRect = ToPixelRect(startDip, bw, bh, stageW, stageH);
                var endRect = ToPixelRect(endDip, bw, bh, stageW, stageH);
                var area = UnionRect(startRect, endRect);

                var before = CropFromWhole(_moveWholeSnapshot, bw, area);
                var after = CaptureRegion(area);

                _moveWholeSnapshot = null;
                _moveBlock = null;

                if (before == null || after == null) return;

                bool changed = false;
                for (int i = 0; i < before.Length && i < after.Length; i++)
                {
                    if (before[i] != after[i]) { changed = true; break; }
                }
                if (!changed) return;

                _undo.Push(new EditAction
                {
                    Undo = () =>
                    {
                        RestoreRegion(area, before);
                        _selRect = startDip;
                        _hasSelRect = true;
                        ShowSelectFrame(startDip);
                    },
                    Redo = () =>
                    {
                        RestoreRegion(area, after);
                        _selRect = endDip;
                        _hasSelRect = true;
                        ShowSelectFrame(endDip);
                    }
                });
                _redo.Clear();
                MarkDirty();
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：移动选区失败"); }
        }

        private static Int32Rect UnionRect(Int32Rect a, Int32Rect b)
        {
            if (a.Width <= 0 || a.Height <= 0) return b;
            if (b.Width <= 0 || b.Height <= 0) return a;
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            int r = Math.Max(a.X + a.Width, b.X + b.Width);
            int bt = Math.Max(a.Y + a.Height, b.Y + b.Height);
            return new Int32Rect(x, y, r - x, bt - y);
        }

        // ---------------------------------------------------------------
        // 复制 / 剪切 / 粘贴 / 删除 / 全选
        // ---------------------------------------------------------------

        private void CopySelection()
        {
            if (!HasSelection) return;
            EnsureSelectionRaster();
            if (_rasterBitmap == null) return;

            try
            {
                var cropped = CropSelection();
                if (cropped == null) return;
                _clipImage = cropped;

                // 复制的是图片数据（不是文件），放到系统剪贴板
                try { Clipboard.SetImage(cropped); } catch { }

                ToastNotification.ShowSuccess(Window.GetWindow(this), "已复制选中的批注");
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：复制批注失败"); }
        }

        private void CutSelection()
        {
            if (!HasSelection) return;
            CopySelection();
            DeleteSelection();
        }

        private void DeleteSelection()
        {
            if (!HasSelection) return;
            EnsureSelectionRaster();
            if (_rasterBitmap == null) return;

            try
            {
                var rect = SelectionPixelRect();
                var before = CaptureRegion(rect);
                if (before == null) return;

                var savedDip = _selRect;

                ClearRegion(rect);
                ClearSelection();
                MarkDirty();

                _undo.Push(new EditAction
                {
                    Undo = () =>
                    {
                        RestoreRegion(rect, before);
                        _selRect = savedDip;
                        _hasSelRect = true;
                        ShowSelectFrame(savedDip);
                    },
                    Redo = () =>
                    {
                        ClearRegion(rect);
                        ClearSelection();
                    }
                });
                _redo.Clear();
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：删除选区失败"); }
        }

        private void SelectAll()
        {
            if (!_hasMedia) return;
            EnsureSelectionRaster();
            _selRect = new Rect(0, 0, Math.Max(1, Stage.ActualWidth), Math.Max(1, Stage.ActualHeight));
            _hasSelRect = true;
            ShowSelectFrame(_selRect);
        }

        /// <summary>读取系统剪贴板里的图片：直接是图片数据，或者是图片文件（如 test.jpg）。</summary>
        private static BitmapSource? GetClipboardImage()
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null) return img;
                }

                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string? file in Clipboard.GetFileDropList())
                    {
                        if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".gif") continue;

                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(file, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch (Exception ex) { RuntimeLog.Warn("封面编辑页：读取剪贴板失败：" + ex.Message); }
            return null;
        }

        private void PasteClipboard()
        {
            if (!_hasMedia) return;

            var source = GetClipboardImage() ?? _clipImage;
            if (source == null)
            {
                try { ToastNotification.ShowWarning(Window.GetWindow(this), "剪贴板里没有图片"); } catch { }
                return;
            }

            EnsureSelectionRaster();
            if (_rasterBitmap == null) return;

            try
            {
                int bw = _rasterBitmap.PixelWidth, bh = _rasterBitmap.PixelHeight;
                double stageW = Math.Max(1, Stage.ActualWidth);
                double stageH = Math.Max(1, Stage.ActualHeight);

                // 有选区就贴到选区左上角，否则贴到 (16,16)
                int dx, dy;
                if (HasSelection)
                {
                    var r = SelectionPixelRect();
                    dx = r.X;
                    dy = r.Y;
                }
                else
                {
                    dx = (int)Math.Round(16 * bw / stageW);
                    dy = (int)Math.Round(16 * bh / stageH);
                }

                int iw = source.PixelWidth, ih = source.PixelHeight;
                int x0 = Math.Max(0, dx), y0 = Math.Max(0, dy);
                int x1 = Math.Min(bw, dx + iw), y1 = Math.Min(bh, dy + ih);
                if (x1 <= x0 || y1 <= y0) return;

                var area = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
                var before = CaptureRegion(area);

                BlendBitmapAt(source, dx, dy);

                var after = CaptureRegion(area);

                var target = new Rect(x0 * stageW / bw, y0 * stageH / bh,
                                      (x1 - x0) * stageW / bw, (y1 - y0) * stageH / bh);

                _selRect = target;
                _hasSelRect = true;
                ShowSelectFrame(target);
                MarkDirty();

                if (before != null && after != null)
                {
                    _undo.Push(new EditAction
                    {
                        Undo = () =>
                        {
                            RestoreRegion(area, before);
                            ClearSelection();
                        },
                        Redo = () =>
                        {
                            RestoreRegion(area, after);
                            _selRect = target;
                            _hasSelRect = true;
                            ShowSelectFrame(target);
                        }
                    });
                    _redo.Clear();
                }
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：粘贴批注失败"); }
        }

        /// <summary>方向键微移：整块选区像素平移若干像素。</summary>
        private void NudgeSelection(double dx, double dy)
        {
            if (!HasSelection) return;
            EnsureSelectionRaster();
            if (_rasterBitmap == null) return;

            try
            {
                BeginSelectMove(_selRect.Location);
                MoveSelectionBy(dx, dy);
                EndSelectMove();
            }
            catch { }
        }

        // ---------------------------------------------------------------
        // 快捷键 / 右键菜单
        // ---------------------------------------------------------------

        /// <summary>键快捷操作：只在「选择」工具下、且焦点不在文本框里时生效。</summary>
        private bool HandleSelectShortcut(KeyEventArgs e)
        {
            if (_mode != ToolMode.Select) return false;
            if (IsInsideTextEditor(e.OriginalSource)) return false;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.C: CopySelection(); e.Handled = true; return true;
                    case Key.X: CutSelection(); e.Handled = true; return true;
                    case Key.V: PasteClipboard(); e.Handled = true; return true;
                    case Key.A: SelectAll(); e.Handled = true; return true;
                }
                return false;
            }

            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                DeleteSelection();
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.Escape)
            {
                ClearSelection();
                e.Handled = true;
                return true;
            }

            double step = shift ? 10 : 1;
            double dx = 0, dy = 0;
            if (e.Key == Key.Left) dx = -step;
            else if (e.Key == Key.Right) dx = step;
            else if (e.Key == Key.Up) dy = -step;
            else if (e.Key == Key.Down) dy = step;
            else return false;

            if (!HasSelection) return false;

            NudgeSelection(dx, dy);
            e.Handled = true;
            return true;
        }

        private void Stage_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mode != ToolMode.Select) return;
            try
            {
                var menu = BuildSelectMenu();
                menu.PlacementTarget = Stage;
                menu.Placement = PlacementMode.MousePoint;
                menu.IsOpen = true;
                e.Handled = true;
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：打开选择菜单失败"); }
        }

        private void Stage_KeyDown(object sender, KeyEventArgs e) => HandleSelectShortcut(e);

        private void CoverEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            HandleSelectShortcut(e);
        }

        /// <summary>选择工具的右键菜单（与荧光样式下拉用同一套主题菜单样式）。</summary>
        private ContextMenu BuildSelectMenu()
        {
            var menu = new ContextMenu();

            bool modern = App.UseModernContextMenu;
            if (TryFindResource(modern ? "Win11ContextMenuStyle" : "LegacyContextMenuStyle") is Style menuStyle)
                menu.Style = menuStyle;
            var itemStyle = TryFindResource(modern ? "Win11MenuItemStyle" : "LegacyMenuItemStyle") as Style;
            var sepStyle = TryFindResource(modern ? "Win11SeparatorStyle" : "LegacySeparatorStyle") as Style;

            bool hasSel = HasSelection;

            // 3.43：菜单项带图标；粘贴始终可用（系统剪贴板里可能是图片，也可能是图片文件）
            AddSelectMenuItem(menu, itemStyle, "\uE8C8", "复制", "Ctrl+C", hasSel, CopySelection);
            AddSelectMenuItem(menu, itemStyle, "\uE8C6", "剪切", "Ctrl+X", hasSel, CutSelection);
            AddSelectMenuItem(menu, itemStyle, "\uE77F", "粘贴", "Ctrl+V", true, PasteClipboard);
            AddSelectSeparator(menu, sepStyle);
            AddSelectMenuItem(menu, itemStyle, "\uE74D", "删除", "Delete", hasSel, DeleteSelection);
            AddSelectSeparator(menu, sepStyle);
            AddSelectMenuItem(menu, itemStyle, "\uE8B3", "全选", "Ctrl+A", _hasMedia, SelectAll);

            return menu;
        }

        private static void AddSelectSeparator(ContextMenu menu, Style? style)
        {
            var separator = new Separator();
            if (style != null) separator.Style = style;
            menu.Items.Add(separator);
        }

        private void AddSelectMenuItem(ContextMenu menu, Style? itemStyle, string glyph, string text,
                                       string shortcut, bool enabled, Action action)
        {
            var iconFont = TryFindResource("AppIconFont") as FontFamily
                           ?? new FontFamily("Segoe Fluent Icons");

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = iconFont,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg_666666");

            var item = new MenuItem
            {
                Header = text,
                Icon = icon,
                InputGestureText = shortcut,
                IsEnabled = enabled
            };
            if (itemStyle != null) item.Style = itemStyle;

            item.Click += (s, e) => action();
            menu.Items.Add(item);
        }

        // ================================================================
        // 拖出文本框
        // ================================================================

        /// <summary>
        /// 橡皮擦按下：在 Preview 阶段接管，避免视频 / 图片控件先把点击消费掉。
        /// （3.31 之前视频封面上「橡皮擦完全没反应」就是被 VideoPreview 吃掉了。）
        /// </summary>
        private void Stage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != MouseButton.Left) return;

                // 3.41：控制点方块有一半在文本框外，点到框外那半块也要能拖大小
                if (_hasMedia && _mode == ToolMode.Text && !_pickingColor
                    && TryBeginResizeAtHandle(e.GetPosition(Stage)))
                {
                    e.Handled = true;
                    return;
                }

                if (_mode != ToolMode.Eraser || !_hasMedia) return;

                BeginEraseStroke();
                EraseAt(e.GetPosition(Stage));
                e.Handled = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：橡皮擦按下失败");
            }
        }

        /// <summary>
        /// 3.54：点是否落在「已固定（不可编辑）」的文字框里 —— 固定后它等同位图，
        /// 点在它上面不应该再拖出一个新文本框盖住它。
        /// </summary>
        private bool IsPointInsideFixedText(Point stagePoint)
        {
            foreach (var item in _texts)
            {
                if (item == null || ReferenceEquals(item, _activeText)) continue;
                try
                {
                    var box = item.Box;
                    if (box == null || box.ActualWidth <= 0 || box.ActualHeight <= 0) continue;

                    var toStage = box.TransformToAncestor(Stage);
                    var local = toStage.Inverse.Transform(stagePoint);
                    if (local.X >= 0 && local.Y >= 0
                        && local.X <= box.ActualWidth && local.Y <= box.ActualHeight)
                        return true;
                }
                catch { }
            }
            return false;
        }

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (!_hasMedia) return;

                // 3.36：取色模式 —— 点哪儿取哪儿的颜色
                if (_pickingColor)
                {
                    PickColorAt(e.GetPosition(Stage));
                    e.Handled = true;
                    return;
                }

                // 点在文本框里：交给 RichTextBox 自己处理（选字、输入），不要当成「点空白」
                if (IsInsideTextEditor(e.OriginalSource)) return;

                if (_mode == ToolMode.Eraser)
                {
                    BeginEraseStroke();
                    EraseAt(e.GetPosition(Stage));
                    e.Handled = true;
                    return;
                }

                // 3.43：选择工具 —— 框选区域，或拖动选中的像素
                if (_mode == ToolMode.Select)
                {
                    var sp = e.GetPosition(Stage);
                    EnsureSelectionRaster();

                    if (HasSelection && _selRect.Contains(sp))
                    {
                        BeginSelectMove(sp);
                    }
                    else
                    {
                        ClearSelection();
                        _selectDragging = true;
                        _selectDragStart = sp;
                        ShowSelectFrame(new Rect(sp, sp));
                        try { Stage.CaptureMouse(); } catch { }
                    }

                    try { Stage.Focus(); } catch { }
                    e.Handled = true;
                    return;
                }

                if (_mode != ToolMode.Text) return;

                // 3.54：点在已固定的文字上 —— 它就是画面的一部分（等同于位图），
                //       不再像以前那样当成空白处拖出一个新文本框盖在它上面。
                if (IsPointInsideFixedText(e.GetPosition(Stage)))
                {
                    CommitActiveText();
                    e.Handled = true;
                    return;
                }

                // 点空白处：先固定当前文本框
                CommitActiveText();

                _dragStart = e.GetPosition(Stage);
                _draggingSelection = true;
                Stage.CaptureMouse();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：点空白处理失败");
            }
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_mode == ToolMode.Eraser)
                {
                    Point ep = e.GetPosition(Stage);
                    UpdateEraserFrame(ep);
                    if (_erasing) { EraseAt(ep); return; }
                }

                // 3.43：选择工具 —— 正在框选 / 正在拖动选中的像素
                if (_mode == ToolMode.Select)
                {
                    Point sp = e.GetPosition(Stage);

                    if (_selectDragging)
                    {
                        ShowSelectFrame(new Rect(Math.Min(sp.X, _selectDragStart.X),
                                                 Math.Min(sp.Y, _selectDragStart.Y),
                                                 Math.Abs(sp.X - _selectDragStart.X),
                                                 Math.Abs(sp.Y - _selectDragStart.Y)));
                        return;
                    }

                    if (_selectMoving && e.LeftButton == MouseButtonState.Pressed)
                    {
                        double mdx = sp.X - _selectMoveStart.X;
                        double mdy = sp.Y - _selectMoveStart.Y;

                        // 整体别拖出画面（按 1 像素精度对齐，避免拖动时抖动）
                        double stageW = Stage.ActualWidth > 0 ? Stage.ActualWidth : double.MaxValue;
                        double stageH = Stage.ActualHeight > 0 ? Stage.ActualHeight : double.MaxValue;
                        mdx = Math.Max(-_selectMoveRect.Left, Math.Min(mdx, stageW - _selectMoveRect.Right));
                        mdy = Math.Max(-_selectMoveRect.Top, Math.Min(mdy, stageH - _selectMoveRect.Bottom));

                        MoveSelectionBy(mdx, mdy);
                        return;
                    }
                }

                if (!_draggingSelection) return;

                Point p = e.GetPosition(Stage);
                double dx = Math.Abs(p.X - _dragStart.X);
                double dy = Math.Abs(p.Y - _dragStart.Y);
                if (dx < 6 && dy < 6) return;   // 还没拖动，先不显示选区

                if (_selectionRect == null)
                {
                    _selectionRect = new System.Windows.Shapes.Rectangle
                    {
                        Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD7)),
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection(new double[] { 4, 3 }),
                        Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x78, 0xD7)),
                        IsHitTestVisible = false
                    };
                    SelectionLayer.Children.Add(_selectionRect);
                }

                double x = Math.Min(p.X, _dragStart.X);
                double y = Math.Min(p.Y, _dragStart.Y);
                Canvas.SetLeft(_selectionRect, x);
                Canvas.SetTop(_selectionRect, y);
                _selectionRect.Width = dx;
                _selectionRect.Height = dy;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：拖出文本框失败");
            }
        }

        private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_erasing)
            {
                EndEraseStroke();
                e.Handled = true;
                return;
            }

            // 3.43：选择工具 —— 结束框选 / 结束移动
            if (_mode == ToolMode.Select)
            {
                if (_selectDragging)
                {
                    _selectDragging = false;
                    try { Stage.ReleaseMouseCapture(); } catch { }

                    double w = _selectFrame?.Width ?? 0;
                    double h = _selectFrame?.Height ?? 0;
                    if (w < 4 && h < 4)
                    {
                        ClearSelection();
                        e.Handled = true;
                        return;
                    }

                    _selRect = new Rect(Canvas.GetLeft(_selectFrame), Canvas.GetTop(_selectFrame), w, h);
                    _hasSelRect = true;
                    ShowSelectFrame(_selRect);
                    e.Handled = true;
                    return;
                }

                if (_selectMoving)
                {
                    EndSelectMove();
                    e.Handled = true;
                    return;
                }
            }

            if (!_draggingSelection) return;

            _draggingSelection = false;
            try { Stage.ReleaseMouseCapture(); } catch { }

            try
            {
                double w = _selectionRect?.Width ?? 0;
                double h = _selectionRect?.Height ?? 0;
                double x = _selectionRect != null ? Canvas.GetLeft(_selectionRect) : 0;
                double y = _selectionRect != null ? Canvas.GetTop(_selectionRect) : 0;

                if (_selectionRect != null)
                {
                    try { SelectionLayer.Children.Remove(_selectionRect); } catch { }
                    _selectionRect = null;
                }

                if (w < 24 || h < 18) return;
                CreateTextBox(x, y, w, h);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：创建文本框失败");
            }
        }

        private void Stage_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_erasing) HideEraserFrame();
        }

        private static bool IsInsideTextEditor(object source)
        {
            var node = source as DependencyObject;
            int guard = 0;
            while (node != null && guard++ < 64)
            {
                if (node is RichTextBox) return true;

                DependencyObject? parent = null;
                try
                {
                    parent = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(node)
                        : LogicalTreeHelper.GetParent(node);
                }
                catch { parent = null; }

                node = parent;
            }
            return false;
        }

        /// <summary>橡皮擦方框（跟随鼠标，边长就是「粗细」）。</summary>
        private void EnsureEraserFrame()
        {
            if (_eraserFrame != null || SelectionLayer == null) return;

            _eraserFrame = new System.Windows.Shapes.Rectangle
            {
                StrokeThickness = 1,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _eraserFrame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Fg_333333");
            Canvas.SetZIndex(_eraserFrame, 6);
            SelectionLayer.Children.Add(_eraserFrame);
        }

        private void UpdateEraserFrame(Point p)
        {
            if (_eraserFrame == null) return;
            try
            {
                double size = Math.Max(2, _penWidth);
                Canvas.SetLeft(_eraserFrame, p.X - size / 2.0);
                Canvas.SetTop(_eraserFrame, p.Y - size / 2.0);
                _eraserFrame.Width = size;
                _eraserFrame.Height = size;
                _eraserFrame.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void HideEraserFrame()
        {
            try { if (_eraserFrame != null) _eraserFrame.Visibility = Visibility.Collapsed; } catch { }
        }

        /// <summary>
        /// 3.33：方形橡皮按像素擦除（与画图 / PS 一致）。
        /// 橡皮擦只擦掉鼠标下方方形区域内的内容，方框外的笔迹 / 文字保留。
        /// 第一次擦除前会把当前批注整体烘焙到 RasterLayer（见 FlattenVectorsIntoRaster）。
        /// </summary>
        private void EraseAt(Point p)
        {
            try
            {
                if (_rasterBitmap == null) FlattenVectorsIntoRaster(recordUndo: false);
                if (_rasterBitmap == null) return;

                double stageW = Math.Max(1, Stage.ActualWidth);
                double stageH = Math.Max(1, Stage.ActualHeight);
                int bw = _rasterBitmap.PixelWidth;
                int bh = _rasterBitmap.PixelHeight;
                if (bw <= 0 || bh <= 0) return;

                double size = Math.Max(2, _penWidth);
                double left = p.X - size / 2.0;
                double top = p.Y - size / 2.0;

                // Stage 坐标（DIP）→ 位图像素；缩放 / 窗口尺寸变化也能对上位置
                int x = (int)Math.Round(left * bw / stageW);
                int y = (int)Math.Round(top * bh / stageH);
                int w = Math.Max(1, (int)Math.Round(size * bw / stageW));
                int h = Math.Max(1, (int)Math.Round(size * bh / stageH));

                x = Math.Max(0, x);
                y = Math.Max(0, y);
                w = Math.Min(w, bw - x);
                h = Math.Min(h, bh - y);
                if (w <= 0 || h <= 0) return;

                var rect = new Int32Rect(x, y, w, h);
                int stride = w * 4;

                var oldPixels = new byte[stride * h];
                _rasterBitmap.CopyPixels(rect, oldPixels, stride, 0);

                var clear = new byte[stride * h];
                _rasterBitmap.WritePixels(rect, clear, stride, 0);

                _eraseBatch.Add(new EraseSnapshot { Rect = rect, Pixels = oldPixels });
                MarkDirty();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：擦除像素失败");
            }
        }

        /// <summary>一次拖动开始：清空本批快照、确保批注已栅格化、捕获鼠标。</summary>
        private void BeginEraseStroke()
        {
            _erasing = true;
            _eraseBatch.Clear();
            try { Stage.CaptureMouse(); } catch { }
            if (_rasterBitmap == null) FlattenVectorsIntoRaster(recordUndo: false);
        }

        /// <summary>一次拖动结束：把本批方形擦除合并成一步撤销。</summary>
        private void EndEraseStroke()
        {
            _erasing = false;
            try { Stage.ReleaseMouseCapture(); } catch { }
            CommitEraseBatch();
        }

        private void CommitEraseBatch()
        {
            if (_eraseBatch.Count == 0) return;

            var batch = _eraseBatch.ToList();
            _eraseBatch.Clear();

            _undo.Push(new EditAction
            {
                Undo = () =>
                {
                    if (_rasterBitmap == null) return;
                    foreach (var snap in batch)
                    {
                        try { _rasterBitmap.WritePixels(snap.Rect, snap.Pixels, snap.Rect.Width * 4, 0); } catch { }
                    }
                },
                Redo = () =>
                {
                    if (_rasterBitmap == null) return;
                    foreach (var snap in batch)
                    {
                        try
                        {
                            var clear = new byte[snap.Rect.Width * snap.Rect.Height * 4];
                            _rasterBitmap.WritePixels(snap.Rect, clear, snap.Rect.Width * 4, 0);
                        }
                        catch { }
                    }
                }
            });
            _redo.Clear();
            MarkDirty();
        }

        /// <summary>撤销「擦掉文字」时把文本框放回去（保持已固定状态）。</summary>
        private void RestoreTextItem(TextItem item)
        {
            try
            {
                if (_texts.Contains(item)) return;
                item.Box.IsHitTestVisible = false;
                item.Box.Focusable = false;
                item.Box.Cursor = Cursors.Arrow;
                TextLayer.Children.Add(item.Box);
                _texts.Add(item);
            }
            catch { }
        }

        /// <summary>3.42：创建文本框控件本身（不绑事件、不入层），供新建与克隆共用。</summary>
        private RichTextBox BuildTextBox(double w, double h, FlowDocument? doc,
                                         double fontSize, FontFamily fontFamily,
                                         SolidColorBrush? foreground, double angle)
        {
            var box = new RichTextBox
            {
                Width = w,
                Height = h,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3),
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                FontSize = fontSize,
                FontFamily = fontFamily,
                Foreground = foreground ?? new SolidColorBrush(_textColor)
            };

            if (doc != null)
            {
                box.Document = doc;
            }
            else
            {
                box.Document.Blocks.Clear();
                box.Document.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });
            }

            // 3.37：右键用项目统一的主题菜单（RichTextBox 默认弹的是系统白底菜单）
            try { box.ContextMenu = new ThemedTextContextMenu(); } catch { }

            // 3.41：旋转以文本框中心为原点
            box.RenderTransform = new RotateTransform(angle, w / 2.0, h / 2.0);

            return box;
        }

        /// <summary>3.42：绑定文本框事件并放进文字层。</summary>
        private void AttachTextBox(TextItem item, double x, double y)
        {
            var box = item.Box;

            box.GotKeyboardFocus += (s, e) => SetActiveText(item);
            box.SelectionChanged += (s, e) => RememberTextSelection(item);
            box.PreviewMouseLeftButtonDown += (s, e) => OnTextBoxMouseDown(item, e);
            box.PreviewMouseMove += (s, e) => OnTextBoxMouseMove(item, e);
            box.PreviewMouseLeftButtonUp += (s, e) => OnTextBoxMouseUp(item, e);
            box.SizeChanged += (s, e) => { if (ReferenceEquals(_activeText, item)) UpdateActiveFrame(); };
            box.TextChanged += (s, e) => MarkDirty();

            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            Canvas.SetZIndex(box, 1);
            TextLayer.Children.Add(box);
            _texts.Add(item);
        }

        /// <summary>3.42：把克隆出来的文本框放到指定位置（限制在画面内）。</summary>
        private void PlaceTextItem(TextItem item, double x, double y)
        {
            double w = ItemWidth(item);
            double h = ItemHeight(item);
            double maxLeft = Math.Max(0, (Stage.Width > 0 ? Stage.Width : double.MaxValue) - w);
            double maxTop = Math.Max(0, (Stage.Height > 0 ? Stage.Height : double.MaxValue) - h);
            AttachTextBox(item, Math.Max(0, Math.Min(x, maxLeft)), Math.Max(0, Math.Min(y, maxTop)));
        }

        private void CreateTextBox(double x, double y, double w, double h)
        {
            var box = BuildTextBox(w, h, null, _fontSize, SafeFontFamily(_fontFamily),
                                   new SolidColorBrush(_textColor), 0);
            var item = new TextItem { Box = box };

            AttachTextBox(item, x, y);

            SetActiveText(item);

            _undo.Push(new EditAction
            {
                Undo = () => RemoveTextItem(item),
                Redo = () => RestoreTextItem(item)
            });
            _redo.Clear();
            MarkDirty();

            try { box.Focus(); } catch { }
        }

        // ================================================================
        // 文本框：画图式虚线框 + 靠近四边 / 四角拖动改大小
        // ================================================================

        /// <summary>
        /// 文本框的虚线选择框：3.34 起固定为黑色 + 加粗虚线，并在四角 / 四边中点画小方块控制点
        /// （与图 2 的选择框样式一致；拖动这些小方块所在的边 / 角仍然是原来的缩放操作）。
        /// </summary>
        private void EnsureActiveFrame()
        {
            if (SelectionLayer == null) return;

            if (_activeFrame == null)
            {
                _activeFrame = new System.Windows.Shapes.Rectangle
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection(new double[] { 3, 2 }),
                    StrokeDashCap = PenLineCap.Flat,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                Canvas.SetZIndex(_activeFrame, 5);
                SelectionLayer.Children.Add(_activeFrame);
            }

            if (_frameHandles.Count > 0) return;

            for (int i = 0; i < 8; i++)
            {
                var handle = new System.Windows.Shapes.Rectangle
                {
                    Width = FrameHandleSize,
                    Height = FrameHandleSize,
                    Fill = Brushes.White,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                Canvas.SetZIndex(handle, 6);
                _frameHandles.Add(handle);
                SelectionLayer.Children.Add(handle);
            }

            // 3.41：文本框上方的圆形旋转按钮（只有文字模式且激活文本框时显示）
            if (_rotateHandle == null)
            {
                var iconFont = TryFindResource("AppIconFont") as FontFamily
                               ?? new FontFamily("Segoe Fluent Icons");
                _rotateHandle = new System.Windows.Controls.Border
                {
                    Width = RotateHandleSize,
                    Height = RotateHandleSize,
                    CornerRadius = new CornerRadius(RotateHandleSize / 2.0),
                    Background = Brushes.White,
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1.2),
                    // 3.43：旋转按钮上鼠标是箭头（其它控件沿用原来的光标样式）
                    Cursor = Cursors.Arrow,
                    ToolTip = "按住拖动旋转文本框",
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = "\uE7AD",
                        FontFamily = iconFont,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Black
                    }
                };
                _rotateHandle.MouseLeftButtonDown += RotateHandle_MouseDown;
                _rotateHandle.MouseMove += RotateHandle_MouseMove;
                _rotateHandle.MouseLeftButtonUp += RotateHandle_MouseUp;
                Canvas.SetZIndex(_rotateHandle, 7);
                SelectionLayer.Children.Add(_rotateHandle);
            }
        }

        private void HideActiveFrame()
        {
            try
            {
                if (_activeFrame != null) _activeFrame.Visibility = Visibility.Collapsed;
                foreach (var handle in _frameHandles) handle.Visibility = Visibility.Collapsed;
                if (_rotateHandle != null) _rotateHandle.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void UpdateActiveFrame()
        {
            if (_activeFrame == null) return;

            var item = _activeText;
            if (item == null)
            {
                HideActiveFrame();
                return;
            }

            try
            {
                double left = Canvas.GetLeft(item.Box);
                double top = Canvas.GetTop(item.Box);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                // 3.41：拖边缘缩放时刚设置的 Width 还没走到布局，ActualWidth 仍是旧值，
                // 虚线框 / 控制点会慢一帧、看起来和鼠标位置对不上。优先用显式宽高。
                double w = double.IsNaN(item.Box.Width) || item.Box.Width <= 0
                    ? item.Box.ActualWidth : item.Box.Width;
                double h = double.IsNaN(item.Box.Height) || item.Box.Height <= 0
                    ? item.Box.ActualHeight : item.Box.Height;
                if (w <= 0) w = 1;
                if (h <= 0) h = 1;

                Canvas.SetLeft(_activeFrame, left);
                Canvas.SetTop(_activeFrame, top);
                _activeFrame.Width = w;
                _activeFrame.Height = h;
                // 3.41：文本框旋转后虚线框跟着转（绕框中心）
                _activeFrame.RenderTransform = item.Angle == 0
                    ? Transform.Identity
                    : new RotateTransform(item.Angle, w / 2.0, h / 2.0);
                _activeFrame.Visibility = Visibility.Visible;

                // 8 个控制点：四角 + 四边中点（3×3 去掉中心）
                // 3.42：位置全部用「绕框中心旋转」手算 —— 刚设完 RenderTransform 时
                // TranslatePoint 读到的还是旋转前的坐标，会让方块 / 按钮慢一帧。
                var center = new Point(left + w / 2.0, top + h / 2.0);

                Point LocalToLayer(double lx, double ly)
                {
                    var v = RotateVector(new Point(lx - w / 2.0, ly - h / 2.0), item.Angle);
                    return new Point(center.X + v.X, center.Y + v.Y);
                }

                int index = 0;
                for (int row = 0; row < 3 && index < _frameHandles.Count; row++)
                {
                    for (int col = 0; col < 3 && index < _frameHandles.Count; col++)
                    {
                        if (row == 1 && col == 1) continue;

                        var pt = LocalToLayer(w * col / 2.0, h * row / 2.0);
                        var handle = _frameHandles[index++];
                        Canvas.SetLeft(handle, pt.X - FrameHandleSize / 2.0);
                        Canvas.SetTop(handle, pt.Y - FrameHandleSize / 2.0);
                        // 3.42：小方块跟着文本框一起转（不再永远横平竖直）
                        handle.RenderTransform = item.Angle == 0
                            ? Transform.Identity
                            : new RotateTransform(item.Angle, FrameHandleSize / 2.0, FrameHandleSize / 2.0);
                        handle.Visibility = Visibility.Visible;
                    }
                }

                // 3.42：旋转按钮跟着文本框转（沿旋转后的上边中点法线方向偏移）
                if (_rotateHandle != null)
                {
                    var topMid = LocalToLayer(w / 2.0, 0);
                    var normal = RotateVector(new Point(0, -1), item.Angle);

                    double bx = topMid.X + normal.X * 18.0;
                    double by = topMid.Y + normal.Y * 18.0;

                    // 贴边时别把按钮甩到画布外（夹在舞台范围内）
                    double stageW = Stage.ActualWidth > 0 ? Stage.ActualWidth : bx + RotateHandleSize;
                    double stageH = Stage.ActualHeight > 0 ? Stage.ActualHeight : by + RotateHandleSize;
                    bx = Math.Max(RotateHandleSize / 2.0, Math.Min(bx, stageW - RotateHandleSize / 2.0));
                    by = Math.Max(RotateHandleSize / 2.0, Math.Min(by, stageH - RotateHandleSize / 2.0));

                    Canvas.SetLeft(_rotateHandle, bx - RotateHandleSize / 2.0);
                    Canvas.SetTop(_rotateHandle, by - RotateHandleSize / 2.0);
                    _rotateHandle.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------
        // 3.41：旋转文本框（按住上方的圆形按钮拖动）
        // ---------------------------------------------------------------

        private void RotateHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var item = _activeText;
                if (item == null) return;

                _rotatingItem = item;
                _rotateCenter = item.Box.TranslatePoint(new Point(ItemWidth(item) / 2.0, ItemHeight(item) / 2.0), Stage);
                var p = e.GetPosition(Stage);
                _rotateStartMouseAngle = Math.Atan2(p.Y - _rotateCenter.Y, p.X - _rotateCenter.X) * 180.0 / Math.PI;
                _rotateStartItemAngle = item.Angle;
                try { _rotateHandle?.CaptureMouse(); } catch { }
                e.Handled = true;
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：开始旋转文本框失败"); }
        }

        private void RotateHandle_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_rotatingItem == null || e.LeftButton != MouseButtonState.Pressed) return;

                var p = e.GetPosition(Stage);
                double now = Math.Atan2(p.Y - _rotateCenter.Y, p.X - _rotateCenter.X) * 180.0 / Math.PI;
                double angle = _rotateStartItemAngle + (now - _rotateStartMouseAngle);
                angle = ((angle % 360) + 540) % 360 - 180;   // 规范到 (-180, 180]
                ApplyItemAngle(_rotatingItem, angle);
                e.Handled = true;
            }
            catch (Exception ex) { RuntimeLog.Error(ex, "封面编辑页：旋转文本框失败"); }
        }

        private void RotateHandle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_rotatingItem == null) return;
                _rotatingItem = null;
                try { _rotateHandle?.ReleaseMouseCapture(); } catch { }
                MarkDirty();
                e.Handled = true;
            }
            catch { }
        }

        private void ApplyItemAngle(TextItem item, double angle)
        {
            try
            {
                item.Angle = angle;
                item.Box.RenderTransform = new RotateTransform(angle, ItemWidth(item) / 2.0, ItemHeight(item) / 2.0);
                UpdateActiveFrame();
            }
            catch { }
        }

        private static double ItemWidth(TextItem item)
        {
            double w = item.Box.Width;
            if (double.IsNaN(w) || w <= 0) w = item.Box.ActualWidth;
            return w > 0 ? w : 1;
        }

        private static double ItemHeight(TextItem item)
        {
            double h = item.Box.Height;
            if (double.IsNaN(h) || h <= 0) h = item.Box.ActualHeight;
            return h > 0 ? h : 1;
        }

        private static Point RotateVector(Point p, double degrees)
        {
            if (degrees == 0) return p;
            double rad = degrees * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            return new Point(p.X * c - p.Y * s, p.X * s + p.Y * c);
        }

        private void SetActiveText(TextItem? item)
        {
            if (!ReferenceEquals(_activeText, item)) CommitActiveText();
            _activeText = item;

            // 3.36：换到另一个文本框时，别把上一个框的选区记到它头上
            if (item != null && !ReferenceEquals(_styleSelBox, item.Box)) ClearRememberedSelection();

            if (item != null)
            {
                _lastTextItem = item;
                BringTextLayerToFront();
            }
            UpdateActiveFrame();
        }

        // ================================================================
        // 图层顺序：文字与笔迹谁最近被操作，谁就在上面
        // （之前 TextLayer 永远在 InkLayer 之上，画笔怎么画都盖不住文字）
        // ================================================================

        private void BringTextLayerToFront() => SetLayerOrder(textOnTop: true);

        private void BringInkLayerToFront() => SetLayerOrder(textOnTop: false);

        private void SetLayerOrder(bool textOnTop)
        {
            try
            {
                Panel.SetZIndex(InkLayer, textOnTop ? 2 : 3);
                Panel.SetZIndex(TextLayer, textOnTop ? 3 : 2);
            }
            catch { }
        }

        private static ResizeEdge HitTestResizeEdge(FrameworkElement element, Point p)
        {
            if (element == null) return ResizeEdge.None;

            double w = element.ActualWidth;
            double h = element.ActualHeight;
            if (w < 1 || h < 1) return ResizeEdge.None;

            bool left = p.X <= EdgeGrip;
            bool right = p.X >= w - EdgeGrip;
            bool top = p.Y <= EdgeGrip;
            bool bottom = p.Y >= h - EdgeGrip;

            ResizeEdge edge = ResizeEdge.None;
            if (left) edge |= ResizeEdge.Left;
            else if (right) edge |= ResizeEdge.Right;
            if (top) edge |= ResizeEdge.Top;
            else if (bottom) edge |= ResizeEdge.Bottom;

            return edge;
        }

        private static Cursor? CursorForEdge(ResizeEdge edge)
        {
            if (edge == ResizeEdge.None) return null;

            bool lr = (edge & (ResizeEdge.Left | ResizeEdge.Right)) != 0;
            bool tb = (edge & (ResizeEdge.Top | ResizeEdge.Bottom)) != 0;

            if (lr && tb)
            {
                bool sameDirection = ((edge & ResizeEdge.Left) != 0 && (edge & ResizeEdge.Top) != 0)
                                  || ((edge & ResizeEdge.Right) != 0 && (edge & ResizeEdge.Bottom) != 0);
                return sameDirection ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }
            if (lr) return Cursors.SizeWE;
            if (tb) return Cursors.SizeNS;
            return null;
        }

        private void OnTextBoxMouseDown(TextItem item, MouseButtonEventArgs e)
        {
            try
            {
                SetActiveText(item);

                var pos = e.GetPosition(item.Box);

                // 上边界中间：按住拖动整块文字（四向箭头）
                if (IsMoveHandle(item.Box, pos))
                {
                    BeginMove(item, e);
                    return;
                }

                var edge = HitTestResizeEdge(item.Box, pos);
                if (edge == ResizeEdge.None) return;   // 不靠近边角：正常选字 / 输入

                BeginResize(item, edge, e.GetPosition(Stage));
                e.Handled = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：开始缩放文本框失败");
            }
        }

        /// <summary>开始拖动边缘缩放（框内边缘与框外控制点共用）。</summary>
        private void BeginResize(TextItem item, ResizeEdge edge, Point stageStart)
        {
            _resizingItem = item;
            _resizeEdge = edge;
            _resizeStart = stageStart;
            _resizeLeft = Canvas.GetLeft(item.Box);
            _resizeTop = Canvas.GetTop(item.Box);
            if (double.IsNaN(_resizeLeft)) _resizeLeft = 0;
            if (double.IsNaN(_resizeTop)) _resizeTop = 0;
            _resizeWidth = ItemWidth(item);
            _resizeHeight = ItemHeight(item);

            // 3.42：边界小方块的光标跟随缩放方向（上一版曾统一改成箭头，3.43 恢复）
            item.Box.Cursor = CursorForEdge(edge);
            try { item.Box.CaptureMouse(); } catch { }
        }

        /// <summary>
        /// 3.41：白色小方块有一半在文本框外，点到框外那半块也要能拖大小。
        /// 方块本身不接收鼠标（SelectionLayer 只在按钮上命中），这里按文本框本地坐标反查。
        /// </summary>
        private bool TryBeginResizeAtHandle(Point stagePoint)
        {
            try
            {
                double grip = FrameHandleSize / 2.0 + 1.5;
                foreach (var item in _texts.ToList())
                {
                    // 3.54：只有正在编辑的文本框能拖边角；固定后的文字已是画面内容，不再缩放
                    if (!ReferenceEquals(item, _activeText)) continue;

                    var box = item.Box;
                    if (box == null) continue;

                    double w = ItemWidth(item);
                    double h = ItemHeight(item);
                    if (w < 1 || h < 1) continue;

                    // 3.42：按文本框本地坐标反查（手算旋转，避免 TranslatePoint 读旧变换）
                    double left = Canvas.GetLeft(box);
                    double top = Canvas.GetTop(box);
                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    var v = RotateVector(new Point(stagePoint.X - (left + w / 2.0),
                                                   stagePoint.Y - (top + h / 2.0)), -item.Angle);
                    var local = new Point(v.X + w / 2.0, v.Y + h / 2.0);
                    ResizeEdge edge = ResizeEdge.None;
                    bool hit = false;

                    for (int row = 0; row < 3 && !hit; row++)
                    {
                        for (int col = 0; col < 3 && !hit; col++)
                        {
                            if (row == 1 && col == 1) continue;

                            double cx = w * col / 2.0;
                            double cy = h * row / 2.0;
                            if (Math.Abs(local.X - cx) > grip || Math.Abs(local.Y - cy) > grip) continue;

                            hit = true;
                            if (col == 0) edge |= ResizeEdge.Left;
                            else if (col == 2) edge |= ResizeEdge.Right;
                            if (row == 0) edge |= ResizeEdge.Top;
                            else if (row == 2) edge |= ResizeEdge.Bottom;
                        }
                    }

                    if (!hit) continue;

                    SetActiveText(item);
                    BeginResize(item, edge, stagePoint);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void OnTextBoxMouseMove(TextItem item, MouseEventArgs e)
        {
            try
            {
                if (ReferenceEquals(_movingItem, item))
                {
                    DragMove(e.GetPosition(Stage));
                    e.Handled = true;
                    return;
                }

                if (ReferenceEquals(_resizingItem, item))
                {
                    DragResize(e.GetPosition(Stage));
                    e.Handled = true;
                    return;
                }

                var pos = e.GetPosition(item.Box);
                // 3.43：恢复上一版之前的鼠标指针 —— 移动手柄四向箭头、缩放边缘按方向
                if (IsMoveHandle(item.Box, pos)) { item.Box.Cursor = Cursors.SizeAll; return; }

                var edge = HitTestResizeEdge(item.Box, pos);
                var cursor = CursorForEdge(edge);
                if (cursor != null) item.Box.Cursor = cursor;
                else item.Box.Cursor = Cursors.IBeam;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：缩放光标更新失败");
            }
        }

        private void OnTextBoxMouseUp(TextItem item, MouseButtonEventArgs e)
        {
            try
            {
                if (ReferenceEquals(_movingItem, item))
                {
                    EndMove();
                    e.Handled = true;
                    return;
                }

                if (!ReferenceEquals(_resizingItem, item)) return;

                EndResize();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：结束缩放失败");
            }
        }

        private void DragResize(Point current)
        {
            var item = _resizingItem;
            if (item == null) return;

            // 3.41：旋转后按文本框自身的轴缩放，斜着拖也不会跑偏
            var delta = RotateVector(new Point(current.X - _resizeStart.X, current.Y - _resizeStart.Y), -item.Angle);
            double dx = delta.X;
            double dy = delta.Y;
            double left = _resizeLeft;
            double top = _resizeTop;
            double width = _resizeWidth;
            double height = _resizeHeight;

            const double minW = 40;
            const double minH = 24;

            if ((_resizeEdge & ResizeEdge.Left) != 0)
            {
                double newLeft = _resizeLeft + dx;
                double newWidth = _resizeWidth - dx;
                if (newWidth >= minW && newLeft >= 0)
                {
                    left = newLeft;
                    width = newWidth;
                }
            }
            else if ((_resizeEdge & ResizeEdge.Right) != 0)
            {
                double maxRight = Math.Max(minW, (Stage.Width > 0 ? Stage.Width : double.MaxValue) - left);
                width = Math.Max(minW, Math.Min(_resizeWidth + dx, maxRight));
            }

            if ((_resizeEdge & ResizeEdge.Top) != 0)
            {
                double newTop = _resizeTop + dy;
                double newHeight = _resizeHeight - dy;
                if (newHeight >= minH && newTop >= 0)
                {
                    top = newTop;
                    height = newHeight;
                }
            }
            else if ((_resizeEdge & ResizeEdge.Bottom) != 0)
            {
                double maxBottom = Math.Max(minH, (Stage.Height > 0 ? Stage.Height : double.MaxValue) - top);
                height = Math.Max(minH, Math.Min(_resizeHeight + dy, maxBottom));
            }

            Canvas.SetLeft(item.Box, left);
            Canvas.SetTop(item.Box, top);
            item.Box.Width = width;
            item.Box.Height = height;
            UpdateActiveFrame();
        }

        private void EndResize()
        {
            var item = _resizingItem;
            _resizingItem = null;
            _resizeEdge = ResizeEdge.None;

            if (item == null) return;
            try { item.Box.ReleaseMouseCapture(); } catch { }
            try { item.Box.Cursor = Cursors.IBeam; } catch { }
            UpdateActiveFrame();
            MarkDirty();
        }

        /// <summary>上边界中间的一小条 = 移动手柄（与画图程序的文本框一致，四向箭头）。</summary>
        private static bool IsMoveHandle(FrameworkElement box, Point p)
        {
            double w = box.ActualWidth > 0 ? box.ActualWidth : box.Width;
            return w > EdgeGrip * 3 && p.Y <= EdgeGrip && p.X > EdgeGrip && p.X < w - EdgeGrip;
        }

        private void BeginMove(TextItem item, MouseButtonEventArgs e)
        {
            _movingItem = item;
            _moveStart = e.GetPosition(Stage);
            _moveLeft = Canvas.GetLeft(item.Box);
            _moveTop = Canvas.GetTop(item.Box);
            if (double.IsNaN(_moveLeft)) _moveLeft = 0;
            if (double.IsNaN(_moveTop)) _moveTop = 0;

            // 3.43：移动手柄恢复四向箭头
            item.Box.Cursor = Cursors.SizeAll;
            try { item.Box.CaptureMouse(); } catch { }
            e.Handled = true;
        }

        private void DragMove(Point current)
        {
            var item = _movingItem;
            if (item == null) return;

            double w = item.Box.ActualWidth > 0 ? item.Box.ActualWidth : item.Box.Width;
            double h = item.Box.ActualHeight > 0 ? item.Box.ActualHeight : item.Box.Height;

            // 3.41：旋转后按文本框自身的轴移动
            var delta = RotateVector(new Point(current.X - _moveStart.X, current.Y - _moveStart.Y), -item.Angle);
            double left = _moveLeft + delta.X;
            double top = _moveTop + delta.Y;

            double maxLeft = Math.Max(0, (Stage.Width > 0 ? Stage.Width : 0) - w);
            double maxTop = Math.Max(0, (Stage.Height > 0 ? Stage.Height : 0) - h);

            Canvas.SetLeft(item.Box, Math.Max(0, Math.Min(left, maxLeft)));
            Canvas.SetTop(item.Box, Math.Max(0, Math.Min(top, maxTop)));
            UpdateActiveFrame();
        }

        private void EndMove()
        {
            var item = _movingItem;
            _movingItem = null;
            if (item == null) return;
            try { item.Box.ReleaseMouseCapture(); } catch { }
            try { item.Box.Cursor = Cursors.IBeam; } catch { }
            UpdateActiveFrame();
            MarkDirty();
        }

        /// <summary>
        /// 固定当前文本框：先让文本框失焦，再用 Dispatcher.BeginInvoke 延后移除虚线框，
        /// 整个过程 try/catch 兜底 —— 直接在当前鼠标事件里改边框会让 RichTextBox 的
        /// 输入回调踩到已经失效的选区，之前就表现为「输入文字后点空白闪退」。
        /// </summary>
        private void CommitActiveText()
        {
            var item = _activeText;
            if (item == null) return;

            _activeText = null;
            EndMove();
            EndResize();

            // 3.36：文本框固定后就不再「只改选中的那几个字」了，清掉记忆选区
            ClearRememberedSelection();

            try
            {
                HideActiveFrame();

                try { if (item.Box.IsKeyboardFocusWithin) Keyboard.ClearFocus(); } catch { }

                // 3.54：立即断开鼠标 / 键盘 —— 以前放在 Dispatcher.BeginInvoke 里，
                //       固定后马上再点一下（或拖一下边角）还能重新激活它继续打字。
                item.Box.IsHitTestVisible = false;
                item.Box.Focusable = false;
                item.Box.IsTabStop = false;
                item.Box.Cursor = Cursors.Arrow;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        item.Box.BorderThickness = new Thickness(0);
                        item.Box.Cursor = Cursors.Arrow;

                        // ★ 固定后就是画面的一部分（与画图程序一致）：不再响应点击 / 编辑
                        //   橡皮擦仍然按外框擦掉它，撤销也能把它恢复出来。
                        item.Box.IsHitTestVisible = false;
                        item.Box.Focusable = false;

                        if (ReferenceEquals(_activeText, null)) HideActiveFrame();
                    }
                    catch { }
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(ex, "封面编辑页：固定文本框失败");
            }
        }

        private void RemoveTextItem(TextItem item)
        {
            try
            {
                if (ReferenceEquals(_activeText, item)) CommitActiveText();
                TextLayer.Children.Remove(item.Box);
            }
            catch { }
            _texts.Remove(item);
            if (ReferenceEquals(_activeText, item)) _activeText = null;
            UpdateActiveFrame();
            MarkDirty();
        }

        private void ClearTexts()
        {
            try { TextLayer.Children.Clear(); } catch { }
            _texts.Clear();
            _activeText = null;
            _resizingItem = null;
            _resizeEdge = ResizeEdge.None;
            _movingItem = null;
            _erasing = false;
            HideEraserFrame();

            if (_selectionRect != null)
            {
                try { SelectionLayer.Children.Remove(_selectionRect); } catch { }
                _selectionRect = null;
            }

            // 3.42：换封面 / 清空时把选择框一起收掉
            ClearSelection();

            HideActiveFrame();
            EnsureActiveFrame();
        }

        // ================================================================
        // 缩放（10% ~ 400%，步进 10%，Ctrl + 滚轮）
        // 3.35：功能区下方的「音量条」滑块是主入口，- / + 按钮与 Ctrl+滚轮保持同步。
        // ================================================================

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - ZoomStep);

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + ZoomStep);

        private void SlZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_zoomSync) return;
            SetZoom(e.NewValue);
        }

        /// <summary>3.36：缩放滑块支持鼠标滚轮（滚一格 ±10%，与按钮、Ctrl+滚轮一致）。</summary>
        private void SlZoom_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                if (e.Delta == 0) return;
                e.Handled = true;
                SetZoom(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
            }
            catch { }
        }

        private void StageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            SetZoom(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
        }

        private void SetZoom(double percent)
        {
            try
            {
                if (double.IsNaN(percent)) percent = 100;
                _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, percent));
                if (ZoomScale != null)
                {
                    ZoomScale.ScaleX = _zoom / 100.0;
                    ZoomScale.ScaleY = _zoom / 100.0;
                }

                if (SlZoom != null && Math.Abs(SlZoom.Value - _zoom) > 0.01)
                {
                    _zoomSync = true;
                    try { SlZoom.Value = _zoom; } catch { }
                    finally { _zoomSync = false; }
                }

                UpdateZoomText();
            }
            catch { }
        }

        private void UpdateZoomText()
        {
            try
            {
                if (TxtZoom == null) return;
                string text = ((int)Math.Round(_zoom)).ToString() + "%";
                if (TxtZoom.Text != text) TxtZoom.Text = text;
            }
            catch { }
        }

        // ================================================================
        // 撤销 / 恢复
        // ================================================================

        private void OnStrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            var stroke = e.Stroke;
            if (stroke == null) return;

            _undo.Push(new EditAction
            {
                Undo = () => InkLayer.Strokes.Remove(stroke),
                Redo = () => { if (!InkLayer.Strokes.Contains(stroke)) InkLayer.Strokes.Add(stroke); }
            });
            _redo.Clear();
            // 新画的这一笔盖住已有的文字（与「画图」一致：后画的在上）
            BringInkLayerToFront();
            MarkDirty();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_undo.Count == 0) return;
            var action = _undo.Pop();
            try { action.Undo(); } catch { }
            _redo.Push(action);
            MarkDirty();
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0) return;
            var action = _redo.Pop();
            try { action.Redo(); } catch { }
            _undo.Push(action);
            MarkDirty();
        }

        // ================================================================
        // 封面样式（圆角 / 方角）
        // ================================================================

        private void BtnStyleRound_Click(object sender, RoutedEventArgs e) => SetRoundCover(true);

        private void BtnStyleSquare_Click(object sender, RoutedEventArgs e) => SetRoundCover(false);

        private void SetRoundCover(bool round)
        {
            if (_roundCover == round)
            {
                UpdateStyleButtons();
                return;
            }

            _roundCover = round;
            UpdateStyleButtons();
            ApplyRoundPreview();
            MarkDirty();
        }

        private void UpdateStyleButtons()
        {
            SetToggle(BtnStyleRound, _roundCover);
            SetToggle(BtnStyleSquare, !_roundCover);
        }

        private void ApplyRoundPreview()
        {
            try { RoundCorner.SetEnabled(Stage, _roundCover); } catch { }
        }

        // ================================================================
        // 悬浮提示（复用 HintBubble：可显示到窗口外，但不出屏幕）
        // ================================================================

        private void AttachToolHints()
        {
            AttachHint(BtnImport, "替换 / 导入封面");
            AttachHint(BtnSave, "保存封面");
            // 3.37：页签不再有悬浮提示（只靠文字颜色 + 下方横线表示选中）
            AttachHint(BtnTextTool, "文本框");
            AttachHint(BtnSelectTool, "选择：框选后可复制、剪切、删除或拖动移动");
            AttachHint(BtnPen, "画笔");
            _highlightHint = AttachHint(BtnHighlight, "荧光笔：给选中的文字加荧光");
            AttachHint(BtnHighlightMenu, "荧光样式");
            AttachHint(BtnEyedropper, "取色器：点画面、文字或笔迹取色");
            AttachHint(ComboFontFamily, "字体");
            _textColorHint = AttachHint(BtnTextColor, "文字颜色");
            AttachHint(BtnBold, "粗体");
            AttachHint(BtnItalic, "斜体");
            AttachHint(BtnUnderline, "下划线");
            AttachHint(BtnStrike, "删除线");
            _paintColorHint = AttachHint(BtnPaintColor, "颜色");
            AttachHint(BtnEraser, "橡皮擦");
            AttachHint(ComboPenSize, "粗细");
            AttachHint(BtnStyleRound, "圆角封面");
            AttachHint(BtnStyleSquare, "方角封面");
            AttachHint(BtnUndo, "撤销");
            AttachHint(BtnRedo, "恢复");
            AttachHint(BtnZoomOut, "缩小");
            AttachHint(BtnZoomIn, "放大");
            AttachHint(SlZoom, "缩放（也可 Ctrl + 滚轮）");
            AttachHint(TxtZoom, "当前缩放");
            AttachHint(BtnMediaPlayPause, "播放 / 暂停");
            AttachHint(SlMediaProgress, "播放进度");
        }

        private HintBubble? AttachHint(FrameworkElement target, string text)
        {
            if (target == null) return null;

            var hint = new HintBubble(target, text);
            _toolHints.Add(hint);

            if (target is ComboBox combo)
            {
                // 下拉展开后鼠标在弹出层里，MouseLeave 不会触发；而且展开期间仍可能收到
                // MouseMove，所以展开时主动收起，并在 IsDropDownOpen 期间不再显示。
                combo.DropDownOpened += (s, e) => hint.Hide();
                combo.MouseEnter += (s, e) => { if (!combo.IsDropDownOpen) hint.Show(Mouse.GetPosition(target)); };
                combo.MouseMove += (s, e) => { if (!combo.IsDropDownOpen) hint.Show(Mouse.GetPosition(target)); };
                combo.MouseLeave += (s, e) => hint.Hide();
                combo.Unloaded += (s, e) => hint.Hide();
                return hint;
            }

            target.MouseEnter += (s, e) => hint.Show(Mouse.GetPosition(target));
            target.MouseMove += (s, e) => hint.Show(Mouse.GetPosition(target));
            target.MouseLeave += (s, e) => hint.Hide();
            target.Unloaded += (s, e) => hint.Hide();
            return hint;
        }

        /// <summary>缩放 / 样式等按钮的提示统一由 <see cref="AttachToolHints"/> 挂载，这里不再单独处理。</summary>
        private void VideoPreview_MediaOpened(object sender, RoutedEventArgs e)
        {
            try
            {
                var me = sender as MediaElement;
                if (me == null) return;
                if (me.NaturalVideoWidth > 0 && me.NaturalVideoHeight > 0)
                {
                    // 批注层尺寸跟着视频走：否则保存后叠在视频上会被拉伸变形
                    _coverAspect = me.NaturalVideoWidth / (double)me.NaturalVideoHeight;
                    LayoutStage();
                }
                UpdateMediaBarVisibility();
                UpdateMediaProgress();
            }
            catch { }
        }

        private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                VideoPreview.Position = TimeSpan.Zero;
                // 3.36：暂停状态下（取色 / 拖进度）不要自动续播，否则永远停在第二帧
                if (!_mediaPaused) VideoPreview.Play();
            }
            catch { }
        }
    }
}
