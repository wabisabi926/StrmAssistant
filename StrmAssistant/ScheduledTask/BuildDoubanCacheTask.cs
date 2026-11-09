using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 构建豆瓣缓存：遍历电影/剧集条目，预取豆瓣数据写入本地磁盘缓存
    /// （strmassistant_douban_cache.json + 评分快照），供后续刮削离线命中、
    /// 降低限流风险。已缓存的条目自动跳过，不产生网络请求。
    /// 豆瓣评分统一请用「统一社区评分」（轻量 IMDb 通道，见
    /// <see cref="DoubanAssist"/> 的 UnifyRatingsAsync），本任务只做完整数据预取。
    /// </summary>
    public class BuildDoubanCacheTask : IScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            _logger.Info("DoubanCacheBuild - Scheduled Task Execute");

            var report = await Plugin.DoubanAssist.BuildAllCacheAsync(progress, cancellationToken)
                .ConfigureAwait(false);

            _logger.Info("DoubanCacheBuild - Scheduled Task Complete: " + report.BuildSummary());
        }

        public string Category =>
            Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                Plugin.Instance.DefaultUICulture);

        public string Key => "BuildDoubanCacheTask";

        public string Description => Resources.ResourceManager.GetString(
            "BuildDoubanCacheTask_Description_Pre_fetches_Douban_subject_data_for_movies_and_series_into_the_local_cache__Cached_items_are_skipped",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString(
            "BuildDoubanCacheTask_Name_Build_Douban_Cache",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
