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
    /// 同步豆瓣数据：遍历电影/剧集/季，匹配并写入豆瓣关联 ID，缓存 subject 数据与评分快照，
    /// 并按勾选应用徽章评分 / Kodi 第三评分 / 简介 / 演员 / 海报 / MdbList。
    /// 来源=豆瓣时同时写主评分；来源不是豆瓣时不动主评分。
    /// </summary>
    public class BuildDoubanCacheTask : IScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            _logger.Info("DoubanSync - Scheduled Task Execute");

            var report = await Plugin.DoubanAssist.SyncAllDoubanDataAsync(progress, cancellationToken)
                .ConfigureAwait(false);

            _logger.Info("DoubanSync - Scheduled Task Complete: " + report.BuildSummary());
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
