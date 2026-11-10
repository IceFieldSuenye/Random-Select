using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RandomDrawer
{
    /// <summary>「本次抽取详情」：只展示已抽取 / 未抽取数量，可展开查看对象列表。</summary>
    public partial class DrawDetailDialog : FloatingDialogBase
    {
        private const string ChevronRight = "\uE76C";
        private const string ChevronDown = "\uE70D";

        private readonly List<string> _drawn;
        private readonly List<string> _undrawn;

        public DrawDetailDialog(IEnumerable<string> drawn, IEnumerable<string> undrawn)
        {
            InitializeComponent();

            _drawn = drawn?.ToList() ?? new List<string>();
            _undrawn = undrawn?.ToList() ?? new List<string>();

            TxtSummary.Text = $"已抽取数量：{_drawn.Count}    未抽取数量：{_undrawn.Count}";

            Fill(PanelDrawn, _drawn);
            Fill(PanelUndrawn, _undrawn);

            // 3.72：「已抽取的对象」默认展开（未抽取保持收起）
            _drawnExpanded = true;
            UpdateHeaders();

            Loaded += (s, e) =>
            {
                try
                {
                    if (_drawnExpanded) PanelAnimator.Animate(BoxDrawn, true);
                    if (_undrawnExpanded) PanelAnimator.Animate(BoxUndrawn, true);
                }
                catch { }
            };
        }

        private static void Fill(StackPanel panel, List<string> names)
        {
            panel.Children.Clear();

            if (names.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "（无）",
                    FontSize = 13,
                    Opacity = 0.6,
                    Margin = new Thickness(0, 2, 0, 2)
                });
                return;
            }

            for (int i = 0; i < names.Count; i++)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"{i + 1}.{names[i]}",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }

        private void UpdateHeaders()
        {
            ChevronDrawn.Text = _drawnExpanded ? ChevronDown : ChevronRight;
            ChevronUndrawn.Text = _undrawnExpanded ? ChevronDown : ChevronRight;
        }

        private bool _drawnExpanded;
        private bool _undrawnExpanded;

        private void DrawnHeader_Click(object sender, MouseButtonEventArgs e)
        {
            _drawnExpanded = !_drawnExpanded;
            UpdateHeaders();
            PanelAnimator.Animate(BoxDrawn, _drawnExpanded, 170);
        }

        private void UndrawnHeader_Click(object sender, MouseButtonEventArgs e)
        {
            _undrawnExpanded = !_undrawnExpanded;
            UpdateHeaders();
            PanelAnimator.Animate(BoxUndrawn, _undrawnExpanded, 170);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            try { DialogResult = true; } catch { }
        }
    }
}
