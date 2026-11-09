// 该文件大量使用「查不到就按没有处理」的防御式写法，这里关闭可空性检查以减少噪音
#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace RandomDrawer
{
    /// <summary>
    /// 图标字体兼容层。
    ///
    /// Windows 7 / 8 / 早期 Windows 10 以及图标字体被裁剪、损坏的系统上，
    /// 「Segoe Fluent Icons」「Segoe MDL2 Assets」并不会提供界面用到的全部字形，
    /// WPF 遇到缺失字形时只会画出一个「方框」。
    ///
    /// 这里统一做三件事：
    /// 1. 启动时逐个检查界面用到的图标码位在主图标字体里是否真实存在；
    /// 2. 缺失的码位换成语义相同、旧系统也一定有的码位；
    /// 3. 仍然无法显示的码位直接从文本里去掉，纯图标按钮补上文字，
    ///    从而保证任何系统上都不会再出现「方框」。
    /// </summary>
    internal static class IconSupport
    {
        public const string FluentFontName = "Segoe Fluent Icons";
        public const string Mdl2FontName = "Segoe MDL2 Assets";

        /// <summary>没有可用图标字体时使用的字体，避免保留一个不存在的字体名。</summary>
        private const string FallbackFontName = "Segoe UI";

        /// <summary>
        /// 界面用到的全部图标码位（新增图标时同步维护；即使漏掉，
        /// 运行期的 <see cref="Resolve"/> 仍会兜底处理，不会出现方框）。
        /// </summary>
        private static readonly int[] RequiredCodepoints =
        {
            0xE70D, 0xE70F, 0xE710, 0xE711, 0xE713, 0xE715, 0xE721, 0xE72A, 0xE72B,
            0xE72C, 0xE72D, 0xE72E, 0xE733, 0xE738, 0xE73A, 0xE73E, 0xE74D, 0xE74E,
            0xE74F, 0xE768, 0xE76C, 0xE777, 0xE77B, 0xE783, 0xE790, 0xE7B3, 0xE7BA,
            0xE7C3, 0xE80F, 0xE81C, 0xE838, 0xE893, 0xE8A7, 0xE8AC, 0xE8B0, 0xE8B7,
            0xE8C8, 0xE8D6, 0xE8E5, 0xE8FD, 0xE8FF, 0xE90F, 0xE91B, 0xE926, 0xE946,
            0xE993, 0xE994, 0xE995, 0xE9CE, 0xEA39, 0xEA57, 0xEA58, 0xEB9F, 0xF0E2,
            0xF3B1
        };

        /// <summary>
        /// 缺失字形 → 语义等价、旧系统也存在的替代字形（按顺序取第一个可用的）。
        /// 只在系统缺少该字形时才会用到。
        /// </summary>
        private static readonly Dictionary<int, int[]> Fallbacks = new Dictionary<int, int[]>
        {
            { 0xF0E2, new[] { 0xE713 } },          // 常规（网格） → 设置
            { 0xF3B1, new[] { 0xE7E8, 0xE8BB } },  // 退出（门） → 电源 / 关闭
            { 0xE9CE, new[] { 0xE897 } },          // 帮助 → 帮助（旧字形）
            { 0xE8AC, new[] { 0xE70F } },          // 重命名 → 编辑
            { 0xE8B0, new[] { 0xE77B } },          // 头像 → 联系人
            { 0xE8E5, new[] { 0xE91B, 0xEB9F } },  // 图片（带刷新） → 图片
            { 0xEB9F, new[] { 0xE91B } },          // 图片 → 图片（旧字形）
            { 0xE91B, new[] { 0xEB9F } },          // 图片 → 图片（另一字形）
            { 0xE738, new[] { 0xE74D } },          // 移除 → 删除
            { 0xE777, new[] { 0xE72C } },          // 刷新（另一字形） → 刷新
            { 0xE893, new[] { 0xE768 } },          // 下一段 → 播放
            { 0xEA57, new[] { 0xE768 } },          // 单次抽取 → 播放
            { 0xEA58, new[] { 0xE710 } },          // 多次抽取 → 新增
            { 0xEA39, new[] { 0xE8B7, 0xE838 } }   // 未创建目录（占位） → 文件夹 / 打开文件夹
        };

        private static readonly HashSet<int> MissingCodepoints = new HashSet<int>();
        private static readonly Dictionary<int, int> Replacements = new Dictionary<int, int>();

        /// <summary>设置环境变量 RS_ICON_DEBUG=1 后，把图标处理中的问题写入运行日志（排查用）。</summary>
        private static readonly bool DebugEnabled = IsEnvTrue("RS_ICON_DEBUG");

        private static HashSet<int> _glyphMap;
        private static bool _initialized;

        private static void Debug(string message)
        {
            if (!DebugEnabled) return;
            try { RuntimeLog.Info("[ICON] " + message); } catch { }
        }


        /// <summary>主图标字体名（缺字形时也仍然用它，缺失的码位交给替换逻辑处理）。</summary>
        public static string PrimaryFontName { get; private set; } = FluentFontName;

        /// <summary>主图标字体对应的 FontFamily，代码里创建图标 TextBlock 时统一使用。</summary>
        public static FontFamily IconFontFamily { get; private set; } = new FontFamily(FluentFontName);

        /// <summary>系统是否提供了图标字体（哪怕字形不全）。</summary>
        public static bool FontInstalled => _glyphMap != null;

        /// <summary>是否需要启用图标兼容模式（有字形缺失或被替换）。</summary>
        public static bool CompatActive => MissingCodepoints.Count > 0 || Replacements.Count > 0;

        /// <summary>
        /// 是否需要提醒用户「图标显示异常」。
        ///
        /// 只有「完全找不到图标字体」或「存在多处彻底无法显示的字形」才算异常；
        /// Windows 10 上只是把少数新字形换成旧的等价字形（全部替换成功、界面看不出问题），
        /// 属于正常的兼容处理，不再弹窗打扰；个别字形实在换不了，也只是少一个图标。
        /// </summary>
        public static bool NeedsCompatNotice => !FontInstalled || MissingCodepoints.Count >= 3;

        /// <summary>完全无法显示、只能去掉的字形数量。</summary>
        public static int MissingGlyphCount => MissingCodepoints.Count;

        /// <summary>换成了等价旧字形的数量。</summary>
        public static int ReplacedGlyphCount => Replacements.Count;

        /// <summary>兼容状态描述，写入运行日志。</summary>
        public static string StatusDescription
        {
            get
            {
                if (!FontInstalled) return $"未找到图标字体，{RequiredCodepoints.Length} 个图标全部不可用";
                if (!CompatActive) return $"{PrimaryFontName}，全部 {RequiredCodepoints.Length} 个图标可用";
                return $"{PrimaryFontName}，缺失 {MissingGlyphCount} 个字形、替换 {ReplacedGlyphCount} 个字形";
            }
        }

        // ================================================================
        // 初始化
        // ================================================================

        /// <summary>
        /// 初始化图标检测。
        /// 调试用环境变量：
        ///   RS_ICON_COMPAT=1   —— 装作系统没有图标字体，验证兼容模式的最终效果；
        ///   RS_ICON_MISSING=E70F,E838 —— 装作这些码位缺失，验证「替换为等价字形 / 去图标」的效果。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                bool forceCompat = IsEnvTrue("RS_ICON_COMPAT");
                bool hasFluent = false, hasMdl2 = false;
                HashSet<int> fluentMap = null, mdl2Map = null;

                if (!forceCompat)
                {
                    foreach (var family in Fonts.SystemFontFamilies)
                    {
                        string name = family?.Source ?? "";
                        if (name.Equals(FluentFontName, StringComparison.OrdinalIgnoreCase))
                        {
                            fluentMap = ReadGlyphMap(family);
                            hasFluent = fluentMap != null;
                        }
                        else if (name.Equals(Mdl2FontName, StringComparison.OrdinalIgnoreCase))
                        {
                            mdl2Map = ReadGlyphMap(family);
                            hasMdl2 = mdl2Map != null;
                        }
                    }
                }

                HashSet<int> ApplySimulatedMissing(HashSet<int> map)
                {
                    string simulated = null;
                    try { simulated = Environment.GetEnvironmentVariable("RS_ICON_MISSING"); } catch { }
                    if (string.IsNullOrWhiteSpace(simulated) || map == null) return map;

                    var result = new HashSet<int>(map);
                    foreach (string part in simulated.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string hex = part.Trim();
                        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int cp))
                            result.Remove(cp);
                    }
                    return result;
                }

                fluentMap = ApplySimulatedMissing(fluentMap);
                mdl2Map = ApplySimulatedMissing(mdl2Map);

                bool coversAll(HashSet<int> map) => map != null && RequiredCodepoints.All(map.Contains);

                if (hasFluent && coversAll(fluentMap))
                {
                    PrimaryFontName = FluentFontName;
                    _glyphMap = fluentMap;
                }
                else if (hasMdl2 && coversAll(mdl2Map))
                {
                    PrimaryFontName = Mdl2FontName;
                    _glyphMap = mdl2Map;
                }
                else if (hasFluent)
                {
                    PrimaryFontName = FluentFontName;
                    _glyphMap = fluentMap;
                }
                else if (hasMdl2)
                {
                    PrimaryFontName = Mdl2FontName;
                    _glyphMap = mdl2Map;
                }
                else
                {
                    PrimaryFontName = FallbackFontName;
                    _glyphMap = null;
                }

                MissingCodepoints.Clear();
                Replacements.Clear();

                foreach (int codepoint in RequiredCodepoints)
                {
                    if (_glyphMap != null && _glyphMap.Contains(codepoint)) continue;

                    int substitute = 0;
                    if (_glyphMap != null && Fallbacks.TryGetValue(codepoint, out int[] alternatives))
                    {
                        foreach (int alternative in alternatives)
                        {
                            if (_glyphMap.Contains(alternative)) { substitute = alternative; break; }
                        }
                    }

                    if (substitute > 0) Replacements[codepoint] = substitute;
                    else MissingCodepoints.Add(codepoint);
                }

                IconFontFamily = new FontFamily(PrimaryFontName);
            }
            catch (Exception ex)
            {
                // 检测失败时按「没有图标字体」处理，保证不会出现方框
                PrimaryFontName = FallbackFontName;
                IconFontFamily = new FontFamily(FallbackFontName);
                _glyphMap = null;
                MissingCodepoints.Clear();
                Replacements.Clear();
                foreach (int codepoint in RequiredCodepoints) MissingCodepoints.Add(codepoint);
                try { RuntimeLog.Error(ex, "图标字体检测失败"); } catch { }
            }

            try { RuntimeLog.Info("图标字体检测：" + StatusDescription); } catch { }
        }

        private static HashSet<int> ReadGlyphMap(FontFamily family)
        {
            try
            {
                Typeface best = null;
                foreach (var typeface in family.GetTypefaces())
                {
                    try
                    {
                        if (!typeface.TryGetGlyphTypeface(out GlyphTypeface glyph) || glyph == null) continue;
                        bool regular = typeface.Style == FontStyles.Normal && typeface.Weight == FontWeights.Normal;
                        if (best == null || regular) best = typeface;
                        if (regular) break;
                    }
                    catch { }
                }

                if (best == null) return null;
                if (!best.TryGetGlyphTypeface(out GlyphTypeface result) || result == null) return null;
                return new HashSet<int>(result.CharacterToGlyphMap.Keys);
            }
            catch { return null; }
        }

        private static bool IsEnvTrue(string name)
        {
            try
            {
                string value = Environment.GetEnvironmentVariable(name);
                return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ================================================================
        // 文本处理
        // ================================================================

        /// <summary>字符是否位于图标字体常用的 Unicode 私用区。</summary>
        public static bool IsIconChar(char ch) => ch >= '\uE000' && ch <= '\uF8FF';

        public static bool ContainsIconChar(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char ch in text)
                if (IsIconChar(ch)) return true;
            return false;
        }

        /// <summary>当前字体能否显示该码位。</summary>
        public static bool HasGlyph(int codepoint)
        {
            if (_glyphMap == null) return false;
            if (_glyphMap.Contains(codepoint)) return true;
            return Replacements.ContainsKey(codepoint);
        }

        /// <summary>
        /// 把文本中无法显示的图标字形替换成等价字形，或者直接去掉。
        /// 只有在兼容模式下、且文本里确实含有图标字符时才会真正处理。
        /// </summary>
        public static string Resolve(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!CompatActive) return text;
            if (!ContainsIconChar(text)) return text;

            var builder = new StringBuilder(text.Length);
            bool changed = false;

            foreach (char ch in text)
            {
                if (!IsIconChar(ch)) { builder.Append(ch); continue; }

                int codepoint = ch;
                if (_glyphMap != null && _glyphMap.Contains(codepoint)) { builder.Append(ch); continue; }

                if (Replacements.TryGetValue(codepoint, out int substitute))
                {
                    builder.Append((char)substitute);
                    changed = true;
                    continue;
                }

                // 完全无法显示：直接去掉，避免出现方框
                changed = true;
            }

            if (!changed) return text;
            return NormalizeSpaces(builder.ToString());
        }

        /// <summary>去掉字形后可能留下连续空格和首尾空格。</summary>
        private static string NormalizeSpaces(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            foreach (char ch in text)
            {
                bool isSpace = ch == ' ' || ch == '\u3000';
                if (isSpace && lastWasSpace) continue;
                builder.Append(ch);
                lastWasSpace = isSpace;
            }
            return builder.ToString().Trim();
        }

        /// <summary>整个文本是否只由图标字符组成。</summary>
        private static bool IsIconOnlyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool hasIcon = false;
            foreach (char ch in text)
            {
                if (IsIconChar(ch)) { hasIcon = true; continue; }
                if (!char.IsWhiteSpace(ch)) return false;
            }
            return hasIcon;
        }

        // ================================================================
        // 界面元素处理
        // ================================================================

        private static bool _normalizing;
        private static readonly HashSet<TextBlock> CollapsedByUs = new HashSet<TextBlock>();
        private static readonly HashSet<TextBlock> Watched = new HashSet<TextBlock>();
        private static readonly DependencyPropertyDescriptor TextBlockTextDescriptor =
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

        /// <summary>兼容模式下需要规范化文本的元素统一从这里处理。</summary>
        public static void Normalize(TextBlock textBlock)
        {
            if (!CompatActive || textBlock == null) return;
            if (_normalizing) return;

            _normalizing = true;
            try
            {
                int inlineCount = textBlock.Inlines?.Count ?? 0;
                string current = textBlock.Text ?? "";
                bool inlineIcons = inlineCount > 0 && InlinesContainIcon(textBlock.Inlines);
                bool textIcons = !string.IsNullOrEmpty(current) && ContainsIconChar(current);
                if (inlineIcons)
                {
                    // 用 <Run> 拼出来的文本：Text 属性为空，只能逐段处理
                    NormalizeInlines(textBlock.Inlines);
                    if (!TextHasVisibleContent(textBlock) && !InlinesHaveVisibleText(textBlock.Inlines))
                    {
                        textBlock.Visibility = Visibility.Collapsed;
                        CollapsedByUs.Add(textBlock);
                    }
                }
                else if (textIcons)
                {
                    if (BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty)) return;

                    string resolved = Resolve(current);
                    if (!string.Equals(resolved, current, StringComparison.Ordinal))
                    {
                        textBlock.Text = resolved;
                        if (resolved.Length == 0)
                        {
                            textBlock.Visibility = Visibility.Collapsed;
                            CollapsedByUs.Add(textBlock);
                        }
                    }
                }

                if (CollapsedByUs.Contains(textBlock) && TextHasVisibleContent(textBlock))
                {
                    textBlock.Visibility = Visibility.Visible;
                    CollapsedByUs.Remove(textBlock);
                }

                // 只监听「可能显示图标」的文本，避免为全界面每个 TextBlock 都挂监听
                bool stillIconText = ContainsIconChar(textBlock.Text) || InlinesContainIcon(textBlock.Inlines);
                if (stillIconText || (textBlock.FontFamily != null
                         && string.Equals(textBlock.FontFamily.Source, PrimaryFontName, StringComparison.OrdinalIgnoreCase)))
                {
                    Watch(textBlock);
                    WatchVisibility(textBlock);
                }
            }
            catch (Exception ex) { Debug("normalize failed: " + ex.GetType().Name + " " + ex.Message); }
            finally { _normalizing = false; }
        }

        /// <summary>TextBlock 的 Text 是否还有可见内容（忽略空白）。</summary>
        private static bool TextHasVisibleContent(TextBlock textBlock)
        {
            string text = textBlock.Text ?? "";
            foreach (char ch in text)
                if (!char.IsWhiteSpace(ch)) return true;
            return false;
        }

        private static bool InlinesContainIcon(InlineCollection inlines)
        {
            if (inlines == null) return false;
            foreach (Inline inline in inlines)
            {
                if (inline is Run run && !string.IsNullOrEmpty(run.Text) && ContainsIconChar(run.Text)) return true;
                if (inline is Span span && InlinesContainIcon(span.Inlines)) return true;
            }
            return false;
        }

        private static bool InlinesHaveVisibleText(InlineCollection inlines)
        {
            if (inlines == null) return false;
            foreach (Inline inline in inlines)
            {
                if (inline is Run run && !string.IsNullOrEmpty(run.Text))
                {
                    foreach (char ch in run.Text)
                        if (!char.IsWhiteSpace(ch)) return true;
                }
                else if (inline is Span span && InlinesHaveVisibleText(span.Inlines)) return true;
                else if (inline is InlineUIContainer) return true;
            }
            return false;
        }


        public static void Normalize(AccessText accessText)
        {
            if (!CompatActive || accessText == null) return;
            if (_normalizing) return;

            _normalizing = true;
            try
            {
                if (string.IsNullOrEmpty(accessText.Text) || !ContainsIconChar(accessText.Text)) return;
                if (BindingOperations.IsDataBound(accessText, AccessText.TextProperty)) return;

                string resolved = Resolve(accessText.Text);
                if (!string.Equals(resolved, accessText.Text, StringComparison.Ordinal))
                {
                    accessText.Text = resolved;
                    if (resolved.Length == 0) accessText.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
            finally { _normalizing = false; }
        }

        private static void NormalizeInlines(InlineCollection inlines)
        {
            if (inlines == null) return;

            foreach (Inline inline in inlines)
            {
                if (inline is Run run)
                {
                    if (string.IsNullOrEmpty(run.Text) || !ContainsIconChar(run.Text)) continue;
                    string resolved = Resolve(run.Text);
                    if (!string.Equals(resolved, run.Text, StringComparison.Ordinal)) run.Text = resolved;
                }
                else if (inline is Span span)
                {
                    NormalizeInlines(span.Inlines);
                }
            }
        }

        /// <summary>
        /// 内容为图标字符串的控件：先处理内容本身；处理完后仍为空的纯图标控件补上文字。
        /// </summary>
        public static void NormalizeContentControl(ContentControl control)
        {
            NormalizeContentControl(control, false);
        }

        private static void NormalizeContentControl(ContentControl control, bool iconOnlyContent)
        {
            if (!CompatActive || control == null) return;

            try
            {
                object content = control.Content;
                if (content == null) return;

                if (content is string text)
                {
                    if (!ContainsIconChar(text)) return;
                    if (BindingOperations.IsDataBound(control, ContentControl.ContentProperty)) return;

                    string resolved = Resolve(text);
                    if (resolved.Length > 0)
                    {
                        if (!string.Equals(resolved, text, StringComparison.Ordinal)) control.Content = resolved;
                        return;
                    }

                    ApplyTextFallback(control);
                    return;
                }

                // 图标被去掉后控件里已经没有可见文字（比如纯图标按钮）→ 用提示文字补上
                if (HasVisibleText(control)) return;
                if (!iconOnlyContent && !HasCollapsedIcon(control) && !IsIconOnlyContent(control.Content)) return;
                ApplyTextFallback(control);
            }
            catch { }
        }

        /// <summary>内容是否「只有图标」（没有实际文字）。</summary>
        private static bool IsIconOnlyContent(object content)
        {
            if (content == null) return false;
            if (content is string text) return IsIconOnlyText(text);
            if (content is TextBlock block) return IsIconOnlyText(block.Text ?? "");
            if (content is Border border) return IsIconOnlyContent(border.Child);
            if (content is ContentControl control) return IsIconOnlyContent(control.Content);
            if (content is Panel panel)
            {
                bool hasIcon = false;
                foreach (UIElement child in panel.Children)
                {
                    if (IsIconOnlyContent(child)) { hasIcon = true; continue; }
                    if (child is TextBlock tb && IsIconOnlyText(tb.Text ?? "")) { hasIcon = true; continue; }
                    return false;
                }
                return hasIcon;
            }
            // Image / Path / 媒体等：不算纯图标文本内容
            return false;
        }

        /// <summary>该控件内部是否有被我们隐藏掉的图标文本。</summary>
        private static bool HasCollapsedIcon(DependencyObject control)
        {
            foreach (TextBlock block in CollapsedByUs)
            {
                DependencyObject node = block;
                for (int depth = 0; depth < 12 && node != null; depth++)
                {
                    if (ReferenceEquals(node, control)) return true;
                    node = node is Visual
                        ? VisualTreeHelper.GetParent(node)
                        : null;
                }
            }
            return false;
        }

        private static bool HasVisibleText(DependencyObject root)
        {
            if (root is TextBlock self)
            {
                if (self.Visibility == Visibility.Visible && TextHasVisibleContent(self)) return true;
            }
            else if (root is FrameworkElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (root is Image || root is System.Windows.Shapes.Shape || root is MediaElement) return true;

            int count = 0;
            try { count = VisualTreeHelper.GetChildrenCount(root); } catch { }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(root, i); } catch { }
                if (child != null && HasVisibleText(child)) return true;
            }
            return false;
        }

        private static void ApplyTextFallback(ContentControl control)
        {
            try
            {
                // 优先用自动化名称（通常更短，如「重置」「上一页」），没有再用提示文字
                string label = System.Windows.Automation.AutomationProperties.GetName(control);
                if (string.IsNullOrWhiteSpace(label)) label = control.ToolTip as string;
                if (string.IsNullOrWhiteSpace(label)) return;

                control.Content = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                if (control is FrameworkElement element)
                {
                    if (!double.IsNaN(element.Width)) element.Width = double.NaN;
                    if (element.MinWidth > 0 && element.MinWidth < 40) element.MinWidth = 0;
                }
            }
            catch { }
        }

        /// <summary>遍历元素树：处理所有图标文本，处理纯图标按钮。</summary>
        public static void NormalizeTree(DependencyObject root)
        {
            if (!CompatActive || root == null) return;
            try { NormalizeTreeCore(root, 0); }
            catch (Exception ex) { Debug("tree-walk failed: " + ex.Message); }
        }

        // ================================================================
        // 定时兜底：界面可能先创建、后显示，Loaded 未必赶在图标出现之后
        // ================================================================

        private static Window _sweepWindow;
        private static DispatcherTimer _autoSweepTimer;

        /// <summary>主窗口加载后启动低频兜底扫描，确保任何后出现的图标都会被处理。</summary>
        public static void StartAutoSweep(Window window)
        {
            if (!CompatActive || window == null) return;
            _sweepWindow = window;
            if (_autoSweepTimer != null) return;

            _autoSweepTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _autoSweepTimer.Tick += (s, e) =>
            {
                var target = _sweepWindow;
                if (target == null || !target.IsLoaded)
                {
                    _autoSweepTimer.Stop();
                    return;
                }
                NormalizeTree(target);
            };
            _autoSweepTimer.Start();
        }

        /// <summary>页面切换/弹层打开后延迟补扫，避开动画与延迟创建的控件。</summary>
        public static void RequestSweep(DependencyObject context = null)
        {
            if (!CompatActive) return;

            DependencyObject target = context;
            if (target == null)
            {
                if (_sweepWindow == null) return;
                target = _sweepWindow;
            }

            foreach (int delay in new[] { 120, 450, 1000 })
            {
                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(delay)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var current = target;
                    if (current is Window w && !w.IsVisible) current = _sweepWindow;
                    if (current != null) NormalizeTree(current);
                };
                timer.Start();
            }
        }

        private static void NormalizeTreeCore(DependencyObject node, int depth)
        {
            if (node == null || depth > 192) return;

            // 记录「处理前内容是否只有图标」，子元素处理完后再据此决定要不要补文字
            bool iconOnlyContent = node is ContentControl preContent && IsIconOnlyContent(preContent.Content);

            // 图标字符串内容（Button.Content 等）先处理，避免模板再生成图标
            if (node is ContentControl contentControl && contentControl.Content is string)
                NormalizeContentControl(contentControl);

            if (node is TextBlock textBlock) Normalize(textBlock);
            else if (node is AccessText accessText) Normalize(accessText);

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); } catch { count = 0; }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(node, i); } catch { child = null; }
                if (child != null) NormalizeTreeCore(child, depth + 1);
            }

            // 子元素处理完后再判断「纯图标控件是否已经空白」
            if (node is ContentControl control)
                NormalizeContentControl(control, iconOnlyContent);

            // 逻辑子元素（弹层、菜单等不在视觉树里的内容）
            try
            {
                foreach (object logical in LogicalTreeHelper.GetChildren(node))
                {
                    if (logical is DependencyObject child && !(child is Visual)
                        && !(child is System.Windows.Media.Media3D.Visual3D))
                        NormalizeTreeCore(child, depth + 1);
                }
            }
            catch { }
        }

        // ================================================================
        // 运行期文本变化的兜底监听
        // ================================================================

        private static void Watch(TextBlock textBlock)
        {
            if (textBlock == null) return;
            if (!Watched.Add(textBlock)) return;

            try
            {
                TextBlockTextDescriptor.AddValueChanged(textBlock, TextBlockTextChanged);
                textBlock.Unloaded += WatchedElementUnloaded;
            }
            catch { }
        }

        private static readonly HashSet<TextBlock> VisibilityWatched = new HashSet<TextBlock>();

        /// <summary>
        /// 页面/面板先折叠、切换后才显示时，图标文本可能已经错过了 Loaded；
        /// 这里在元素真正可见时再处理一次。
        /// </summary>
        private static void WatchVisibility(TextBlock textBlock)
        {
            if (textBlock == null) return;
            if (!VisibilityWatched.Add(textBlock)) return;

            try { textBlock.IsVisibleChanged += IconTextBlockIsVisibleChanged; }
            catch { }
        }

        private static void IconTextBlockIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool visible && visible && sender is TextBlock textBlock)
                Normalize(textBlock);
        }

        private static void StopWatching(TextBlock textBlock)
        {
            if (textBlock == null) return;
            if (!Watched.Remove(textBlock)) return;

            try
            {
                TextBlockTextDescriptor.RemoveValueChanged(textBlock, TextBlockTextChanged);
                textBlock.Unloaded -= WatchedElementUnloaded;
                textBlock.IsVisibleChanged -= IconTextBlockIsVisibleChanged;
            }
            catch { }
            CollapsedByUs.Remove(textBlock);
            VisibilityWatched.Remove(textBlock);
        }

        private static void WatchedElementUnloaded(object sender, RoutedEventArgs e)
        {
            StopWatching(sender as TextBlock);
        }

        private static void TextBlockTextChanged(object sender, EventArgs e)
        {
            if (sender is TextBlock textBlock) Normalize(textBlock);
        }
    }
}
