using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(FingerprintApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);
        private readonly object _audioFingerprintManager;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly MethodInfo _getAllFingerprintFilesForSeason;
        private readonly MethodInfo _updateSequencesForSeason;
        private readonly FieldInfo _timeoutMs;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer, IItemRepository itemRepository,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;

            UpdateLibraryPathsInScope();

            try
            {
                var embyProviders = EmbyVersionCompatibility.TryLoadAssembly("Emby.Providers");
                if (embyProviders == null)
                {
                    _logger.Error($"{nameof(FingerprintApi)} - Failed to load Emby.Providers assembly");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    return;
                }

                var audioFingerprintManager = EmbyVersionCompatibility.TryGetType(embyProviders, "Emby.Providers.Markers.AudioFingerprintManager");
                if (audioFingerprintManager != null)
                {
                    var audioFingerprintManagerConstructor = audioFingerprintManager.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[]
                        {
                            typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                            typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                            typeof(IServerApplicationHost)
                        }, null);
                    
                    if (audioFingerprintManagerConstructor != null)
                    {
                        _audioFingerprintManager = audioFingerprintManagerConstructor.Invoke(new object[]
                        {
                            fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                            jsonSerializer, serverApplicationHost
                        });
                        
                        _createTitleFingerprint = EmbyVersionCompatibility.FindCompatibleMethod(
                            audioFingerprintManager,
                            "CreateTitleFingerprint",
                            BindingFlags.Public | BindingFlags.Instance,
                            new[] { typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) },
                            new[] { typeof(Episode), typeof(IDirectoryService), typeof(CancellationToken) },
                            new[] { typeof(Episode), typeof(CancellationToken) }
                        );
                        
                        _getAllFingerprintFilesForSeason = audioFingerprintManager.GetMethod("GetAllFingerprintFilesForSeason",
                            BindingFlags.Public | BindingFlags.Instance);
                        
                        var updateMethods = audioFingerprintManager.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name == "UpdateSequencesForSeason")
                            .ToArray();
                        
                        if (updateMethods.Length > 0)
                        {
                            _updateSequencesForSeason = updateMethods.OrderByDescending(m => m.GetParameters().Length).First();
                            
                            if (Plugin.Instance.DebugMode)
                            {
                                var paramCount = _updateSequencesForSeason.GetParameters().Length;
                                _logger.Debug($"{nameof(FingerprintApi)}: Found UpdateSequencesForSeason with {paramCount} parameters");
                            }
                        }
                        
                        _timeoutMs = audioFingerprintManager.GetField("TimeoutMs",
                            BindingFlags.NonPublic | BindingFlags.Instance);

                        if (_audioFingerprintManager != null && _timeoutMs != null)
                        {
                            PatchTimeout(Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.MaxConcurrentCount);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"{nameof(FingerprintApi)} - Initialization failed: {e.Message}");
            }

            if (_audioFingerprintManager is null || _createTitleFingerprint is null ||
                _getAllFingerprintFilesForSeason is null || _updateSequencesForSeason is null || _timeoutMs is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug($"[FingerprintApi] ✗ Initialize incomplete - Manager={(_audioFingerprintManager != null ? "OK" : "NULL")}, CreateTitle={(_createTitleFingerprint != null ? "OK" : "NULL")}, GetAll={(_getAllFingerprintFilesForSeason != null ? "OK" : "NULL")}, UpdateSeq={(_updateSequencesForSeason != null ? "OK" : "NULL")}, TimeoutMs={(_timeoutMs != null ? "OK" : "NULL")}");
                }
            }
            else if (Plugin.Instance.IsModSupported)
            {
                PatchManager.ReversePatch(PatchTracker, _createTitleFingerprint, nameof(CreateTitleFingerprintStub));
                PatchManager.ReversePatch(PatchTracker, _getAllFingerprintFilesForSeason, nameof(GetAllFingerprintFilesForSeasonStub));
                PatchManager.ReversePatch(PatchTracker, _updateSequencesForSeason, nameof(UpdateSequencesForSeasonStub));
            }

            // ★ debug 日志：FingerprintApi 初始化完成状态
            if (Plugin.Instance.DebugMode)
            {
                _logger.Debug($"[FingerprintApi] ✓ Initialize done - Fallback={PatchTracker.FallbackPatchApproach}, Status={PatchTracker.Status}");
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<Tuple<string, bool>> CreateTitleFingerprintStub(object instance, Episode item,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    try { return CreateTitleFingerprintStub(_audioFingerprintManager, item, libraryOptions, directoryService, cancellationToken); }
                    catch { PatchTracker.FallbackPatchApproach = PatchApproach.Reflection; return CreateTitleFingerprint(item, directoryService, cancellationToken); }
                case PatchApproach.Reflection:
                    try { return _createTitleFingerprint.Invoke(_audioFingerprintManager, new object[] { item, libraryOptions, directoryService, cancellationToken }) as Task<Tuple<string, bool>> ?? Task.FromResult<Tuple<string, bool>>(null); }
                    catch { return Task.FromResult<Tuple<string, bool>>(null); }
                default:
                    return Task.FromResult<Tuple<string, bool>>(null);
            }
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken)
        {
            return CreateTitleFingerprint(item, new DirectoryService(_logger, _fileSystem), cancellationToken);
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<object> GetAllFingerprintFilesForSeasonStub(object instance, Season season,
            Episode[] episodes, LibraryOptions libraryOptions, IDirectoryService directoryService,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        private Task<object> GetAllFingerprintFilesForSeason(Season season, Episode[] episodes,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return GetAllFingerprintFilesForSeasonStub(_audioFingerprintManager, season, episodes, libraryOptions, directoryService, cancellationToken);
                case PatchApproach.Reflection:
                    return (Task<object>)_getAllFingerprintFilesForSeason.Invoke(_audioFingerprintManager, new object[] { season, episodes, libraryOptions, directoryService, cancellationToken });
                default:
                    throw new NotImplementedException();
            }
        }

        [HarmonyReversePatch]
        private static void UpdateSequencesForSeasonStub(object instance, Season season, object seasonFingerprintInfo,
            Episode episode, LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        private void UpdateSequencesForSeason(Season season, object seasonFingerprintInfo, Episode episode,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    UpdateSequencesForSeasonStub(_audioFingerprintManager, season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken);
                    break;
                case PatchApproach.Reflection:
                    var args = _updateSequencesForSeason.GetParameters().Length == 6 
                        ? new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken }
                        : new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService };
                    _updateSequencesForSeason.Invoke(_audioFingerprintManager, args);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = maxConcurrentCount * Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            _timeoutMs.SetValue(_audioFingerprintManager, newTimeout);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope(string currentScope)
        {
            var validLibraryIds = GetValidLibraryIds(currentScope);
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.All(id => id == "-1") ||
                             validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public void UpdateLibraryPathsInScope()
        {
            UpdateLibraryPathsInScope(Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.MarkerEnabledLibraryScope);
        }

        /// <summary>
        /// 关键字黑名单：集名或剧名命中关键字的集跳过声纹片头检测（如 NCOP、Menu、特典）。
        /// </summary>
        private List<Episode> FilterBlacklistKeywords(List<Episode> episodes)
        {
            var keywords = Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.FingerprintBlacklistKeywords
                .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToArray();

            if (keywords.Length == 0 || episodes.Count == 0) return episodes;

            var filtered = episodes.Where(e => !keywords.Any(k =>
                (e.Name?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.SeriesName?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();

            if (filtered.Count < episodes.Count)
            {
                _logger.Info($"IntroFingerprintExtract - {episodes.Count - filtered.Count} episode(s) skipped by keyword blacklist");
            }

            return filtered;
        }

        public HashSet<long> GetAllBlacklistSeasons()
        {
            var blacklistShowIds = Plugin.Instance.IntroSkipStore.GetOptions()
                .Fingerprint.FingerprintBlacklistShows.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => long.TryParse(part.Trim(), out var id) ? id : (long?)null)
                .Where(id => id.HasValue).Select(id => id.Value).ToArray();

            if (!blacklistShowIds.Any()) return new HashSet<long>();

            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = blacklistShowIds });
            var seasons = items.OfType<Season>().Select(s => s.InternalId).ToList();
            seasons.AddRange(items.OfType<Series>().SelectMany(series => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Season) },
                ParentWithPresentationUniqueKeyFromItemId = series.InternalId
            }).OfType<Season>().Select(s => s.InternalId)));

            return new HashSet<long>(seasons);
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u, IsFavorite = true, IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();
            var result = expanded.GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();
            var blackListSeasons = GetAllBlacklistSeasons();
            return result.Where(s => !blackListSeasons.Contains(s)).ToArray();
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.MarkerEnabledLibraryScope?
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds?.Contains("-1") == true;
            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(TaskConcurrencyOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites) resultItems = Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>().ToList();
                if (libraryIds is null || !libraryIds.Any() || libraryIds.Any(id => id != "-1"))
                {
                    var filteredItems = incomingItems.Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p))).ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = resultItems.Where(i => isModSupported || !i.IsShortcut).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();
            var blackListSeasons = GetAllBlacklistSeasons();
            return FilterBlacklistKeywords(
                FilterUnprocessed(resultItems.Where(e => !blackListSeasons.Contains(e.ParentId)).ToList()));
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().ImageCapture.EnableImageCapture;
            var results = items.Where(item => Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture) || IsExtractNeeded(item)).ToList();
            _logger.Info("IntroFingerprintExtract - Number of items: " + results.Count);
            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            return !Plugin.ChapterApi.HasIntro(item) && string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        // 判断声纹提取范围是否为"仅最爱"：非空且全部为 -1；空或 null 视为"所有启用 marker 检测的媒体库"
        private static bool IsFavoritesOnlyScope(string scope)
        {
            var ids = scope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToArray();
            return ids is { Length: > 0 } && ids.All(id => id == "-1");
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.MarkerEnabledLibraryScope;
            var itemsFingerprintQuery = new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Episode) }, Recursive = true, GroupByPresentationUniqueKey = false, HasPath = true, HasAudioStream = false };

            if (IsFavoritesOnlyScope(markerEnabledLibraryScope))
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            else
            {
                if (LibraryPathsInScope.Any()) itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                var blackListSeasons = GetAllBlacklistSeasons();
                if (blackListSeasons.Any()) itemsFingerprintQuery.ExcludeParentIds = blackListSeasons.ToArray();
            }

            return FilterBlacklistKeywords(_libraryManager.GetItemList(itemsFingerprintQuery)
                .Where(i => Plugin.Instance.IsModSupported || !i.IsShortcut).OfType<Episode>()
                .Where(e => !IsSpecialEpisode(e)).ToList());
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.MarkerEnabledLibraryScope;
            var introDetectionFingerprintMinutes = Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.IntroDetectionFingerprintMinutes;

            var itemsFingerprintQuery = new InternalItemsQuery {
                IncludeItemTypes = new[] { nameof(Episode) }, Recursive = true, GroupByPresentationUniqueKey = false,
                WithoutChapterMarkers = new[] { MarkerType.IntroStart }, MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false, HasAudioStream = true
            };

            if (IsFavoritesOnlyScope(markerEnabledLibraryScope))
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            else
            {
                if (LibraryPathsInScope.Any()) itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                var blackListSeasons = GetAllBlacklistSeasons();
                if (blackListSeasons.Any()) itemsFingerprintQuery.ExcludeParentIds = blackListSeasons.ToArray();
            }

            return FilterBlacklistKeywords(_libraryManager.GetItemList(itemsFingerprintQuery)
                .Where(i => Plugin.Instance.IsModSupported || !i.IsShortcut).OfType<Episode>()
                .Where(e => !IsSpecialEpisode(e)).ToList());
        }

        /// <summary>
        /// 特别篇（Season 0）不参与声纹片头提取：
        /// 它们没有正片那种稳定的片头，混进指纹库会拉高误检率（PRO 3.0.0.46 同类修复）。
        /// 仅在 ParentIndexNumber 明确为 0 时判定为特别篇，未知的不动。
        /// </summary>
        private static bool IsSpecialEpisode(Episode episode) =>
            episode.ParentIndexNumber.HasValue && episode.ParentIndexNumber.Value == 0;

        public void UpdateLibraryIntroDetectionFingerprintLength(int currentLength)
        {
            var libraries = _libraryManager.GetVirtualFolders().Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null).ToList();
            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;
                var effectiveMinutes = GetLibraryFingerprintMinutesOverride(library.ItemId) ?? currentLength;
                if (options.IntroDetectionFingerprintLength != effectiveMinutes && long.TryParse(library.ItemId, out var itemId))
                {
                    options.IntroDetectionFingerprintLength = effectiveMinutes;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        public void UpdateLibraryIntroDetectionFingerprintLength()
        {
            UpdateLibraryIntroDetectionFingerprintLength(Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.IntroDetectionFingerprintMinutes);
        }

        /// <summary>
        /// 解析 LibraryFingerprintMinutes（格式 libraryId:minutes，逗号分隔）中指定库的覆盖值，
        /// 未配置或格式非法返回 null（回退全局值）。值域钳制 2-20。
        /// </summary>
        public static int? GetLibraryFingerprintMinutesOverride(string libraryId)
        {
            var raw = Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.LibraryFingerprintMinutes;
            if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrEmpty(libraryId)) return null;

            foreach (var pair in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].Trim() == libraryId.Trim() &&
                    int.TryParse(parts[1].Trim(), out var minutes))
                {
                    return Math.Max(2, Math.Min(20, minutes));
                }
            }

            return null;
        }

        /// <summary>
        /// 取 season 所属媒体库的有效声纹探测时长（分库覆盖 > 全局值）。
        /// </summary>
        public int GetEffectiveFingerprintMinutes(Season season)
        {
            var collectionFolder = _libraryManager.GetCollectionFolders(season).FirstOrDefault();
            if (collectionFolder != null &&
                GetLibraryFingerprintMinutesOverride(collectionFolder.Id.ToString()) is { } minutes)
            {
                return minutes;
            }

            return Plugin.Instance.IntroSkipStore.GetOptions().Fingerprint.IntroDetectionFingerprintMinutes;
        }

#nullable enable
        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            var minutes = GetEffectiveFingerprintMinutes(season);
            var libraryOptions = _libraryManager.GetLibraryOptions(season);
            var directoryService = new DirectoryService(_logger, _fileSystem);

            var query = new InternalItemsQuery { GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false, MinRunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks, HasIntroDetectionFailure = false, HasAudioStream = true };
            var allEpisodes = season.GetEpisodes(query).Items.OfType<Episode>().ToArray();

            query.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var targetEpisodes = season.GetEpisodes(query).Items.OfType<Episode>().ToList();

            var seasonFingerprintInfo = await GetAllFingerprintFilesForSeason(season, allEpisodes, libraryOptions, directoryService, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < targetEpisodes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateSequencesForSeason(season, seasonFingerprintInfo, targetEpisodes[i], libraryOptions, directoryService, cancellationToken);
                progress?.Report((double)(i + 1) / targetEpisodes.Count);
            }
            progress?.Report(1.0);
        }
#nullable restore
    }
}