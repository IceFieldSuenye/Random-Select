using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RandomDrawer
{
    /// <summary>
    /// 可展开搜索框：收起时无边框显示「放大镜 + 搜索」，点击后宽度平滑展开；
    /// 输入停止 0.5 秒后触发 SearchChanged。
    /// </summary>
    public partial class SearchBox : UserControl
    {
        private readonly DispatcherTimer _debounce;
        private const double CollapsedWidth = 0;
        private const double ExpandedWidth = 220;
        private bool _isCollapsing;

        public event Action<string> SearchChanged;

        public string SearchText => TxtSearch?.Text ?? "";

        public SearchBox()
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                SearchChanged?.Invoke(SearchText.Trim());
            };

            InitializeComponent();

            // 新交互：搜索框始终直接显示，不再使用“点击展开”的按钮形态
            BtnCollapsed.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.Width = ExpandedWidth;
        }

        private void Expand_Click(object sender, RoutedEventArgs e)
        {
            _isCollapsing = false;
            ExpandedPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
            BtnCollapsed.Visibility = Visibility.Collapsed;
            ExpandedPanel.Visibility = Visibility.Visible;
            ExpandedPanel.Width = CollapsedWidth;

            var anim = new DoubleAnimation(CollapsedWidth, ExpandedWidth,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            ExpandedPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    TxtSearch.Focus();
                    TxtSearch.CaretIndex = TxtSearch.Text.Length;
                }
                catch { }
            }), DispatcherPriority.Input);
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtPlaceholder != null)
                TxtPlaceholder.Visibility = (string.IsNullOrEmpty(TxtSearch.Text) && !IsKeyboardFocusWithin)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (BtnClear != null)
                BtnClear.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
                    ? Visibility.Collapsed : Visibility.Visible;

            _debounce.Stop();
            _debounce.Start();
        }

        private void TxtSearch_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            // 搜索框始终显示，不再在失焦时收回
            if (TxtPlaceholder != null && !IsKeyboardFocusWithin && string.IsNullOrEmpty(TxtSearch.Text))
                TxtPlaceholder.Visibility = Visibility.Visible;
        }

        private void TxtSearch_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            // 进入输入状态立即隐藏占位文本（含输入法组合输入阶段）
            if (TxtPlaceholder != null) TxtPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void TxtSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            _debounce.Stop();
            SearchChanged?.Invoke(SearchText.Trim());
        }

        private void CollapseSearch(bool animate)
        {
            if (_isCollapsing || BtnCollapsed.Visibility == Visibility.Visible) return;

            _debounce.Stop();
            _isCollapsing = true;

            if (!animate)
            {
                FinishCollapse();
                return;
            }

            double from = ExpandedPanel.ActualWidth > 0 ? ExpandedPanel.ActualWidth : ExpandedWidth;
            var anim = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (s, e) => FinishCollapse();
            ExpandedPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void FinishCollapse()
        {
            try
            {
                ExpandedPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                ExpandedPanel.Width = 0;
                ExpandedPanel.Visibility = Visibility.Collapsed;
                BtnCollapsed.Visibility = Visibility.Visible;
            }
            catch { }
            _isCollapsing = false;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _debounce.Stop();
            TxtSearch.Text = "";
            SearchChanged?.Invoke("");
            try { TxtSearch.Focus(); } catch { }
        }

        /// <summary>外部设置搜索词（不会触发防抖，直接更新显示）。</summary>
        public void SetTextWithoutSearch(string text)
        {
            if (TxtSearch == null) return;
            _debounce.Stop();
            TxtSearch.Text = text ?? "";
        }
    }
}
