using System;

namespace StrmAssistant.Provider.Douban
{
    public enum DoubanRatingKeyType
    {
        Imdb,
        Tmdb,
        Douban
    }

    public sealed class DoubanRatingKey
    {
        public DoubanRatingKeyType Type { get; set; }

        /// <summary>规范化后的值：IMDb 为小写 tt...；TMDb / 豆瓣为纯数字。</summary>
        public string Value { get; set; }

        /// <summary>调用方原始输入（去除首尾空白）。</summary>
        public string Raw { get; set; }

        public string CanonicalKey => Type == DoubanRatingKeyType.Imdb ? Value : $"{TypeName}:{Value}";

        public string IndexKey => $"{TypeName}:{Value}";

        public string TypeName => Type switch
        {
            DoubanRatingKeyType.Imdb => "imdb",
            DoubanRatingKeyType.Tmdb => "tmdb",
            DoubanRatingKeyType.Douban => "douban",
            _ => "unknown"
        };
    }

    /// <summary>
    /// 评分查询键解析：兼容原有 IMDb（tt...），新增 tmdb:123 / douban:123。
    /// </summary>
    public static class DoubanRatingKeyParser
    {
        public static bool TryParse(string raw, out DoubanRatingKey key)
        {
            key = null;

            if (string.IsNullOrWhiteSpace(raw)) return false;

            var text = raw.Trim();

            if (TryStripPrefix(text, "imdb:", out var imdbValue) ||
                text.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                var value = imdbValue ?? text;
                if (!value.StartsWith("tt", StringComparison.OrdinalIgnoreCase) || value.Length <= 2 ||
                    !IsAsciiDigits(value, 2))
                {
                    return false;
                }

                key = new DoubanRatingKey
                {
                    Type = DoubanRatingKeyType.Imdb,
                    Value = value.ToLowerInvariant(),
                    Raw = text
                };
                return true;
            }

            if (TryStripPrefix(text, "tmdb:", out var tmdbValue) && IsAsciiDigits(tmdbValue))
            {
                key = new DoubanRatingKey
                {
                    Type = DoubanRatingKeyType.Tmdb,
                    Value = tmdbValue,
                    Raw = text
                };
                return true;
            }

            if (TryStripPrefix(text, "douban:", out var doubanValue) && IsAsciiDigits(doubanValue))
            {
                key = new DoubanRatingKey
                {
                    Type = DoubanRatingKeyType.Douban,
                    Value = doubanValue,
                    Raw = text
                };
                return true;
            }

            return false;
        }

        private static bool TryStripPrefix(string text, string prefix, out string value)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = text.Substring(prefix.Length);
                return true;
            }

            value = null;
            return false;
        }

        private static bool IsAsciiDigits(string value, int start = 0)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= start) return false;

            for (var i = start; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }

            return true;
        }
    }
}
