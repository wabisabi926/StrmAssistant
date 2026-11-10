using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Options.Store
{
    public class IntroSkipOptionsStore : SimpleFileStore<IntroSkipOptions>
    {
        private readonly ILogger _logger;

        public IntroSkipOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            // 分组化改造：设置项由平铺改为 3 个分组嵌套。先把旧配置就地迁移为嵌套结构，
            // 否则旧键会被反序列化忽略、用户设置静默回退为默认值。
            if (OptionsSchema.ShouldRunMigrations)
            {
                IntroSkipOptionsMigration.MigrateIfNeeded(OptionsFilePath,
                    applicationHost.Resolve<MediaBrowser.Model.Serialization.IJsonSerializer>(), logger);
            }

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public IntroSkipOptions IntroSkipOptions => GetOptions();

        // 过滤 scope 中不在可选列表内的值；可选列表为空(未初始化)时保留用户原值并告警，避免静默清空
        private string FilterScope(string rawScope, List<EditorSelectOption> allowedList, string scopeName)
        {
            var inputIds = rawScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToArray() ?? Array.Empty<string>();

            if (allowedList.Count == 0 && inputIds.Length > 0)
            {
                _logger.Warn("{0}: 可选项列表为空（可能未调用 Initialize），已保留用户原值: {1}", scopeName, rawScope);
                return rawScope;
            }

            var validValues = new HashSet<string>(allowedList.Select(o => o.Value));
            var kept = inputIds.Where(validValues.Contains).ToArray();
            var dropped = inputIds.Except(kept).ToArray();
            if (dropped.Length > 0)
                _logger.Warn("{0}: 过滤掉 {1} 个不在可选范围内的值: {2}", scopeName, dropped.Length, string.Join(", ", dropped));

            return string.Join(",", kept);
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is IntroSkipOptions options)
            {
                options.Playback.LibraryScope = FilterScope(options.Playback.LibraryScope, options.Playback.LibraryList,
                    nameof(IntroSkipPlaybackOptions.LibraryScope));

                options.Playback.UserScope = FilterScope(options.Playback.UserScope, options.Playback.UserList,
                    nameof(IntroSkipPlaybackOptions.UserScope));

                var isModSupported = options.IsModSupported;
                options.Fingerprint.MarkerEnabledLibraryScope = isModSupported
                    ? options.Fingerprint.MarkerEnabledLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Contains("-1") == true
                        ? "-1"
                        : FilterScope(options.Fingerprint.MarkerEnabledLibraryScope, options.Fingerprint.MarkerEnabledLibraryList,
                            nameof(IntroSkipFingerprintOptions.MarkerEnabledLibraryScope))
                    : string.Empty;

                if (isModSupported)
                {
                    var blacklistShowIds = options.Fingerprint.FingerprintBlacklistShows
                        .Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => long.TryParse(part.Trim(), out var id) ? id : (long?)null)
                        .Where(id => id.HasValue)
                        .Select(id => id.Value)
                        .ToArray();

                    var items = Plugin.LibraryApi.GetItemsByIds(blacklistShowIds);

                    options.Fingerprint.FingerprintBlacklistShowsResult.Clear();

                    foreach (var item in items.Where(item => item is Series || item is Season))
                    {
                        var listItem = new GenericListItem();

                        if (item is Series series)
                        {
                            listItem.PrimaryText = $"{series.Name} ({series.InternalId}) - {series.ContainingFolderPath}";
                        }
                        else if (item is Season season)
                        {
                            listItem.PrimaryText =
                                $"{season.SeriesName} - {season.Name} ({season.InternalId}) - {season.ContainingFolderPath}";
                        }

                        listItem.Icon = IconNames.block;
                        listItem.IconMode = ItemListIconMode.SmallRegular;

                        options.Fingerprint.FingerprintBlacklistShowsResult.Add(listItem);
                    }
                }
                else
                {
                    options.Fingerprint.FingerprintBlacklistShows = string.Empty;
                    options.Fingerprint.FingerprintBlacklistShowsResult.Clear();
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(IntroSkipOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(IntroSkipPlaybackOptions.EnableIntroSkip)))
                {
                    if (options.Playback.EnableIntroSkip)
                    {
                        Plugin.PlaySessionMonitor.Initialize();
                    }
                    else
                    {
                        Plugin.PlaySessionMonitor.Dispose();
                    }
                }

                if (changedProperties.Contains(nameof(IntroSkipPlaybackOptions.IntroSkipPreferences)))
                {
                    UpdateIntroSkipPreferences(options.Playback.IntroSkipPreferences);
                }

                if (changedProperties.Contains(nameof(IntroSkipPlaybackOptions.LibraryScope)) ||
                    changedProperties.Contains(nameof(IntroSkipPlaybackOptions.EnableIntroSkip)))
                {
                    if (options.Playback.EnableIntroSkip)
                        Plugin.PlaySessionMonitor.UpdateLibraryPathsInScope(options.Playback.LibraryScope);
                }

                if (changedProperties.Contains(nameof(IntroSkipPlaybackOptions.UserScope)) ||
                    changedProperties.Contains(nameof(IntroSkipPlaybackOptions.EnableIntroSkip)))
                {
                    if (options.Playback.EnableIntroSkip)
                        Plugin.PlaySessionMonitor.UpdateUsersInScope(options.Playback.UserScope);
                }

                if (changedProperties.Contains(nameof(IntroSkipPlaybackOptions.ClientScope)) ||
                    changedProperties.Contains(nameof(IntroSkipPlaybackOptions.EnableIntroSkip)))
                {
                    if (options.Playback.EnableIntroSkip)
                        Plugin.PlaySessionMonitor.UpdateClientInScope(options.Playback.ClientScope);
                }

                if (changedProperties.Contains(nameof(IntroSkipFingerprintOptions.UnlockIntroSkip)))
                {
                    if (options.Fingerprint.UnlockIntroSkip)
                    {
                        if (options.IsModSupported) PatchManager.UnlockIntroSkip.Patch();
                    }
                    else
                    {
                        if (options.IsModSupported) PatchManager.UnlockIntroSkip.Unpatch();
                        Plugin.FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength(10);
                    }
                }

                if (options.Fingerprint.UnlockIntroSkip)
                {
                    Plugin.FingerprintApi.UpdateLibraryPathsInScope(options.Fingerprint.MarkerEnabledLibraryScope);
                    Plugin.FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength(options.Fingerprint.IntroDetectionFingerprintMinutes);
                }

                if (changedProperties.Contains(nameof(IntroSkipFingerprintOptions.LibraryFingerprintMinutes)) &&
                    options.Fingerprint.UnlockIntroSkip)
                {
                    Plugin.FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength(options.Fingerprint.IntroDetectionFingerprintMinutes);
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is IntroSkipOptions options)
            {
                _logger.Info("UnlockIntroSkip is set to {0}", options.Fingerprint.UnlockIntroSkip);
                _logger.Info("IntroDetectionFingerprintMinutes is set to {0}",
                    options.Fingerprint.IntroDetectionFingerprintMinutes);
                _logger.Info("LibraryFingerprintMinutes is set to {0}",
                    options.Fingerprint.LibraryFingerprintMinutes);

                var markerEnabledLibraryScope = string.Join(", ",
                    options.Fingerprint.MarkerEnabledLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v =>
                            options.Fingerprint.MarkerEnabledLibraryList
                                .FirstOrDefault(option => option.Value == v)?.Name) ?? Enumerable.Empty<string>());
                _logger.Info("MarkerEnabledLibraryScope is set to {0}",
                    string.IsNullOrEmpty(markerEnabledLibraryScope)
                        ? options.Fingerprint.MarkerEnabledLibraryList.Any(o => o.Value != "-1") ? "ALL" : "EMPTY"
                        : markerEnabledLibraryScope);

                _logger.Info("FingerprintBlacklistShows is set to {0}", options.Fingerprint.FingerprintBlacklistShows);

                _logger.Info("EnableIntroSkip is set to {0}", options.Playback.EnableIntroSkip);
                _logger.Info("MaxIntroDurationSeconds is set to {0}", options.Playback.MaxIntroDurationSeconds);
                _logger.Info("MaxCreditsDurationSeconds is set to {0}", options.Playback.MaxCreditsDurationSeconds);
                _logger.Info("MinOpeningPlotDurationSeconds is set to {0}",
                    options.Playback.MinOpeningPlotDurationSeconds);

                var intoSkipLibraryScope = string.Join(", ",
                    options.Playback.LibraryScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.Playback.LibraryList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                _logger.Info("IntroSkip - LibraryScope is set to {0}",
                    string.IsNullOrEmpty(intoSkipLibraryScope) ? "ALL" : intoSkipLibraryScope);

                var introSkipUserScope = string.Join(", ",
                    options.Playback.UserScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.Playback.UserList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                _logger.Info("IntroSkip - UserScope is set to {0}",
                    string.IsNullOrEmpty(introSkipUserScope) ? "ALL" : introSkipUserScope);

                _logger.Info("IntroSkip - ClientScope is set to {0}", options.Playback.ClientScope);

                var introSkipPreferences = GetSelectedIntroSkipPreferenceDescription();
                _logger.Info("IntroSkip - Preferences is set to {0}",
                    string.IsNullOrEmpty(introSkipPreferences) ? "EMPTY" : introSkipPreferences);

                _logger.Info("EnableIntroDbScrape is set to {0}", options.Scrape.EnableIntroDbScrape);
                _logger.Info("EnableAutomaticIntroScraping is set to {0}",
                    options.Scrape.EnableAutomaticIntroScraping);
                _logger.Info("IntroDbApiKey is set to {0}",
                    !string.IsNullOrEmpty(options.Scrape.IntroDbApiKey) ? "CONFIGURED" : "EMPTY");
                _logger.Info("FingerprintBlacklistKeywords is set to {0}",
                    string.IsNullOrEmpty(options.Fingerprint.FingerprintBlacklistKeywords) ? "EMPTY" : "CONFIGURED");

                // UnlockIntroSkip 已开启但声纹提取范围为空的可见提示
                if (options.Fingerprint.UnlockIntroSkip && string.IsNullOrEmpty(options.Fingerprint.MarkerEnabledLibraryScope))
                {
                    var hasMarkerEnabledLibs = options.Fingerprint.MarkerEnabledLibraryList.Any(o => o.Value != "-1");
                    _logger.Warn(hasMarkerEnabledLibs
                        ? "UnlockIntroSkip 已开启但「声纹提取媒体库范围」为空：指纹提取将覆盖所有启用 marker 检测的剧集媒体库。若需限定范围，请在「声纹提取媒体库范围」中设置。"
                        : "UnlockIntroSkip 已开启但没有任何启用 marker 检测的剧集媒体库，指纹提取将无可用媒体库。请先在媒体库选项中开启片头检测。");
                }
            }
        }
    }
}
