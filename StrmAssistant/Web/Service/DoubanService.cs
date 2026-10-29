using MediaBrowser.Controller.Api;
using MediaBrowser.Model.Services;
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
            public List<DoubanAssist.DoubanRatingInfo> Ratings { get; set; } = new List<DoubanAssist.DoubanRatingInfo>();
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

            if (!Plugin.Instance.MetadataEnhanceStore.GetOptions().DoubanAssist.EnableDoubanAssist)
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
