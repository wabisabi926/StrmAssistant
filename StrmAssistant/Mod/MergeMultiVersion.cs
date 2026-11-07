using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.Provider;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.VersionMergeOptions;

namespace StrmAssistant.Mod
{
    public class MergeMultiVersion : PatchBase<MergeMultiVersion>
    {
        // 库选项按库缓存：合并时可对上万个剧集调用 GetAllCollectionFolders，
        // 每个都走 GetLibraryOptions(item) 会重复解析库归属并读库配置（PRO 3.0.0.57 也做了同类优化）。
        // 只缓存“库 → 是否自动分组合并”这一位，条目级缓存会随剧集数量膨胀，收益也远小于此。
        private static readonly ConcurrentDictionary<long, bool> SeriesGroupingCache =
            new ConcurrentDictionary<long, bool>();

        /// <summary>库选项变化（开关切换、自动分组被改写）后清空，避免读到旧的布尔值。</summary>
        internal static void ClearSeriesGroupingCache() => SeriesGroupingCache.Clear();

        private static MethodInfo _isEligibleForMultiVersion;
        private static MethodInfo _canRefreshImage;
        private static MethodInfo _addLibrariesToPresentationUniqueKey;
        private static MethodInfo _getRefreshOptions;
        private static MethodInfo _getVideosGroupedByVersion;

        private static int _folderGroupingLimit = 8;

        public static readonly AsyncLocal<BaseItem[]> CurrentAllCollectionFolders = new AsyncLocal<BaseItem[]>();

        public MergeMultiVersion()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().Merge.MergeMultiVersion)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                // 加载 Emby.Naming
                var namingAssembly = EmbyVersionCompatibility.TryLoadAssembly("Emby.Naming");
                if (namingAssembly != null)
                {
                    var videoListResolverType = EmbyVersionCompatibility.TryGetType(namingAssembly, "Emby.Naming.Video.VideoListResolver");
                    if (videoListResolverType != null)
                    {
                        _isEligibleForMultiVersion = videoListResolverType.GetMethod("IsEligibleForMultiVersion",
                            BindingFlags.Static | BindingFlags.NonPublic);

                        _getVideosGroupedByVersion = videoListResolverType.GetMethod("GetVideosGroupedByVersion",
                            BindingFlags.Instance | BindingFlags.NonPublic);

                        if (_isEligibleForMultiVersion != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: IsEligibleForMultiVersion method found");
                        }

                        if (_getVideosGroupedByVersion != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: GetVideosGroupedByVersion method found");
                        }
                    }
                }

                // 加载 Emby.Providers
                var embyProviders = EmbyVersionCompatibility.TryLoadAssembly("Emby.Providers");
                if (embyProviders != null)
                {
                    var providerManager = EmbyVersionCompatibility.TryGetType(embyProviders, "Emby.Providers.Manager.ProviderManager");
                    if (providerManager != null)
                    {
                        _canRefreshImage = providerManager.GetMethod("CanRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
                        
                        if (_canRefreshImage != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: CanRefresh method found");
                        }
                    }
                }

                // 获取 Series 的方法
                _addLibrariesToPresentationUniqueKey = typeof(Series).GetMethod("AddLibrariesToPresentationUniqueKey",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (_addLibrariesToPresentationUniqueKey != null && Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("MergeMultiVersion: AddLibrariesToPresentationUniqueKey method found");
                }

                // 加载 Emby.Api
                var embyApi = EmbyVersionCompatibility.TryLoadAssembly("Emby.Api");
                if (embyApi != null)
                {
                    var itemRefreshService = EmbyVersionCompatibility.TryGetType(embyApi, "Emby.Api.ItemRefreshService");
                    if (itemRefreshService != null)
                    {
                        _getRefreshOptions = itemRefreshService.GetMethod("GetRefreshOptions", 
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        
                        if (_getRefreshOptions != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: GetRefreshOptions method found");
                        }
                    }
                }

                // 验证所有必需的组件
                var missingComponents = new List<string>();
                if (_isEligibleForMultiVersion == null) missingComponents.Add("IsEligibleForMultiVersion");
                if (_canRefreshImage == null) missingComponents.Add("CanRefresh");
                if (_addLibrariesToPresentationUniqueKey == null) missingComponents.Add("AddLibrariesToPresentationUniqueKey");
                if (_getRefreshOptions == null) missingComponents.Add("GetRefreshOptions");

                if (missingComponents.Any())
                {
                    Plugin.Instance.Logger.Warn($"MergeMultiVersion: Missing components - {string.Join(", ", missingComponents)}");
                    Plugin.Instance.Logger.Warn("Multi-version merge may not work fully on this Emby version");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(MergeMultiVersion),
                        false,
                        $"{missingComponents.Count} required methods not found");
                }
                else
                {
                    Plugin.Instance.Logger.Info("MergeMultiVersion: All components loaded successfully");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(MergeMultiVersion),
                        true,
                        "All required components available");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error($"MergeMultiVersion initialization failed: {ex.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(MergeMultiVersion),
                    false,
                    "Initialization error - feature may be limited");
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _isEligibleForMultiVersion,
                prefix: nameof(IsEligibleForMultiVersionPrefix));
            PatchUnpatch(PatchTracker, apply, _canRefreshImage, prefix: nameof(CanRefreshImagePrefix));
            PatchUnpatch(PatchTracker, apply, _addLibrariesToPresentationUniqueKey,
                prefix: nameof(AddLibrariesToPresentationUniqueKeyPrefix));
            PatchUnpatch(PatchTracker, apply, _getRefreshOptions, postfix: nameof(GetRefreshOptionsPostfix));
            ApplyFolderGroupingLimit(apply);
        }

        /// <summary>
        /// 同目录版本合并上限（Emby 默认 8）：仅当配置值 != 8 时，用 Transpiler 把
        /// VideoListResolver.GetVideosGroupedByVersion 中 videos.Count &lt;= 8 的常量替换为静态字段。
        /// 字段随配置保存即时更新，无需重打补丁。
        /// </summary>
        private static void ApplyFolderGroupingLimit(bool apply)
        {
            _folderGroupingLimit = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Merge.FolderVideoGroupingLimit;

            if (_getVideosGroupedByVersion == null)
            {
                if (_folderGroupingLimit != 8)
                {
                    Plugin.Instance.Logger.Warn(
                        "MergeMultiVersion: GetVideosGroupedByVersion not found - folder grouping limit ignored");
                }

                return;
            }

            if (_folderGroupingLimit != 8)
            {
                PatchUnpatch(Instance.PatchTracker, apply, _getVideosGroupedByVersion,
                    transpiler: nameof(FolderGroupingLimitTranspiler));
            }
        }

        /// <summary>
        /// 配置变更时由 ExperienceEnhanceOptionsStore 调用：更新静态上限值，
        /// 并按需补上/移除 Transpiler（8 = Emby 原生行为，无需补丁）。
        /// </summary>
        public static void UpdateFolderGroupingLimit(int limit)
        {
            _folderGroupingLimit = Math.Max(1, limit);

            if (_getVideosGroupedByVersion == null || Instance.PatchTracker.Status != PatchStatus.Applied)
            {
                return;
            }

            if (_folderGroupingLimit != 8)
            {
                PatchUnpatch(Instance.PatchTracker, true, _getVideosGroupedByVersion,
                    transpiler: nameof(FolderGroupingLimitTranspiler));
            }
            else
            {
                PatchUnpatch(Instance.PatchTracker, false, _getVideosGroupedByVersion,
                    transpiler: nameof(FolderGroupingLimitTranspiler));
            }
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> FolderGroupingLimitTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();

            if (_getVideosGroupedByVersion == null) return code;

            var videosParameter = _getVideosGroupedByVersion.GetParameters()[0].ParameterType;
            var countGetter = videosParameter.GetProperty("Count")?.GetGetMethod();
            var limitField = AccessTools.Field(typeof(MergeMultiVersion), nameof(_folderGroupingLimit));

            if (countGetter == null || limitField == null) return code;

            var replaced = 0;
            for (var i = 0; i < code.Count - 1; i++)
            {
                // 目标模式：callvirt get_Count 后紧跟常量 8（覆盖 ldc.i4.8 / ldc.i4.s 8 / ldc.i4 8 三种编码形态）
                if (!code[i].Calls(countGetter)) continue;

                var next = code[i + 1];
                var isLimitConstant =
                    next.opcode == OpCodes.Ldc_I4_8 ||
                    (next.opcode == OpCodes.Ldc_I4_S && next.operand is byte b && b == 8) ||
                    (next.opcode == OpCodes.Ldc_I4 && next.operand is int n && n == 8);

                if (!isLimitConstant) continue;

                code[i + 1] = new CodeInstruction(OpCodes.Ldsfld, limitField)
                {
                    labels = next.labels
                };
                replaced++;
            }

            if (replaced > 0)
            {
                Plugin.Instance.Logger.Info(
                    $"MergeMultiVersion: FolderGroupingLimit transpiler replaced {replaced} constant(s)");
            }
            else
            {
                // 静默不替换比报错更危险：任何形态变化都必须暴露
                Plugin.Instance.Logger.Warn(
                    "MergeMultiVersion: FolderGroupingLimit transpiler matched 0 instructions - " +
                    "Emby IL layout may have changed, folder grouping limit patch is INACTIVE");
            }

            return code;
        }

        [HarmonyPrefix]
        private static bool IsEligibleForMultiVersionPrefix(string folderName, string testFilename, ref bool __result)
        {
            __result = string.Equals(folderName, Path.GetFileName(Path.GetDirectoryName(testFilename)),
                StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static BaseItem[] GetAllCollectionFolders(Series series)
        {
            if (!(series.HasProviderId(MetadataProviders.Tmdb) || series.HasProviderId(MetadataProviders.Imdb) ||
                  series.HasProviderId(MetadataProviders.Tvdb)))
            {
                return Array.Empty<BaseItem>();
            }

            var allSeries = BaseItem.LibraryManager.GetItemList(new InternalItemsQuery
            {
                EnableTotalRecordCount = false,
                Recursive = false,
                ExcludeItemIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { nameof(Series) },
                AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(MetadataProviders.Tmdb.ToString(),
                        series.GetProviderId(MetadataProviders.Tmdb)),
                    new KeyValuePair<string, string>(MetadataProviders.Imdb.ToString(),
                        series.GetProviderId(MetadataProviders.Imdb)),
                    new KeyValuePair<string, string>(MetadataProviders.Tvdb.ToString(),
                        series.GetProviderId(MetadataProviders.Tvdb))
                }
            }).Concat(new[] { series }).ToList();

            var collectionFolders = new HashSet<BaseItem>();

            foreach (var item in allSeries)
            {
                var libraries = BaseItem.LibraryManager.GetCollectionFolders(item);
                if (libraries == null || libraries.Length == 0) continue;

                // 同一部剧可能同时挂在多个库上；取 InternalId 最小者做代表，保证每次判定一致
                var primary = libraries.OrderBy(l => l.InternalId).First();

                if (!IsSeriesGroupingEnabled(primary)) continue;

                foreach (var library in libraries)
                {
                    collectionFolders.Add(library);
                }
            }

            return collectionFolders.OrderBy(c => c.InternalId).ToArray();
        }

        private static bool IsSeriesGroupingEnabled(BaseItem library)
        {
            return SeriesGroupingCache.GetOrAdd(library.InternalId, _ =>
                BaseItem.LibraryManager.GetLibraryOptions(library)?.EnableAutomaticSeriesGrouping ?? false);
        }

        [HarmonyPrefix]
        private static void CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions)
        {
            if (CurrentAllCollectionFolders.Value != null) return;

            if (item.Parent is null && item.ExtraType is null) return;

            if (item is Series series && Plugin.Instance.ExperienceEnhanceStore.GetOptions().Merge.MergeSeriesPreference ==
                MergeSeriesScopeOption.GlobalScope)
            {
                CurrentAllCollectionFolders.Value = GetAllCollectionFolders(series);
            }
        }

        [HarmonyPrefix]
        private static bool AddLibrariesToPresentationUniqueKeyPrefix(Series __instance, string key,
            ref BaseItem[] collectionFolders, LibraryOptions libraryOptions, ref string __result)
        {
            if (CurrentAllCollectionFolders.Value != null)
            {
                if (CurrentAllCollectionFolders.Value.Length > 1)
                {
                    collectionFolders = CurrentAllCollectionFolders.Value;
                }

                CurrentAllCollectionFolders.Value = null;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void GetRefreshOptionsPostfix(IReturnVoid request, MetadataRefreshOptions __result)
        {
            var id = Traverse.Create(request).Property("Id").GetValue<string>();
            var item = BaseItem.LibraryManager.GetItemById(id);

            if (item is Series || item is Season)
            {
                var series = item as Series ?? (item as Season).Series;
                var seriesTmdbId = series?.GetProviderId(MetadataProviders.Tmdb);
                var episodeGroupId = series?.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();

                var itemsToRefresh = BaseItem.LibraryManager.GetItemList(new InternalItemsQuery
                {
                    PresentationUniqueKey = item.PresentationUniqueKey,
                    ExcludeItemIds = new[] { item.InternalId }
                });

                foreach (var alt in itemsToRefresh)
                {
                    if (!string.IsNullOrEmpty(episodeGroupId))
                    {
                        var altSeries = alt as Series ?? (alt as Season)?.Series;

                        if (altSeries != null)
                        {
                            var altSeriesTmdbId = altSeries.GetProviderId(MetadataProviders.Tmdb);
                            var altEpisodeGroupId = altSeries.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName);
                            if (string.IsNullOrEmpty(altEpisodeGroupId) && !string.IsNullOrEmpty(seriesTmdbId) &&
                                !string.IsNullOrEmpty(altSeriesTmdbId) && string.Equals(seriesTmdbId, altSeriesTmdbId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                alt.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, episodeGroupId);
                                alt.UpdateToRepository(ItemUpdateType.MetadataEdit);
                            }
                        }
                    }

                    BaseItem.ProviderManager.QueueRefresh(alt.InternalId, __result, RefreshPriority.Normal, true);
                }
            }
        }
    }
}
