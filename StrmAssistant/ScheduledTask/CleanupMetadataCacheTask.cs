using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 清理超过有效期的被动 TMDB 缓存；override 与 patches 永远不动。
    /// 默认不自动触发，由管理员在计划任务页手动运行。
    /// </summary>
    public class CleanupMetadataCacheTask : IScheduledTask
    {
        public string Key => "CleanupMetadataCacheTask";

        public string Name => Resources.ResourceManager.GetString(
            "CleanupMetadataCacheTask_Name_Cleanup_Metadata_Cache", Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "CleanupMetadataCacheTask_Description_Delete_expired_passive_cache_files__override_and_patches_are_kept",
            Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString(
            "PluginOptions_EditorTitle_Strm_Assistant", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            if (DryRunMode.Skip("cleanup expired metadata cache", Plugin.Instance.Logger))
            {
                progress.Report(100);
                return;
            }

            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions().MetadataCache;
            if (!options.IsMetadataCacheEnabled || options.MetadataCacheTtlDays <= 0)
            {
                Plugin.Instance.Logger.Info(
                    "CleanupMetadataCache - skipped: cache disabled or TTL is 0");
                progress.Report(100);
                return;
            }

            progress.Report(0);
            var removed = LocalMetadataCache.ClearExpiredPassive(options.MetadataCacheTtlDays, cancellationToken);
            progress.Report(100);

            Plugin.Instance.Logger.Info(removed < 0
                ? "CleanupMetadataCache - skipped: cache root not initialized"
                : $"CleanupMetadataCache - removed {removed} expired file(s), TTL={options.MetadataCacheTtlDays} day(s)");

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
