using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RandomDrawer
{
    /// <summary>
    /// 「模糊效果」设置弹窗：模糊强度 + 应用到哪些界面（抽取结果 / 抽取动画 / 加载界面），右侧带模糊预览。
    /// 拖动滑块或勾选时通过 <see cref="Changed"/> 让主界面实时预览。
    /// </summary>
    public partial class BlurSettingsDialog : FloatingDialogBase
    {
        /// <summary>参数：模糊值、结果页、动画、加载界面。</summary>
        public event Action<double, bool, bool, bool> Changed;

        public double Blur { get; private set; }
        public bool OnResult { get; private set; }
        public bool OnAnimation { get; private set; }
        public bool OnLoading { get; private set; }

        private bool _loading;
        private readonly BitmapSource _previewSource;
        private readonly bool _animationAvailable;

        public BlurSettingsDialog(double blur, bool onResult, bool onAnimation, bool onLoading,
                                  BitmapSource previewSource = null, bool animationAvailable = true)
        {
            InitializeComponent();

            Blur = Math.Max(0, Math.Min(ImageBlurUtil.MaxRadius, blur));
            OnResult = onResult;
            OnLoading = onLoading;
            _previewSource = previewSource;
            _animationAvailable = animationAvailable;
            OnAnimation = animationAvailable && onAnimation;

            _loading = true;
            try
            {
                SliderBlur.Value = Blur;
                TxtBlurValue.Text = ((int)Math.Round(Blur)).ToString();
                ChkResult.IsChecked = OnResult;
                ChkAnimation.IsChecked = OnAnimation;
                ChkLoading.IsChecked = OnLoading;

                // 没有头像 / 立绘时，抽取动画根本播不了，模糊也没意义
                if (!animationAvailable)
                {
                    ChkAnimation.IsEnabled = false;
                    if (TxtAnimationHint != null) TxtAnimationHint.Visibility = Visibility.Visible;
                }
            }
            finally { _loading = false; }

            UpdatePreview();
        }

        /// <summary>右侧预览：按当前模糊值渲染缩略图。</summary>
        private void UpdatePreview()
        {
            try
            {
                if (PreviewPanel == null) return;

                if (_previewSource == null)
                {
                    // 没有可预览的图片时不显示任何误导性文字，只留一层底色
                    PreviewPanel.Background = ThemeManager.GetBrush("Bg_F0F0F0");
                    return;
                }

                BitmapSource shown = Blur > 0.5
                    ? ImageBlurUtil.MakeBlurred(_previewSource, Blur, 320)
                    : _previewSource;

                PreviewPanel.Background = new ImageBrush(shown) { Stretch = Stretch.UniformToFill };
            }
            catch { }
        }

        private void SliderBlur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            Blur = e.NewValue;
            if (TxtBlurValue != null) TxtBlurValue.Text = ((int)Math.Round(e.NewValue)).ToString();
            UpdatePreview();
            if (!_loading) RaiseChanged();
        }

        private void Target_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            OnResult = ChkResult.IsChecked == true;
            OnAnimation = _animationAvailable && ChkAnimation.IsChecked == true;
            OnLoading = ChkLoading.IsChecked == true;
            try { Changed?.Invoke(Blur, OnResult, OnAnimation, OnLoading); } catch { }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            OnResult = ChkResult.IsChecked == true;
            OnAnimation = _animationAvailable && ChkAnimation.IsChecked == true;
            OnLoading = ChkLoading.IsChecked == true;
            DialogResult = true;
        }
    }
}
