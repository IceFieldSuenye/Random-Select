using System.Collections.Generic;

namespace RandomDrawer.Messaging
{
    public enum DrawResultDisplayMode
    {
        Text,
        List,
        CardGrid
    }

    public static class AppMessages
    {
        /// <summary>打开卡池封面编辑页。</summary>
        public sealed class OpenCoverEditor
        {
            public string PoolDir { get; }
            public string CoverPath { get; }

            public OpenCoverEditor(string poolDir, string coverPath)
            {
                PoolDir = poolDir ?? "";
                CoverPath = coverPath ?? "";
            }
        }

        /// <summary>封面编辑页结束：CoverPath 为新的封面文件（空表示取消），Saved 表示是否要应用。</summary>
        public sealed class CoverEditorFinished
        {
            public string CoverPath { get; }
            public bool Saved { get; }

            /// <summary>是否「模糊并应用到主界面背景」。</summary>
            public bool BlurBackground { get; }

            public CoverEditorFinished(string coverPath, bool saved, bool blurBackground = false)
            {
                CoverPath = coverPath ?? "";
                Saved = saved;
                BlurBackground = blurBackground;
            }
        }
        public sealed class NavigateTo
        {
            public string PageKey { get; }
            public NavigateTo(string pageKey) { PageKey = pageKey ?? ""; }
        }

        public sealed class NavigateBack { }

        public sealed class PageActivated
        {
            public string PageKey { get; }
            public PageActivated(string pageKey) { PageKey = pageKey ?? ""; }
        }

        public sealed class PoolListChanged { }

        public sealed class CurrentPoolChanged
        {
            public string PoolDir { get; }
            public CurrentPoolChanged(string poolDir) { PoolDir = poolDir ?? ""; }
        }

        public sealed class MusicSettingsChanged { }
        public sealed class HistorySettingsChanged { }
        public sealed class UiOptionsChanged { }

        public sealed class OpenPoolEditor
        {
            public string PoolDir { get; }
            /// <summary>是否从主界面右键菜单进入（只有主界面使用「从下往上淡入」动画）。</summary>
            public bool FromMainPage { get; }
            /// <summary>调用方是否已经验证过该卡池的密码（加密卡池必须为 true，否则编辑页会自己再要一次密码）。</summary>
            public bool PasswordVerified { get; }
            public OpenPoolEditor(string poolDir, bool fromMainPage = true, bool passwordVerified = false)
            {
                PoolDir = poolDir ?? "";
                FromMainPage = fromMainPage;
                PasswordVerified = passwordVerified;
            }
        }

        public sealed class OpenAdvancedGacha
        {
            public string PoolDir { get; }
            public Dictionary<string, List<string>> CategoryMap { get; }
            public GachaSettings InitialSettings { get; }
            public bool HasCustomColors { get; }

            public OpenAdvancedGacha(string poolDir, Dictionary<string, List<string>> map, GachaSettings initial, bool hasCustomColors = false)
            {
                PoolDir = poolDir ?? "";
                CategoryMap = map ?? new Dictionary<string, List<string>>();
                InitialSettings = initial;
                HasCustomColors = hasCustomColors;
            }
        }

        public sealed class AdvancedGachaFinished
        {
            public string PoolDir { get; }
            public bool Saved { get; }
            public GachaSettings Result { get; }

            public AdvancedGachaFinished(string poolDir, bool saved, GachaSettings result)
            {
                PoolDir = poolDir ?? "";
                Saved = saved;
                Result = result;
            }
        }

        public sealed class OpenMultiSelect
        {
            public List<string> AllItems { get; }
            public List<string> PreSelected { get; }
            public string Title { get; }
            public int MaxSelection { get; }

            public OpenMultiSelect(List<string> allItems, List<string> preSelected, string title, int maxSelection)
            {
                AllItems = allItems ?? new List<string>();
                PreSelected = preSelected ?? new List<string>();
                Title = title ?? "";
                MaxSelection = maxSelection;
            }
        }

        public sealed class MultiSelectFinished
        {
            public List<string> SelectedItems { get; }
            public bool Ok { get; }

            public MultiSelectFinished(List<string> selected, bool ok)
            {
                SelectedItems = selected ?? new List<string>();
                Ok = ok;
            }
        }

        public sealed class OpenCelebrationUpSelector
        {
            public List<string> AllItems { get; }
            public string CurrentMainUp { get; }
            public List<string> CurrentSubUps { get; }

            public OpenCelebrationUpSelector(List<string> allItems, string currentMainUp, List<string> currentSubUps)
            {
                AllItems = allItems ?? new List<string>();
                CurrentMainUp = currentMainUp ?? "";
                CurrentSubUps = currentSubUps ?? new List<string>();
            }
        }

        public sealed class CelebrationUpFinished
        {
            public bool Ok { get; }
            public string MainUp { get; }
            public List<string> SubUps { get; }

            public CelebrationUpFinished(bool ok, string mainUp, List<string> subUps)
            {
                Ok = ok;
                MainUp = mainUp ?? "";
                SubUps = subUps ?? new List<string>();
            }
        }

        public sealed class OpenPersonalization
        {
            public List<string> ObjectNames { get; }
            public List<string> OtherColumnHeaders { get; }
            public Dictionary<string, Dictionary<string, string>> OtherValues { get; }
            public PersonalizationSettings InitialSettings { get; }
            public string PoolDir { get; }

            public OpenPersonalization(
                List<string> objectNames,
                List<string> otherColumnHeaders,
                Dictionary<string, Dictionary<string, string>> otherValues,
                PersonalizationSettings initialSettings,
                string poolDir)
            {
                ObjectNames = objectNames ?? new List<string>();
                OtherColumnHeaders = otherColumnHeaders ?? new List<string>();
                OtherValues = otherValues ?? new Dictionary<string, Dictionary<string, string>>();
                InitialSettings = initialSettings;
                PoolDir = poolDir ?? "";
            }
        }

        public sealed class PersonalizationFinished
        {
            public bool Ok { get; }
            public PersonalizationSettings Settings { get; }

            public PersonalizationFinished(bool ok, PersonalizationSettings settings)
            {
                Ok = ok;
                Settings = settings;
            }
        }

        public sealed class OpenDrawAnimation
        {
            public List<DrawResultData> Items { get; }
            public string PoolDir { get; }
            public int Count { get; }
            public PersonalizationSettings Settings { get; }
            public HighlightConfig Highlight { get; }
            public DrawResultDisplayMode DisplayMode { get; }
            public bool IsSingleDraw { get; }

            public OpenDrawAnimation(
                List<DrawResultData> items, string poolDir, int count,
                PersonalizationSettings settings, HighlightConfig highlight,
                DrawResultDisplayMode displayMode, bool isSingleDraw)
            {
                Items = items ?? new List<DrawResultData>();
                PoolDir = poolDir ?? "";
                Count = count;
                Settings = settings;
                Highlight = highlight;
                DisplayMode = displayMode;
                IsSingleDraw = isSingleDraw;
            }
        }

        public sealed class OpenDrawResultPage
        {
            public List<DrawResultData> Items { get; }
            public string PoolDir { get; }
            public int LastDrawCount { get; }
            public PersonalizationSettings Settings { get; }
            public HighlightConfig Highlight { get; }
            public DrawResultDisplayMode DisplayMode { get; }

            public OpenDrawResultPage(
                List<DrawResultData> items,
                string poolDir,
                int lastDrawCount,
                PersonalizationSettings settings,
                HighlightConfig highlight,
                DrawResultDisplayMode displayMode = DrawResultDisplayMode.CardGrid)
            {
                Items = items ?? new List<DrawResultData>();
                PoolDir = poolDir ?? "";
                LastDrawCount = lastDrawCount;
                Settings = settings;
                Highlight = highlight;
                DisplayMode = displayMode;
            }
        }

        public sealed class DrawResultPageContinueDraw
        {
            public int DrawCount { get; }
            public DrawResultPageContinueDraw(int count) { DrawCount = count; }
        }

        public sealed class DrawResultPageClosed { }
    }

    public class DrawResultData
    {
        public int Index { get; set; }
        public string ObjectName { get; set; } = "";
        public string ObjectHeader { get; set; } = "抽取对象";
        public string ColorHex { get; set; } = "";
        public List<string> OtherFields { get; set; } = new List<string>();
        public List<string> OtherHeaders { get; set; } = new List<string>();
        public string Category { get; set; } = "";
        public string AvatarPath { get; set; } = "";
        public string IllustrationPath { get; set; } = "";
    }
}