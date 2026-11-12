using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 同步社区评分：按「元数据增强 → 社区评分 → 来源」把全库电影与剧集的
    /// <c>CommunityRating</c> 写成同一个来源（TMDB / IMDb / 豆瓣）。
    ///
    /// 默认不自动触发，由管理员在计划任务页手动运行。开启「体验增强 → 安全 → 演练模式」时
    /// 只统计差异、不写库（仅 TMDB / IMDb），豆瓣来源不支持演练。
    /// </summary>
    public class UnifyCommunityRatingTask : IScheduledTask
    {
        public string Key => "UnifyCommunityRatingTask";

        public string Name => Resources.ResourceManager.GetString(
            "UnifyCommunityRatingTask_Name_Unify_Community_Rating", Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "UnifyCommunityRatingTask_Description_Unify_CommunityRating_to_the_configured_source",
            Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString(
            "PluginOptions_EditorTitle_Strm_Assistant", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            var logger = Plugin.Instance.Logger;
            var options = CommunityRatingService.GetOptions();

            // 豆瓣来源：优先豆瓣条目分，缺失时回退 IMDb API；带 2 秒频控 + 熔断，不适用演练模式。
            if (options?.ParsedSource == CommunityRatingSourceOption.Douban)
            {
                if (DryRunMode.Enabled)
                {
                    logger.Info(
                        "CommunityRating - Douban source does not support dry-run; disable Dry Run and run again");
                    progress?.Report(100);
                    return;
                }

                logger.Info("CommunityRating - Task Execute (source=Douban, subject first, IMDb fallback)");
                var doubanReport = await Plugin.DoubanAssist
                    .UnifyRatingsAsync(progress, cancellationToken).ConfigureAwait(false);
                logger.Info(doubanReport.BuildSummary());

                Plugin.NotificationApi?.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {doubanReport.BuildUserSummary()}", 15000);

                return;
            }

            var apply = !DryRunMode.Enabled;

            logger.Info($"CommunityRating - Task Execute ({(apply ? "apply" : "dry-run")})");

            var report = await Plugin.RatingUnifier
                .UnifyAllAsync(apply, progress, cancellationToken).ConfigureAwait(false);

            logger.Info(report.BuildSummary());

            foreach (var sample in report.Samples)
            {
                logger.Info("CommunityRating - " + sample);
            }

            if (report.Total == 0)
            {
                logger.Info("CommunityRating - nothing to do (source not TMDB/IMDb, or no items)");
                progress.Report(100);
                return;
            }

            if (!apply)
            {
                logger.Info(
                    "CommunityRating - dry-run completed, no data written. Disable Dry Run and run again to apply.");
            }

            Plugin.NotificationApi?.SendMessageToAdmins(
                $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {report.BuildUserSummary()}", 15000);
        }
    }
}
