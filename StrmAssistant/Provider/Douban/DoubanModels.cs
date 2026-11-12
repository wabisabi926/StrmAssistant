using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace StrmAssistant.Provider.Douban
{
    /// <summary>豆瓣 subject 的评分与基础元数据，供评分服务和元数据服务共用。</summary>
    public class DoubanSubject
    {
        public string SubjectId { get; set; }
        public double? Rating { get; set; }
        public long? Votes { get; set; }
        public string Summary { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Countries { get; set; } = new List<string>();
        public List<string> Languages { get; set; } = new List<string>();
    }

    /// <summary>评分快照（无过期）：豆瓣评分徽章数据源，条目通过 ProviderIds["Douban"] 关联。</summary>
    public class DoubanRatingSnapshot
    {
        public double Rating { get; set; }
        public long? Votes { get; set; }
        public DateTime Updated { get; set; }

        /// <summary>
        /// <c>CommunityRating</c> 是否由豆瓣评分写入。
        /// 与 <see cref="Rating"/> 的取值互相独立：抓到豆瓣分就会记 <see cref="Rating"/>，
        /// 但只有真正写入 <c>CommunityRating</c> 后本标志才为 true。
        /// 可空：null 表示旧版快照（无此字段），此时按实测推断。
        /// </summary>
        public bool? Applied { get; set; }

        /// <summary>最近一次写社区评分时记录的来源；空表示旧版快照（无此字段）。</summary>
        public string WrittenSource { get; set; }

        /// <summary>
        /// 最近一次成功检查豆瓣条目的时间（与评分是否变化无关），用于「按需刷新」冷却判定。
        /// 可空：旧版快照无此字段，回退用 <see cref="Updated"/>。
        /// </summary>
        public DateTime? Checked { get; set; }

        /// <summary>
        /// 是否曾由豆瓣写入过社区评分（评分变化时也保留）。
        /// 回看期只限制后续更新，首次写入不受限，避免老片入库后永远没有社区评分。
        /// </summary>
        public bool EverApplied { get; set; }
    }

    public class DoubanRatingInfo
    {
        public string ItemId { get; set; }
        public string SubjectId { get; set; }
        public double? Rating { get; set; }
        public long? Votes { get; set; }

        /// <summary>当前 <c>CommunityRating</c> 是否确为豆瓣评分（已扣除被外部刮削覆盖的情况）。</summary>
        public bool Applied { get; set; }

        /// <summary>当前 <c>CommunityRating</c> 的来源：douban / tmdb / imdb，空表示无显式来源。</summary>
        public string Source { get; set; }

        /// <summary>条目当前的社区评分，供前端校验「图标与数字同源」。</summary>
        public double? CommunityRating { get; set; }
    }

    /// <summary>按 IMDb 查分的结果：供 Kodi 侧（助手/皮肤）用外部 ID 定位条目。</summary>
    public class DoubanImdbRatingInfo
    {
        public string Imdb { get; set; }
        public string ItemId { get; set; }
        public string SubjectId { get; set; }
        public double? Rating { get; set; }
        public long? Votes { get; set; }
        public bool Applied { get; set; }
        public string Source { get; set; }
        public double? CommunityRating { get; set; }
    }

    /// <summary>豆瓣统一评分的统计结果。</summary>
    public sealed class DoubanUnifyReport
    {
        private const int MaxSamples = 20;

        public int Total { get; set; }
        public int Updated { get; set; }
        public int Same { get; set; }
        public int Cleared { get; set; }
        public int Locked { get; set; }
        public int NoId { get; set; }
        public int NoRating { get; set; }
        public int Failed { get; set; }
        public List<string> Samples { get; } = new List<string>();

        public string BuildSummary()
        {
            return string.Format(
                "DoubanUnify - total={0} updated={1} same={2} cleared={3} locked={4} noId={5} noRating={6} failed={7}",
                Total, Updated, Same, Cleared, Locked, NoId, NoRating, Failed);
        }

        public string BuildUserSummary()
        {
            var format = Resources.ResourceManager.GetString("DoubanUnify_Notify_Summary",
                CultureInfo.CurrentUICulture);
            return string.Format(format ?? string.Empty,
                Total, Updated, Same, Cleared, Locked, NoId, NoRating, Failed);
        }
    }
}
