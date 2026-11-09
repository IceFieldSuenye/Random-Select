using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RandomDrawer
{
    public enum SoundMode { None = 0, Global = 1, PerItem = 2 }
    public enum AnimationMode { None = 0, Partial = 1, Full = 2 }
    public enum BackgroundMode { Color = 0, Image = 1, Default = 2 }

    /// <summary>单个抽取对象的个性化数据。</summary>
    public class PersonalizationItem
    {
        public string ObjectName { get; set; } = "";

        /// <summary>自定义颜色（HEX 形式，如 "#FF5722"）；为空表示不设置</summary>
        public string ColorHex { get; set; } = "";

        public string AvatarFile { get; set; } = "";
        public string IllustrationFile { get; set; } = "";
        public string SoundFile { get; set; } = "";
    }

    /// <summary>整个卡池的个性化抽取设置。</summary>
    public class PersonalizationSettings
    {
        public SoundMode SoundMode { get; set; } = SoundMode.None;
        public string GlobalSoundFile { get; set; } = "";

        public AnimationMode AnimationMode { get; set; } = AnimationMode.Full;

        public BackgroundMode BackgroundMode { get; set; } = BackgroundMode.Default;
        public string BackgroundColor { get; set; } = "#F0F0F0";
        public string BackgroundImage { get; set; } = "";

        /// <summary>背景图模糊半径（0~100）。仅「背景 = 图片」时生效。</summary>
        public double BackgroundBlur { get; set; } = 0;

        /// <summary>模糊是否应用到抽取结果页。</summary>
        public bool BlurOnResult { get; set; } = true;

        /// <summary>模糊是否应用到抽取动画。</summary>
        public bool BlurOnAnimation { get; set; } = true;

        /// <summary>模糊是否应用到加载界面（正在生成抽取结果）。</summary>
        public bool BlurOnLoading { get; set; } = true;

        public List<PersonalizationItem> Items { get; set; } = new List<PersonalizationItem>();

        /// <summary>
        /// 实际生效的背景模式：
        ///   · 图片存在 → 图片；纯色且不是旧的默认灰 → 纯色；其余 → 默认（跟随主题）。
        /// </summary>
        [JsonIgnore]
        public BackgroundMode EffectiveBackgroundMode
        {
            get
            {
                if (BackgroundMode == BackgroundMode.Image && !string.IsNullOrEmpty(BackgroundImage))
                    return BackgroundMode.Image;
                if (BackgroundMode == BackgroundMode.Color)
                    return BackgroundMode.Color;
                return BackgroundMode.Default;
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static PersonalizationSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new PersonalizationSettings();
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<PersonalizationSettings>(json, JsonOpts)
                             ?? new PersonalizationSettings();

                // 旧版本的默认值是「纯色 + #F0F0F0」，这里迁移为「默认（跟随主题）」
                if (loaded.BackgroundMode == BackgroundMode.Color
                    && string.Equals(loaded.BackgroundColor, "#F0F0F0", StringComparison.OrdinalIgnoreCase))
                {
                    loaded.BackgroundMode = BackgroundMode.Default;
                }
                return loaded;
            }
            catch { return new PersonalizationSettings(); }
        }

        public static void Save(PersonalizationSettings s, string path)
        {
            try
            {
                string json = JsonSerializer.Serialize(s, JsonOpts);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public PersonalizationSettings Clone()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, JsonOpts);
                var loaded = JsonSerializer.Deserialize<PersonalizationSettings>(json, JsonOpts)
                             ?? new PersonalizationSettings();

                // 旧版本的默认值是「纯色 + #F0F0F0」，这里迁移为「默认（跟随主题）」
                if (loaded.BackgroundMode == BackgroundMode.Color
                    && string.Equals(loaded.BackgroundColor, "#F0F0F0", StringComparison.OrdinalIgnoreCase))
                {
                    loaded.BackgroundMode = BackgroundMode.Default;
                }
                return loaded;
            }
            catch { return new PersonalizationSettings(); }
        }
    }
}
