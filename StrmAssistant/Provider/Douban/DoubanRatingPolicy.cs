using System;

namespace StrmAssistant.Provider.Douban
{
    /// <summary>
    /// 评分写入策略：自动刷新受评分回看期限制；
    /// 手动全库同步忽略回看期，首次写入也不受回看期限制。
    /// </summary>
    public static class DoubanRatingPolicy
    {
        public static bool IsRatingUpdateAllowed(bool ignoreLookback, int lookbackDays, DateTimeOffset? premiereDate,
            bool everApplied, DateTimeOffset utcNow)
        {
            if (ignoreLookback || lookbackDays <= 0 || !everApplied || !premiereDate.HasValue) return true;

            return premiereDate.Value >= utcNow.AddDays(-lookbackDays);
        }
    }
}
