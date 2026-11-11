using StrmAssistant.Options;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmAssistant.IntroSkip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class ChapterApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        private const string MarkerSuffix = "#SA";

        public ChapterApi(ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public bool HasIntro(BaseItem item)
        {
            return _itemRepository.GetChapters(item, CancellationToken.None).Any(c => c.MarkerType == MarkerType.IntroStart);
        }

        /// <summary>
        /// 是否存在非 IntroDb 来源的片头标记（探测 #SA、其他插件或原生），用于判断能否用 #DB 刷新。
        /// </summary>
        public bool HasNonDbIntro(BaseItem item)
        {
            return _itemRepository.GetChapters(item, CancellationToken.None).Any(c =>
                c.MarkerType == MarkerType.IntroStart &&
                !c.Name.EndsWith(DbMarkerSuffix, StringComparison.Ordinal));
        }

        public long? GetIntroStart(BaseItem item)
        {
            var introStart = _itemRepository.GetChapters(item, CancellationToken.None)
                .FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);

            return introStart?.StartPositionTicks;
        }

        public long? GetIntroEnd(BaseItem item)
        {
            var introEnd = _itemRepository.GetChapters(item, CancellationToken.None)
                .FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

            return introEnd?.StartPositionTicks;
        }

        public bool HasCredits(BaseItem item)
        {
            return _itemRepository.GetChapters(item, CancellationToken.None).Any(c => c.MarkerType == MarkerType.CreditsStart);
        }

        public long? GetCreditsStart(BaseItem item)
        {
            var creditsStart = _itemRepository.GetChapters(item, CancellationToken.None)
                .FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);

            return creditsStart?.StartPositionTicks;
        }

        public void UpdateIntro(Episode item, SessionInfo session, long introStartPositionTicks,
            long introEndPositionTicks)
        {
            if (introStartPositionTicks > introEndPositionTicks) return;

            var resultEpisodes = FetchEpisodes(item, MarkerType.IntroEnd);

            foreach (var episode in resultEpisodes)
            {
                var chapters = _itemRepository.GetChapters(episode);

                chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

                var introStart = new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartPositionTicks
                };
                chapters.Add(introStart);
                var introEnd = new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndPositionTicks
                };
                chapters.Add(introEnd);

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                _itemRepository.SaveChapters(episode.InternalId, chapters);
                IntroMarkerNotifier.Notify(episode);
            }

            _logger.Info("Intro marker updated by " + session.UserName + " for " +
                         item.FindSeriesName() + " - " + item.FindSeasonName() + " - " + item.Season.Path);
            var introStartTime = new TimeSpan(introStartPositionTicks).ToString(@"hh\:mm\:ss\.fff");
            _logger.Info("Intro start time: " + introStartTime);
            var introEndTime = new TimeSpan(introEndPositionTicks).ToString(@"hh\:mm\:ss\.fff");
            _logger.Info("Intro end time: " + introEndTime);
            _ = Plugin.NotificationApi.IntroUpdateSendNotification(item, session, introStartTime, introEndTime);
        }

        public void UpdateCredits(Episode item, SessionInfo session, long creditsDurationTicks)
        {
            var resultEpisodes = FetchEpisodes(item, MarkerType.CreditsStart);

            foreach (var episode in resultEpisodes)
            {
                if (episode.RunTimeTicks.HasValue)
                {
                    if (episode.RunTimeTicks.Value - creditsDurationTicks > 0)
                    {
                        var chapters = _itemRepository.GetChapters(episode);
                        chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);

                        var creditsStart = new ChapterInfo
                        {
                            Name = MarkerType.CreditsStart + MarkerSuffix,
                            MarkerType = MarkerType.CreditsStart,
                            StartPositionTicks = episode.RunTimeTicks.Value - creditsDurationTicks
                        };
                        chapters.Add(creditsStart);

                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                        _itemRepository.SaveChapters(episode.InternalId, chapters);
                        IntroMarkerNotifier.Notify(episode);
                    }
                }
            }

            _logger.Info("Credits marker updated by " + session.UserName + " for " +
                         item.FindSeriesName() + " - " + item.FindSeasonName() + " - " + item.Season.Path);
            var creditsDuration = new TimeSpan(creditsDurationTicks).ToString(@"hh\:mm\:ss\.fff");
            _logger.Info("Credits duration: " + new TimeSpan(creditsDurationTicks).ToString(@"hh\:mm\:ss\.fff"));
            _ = Plugin.NotificationApi.CreditsUpdateSendNotification(item, session, creditsDuration);
        }

        private static bool IsMarkerAddedByIntroSkip(ChapterInfo chapter)
        {
            return chapter.Name.EndsWith(MarkerSuffix);
        }

        /// <summary>
        /// IntroDb 刮削标记后缀，区别于播放行为探测的 #SA，用于识别与更新自身写入的标记。
        /// </summary>
        public const string DbMarkerSuffix = "#DB";

        /// <summary>
        /// 多人提交时取时长最长的片段（社区数据里最长的通常是完整片头/片尾）。
        /// </summary>
        internal static IntroDbApi.IntroDbSegment PickLongestSegment(List<IntroDbApi.IntroDbSegment> segments)
        {
            if (segments == null || segments.Count == 0) return null;

            // TheIntroDB 的电影 intro 常只给 end_ms（start_ms=null），按起点 0 处理；
            // 两端都缺的片段无法定位，直接丢弃。
            return segments
                .Where(s => s.start_ms.HasValue || s.end_ms.HasValue)
                .OrderByDescending(s => (s.end_ms ?? 0) - (s.start_ms ?? 0))
                .FirstOrDefault();
        }

        /// <summary>
        /// 把 IntroDb 的毫秒时间换算为 Emby ticks；缺少结束时间时用片长补足，并限制不超过片长。
        /// </summary>
        internal static (long StartTicks, long EndTicks) GetIntroMarkerTicks(
            IntroDbApi.IntroDbSegment segment, long? runtimeTicks)
        {
            var startTicks = (segment.start_ms ?? 0) * TimeSpan.TicksPerMillisecond;
            var endTicks = segment.end_ms.HasValue
                ? segment.end_ms.Value * TimeSpan.TicksPerMillisecond
                : runtimeTicks ?? startTicks;

            if (runtimeTicks.HasValue && endTicks > runtimeTicks.Value) endTicks = runtimeTicks.Value;

            return (startTicks, endTicks);
        }

        /// <summary>
        /// 写入 IntroDb 刮削的片头/片尾标记：仅填充缺失（不覆盖探测或其他来源），
        /// 自身上一轮的 #DB 标记允许更新。返回是否发生写入。
        /// </summary>
        public bool UpdateIntroDbMarkers(BaseItem item, List<IntroDbApi.IntroDbSegment> intro,
            List<IntroDbApi.IntroDbSegment> credits)
        {
            var changed = false;
            var chapters = _itemRepository.GetChapters(item);
            var runtime = item.RunTimeTicks;

            var introSegment = PickLongestSegment(intro);
            var creditsSegment = PickLongestSegment(credits);

            if (introSegment != null)
            {
                var hasExternalIntro = chapters.Any(c =>
                    (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd) &&
                    !c.Name.EndsWith(DbMarkerSuffix, StringComparison.Ordinal));

                if (!hasExternalIntro)
                {
                    var (introStartTicks, introEndTicks) = GetIntroMarkerTicks(introSegment, runtime);

                    if (introEndTicks > introStartTicks)
                    {
                        chapters.RemoveAll(c =>
                            c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.IntroStart + DbMarkerSuffix,
                            MarkerType = MarkerType.IntroStart,
                            StartPositionTicks = introStartTicks
                        });
                        chapters.Add(new ChapterInfo
                        {
                            Name = MarkerType.IntroEnd + DbMarkerSuffix,
                            MarkerType = MarkerType.IntroEnd,
                            StartPositionTicks = introEndTicks
                        });

                        changed = true;
                        _logger.Info("IntroDbScrape - Intro marker {0}-{1} for {2}",
                            new TimeSpan(introStartTicks).ToString(@"hh\:mm\:ss"),
                            new TimeSpan(introEndTicks).ToString(@"hh\:mm\:ss"), item.Name);
                    }
                }
            }

            if (creditsSegment?.start_ms != null)
            {
                var hasExternalCredits = chapters.Any(c =>
                    c.MarkerType == MarkerType.CreditsStart &&
                    !c.Name.EndsWith(DbMarkerSuffix, StringComparison.Ordinal));

                var creditsStartTicks = creditsSegment.start_ms.Value * TimeSpan.TicksPerMillisecond;

                if (!hasExternalCredits && (!runtime.HasValue || creditsStartTicks < runtime.Value))
                {
                    chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);

                    chapters.Add(new ChapterInfo
                    {
                        Name = MarkerType.CreditsStart + DbMarkerSuffix,
                        MarkerType = MarkerType.CreditsStart,
                        StartPositionTicks = creditsStartTicks
                    });

                    changed = true;
                    _logger.Info("IntroDbScrape - Credits marker {0} for {1}",
                        new TimeSpan(creditsStartTicks).ToString(@"hh\:mm\:ss"), item.Name);
                }
            }

            if (changed)
            {
                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                _itemRepository.SaveChapters(item.InternalId, chapters);
                IntroMarkerNotifier.Notify(item);
            }

            return changed;
        }

        private static bool AllowOverwrite(ChapterInfo chapter)
        {
            return IsMarkerAddedByIntroSkip(chapter) ||
                   IsIntroSkipPreferenceSelected(IntroSkipPlaybackOptions.IntroSkipPreference.ResetAndOverwrite);
        }

        private static bool AllowOverwrite(ChapterInfo chapter, bool ignore)
        {
            return IsMarkerAddedByIntroSkip(chapter) || ignore;
        }

        private static BaseItem[] GetEpisodesInSeason(Season season, int? minIndexNumber = null)
        {
            return season.GetEpisodes(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    MinIndexNumber = minIndexNumber ?? 1,
                    OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
                })
                .Items;
        }

        public List<BaseItem> FetchEpisodes(Episode item, MarkerType markerType)
        {
            var episodesInSeason = GetEpisodesInSeason(item.Season);

            var priorEpisodesWithoutMarkers = episodesInSeason.Where(e => e.IndexNumber < item.IndexNumber)
                .Where(e =>
                {
                    if (!Plugin.LibraryApi.HasMediaInfo(e))
                    {
                        QueueManager.MediaInfoExtractItemQueue.Enqueue(e);
                        return false;
                    }

                    var chapters = _itemRepository.GetChapters(e);
                    switch (markerType)
                    {
                        case MarkerType.IntroEnd:
                        {
                            var hasIntroStart = chapters.Any(c => c.MarkerType == MarkerType.IntroStart);
                            var hasIntroEnd = chapters.Any(c => c.MarkerType == MarkerType.IntroEnd);
                            return !hasIntroStart || !hasIntroEnd;
                        }
                        case MarkerType.CreditsStart:
                            var hasCredits = chapters.Any(c => c.MarkerType == MarkerType.CreditsStart);
                            return !hasCredits;
                    }

                    return false;
                });

            var currentEpisodes = episodesInSeason.Where(e => e.IndexNumber == item.IndexNumber);

            var followingEpisodes = episodesInSeason.Where(e => e.IndexNumber > item.IndexNumber)
                .Where(e =>
                {
                    if (!Plugin.LibraryApi.HasMediaInfo(e))
                    {
                        QueueManager.MediaInfoExtractItemQueue.Enqueue(e);
                        return false;
                    }

                    var chapters = _itemRepository.GetChapters(e);
                    switch (markerType)
                    {
                        case MarkerType.IntroEnd:
                        {
                            var hasIntroStart = chapters.Any(c =>
                                c.MarkerType == MarkerType.IntroStart && !AllowOverwrite(c));
                            var hasIntroEnd = chapters.Any(c =>
                                c.MarkerType == MarkerType.IntroEnd && !AllowOverwrite(c));
                            return !hasIntroStart || !hasIntroEnd;
                        }
                        case MarkerType.CreditsStart:
                            var hasCredits = chapters.Any(c =>
                                c.MarkerType == MarkerType.CreditsStart && !AllowOverwrite(c));
                            return !hasCredits;
                    }

                    return false;
                });

            var result = priorEpisodesWithoutMarkers.Concat(currentEpisodes).Concat(followingEpisodes).ToList();
            return result;
        }

        public void RemoveSeasonIntroCreditsMarkers(Episode item, SessionInfo session)
        {
            if (DryRunMode.Skip($"clear season intro/credits markers: {item.Name}", _logger)) return;

            var episodesInSeason = GetEpisodesInSeason(item.Season, item.IndexNumber);

            foreach (var episode in episodesInSeason)
            {
                RemoveIntroCreditsMarkers(episode);
            }

            _logger.Info("Intro and Credits markers are cleared by " + session.UserName + " since " + item.Name +
                         " in " + item.FindSeriesName() + " - " + item.FindSeasonName() + " - " + item.Season.Path);
        }

        public void RemoveIntroCreditsMarkers(BaseItem item)
        {
            if (DryRunMode.Skip($"clear intro/credits markers: {item.Name}", _logger)) return;

            var chapters = _itemRepository.GetChapters(item);
            chapters.RemoveAll(c =>
                c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd ||
                c.MarkerType == MarkerType.CreditsStart);

            // 用户主动清除：让 IntroMarkerProtector 放行，避免标记被合并回来
            StrmAssistant.Mod.ChapterChangeTracker.BypassInstance(item);
            try
            {
                _itemRepository.SaveChapters(item.InternalId, chapters);
            }
            finally
            {
                StrmAssistant.Mod.ChapterChangeTracker.ClearBypass();
            }

            IntroMarkerNotifier.Notify(item);
        }

        public void ClearChapterImages(BaseItem item)
        {
            if (DryRunMode.Skip($"clear chapter images: {item.Name}", _logger)) return;

            var chapters = _itemRepository.GetChapters(item);
            var changed = false;

            foreach (var chapter in chapters)
            {
                if (string.IsNullOrEmpty(chapter.ImagePath)) continue;

                try
                {
                    if (File.Exists(chapter.ImagePath))
                    {
                        File.Delete(chapter.ImagePath);
                    }
                }
                catch (Exception e)
                {
                    _logger.Warn($"ChapterImageClear - Failed to delete {chapter.ImagePath}: {e.Message}");
                }

                chapter.ImagePath = null;
                chapter.ImageDateModified = DateTime.MinValue;
                changed = true;
            }

            if (changed)
            {
                _itemRepository.SaveChapters(item.InternalId, chapters);
            }

            DeleteBifFiles(item);

            _logger.Info("ChapterImageClear - " + item.Name + " - " + item.Path);
        }

        private void DeleteBifFiles(BaseItem item)
        {
            try
            {
                var directory = Path.GetDirectoryName(item.Path);
                var basename = Path.GetFileNameWithoutExtension(item.Path);

                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(basename) || !Directory.Exists(directory))
                    return;

                foreach (var bif in Directory.GetFiles(directory, basename + "*.bif"))
                {
                    try
                    {
                        File.Delete(bif);
                        _logger.Info("ChapterImageClear - Deleted BIF: " + bif);
                    }
                    catch (Exception e)
                    {
                        _logger.Warn($"ChapterImageClear - Failed to delete BIF {bif}: {e.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Warn($"ChapterImageClear - Failed to enumerate BIF for {item.Path}: {e.Message}");
            }
        }

        /// <summary>
        /// 清除动作覆盖三种行为的并集：播放探测、片头指纹检测、在线刮削。
        /// 任一范围留空即视为全部剧集/混合库；电影库不参与。
        /// </summary>
        private List<string> GetClearLibraryPaths()
        {
            var options = Plugin.Instance.IntroSkipStore.GetOptions();
            var scopes = new[]
            {
                options.Playback.LibraryScope,
                options.Fingerprint.MarkerEnabledLibraryScope,
                options.Scrape.LibraryScope
            };

            var selectedIds = new HashSet<string>(StringComparer.Ordinal);
            var includeAll = false;

            foreach (var scope in scopes)
            {
                if (string.IsNullOrWhiteSpace(scope))
                {
                    includeAll = true;
                    break;
                }

                var ids = GetValidLibraryIds(scope);

                if (ids.Length == 0)
                {
                    // 收藏(-1)没有媒体库路径；无效/已删除的库也不扩大清除范围
                    continue;
                }

                foreach (var id in ids)
                {
                    if (id != "-1") selectedIds.Add(id);
                }
            }

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (includeAll || selectedIds.Contains(f.Id)))
                .ToList();

            _logger.Info("IntroSkipClear - LibraryScope: " +
                         (includeAll ? "ALL" : libraries.Any() ? string.Join(", ", libraries.Select(l => l.Name)) : "EMPTY"));

            return libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public List<BaseItem> FetchClearTaskItems(List<BaseItem> clearItems)
        {
            var itemsIntroSkipQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var clearAll = !clearItems.Any();
            List<BaseItem> results;

            if (clearAll)
            {
                var clearLibraryPaths = GetClearLibraryPaths();
                if (clearLibraryPaths.Count == 0) return new List<BaseItem>();

                itemsIntroSkipQuery.PathStartsWithAny = clearLibraryPaths.ToArray();

                results = _libraryManager.GetItemList(itemsIntroSkipQuery).ToList();
            }
            else
            {
                results = new List<BaseItem>();

                foreach (var item in clearItems)
                {
                    if (item is Series series)
                    {
                        itemsIntroSkipQuery.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
                        itemsIntroSkipQuery.ParentWithPresentationUniqueKey = null;

                        results.AddRange(_libraryManager.GetItemList(itemsIntroSkipQuery));

                        _logger.Info(
                            $"IntroSkipClear - {series.Name} ({series.InternalId}) - {series.ContainingFolderPath}");
                    }
                    else if (item is Season season)
                    {
                        itemsIntroSkipQuery.ParentWithPresentationUniqueKey = season.PresentationUniqueKey;
                        itemsIntroSkipQuery.SeriesPresentationUniqueKey = null;

                        results.AddRange(_libraryManager.GetItemList(itemsIntroSkipQuery));

                        _logger.Info(
                            $"IntroSkipClear - {season.SeriesName} - {season.Name} ({season.InternalId}) - {season.ContainingFolderPath}");
                    }
                }

                results = results.GroupBy(i => i.InternalId).Select(g => g.First()).ToList();
            }

            var items = new List<BaseItem>();

            foreach (var item in results)
            {
                var chapters = _itemRepository.GetChapters(item);

                if (chapters != null && chapters.Any())
                {
                    var hasMarkers = chapters.Any(c =>
                        (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd ||
                         c.MarkerType == MarkerType.CreditsStart) && AllowOverwrite(c, !clearAll));
                    if (hasMarkers)
                    {
                        items.Add(item);
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId)))
                {
                    items.Add(item);
                }
            }

            _logger.Info("IntroSkipClear - Number of items: " + items.Count);

            return items;
        }

        public bool SeasonHasIntroCredits(Episode item)
        {
            if (!item.IndexNumber.HasValue || !(item.ParentIndexNumber > 0))
            {
                return false;
            }

            var episodesInSeason = GetEpisodesInSeason(item.Season);

            var result = episodesInSeason.Any(e =>
            {
                var chapters = _itemRepository.GetChapters(e);
                var hasIntroMarkers =
                    chapters.Any(c => c.MarkerType == MarkerType.IntroStart && IsMarkerAddedByIntroSkip(c)) &&
                    chapters.Any(c => c.MarkerType == MarkerType.IntroEnd && IsMarkerAddedByIntroSkip(c));
                var hasCreditsStart =
                    chapters.Any(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerAddedByIntroSkip(c));

                return hasIntroMarkers || hasCreditsStart;
            });

            return result;
        }

        public List<Episode> SeasonHasIntroCredits(List<Episode> episodes)
        {
            var episodesInScope = episodes.Where(e => Plugin.PlaySessionMonitor.IsLibraryInScope(e)).ToList();

            var parentIds = episodesInScope.Select(e => e.ParentId).Distinct().ToArray();

            var groupedByParent = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    ParentIds = parentIds,
                    HasIndexNumber = true
                })
                .OfType<Episode>()
                .Where(ep => !episodesInScope.Select(e => e.InternalId).Contains(ep.InternalId))
                .GroupBy(ep => ep.ParentId);

            var resultEpisodes = new List<Episode>();

            foreach (var parent in groupedByParent)
            {
                var hasMarkers = parent.Any(e =>
                {
                    var chapters = _itemRepository.GetChapters(e);

                    if (chapters != null && chapters.Any())
                    {
                        var hasIntroMarkers =
                            chapters.Any(c => c.MarkerType == MarkerType.IntroStart && IsMarkerAddedByIntroSkip(c)) &&
                            chapters.Any(c => c.MarkerType == MarkerType.IntroEnd && IsMarkerAddedByIntroSkip(c));
                        var hasCreditsMarker = chapters.Any(c =>
                            c.MarkerType == MarkerType.CreditsStart && IsMarkerAddedByIntroSkip(c));
                        return hasIntroMarkers || hasCreditsMarker;
                    }

                    return false;
                });

                if (hasMarkers)
                {
                    var episodesCanMarkers = episodesInScope.Where(e => e.ParentId == parent.Key).ToList();
                    resultEpisodes.AddRange(episodesCanMarkers);
                }
            }

            return resultEpisodes;
        }

        public void PopulateIntroCredits(List<Episode> incomingEpisodes)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ItemIds = incomingEpisodes.Select(e => e.InternalId).ToArray()
            });

            var parentIds = episodes.Select(e => e.ParentId).Distinct().ToArray();

            var groupedByParent = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    ParentIds = parentIds,
                    HasIndexNumber = true
                })
                .Where(ep => !episodes.Select(e => e.InternalId).Contains(ep.InternalId))
                .OfType<Episode>()
                .GroupBy(ep => ep.ParentId);

            foreach (var parent in groupedByParent)
            {
                Episode lastIntroEpisode = null;
                Episode lastCreditsEpisode = null;

                foreach (var episode in parent.Reverse())
                {
                    var chapters = _itemRepository.GetChapters(episode);

                    var hasIntroMarkers =
                        chapters.Any(c => c.MarkerType == MarkerType.IntroStart && IsMarkerAddedByIntroSkip(c)) &&
                        chapters.Any(c => c.MarkerType == MarkerType.IntroEnd && IsMarkerAddedByIntroSkip(c));

                    if (hasIntroMarkers && lastIntroEpisode == null)
                    {
                        lastIntroEpisode = episode;
                    }

                    var hasCreditsMarker =
                        chapters.Any(c => c.MarkerType == MarkerType.CreditsStart && IsMarkerAddedByIntroSkip(c));

                    if (hasCreditsMarker && lastCreditsEpisode == null && episode.RunTimeTicks.HasValue)
                    {
                        lastCreditsEpisode = episode;
                    }

                    if (lastIntroEpisode != null && lastCreditsEpisode != null)
                    {
                        break;
                    }
                }

                if (lastIntroEpisode != null)
                {
                    var introChapters = _itemRepository.GetChapters(lastIntroEpisode);
                    var introStart = introChapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
                    var introEnd = introChapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

                    if (introStart != null && introEnd != null)
                    {
                        foreach (var episode in episodes.Where(e => e.ParentId == parent.Key && e.IndexNumber.HasValue))
                        {
                            var chapters = _itemRepository.GetChapters(episode);
                            chapters.Add(introStart);
                            chapters.Add(introEnd);
                            chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                            _itemRepository.SaveChapters(episode.InternalId, chapters);
                            IntroMarkerNotifier.Notify(episode);
                            _logger.Info("Intro marker updated for " + episode.Path);
                        }
                    }
                }

                if (lastCreditsEpisode?.RunTimeTicks != null)
                {
                    var lastEpisodeChapters = _itemRepository.GetChapters(lastCreditsEpisode);
                    var lastEpisodeCreditsStart = lastEpisodeChapters.FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);

                    if (lastEpisodeCreditsStart != null)
                    {
                        var creditsDurationTicks = lastCreditsEpisode.RunTimeTicks.Value - lastEpisodeCreditsStart.StartPositionTicks;
                        if (creditsDurationTicks > 0)
                        {
                            foreach (var episode in episodes.Where(e =>
                                         e.ParentId == parent.Key && e.IndexNumber.HasValue))
                            {
                                if (episode.RunTimeTicks.HasValue)
                                {
                                    var creditsStartTicks = episode.RunTimeTicks.Value - creditsDurationTicks;
                                    if (creditsStartTicks > 0)
                                    {
                                        var chapters = _itemRepository.GetChapters(episode);
                                        var creditsStart = new ChapterInfo
                                        {
                                            Name = MarkerType.CreditsStart + MarkerSuffix,
                                            MarkerType = MarkerType.CreditsStart,
                                            StartPositionTicks = creditsStartTicks
                                        };
                                        chapters.Add(creditsStart);
                                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                                        _itemRepository.SaveChapters(episode.InternalId, chapters);
                                        IntroMarkerNotifier.Notify(episode);
                                        _logger.Info("Credits marker updated for " + episode.Path);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
