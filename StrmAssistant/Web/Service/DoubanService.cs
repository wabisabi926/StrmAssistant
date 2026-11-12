using MediaBrowser.Controller.Api;
using MediaBrowser.Model.Services;
using StrmAssistant.Options;
using StrmAssistant.Provider.Douban;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    public class DoubanService : BaseApiService
    {
        public class DoubanRatingsResponse
        {
            public bool Enabled { get; set; }
            public List<DoubanRatingInfo> Ratings { get; set; } = new List<DoubanRatingInfo>();
        }

        public class DoubanImdbRatingsResponse
        {
            public bool Enabled { get; set; }

            public List<DoubanImdbRatingInfo> Ratings { get; set; } =
                new List<DoubanImdbRatingInfo>();
        }

        public object Get(GetDoubanRatings request)
        {
            if (!Plugin.IsPluginEnabled) return null;
            var response = new DoubanRatingsResponse();

            // 必须登录：该端点会暴露「内部 ItemId ↔ 豆瓣 SubjectId」的对应关系，
            // 匿名可读等于把私有媒体库的片单泄露给任何能访问 Emby 的人（由 [Authenticated] 拦住）。
            //
            // user == null 只可能出现在「服务端 API Key」调用（无用户上下文），
            // API Key 权限等价管理员，此时不做可见性过滤，保持既有集成可用。
            var user = GetUserForRequest(null, true);

            // Web 详情页的评分图标不只服务豆瓣：来源选 TMDB / IMDb 时也需要本接口
            // 返回 CommunityRating 与来源标记，否则前端会直接禁用整个评分图标模块。
            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
            var ratingIconsEnabled = options.IsDoubanDataEnabled ||
                                     options.CommunityRating?.ParsedSource != CommunityRatingSourceOption.None;

            if (!ratingIconsEnabled)
            {
                return response;
            }

            response.Enabled = true;

            if (string.IsNullOrWhiteSpace(request?.Ids))
            {
                return response;
            }

            var itemIds = request.Ids
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(i => i.Trim())
                .Where(i => long.TryParse(i, out _))
                .Take(80)
                .ToList();

            // 逐条按当前用户可见性过滤，与 MultiVersionService 保持一致
            response.Ratings = Plugin.DoubanAssist.GetRatings(itemIds, user);

            // 未勾选「抓取豆瓣评分」且来源不是豆瓣时，不把豆瓣分暴露给 Web 端，
            // 避免「只要了简介却附带豆瓣徽章」的隐式行为。社区评分来源图标仍可正常使用。
            if (options.CommunityRating?.ParsedSource != CommunityRatingSourceOption.Douban &&
                !options.DoubanAssist.DoubanFetchRating)
            {
                foreach (var rating in response.Ratings)
                {
                    rating.Rating = null;
                    rating.Votes = null;
                    rating.Applied = false;
                }
            }

            return response;
        }

        /// <summary>
        /// 按 IMDb ID 批量查豆瓣分：供 Kodi 侧助手/皮肤用外部 ID 定位条目，无需读取 E4K 映射库。
        /// </summary>
        public object Get(GetDoubanRatingsByImdb request)
        {
            if (!Plugin.IsPluginEnabled) return null;

            var response = new DoubanImdbRatingsResponse();

            var user = GetUserForRequest(null, true);

            if (!Plugin.Instance.MetadataEnhanceStore.GetOptions().IsDoubanDataEnabled)
            {
                return response;
            }

            response.Enabled = true;

            if (string.IsNullOrWhiteSpace(request?.Ids))
            {
                return response;
            }

            var imdbIds = request.Ids
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(i => i.Trim())
                .Where(i => i.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && i.Length > 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();

            response.Ratings = Plugin.DoubanAssist.GetRatingsByImdb(imdbIds, user);

            // Kodi 端同理：只有需要豆瓣评分（社区评分/徽章/第三评分）时才暴露分数。
            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
            if (options.CommunityRating?.ParsedSource != CommunityRatingSourceOption.Douban &&
                !options.DoubanAssist.DoubanFetchRating &&
                !options.DoubanAssist.DoubanRatingToKodi)
            {
                foreach (var rating in response.Ratings)
                {
                    rating.Rating = null;
                    rating.Votes = null;
                    rating.Applied = false;
                }
            }

            return response;
        }

        public class RefreshDoubanRatingResponse
        {
            /// <summary>是否已排入后台刷新（冷却未到、缺豆瓣 ID、熔断中等情况返回 false）。</summary>
            public bool Scheduled { get; set; }
        }

        public object Post(RefreshDoubanRating request)
        {
            var response = new RefreshDoubanRatingResponse();

            if (!Plugin.IsPluginEnabled) return response;

            var user = GetUserForRequest(null, true);

            var item = Plugin.LibraryManager.GetItemById(request.Id);

            if (item == null) return response;
            if (user != null && !item.IsVisible(user)) return response;

            response.Scheduled = Plugin.DoubanAssist.TryScheduleRatingRefresh(item, "DetailPage");

            return response;
        }
    }
}
