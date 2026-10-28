using Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TinyPinyin;

namespace StrmAssistant.Common
{
    public static class LanguageUtility
    {
        private static readonly Regex EnglishRegex = new Regex(@"^[\x00-\x7F]+$", RegexOptions.Compiled);
        private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);
        private static readonly Regex JapaneseRegex = new Regex(@"[\u3040-\u30FF]", RegexOptions.Compiled);
        private static readonly Regex KoreanRegex = new Regex(@"[\uAC00-\uD7A3]", RegexOptions.Compiled);
        private static readonly Regex DefaultEnglishEpisodeNameRegex = new Regex(@"^Episode\s*\d+$", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseEpisodeNameRegex = new Regex(@"^第\s*\d+\s*集$", RegexOptions.Compiled);
        private static readonly Regex DefaultJapaneseEpisodeNameRegex = new Regex(@"^第\s*\d+\s*話$", RegexOptions.Compiled);
        private static readonly Regex DefaultEnglishSeasonNameRegex = new Regex(@"^(Season|Series)\s*\d+$", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseSeasonNameRegex = new Regex(@"^第\s*\d+\s*季$", RegexOptions.Compiled);
        private static readonly Regex DefaultJapaneseSeasonNameRegex = new Regex(@"^(シーズン|第)\s*\d+\s*(季|シーズン)?$", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseCollectionNameRegex = new Regex(@"（系列）$", RegexOptions.Compiled);
        private static readonly Regex CleanPersonNameRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex CleanEpisodeNameRegex =
            new Regex(@"(\.)(?:S[0-9]+[eE][0-9]+|[sS][0-9]+[xX][0-9]+|[sS][0-9]+[-_][0-9]+|第[0-9一二三四五六七八九十百]+集)?",
                RegexOptions.Compiled);

        public static readonly string[] MovieDbFallbackLanguages = { "zh-CN", "zh-SG", "zh-HK", "zh-TW", "ja-JP" };
        public static readonly string[] TvdbFallbackLanguages = { "zho", "zhtw", "yue", "jpn" };

        public static bool IsEnglish(string input) => !string.IsNullOrEmpty(input) && EnglishRegex.IsMatch(input);

        public static bool IsChinese(string input) => !string.IsNullOrEmpty(input) && ChineseRegex.IsMatch(input) &&
                                                      !JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));

        public static bool IsJapanese(string input) => !string.IsNullOrEmpty(input) &&
                                                       JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));

        public static bool IsChineseJapanese(string input) => !string.IsNullOrEmpty(input) &&
                                                              (ChineseRegex.IsMatch(input) ||
                                                               JapaneseRegex.IsMatch(input.Replace("\u30FB",
                                                                   string.Empty)));

        public static bool IsKorean(string input) => !string.IsNullOrEmpty(input) && KoreanRegex.IsMatch(input);
        
        public static bool IsDefaultEnglishEpisodeName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultEnglishEpisodeNameRegex.IsMatch(input);

        public static bool IsDefaultChineseEpisodeName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultChineseEpisodeNameRegex.IsMatch(input);

        public static bool IsDefaultJapaneseEpisodeName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultJapaneseEpisodeNameRegex.IsMatch(input);

        public static bool IsDefaultEnglishSeasonName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultEnglishSeasonNameRegex.IsMatch(input);

        public static bool IsDefaultChineseSeasonName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultChineseSeasonNameRegex.IsMatch(input);

        public static bool IsDefaultJapaneseSeasonName(string input) =>
            !string.IsNullOrEmpty(input) && DefaultJapaneseSeasonNameRegex.IsMatch(input);

        /// <summary>
        /// 判断是否为默认季名（未刮削出真实季名）：如 "Season 1" / "第 1 季" / "シーズン1"。
        /// 三种语言的任一正则命中即视为默认季名。
        /// </summary>
        public static bool IsDefaultSeasonName(string input) =>
            IsDefaultEnglishSeasonName(input) || IsDefaultChineseSeasonName(input) || IsDefaultJapaneseSeasonName(input);

        public static string ConvertTraditionalToSimplified(string input)
        {
            return ChineseConverter.Convert(input, ChineseConversionDirection.TraditionalToSimplified);
        }

        /// <summary>
        /// 中文搜索变体归一化：
        /// 在全角/半角、繁简之外，额外处理常见空格与标点变体，使搜索词的字符变体互通。
        /// 纯字符级归一化，无外部词表依赖。
        /// </summary>
        public static string NormalizeChineseVariants(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var sb = new System.Text.StringBuilder(input.Length);
            foreach (var ch in input)
            {
                // 全角 ASCII（U+FF01–U+FF5E）→ 半角
                if (ch >= '\uFF01' && ch <= '\uFF5E')
                {
                    sb.Append((char)(ch - 0xFEE0));
                }
                // 全角空格 → 半角空格
                else if (ch == '\u3000')
                {
                    sb.Append(' ');
                }
                // 常见异体/兼容字符折叠（中日韩兼容与易混标点）
                else
                {
                    switch (ch)
                    {
                        case '\u30FB': // 片假名中点 ·
                        case '\u00B7': // 中点 ·
                            break;      // 直接丢弃，交由拼音/分词匹配
                        case '\uFF0D': // 全角连字符
                        case '\u2010':
                        case '\u2011':
                        case '\u2012':
                        case '\u2013':
                        case '\u2014':
                            sb.Append('-');
                            break;
                        default:
                            sb.Append(ch);
                            break;
                    }
                }
            }

            var normalized = sb.ToString().Trim();
            return IsChinese(normalized) ? ConvertTraditionalToSimplified(normalized) : normalized;
        }

        public static string ConvertSimplifiedToTraditional(string input)
        {
            return ChineseConverter.Convert(input, ChineseConversionDirection.SimplifiedToTraditional);
        }

        public static string GetLanguageByTitle(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;

            return IsJapanese(input) ? "ja" : IsKorean(input) ? "ko" : IsChinese(input) ? "zh" : "en";
        }

        public static string ConvertToPinyinInitials(string input)
        {
            return PinyinHelper.GetPinyinInitials(input);
        }

        public static string RemoveDefaultCollectionName(string input)
        {
            return string.IsNullOrEmpty(input) ? input : DefaultChineseCollectionNameRegex.Replace(input, "").Trim();
        }

        public static string CleanPersonName(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            if (IsChineseJapanese(input) || IsKorean(input))
            {
                return CleanPersonNameRegex.Replace(input, " ").Trim();
            }

            return input.Trim();
        }

        public static string CleanEpisodeName(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            return CleanEpisodeNameRegex.Replace(input, "");
        }

        public static List<string> GetMovieDbFallbackLanguages()
        {
            var currentFallbackLanguages = Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMetadata.FallbackLanguages;

            if (string.IsNullOrWhiteSpace(currentFallbackLanguages)) return new List<string>();

            var languages = currentFallbackLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries);

            return MovieDbFallbackLanguages
                .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
                .Select(l => l.ToLowerInvariant())
                .ToList();
        }

        public static bool HasMovieDbJapaneseFallback()
        {
            var currentFallbackLanguages = Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMetadata.FallbackLanguages;

            return !string.IsNullOrWhiteSpace(currentFallbackLanguages) &&
                   currentFallbackLanguages.IndexOf("ja-jp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool BlockMovieDbNonFallbackLanguage(string input)
        {
            return !string.IsNullOrEmpty(input) &&
                   Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMetadata.BlockNonFallbackLanguage &&
                   (!HasMovieDbJapaneseFallback() || !IsJapanese(input));
        }

        public static List<string> GetTvdbFallbackLanguages()
        {
            var currentFallbackLanguages = Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMetadata.TvdbFallbackLanguages;

            if (string.IsNullOrWhiteSpace(currentFallbackLanguages)) return new List<string>();

            var languages = currentFallbackLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries);

            return TvdbFallbackLanguages
                .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        public static bool HasTvdbJapaneseFallback()
        {
            var currentFallbackLanguages = Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMetadata.TvdbFallbackLanguages;

            return !string.IsNullOrWhiteSpace(currentFallbackLanguages) &&
                   currentFallbackLanguages.IndexOf("jpn", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool BlockTvdbNonFallbackLanguage(string input)
        {
            return !string.IsNullOrEmpty(input) &&
                   Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMetadata.BlockNonFallbackLanguage &&
                   (!HasTvdbJapaneseFallback() || !IsJapanese(input));
        }
    }
}
