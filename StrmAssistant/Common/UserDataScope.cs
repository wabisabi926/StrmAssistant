using System;
using System.Globalization;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 「各媒体库内记录播放进度」的 key 前缀规则（纯函数，便于单测）。
    ///
    /// Emby 的 UserData（播放进度 + 收藏）按 <c>BaseItem.GetUserDataKey</c> 存储，
    /// 默认只按 tmdb/imdb/tvdb 或条目 Id 计算，因此同一内容放在两个媒体库会共用一条记录，
    /// 「继续观看」里就出现重复项。给 key 加「库 InternalId |」前缀后，各库各记一份。
    ///
    /// 格式与 PRO 一致（前缀式）：<c>{libraryId}|{originalKey}</c>。
    /// 幂等：结果已带本库前缀时原样返回 —— Episode/Season 的 key 由 series key 拼接而来，
    /// 会在一次计算中被多次调用，靠内容判重比维护计数器更可靠。
    /// </summary>
    public static class UserDataScope
    {
        public const char Separator = '|';

        public static string Prefix(long libraryId) =>
            libraryId.ToString(CultureInfo.InvariantCulture) + Separator;

        /// <summary>给 key 加库前缀；任何参数异常都返回原 key。</summary>
        public static string Apply(string key, long? libraryId)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (!libraryId.HasValue || libraryId.Value <= 0) return key;

            var prefix = Prefix(libraryId.Value);
            return key.StartsWith(prefix, StringComparison.Ordinal) ? key : prefix + key;
        }
    }
}
