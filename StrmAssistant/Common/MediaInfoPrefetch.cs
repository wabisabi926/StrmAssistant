using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 按需媒体信息：详情页缺流时入队，播放剧集时预取下一集。
    /// 只入现有媒体信息队列，提取仍由 QueueManager 按并发、范围、排除规则处理。
    /// </summary>
    public static class MediaInfoPrefetch
    {
        private static readonly TimeSpan RecentTtl = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<long, DateTime> Recent =
            new ConcurrentDictionary<long, DateTime>();

        public static bool TryEnqueueFromBrowse(BaseItem item)
        {
            if (!Plugin.Instance.MediaInfoExtractStore.GetOptions().OnDemand.ExtractOnBrowse) return false;
            return TryEnqueue(item, "DetailPage");
        }

        public static bool TryScheduleNextEpisode(BaseItem item)
        {
            if (!Plugin.Instance.MediaInfoExtractStore.GetOptions().OnDemand.EnableMediaInfoPreload)
                return false;
            if (!(item is Episode episode)) return false;

            var next = NextEpisode(episode);
            return next != null && TryEnqueue(next, "PlaybackStart");
        }

        private static bool TryEnqueue(BaseItem item, string source)
        {
            if (item == null || !Plugin.IsPluginEnabled)
            {
                LogSkip("item null or plugin disabled");
                return false;
            }
            if (!(item is Video) && !(item is Audio))
            {
                LogSkip($"not video/audio: {item.GetType().Name}");
                return false;
            }
            if (!Plugin.LibraryApi.IsLibraryInScope(item))
            {
                LogSkip($"out of scope: {item.InternalId}");
                return false;
            }

            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().ImageCapture.EnableImageCapture;
            if (!Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture))
            {
                LogSkip($"extract not needed: {item.InternalId}");
                return false;
            }

            var now = DateTime.UtcNow;
            if (Recent.TryGetValue(item.InternalId, out var last) && now - last < RecentTtl)
            {
                LogSkip($"recently enqueued: {item.InternalId}");
                return false;
            }
            if (Recent.Count > 5000) Recent.Clear();
            Recent[item.InternalId] = now;

            QueueManager.EnsureMediaInfoProcessing();
            QueueManager.MediaInfoExtractItemQueue.Enqueue(item);
            Plugin.Instance.Logger.Info($"MediaInfoPrefetch - {source}: {item.Name}");
            return true;
        }

        private static void LogSkip(string message)
        {
            if (Plugin.Instance.DebugMode)
            {
                Plugin.Instance.Logger.Debug("[MediaInfoPrefetch] " + message);
            }
        }

        /// <summary>QueueManager 的追更最小等待对按需条目放行，避免详情页触发后还要等入库时间。</summary>
        public static bool IsOnDemand(long itemId) =>
            Recent.TryGetValue(itemId, out var last) && DateTime.UtcNow - last < RecentTtl;

        internal static Episode NextEpisode(Episode current)
        {
            var series = current?.Series;
            if (series == null || Plugin.LibraryManager == null) return null;

            var episodes = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    AncestorIds = new[] { series.InternalId },
                    Recursive = true
                })
                .OfType<Episode>()
                .Where(episode => episode.IndexNumber.HasValue && episode.InternalId != current.InternalId)
                // 正常季不把特别篇当下一集；正在看特别篇时允许继续特别篇
                .Where(episode => (current.ParentIndexNumber ?? 0) == 0 ||
                                  (episode.ParentIndexNumber ?? 0) > 0)
                .ToList();

            return PickNext(episodes, current.ParentIndexNumber ?? 0, current.IndexNumber ?? 0);
        }

        public static Episode PickNext(IEnumerable<Episode> episodes, int season, int index)
        {
            return episodes?
                .Where(episode => episode.IndexNumber.HasValue)
                .Where(episode => (episode.ParentIndexNumber ?? 0) > season ||
                                  ((episode.ParentIndexNumber ?? 0) == season && episode.IndexNumber.Value > index))
                .OrderBy(episode => episode.ParentIndexNumber ?? 0)
                .ThenBy(episode => episode.IndexNumber)
                .FirstOrDefault();
        }
    }
}
