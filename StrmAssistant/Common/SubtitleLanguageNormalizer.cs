using System;
using System.Collections.Generic;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 一次语言标签规范化的结果。调用方按 <see cref="HasChange"/> 决定是否落库。
    /// </summary>
    public sealed class TrackLanguageNormalization
    {
        /// <summary>规范化后的语言码（Emby 可识别的 ISO 639-2/B，如 chi / eng / jpn）。</summary>
        public string Language { get; set; }

        /// <summary>规范化后的轨道标题（简体中文 / 繁体中文 / 中英双语…）。</summary>
        public string Title { get; set; }

        /// <summary>文件名含 forced 标记时给出 true；无法判定时为 null（表示不改）。</summary>
        public bool? IsForced { get; set; }

        /// <summary>文件名含 sdh / 听障 标记时给出 true；无法判定时为 null（表示不改）。</summary>
        public bool? IsHearingImpaired { get; set; }

        /// <summary>命中的语言标签文案，用于日志与统计；null 表示没判定出来。</summary>
        public string DetectedLabel { get; set; }

        /// <summary>与传入的当前值相比是否有实际变化。</summary>
        public bool HasChange { get; set; }
    }

    /// <summary>
    /// 外挂音轨 / 字幕的语言标签规范化。纯函数，无 Emby 依赖，便于单测。
    ///
    /// 为什么需要：
    /// 1. Emby 的选轨逻辑（MediaStreamSelector.GetDefaultAudioStreamIndex / GetDefaultSubtitleStream）
    ///    依赖 MediaStream.Language 做语言匹配；外挂字幕的语言由 Emby 的 SubtitleResolver
    ///    按文件名推断，中文命名常落成 und，导致用户设了「简体中文优先」也选不中。
    /// 2. Emby 的 CultureDto.Equals 只比较 TwoLetterISOLanguageName，
    ///    简繁（chi / zh-CN / zh-TW）在选轨层面完全等价——所以这里**不承诺按简繁自动选轨**，
    ///    只把简繁写进 Title 供用户在选择菜单里辨认。
    ///
    /// 判定原则：只认文件名末尾若干段里的明确标记；判定不出来一律「不改」，
    /// 避免把猜错的标签盖到 ffprobe 给出的准确值上。
    /// </summary>
    public static class SubtitleLanguageNormalizer
    {
        /// <summary>从文件名末尾往前最多看几段。语言标记通常在最后，限制扫描避免片名误判。</summary>
        private const int MaxScanSegments = 3;

        private static readonly Dictionary<string, (string Language, string Title)> LanguageTokens =
            new Dictionary<string, (string Language, string Title)>(StringComparer.OrdinalIgnoreCase)
            {
                // 简体
                { "chs", ("chi", "简体中文") },
                { "sc", ("chi", "简体中文") },
                { "zhs", ("chi", "简体中文") },
                { "zh-cn", ("chi", "简体中文") },
                { "zh-sg", ("chi", "简体中文") },
                { "zh-hans", ("chi", "简体中文") },
                { "gb", ("chi", "简体中文") },
                { "gbk", ("chi", "简体中文") },
                { "简", ("chi", "简体中文") },
                { "简体", ("chi", "简体中文") },
                { "简中", ("chi", "简体中文") },
                { "简体中文", ("chi", "简体中文") },
                // 繁体
                { "cht", ("chi", "繁体中文") },
                { "tc", ("chi", "繁体中文") },
                { "zht", ("chi", "繁体中文") },
                { "zh-tw", ("chi", "繁体中文") },
                { "zh-hk", ("chi", "繁体中文") },
                { "zh-hant", ("chi", "繁体中文") },
                { "big5", ("chi", "繁体中文") },
                { "繁", ("chi", "繁体中文") },
                { "繁体", ("chi", "繁体中文") },
                { "繁中", ("chi", "繁体中文") },
                { "繁體", ("chi", "繁体中文") },
                { "繁体中文", ("chi", "繁体中文") },
                { "繁體中文", ("chi", "繁体中文") },
                // 中文（不区分简繁）
                { "chi", ("chi", "中文") },
                { "zho", ("chi", "中文") },
                { "zh", ("chi", "中文") },
                { "chinese", ("chi", "中文") },
                { "中文", ("chi", "中文") },
                // 中英双语
                { "chs&eng", ("chi", "中英双语") },
                { "chi&eng", ("chi", "中英双语") },
                { "zh-en", ("chi", "中英双语") },
                { "zh&en", ("chi", "中英双语") },
                { "双语", ("chi", "中英双语") },
                { "中英", ("chi", "中英双语") },
                { "bilingual", ("chi", "中英双语") },
                { "dual", ("chi", "中英双语") },
                // 英文
                { "eng", ("eng", "English") },
                { "en", ("eng", "English") },
                { "english", ("eng", "English") },
                { "英文", ("eng", "English") },
                // 日文
                { "jpn", ("jpn", "日本語") },
                { "jp", ("jpn", "日本語") },
                { "ja", ("jpn", "日本語") },
                { "japanese", ("jpn", "日本語") },
                { "日文", ("jpn", "日本語") },
                { "日语", ("jpn", "日本語") },
                // 韩文
                { "kor", ("kor", "한국어") },
                { "ko", ("kor", "한국어") },
                { "kr", ("kor", "한국어") },
                { "korean", ("kor", "한국어") },
                { "韩文", ("kor", "한국어") },
                { "韩语", ("kor", "한국어") }
            };

        private static readonly HashSet<string> ForcedTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "forced", "forc", "强制", "字幕强制" };

        private static readonly HashSet<string> HearingImpairedTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sdh", "hi", "cc", "听障", "聽障" };

        /// <summary>
        /// 按文件名（或单独的语言标记段）推断语言标签。
        /// </summary>
        /// <param name="filePathOrName">外挂轨道文件名或完整路径；也可直接传 "chs" 这类语言段。</param>
        /// <param name="currentLanguage">当前 Language 值，用于比较是否需要改动。</param>
        /// <param name="currentTitle">当前 Title 值；已含中文时不覆盖，保留人工/本地化结果。</param>
        public static TrackLanguageNormalization Normalize(string filePathOrName, string currentLanguage,
            string currentTitle, bool currentIsForced = false, bool currentIsHearingImpaired = false)
        {
            var result = new TrackLanguageNormalization
            {
                Language = currentLanguage,
                Title = currentTitle,
                HasChange = false
            };

            if (string.IsNullOrWhiteSpace(filePathOrName)) return result;

            var segments = SplitSegments(GetStem(filePathOrName));
            if (segments.Count == 0) return result;

            var firstIndex = Math.Max(0, segments.Count - MaxScanSegments);
            var languageMatched = false;

            for (var i = segments.Count - 1; i >= firstIndex; i--)
            {
                var token = segments[i];
                if (token.Length == 0) continue;

                // 标记位在整个扫描窗口内都要收集：xxx.forced.chs 与 xxx.chs.forced 等价
                if (ForcedTokens.Contains(token))
                {
                    result.IsForced = true;
                    continue;
                }

                if (HearingImpairedTokens.Contains(token))
                {
                    result.IsHearingImpaired = true;
                    continue;
                }

                // 语言只取最靠近文件名末尾的那一个，避免片名里的语言词覆盖真实标记
                if (languageMatched) continue;

                if (LanguageTokens.TryGetValue(token, out var info))
                {
                    ApplyToken(result, info);
                    languageMatched = true;
                    continue;
                }

                // zh-CN / zh-Hant 已在表内整段命中；这里兜底 "xx-yy" 形式的变体
                if (token.IndexOf('-') > 0)
                {
                    var matched = false;
                    foreach (var sub in token.Split('-'))
                    {
                        if (!LanguageTokens.TryGetValue(sub, out var subInfo)) continue;
                        ApplyToken(result, subInfo);
                        matched = true;
                        break;
                    }

                    if (matched) languageMatched = true;
                }
            }

            if (result.DetectedLabel == null) return result;

            // Title 已有中文说明已被本地化或人工改写，保留原值，只修 Language
            if (string.IsNullOrWhiteSpace(currentTitle) || !LanguageUtility.IsChinese(currentTitle))
            {
                result.Title = result.DetectedLabel;
            }

            result.HasChange =
                !string.Equals(result.Language ?? string.Empty, currentLanguage ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.Title ?? string.Empty, currentTitle ?? string.Empty,
                    StringComparison.Ordinal) ||
                (result.IsForced.HasValue && result.IsForced.Value != currentIsForced) ||
                (result.IsHearingImpaired.HasValue && result.IsHearingImpaired.Value != currentIsHearingImpaired);

            return result;
        }

        private static void ApplyToken(TrackLanguageNormalization result, (string Language, string Title) info)
        {
            result.Language = info.Language;
            result.DetectedLabel = info.Title;
        }

        private static string GetStem(string filePathOrName)
        {
            var name = filePathOrName;

            var separator = name.LastIndexOfAny(new[] { '/', '\\' });
            if (separator >= 0 && separator < name.Length - 1) name = name.Substring(separator + 1);

            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);

            return name.Trim();
        }

        /// <summary>
        /// 按点、下划线、空格、括号切段；保留 "&amp;" 与 "-"，因为 chs&amp;eng、zh-CN 是整体标记。
        /// </summary>
        private static List<string> SplitSegments(string stem)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(stem)) return result;

            foreach (var part in stem.Split(new[] { '.', '_', ' ', '(', ')', '[', ']' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim().TrimEnd('!').ToLowerInvariant();
                if (token.Length > 0) result.Add(token);
            }

            return result;
        }
    }
}
