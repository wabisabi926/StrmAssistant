using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
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
    /// 重建播放进度键。
    ///
    /// 为什么需要它：Emby 把 UserDataKey 存在 <c>MediaItems.UserDataKeyId</c> 上，
    /// 只在**条目被保存**时（<c>SqliteItemRepository.SaveItemsInTranscation</c>）重算。
    /// 元数据刷新在「内容没有变化」时不会重新保存条目，因此启用「各媒体库内记录播放进度」
    /// 后仅靠刷新元数据，大部分条目的键不会更新，功能看起来没生效。
    ///
    /// 本任务对每个电影/剧集调用一次 <c>UpdateToRepository(MetadataEdit)</c>，强制重算并落库。
    /// 开关关闭时同样可用 —— 它会把键恢复成原值（用于回滚）。
    /// </summary>
    public class RebuildUserDataKeysTask : IScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            var enabled = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Playback.LibraryScopePlayProgress;
            _logger.Info($"RebuildUserDataKeys - Execute (LibraryScopePlayProgress={enabled})");

            var items = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { nameof(Movie), nameof(Episode), nameof(Series), nameof(Season) },
            });

            var total = items.Length;
            var done = 0;
            var failed = 0;

            _logger.Info($"RebuildUserDataKeys - {total} item(s) to process");

            if (DryRunMode.Skip($"rebuild user data keys for {total} item(s)", _logger))
            {
                progress.Report(100);
                return;
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                }
                catch (Exception e)
                {
                    failed++;
                    if (failed <= 10)
                    {
                        _logger.Warn($"RebuildUserDataKeys - failed on {item.Name} ({item.InternalId}): {e.Message}");
                    }
                }

                done++;
                progress.Report(total == 0 ? 100 : 100.0 * done / total);
            }

            _logger.Info($"RebuildUserDataKeys - Completed {done - failed}/{total}, failed {failed}");

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public string Category =>
            Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                Plugin.Instance.DefaultUICulture);

        public string Key => "RebuildUserDataKeysTask";

        public string Description => Resources.ResourceManager.GetString(
            "RebuildUserDataKeysTask_Description_Recompute_and_persist_the_per_library_user_data_key_for_every_movie_and_episode__Run_once_after_toggling_Track_Play_Progress_per_Library_and_restarting",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString(
            "RebuildUserDataKeysTask_Name_Rebuild_Play_Progress_Keys",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
