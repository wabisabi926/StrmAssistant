using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.ItemDisplayOptions;
using static StrmAssistant.Options.LibraryOrderOptions;

namespace StrmAssistant.Options.Store
{
    public class ExperienceEnhanceOptionsStore : SimpleFileStore<ExperienceEnhanceOptions>
    {
        private readonly ILogger _logger;

        private bool _currentSuppressOnOptionsSaved;

        public ExperienceEnhanceOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            // 分组化改造：设置项由平铺改为 4 个分组嵌套。先把旧配置就地迁移为嵌套结构，
            // 否则旧键会被反序列化忽略、用户设置静默回退为默认值。
            ExperienceEnhanceOptionsMigration.MigrateIfNeeded(OptionsFilePath,
                applicationHost.Resolve<MediaBrowser.Model.Serialization.IJsonSerializer>(), logger);

            // 第二次迁移：把旧的「界面功能」整组拆成 条目显示 / 媒体库与排序 / 搜索与日志
            ExperienceEnhanceOptionsMigration.MigrateUiGroupsIfNeeded(OptionsFilePath,
                applicationHost.Resolve<MediaBrowser.Model.Serialization.IJsonSerializer>(), logger);

            FileSaving += OnFileSaving;
            FileSaved += OnFileSaved;
        }
        
        public ExperienceEnhanceOptions ExperienceEnhanceOptions => GetOptions();

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SetOptions(ExperienceEnhanceOptions);
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is ExperienceEnhanceOptions options)
            {
                if (string.IsNullOrEmpty(options.ItemDisplay.HidePersonPreference))
                {
                    options.ItemDisplay.HidePersonPreference = HidePersonOption.NoImage.ToString();
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(ExperienceEnhanceOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(VersionMergeOptions.MergeMultiVersion)))
                {
                    if (options.IsModSupported)
                    {
                        if (options.Merge.MergeMultiVersion)
                        {
                            PatchManager.MergeMultiVersion.Patch();
                        }
                        else
                        {
                            PatchManager.MergeMultiVersion.Unpatch();
                        }
                    }
                }

                if (options.Merge.MergeMultiVersion) Plugin.LibraryApi.EnsureLibraryEnabledAutomaticSeriesGrouping();

                if (changedProperties.Contains(nameof(VersionMergeOptions.FolderVideoGroupingLimit)))
                {
                    if (options.Merge.MergeMultiVersion && options.IsModSupported)
                    {
                        MergeMultiVersion.UpdateFolderGroupingLimit(options.Merge.FolderVideoGroupingLimit);
                    }
                }

                // UI 注入选项变更：重新生成前端注入字符串，下次请求即生效
                if (changedProperties.Contains(nameof(ItemDisplayOptions.ReplaceUnplayedWithTotal)) ||
                    changedProperties.Contains(nameof(LibraryOrderOptions.ShowLibraryItemCount)) ||
                    changedProperties.Contains(nameof(SearchLogOptions.ReverseLogLines)) ||
                    changedProperties.Contains(nameof(SearchLogOptions.BlockSearchRecommend)))
                {
                    Web.Helper.ShortcutMenuHelper.Refresh();
                }

                if (changedProperties.Contains(nameof(ExperiencePlaybackOptions.OptimizeSubtitle)))
                {
                    if (options.Playback.OptimizeSubtitle && options.IsModSupported)
                    {
                        PatchManager.OptimizeSubtitle.Patch();
                    }
                    else
                    {
                        PatchManager.OptimizeSubtitle.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperiencePlaybackOptions.LibraryScopePlayProgress)))
                {
                    // 重启生效：GetUserDataKey 方法体极小，JIT 可能已内联，
                    // 运行中 Patch/Unpatch 会出现「部分调用点不生效」。
                    Mod.LibraryScopePlayProgress.ClearCache();

                    if (options.Playback.LibraryScopePlayProgress && options.IsModSupported)
                    {
                        PatchManager.LibraryScopePlayProgress.Patch();
                    }
                    else
                    {
                        PatchManager.LibraryScopePlayProgress.Unpatch();
                    }

                    Plugin.Instance.ApplicationHost.NotifyPendingRestart();

                    _logger.Warn(options.Playback.LibraryScopePlayProgress
                        ? "LibraryScopePlayProgress enabled - restart Emby, then run 'Rebuild Play Progress Keys'. " +
                          "Existing play progress/favorites will be invisible under the new key until the task runs."
                        : "LibraryScopePlayProgress disabled - restart Emby, then run 'Rebuild Play Progress Keys' to restore the original keys.");
                }

                if (changedProperties.Contains(nameof(ExperienceNotificationOptions.EnhanceNotificationSystem)))
                {
                    if (options.Notification.EnhanceNotificationSystem)
                    {
                        PatchManager.EnhanceNotificationSystem.Patch();
                    }
                    else
                    {
                        PatchManager.EnhanceNotificationSystem.Unpatch();
                    }
                }
                
                if (changedProperties.Contains(nameof(ExperiencePlaybackOptions.EnableDeepDelete)))
                {
                    if (options.Playback.EnableDeepDelete)
                    {
                        PatchManager.EnableDeepDelete.Patch();
                    }
                    else
                    {
                        PatchManager.EnableDeepDelete.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.Display.EnableServerSideVersionLabel)) ||
                    changedProperties.Contains(nameof(ExperienceEnhanceOptions.Display.EnableMultiVersionEnhance)))
                {
                    // 两者是与关系，任一变化都要重算补丁状态；补丁只改 MediaSourceInfo.Name，可热切换
                    if (options.Display.EnableMultiVersionEnhance && options.Display.EnableServerSideVersionLabel)
                    {
                        PatchManager.ServerSideVersionLabel.Patch();
                    }
                    else
                    {
                        PatchManager.ServerSideVersionLabel.Unpatch();
                    }

                    _logger.Info("EnableServerSideVersionLabel is set to {0}",
                        options.Display.EnableServerSideVersionLabel);
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.ItemDisplay.HidePersonNoImage)))
                {
                    if (options.ItemDisplay.HidePersonNoImage)
                    {
                        PatchManager.HidePersonNoImage.Patch();
                    }
                    else
                    {
                        PatchManager.HidePersonNoImage.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.LibraryOrder.EnforceLibraryOrder)))
                {
                    if (options.LibraryOrder.EnforceLibraryOrder)
                    {
                        PatchManager.EnforceLibraryOrder.Patch();
                    }
                    else
                    {
                        PatchManager.EnforceLibraryOrder.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.LibraryOrder.EnforceUserPreferencesScope)) ||
                    changedProperties.Contains(nameof(ExperienceEnhanceOptions.LibraryOrder.EnforceLibraryOrder)))
                {
                    var scope = options.LibraryOrder.EnforceUserPreferencesScope ?? string.Empty;
                    var seriesDisplayEnabled =
                        options.LibraryOrder.EnforceLibraryOrder &&
                        scope.Contains(nameof(EnforceUserDisplayOption.SeriesDisplay), StringComparison.OrdinalIgnoreCase);

                    if (seriesDisplayEnabled)
                    {
                        PatchManager.EnforceUserPreferences.Patch();
                    }
                    else
                    {
                        PatchManager.EnforceUserPreferences.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.ItemDisplay.BeautifyMissingMetadata)))
                {
                    if (options.ItemDisplay.BeautifyMissingMetadata)
                    {
                        PatchManager.BeautifyMissingMetadata.Patch();
                    }
                    else
                    {
                        PatchManager.BeautifyMissingMetadata.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.ItemDisplay.EnhanceMissingEpisodes)))
                {
                    if (options.ItemDisplay.EnhanceMissingEpisodes)
                    {
                        PatchManager.EnhanceMissingEpisodes.Patch();
                    }
                    else
                    {
                        PatchManager.EnhanceMissingEpisodes.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.LibraryOrder.NoBoxsetsAutoCreation)))
                {
                    if (options.LibraryOrder.NoBoxsetsAutoCreation)
                    {
                        PatchManager.NoBoxsetsAutoCreation.Patch();
                    }
                    else
                    {
                        PatchManager.NoBoxsetsAutoCreation.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.LibraryOrder.OptimizeItemOrderPreferences)) ||
                    changedProperties.Contains(nameof(ExperienceEnhanceOptions.LibraryOrder.OptimizeMediaItemOrder)))
                {                    var prefs = options.LibraryOrder.OptimizeItemOrderPreferences ?? string.Empty;
                    var orderModuleOn =
                        prefs.Contains(nameof(ItemOrderOption.ReverseSeasonOrder), StringComparison.OrdinalIgnoreCase) ||
                        prefs.Contains(nameof(ItemOrderOption.ReverseEpisodeOrder), StringComparison.OrdinalIgnoreCase) ||
                        prefs.Contains(nameof(ItemOrderOption.CollectionPerItemDateCreated), StringComparison.OrdinalIgnoreCase);

                    if (orderModuleOn)
                    {
                        PatchManager.OptimizeItemOrder.Patch();
                    }
                    else
                    {
                        PatchManager.OptimizeItemOrder.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.ItemDisplay.ShowCollectionsFromSeasons)))
                {
                    if (options.ItemDisplay.ShowCollectionsFromSeasons)
                    {
                        PatchManager.ShowCollectionsFromSeasons.Patch();
                    }
                    else
                    {
                        PatchManager.ShowCollectionsFromSeasons.Unpatch();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is ExperienceEnhanceOptions options)
            {
                var suppress = _currentSuppressOnOptionsSaved;

                if (!suppress)
                {
                    _logger.Info("MergeMultiVersion is set to {0}", options.Merge.MergeMultiVersion);
                    _logger.Info("MergeMoviesPreference is set to {0}", options.Merge.MergeMoviesPreference.GetDescription());
                    _logger.Info("MergeSeriesPreference is set to {0}", options.Merge.MergeSeriesPreference.GetDescription());
                    _logger.Info("EnhanceNotificationSystem is set to {0}", options.Notification.EnhanceNotificationSystem);
                    _logger.Info("EnableDeepDelete is set to {0}", options.Playback.EnableDeepDelete);
                    _logger.Info("HidePersonNoImage is set to {0}", options.ItemDisplay.HidePersonNoImage);
                    var hidePersonPreference = string.Join(", ",
                        options.ItemDisplay.HidePersonPreference
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s =>
                                Enum.TryParse(s.Trim(), true, out HidePersonOption option)
                                    ? option.GetDescription()
                                    : null)
                            .Where(d => d != null) ?? Enumerable.Empty<string>());
                    _logger.Info("HidePersonPreference is set to {0}", hidePersonPreference);
                    _logger.Info("EnforceLibraryOrder is set to {0}", options.LibraryOrder.EnforceLibraryOrder);
                    _logger.Info("BeautifyMissingMetadata is set to {0}",
                        options.ItemDisplay.BeautifyMissingMetadata);
                    _logger.Info("EnhanceMissingEpisodes is set to {0}",
                        options.ItemDisplay.EnhanceMissingEpisodes);
                    _logger.Info("NoBoxsetsAutoCreation is set to {0}",
                        options.LibraryOrder.NoBoxsetsAutoCreation);

                    _logger.Info("EnableAutoMarkWatched is set to {0}", options.Playback.EnableAutoMarkWatched);
                    _logger.Info("AutoMarkWatchedThreshold is set to {0}", options.Playback.AutoMarkWatchedThreshold);
                    _logger.Info("AutoMarkWatchedExcludeFavorites is set to {0}", options.Playback.AutoMarkWatchedExcludeFavorites);
                    _logger.Info("EnableMultiVersionEnhance is set to {0}", options.Display.EnableMultiVersionEnhance);
                    _logger.Info("FolderVideoGroupingLimit is set to {0}", options.Merge.FolderVideoGroupingLimit);

                    // 系列合并仅 GlobalScope 生效的可见提示（参考 Mod/MergeMultiVersion.cs 行 217-218）
                    if (options.Merge.MergeMultiVersion &&
                        options.Merge.MergeSeriesPreference == VersionMergeOptions.MergeSeriesScopeOption.LibraryScope)
                    {
                        _logger.Warn("MergeMultiVersion 已开启但 MergeSeriesPreference 为 LibraryScope：跨媒体库的同一系列版本合并仅在 GlobalScope 下生效。若需跨库按系列合并，请选择 GlobalScope。");
                    }
                }

                if (suppress) _currentSuppressOnOptionsSaved = false;
            }
        }
    }
}
