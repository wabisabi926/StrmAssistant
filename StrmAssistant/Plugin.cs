using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using StrmAssistant.Core;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.IntroSkip;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.View;
using StrmAssistant.Properties;
using StrmAssistant.ScheduledTask;
using StrmAssistant.Web.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.ExperienceEnhanceOptions;
using static StrmAssistant.Options.TaskConcurrencyOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant
{
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages
    {
        private List<IPluginUIPageController> _pages;
        public readonly PluginOptionsStore MainOptionsStore;
        public readonly MediaInfoExtractOptionsStore MediaInfoExtractStore;
        public readonly MetadataEnhanceOptionsStore MetadataEnhanceStore;
        public readonly IntroSkipOptionsStore IntroSkipStore;
        public readonly ExperienceEnhanceOptionsStore ExperienceEnhanceStore;
        public readonly NetworkProxyOptionsStore NetworkProxyStore;

        public static Plugin Instance { get; private set; }

        /// <summary>
        /// 插件总开关（重启生效）。关闭时 <see cref="Initialize"/> 会整段跳过补丁、API 对象与
        /// 事件订阅，定时任务与 Web 端点也据此早退；配置读写仍保留，使设置页可以重新启用。
        /// 默认 true，避免 store 尚未初始化时把功能误判为关闭。
        /// </summary>
        public static bool IsPluginEnabled =>
            Instance?.MainOptionsStore?.GetOptions()?.EnablePlugin ?? true;

        public static LibraryApi LibraryApi { get; private set; }
        public static MediaInfoApi MediaInfoApi { get; private set; }
        public static ChapterApi ChapterApi { get; private set; }
        public static FingerprintApi FingerprintApi { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static SubtitleApi SubtitleApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }
        public static AutoMarkWatched AutoMarkWatched { get; private set; }
        public static IntroDbApi IntroDbApi { get; private set; }
        public static ImagePostProcessor ImagePostProcessor { get; private set; }
        public static global::StrmAssistant.Provider.Douban.DoubanAssist DoubanAssist { get; private set; }
        public static MetadataApi MetadataApi { get; private set; }
        public static VideoThumbnailApi VideoThumbnailApi { get; private set; }

        /// <summary>影音直链等功能使用的媒体库管理器与共享 HttpClient。</summary>
        public static ILibraryManager LibraryManager { get; private set; }

        public static IHttpClient HttpClient { get; private set; }

        /// <summary>共享 JSON 序列化器（本地 TMDB patches 合并等静态工具使用）。</summary>
        public static IJsonSerializer JsonSerializer { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");

        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public readonly IApplicationPaths ApplicationPaths;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;
        private readonly ISessionManager _sessionManager;

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IFileSystem fileSystem, ILibraryManager libraryManager, ISessionManager sessionManager,
            IItemRepository itemRepository, INotificationManager notificationManager, ILibraryMonitor libraryMonitor,
            IMediaSourceManager mediaSourceManager, IMediaMountManager mediaMountManager,
            IProviderManager providerManager, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IUserManager userManager, IUserDataManager userDataManager,
            IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder, IJsonSerializer jsonSerializer,
            IHttpClient httpClient, IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager, ITaskManager taskManager,
            IImageExtractionManager imageExtractionManager, IServerApplicationPaths serverApplicationPaths)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("Plugin is getting loaded.");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;

            if (ApplicationHost.ApplicationVersion < new Version(MinRequiredEmbyVersion))
            {
                throw new NotSupportedException(
                    $"StrmAssistant requires Emby >= {MinRequiredEmbyVersion}, current: {ApplicationHost.ApplicationVersion}");
            }
            
            // 初始化核心架构组件
            try
            {
                // 初始化版本适配器
                EmbyVersionAdapter.Initialize(Logger, applicationHost.ApplicationVersion);
                
                // 初始化高性能反射系统
                FastReflection.Initialize(Logger);
                
                // 初始化性能监控
                PerformanceMonitor.Initialize(Logger);
                
                // 初始化服务定位器
                ServiceLocator.Initialize(Logger);
                
                Logger.Info($"Core architecture initialized - Emby {applicationHost.ApplicationVersion} (API Version: {EmbyVersionAdapter.Instance.ApiVersion})");
                Logger.Info("FastReflection and PerformanceMonitor enabled for optimal performance");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize core architecture: {ex.Message}");
                Logger.Error(ex.StackTrace);
            }

            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _providerManager = providerManager;
            _sessionManager = sessionManager;
            _fileSystem = fileSystem;
            _taskManager = taskManager;

            LibraryManager = libraryManager;
            HttpClient = httpClient;
            JsonSerializer = jsonSerializer;

            MainOptionsStore = new PluginOptionsStore(applicationHost, Logger, ConfigName);
            MediaInfoExtractStore =
                new MediaInfoExtractOptionsStore(applicationHost, Logger, ConfigName + "_" + nameof(MediaInfoExtractOptions));
            MetadataEnhanceStore =
                new MetadataEnhanceOptionsStore(applicationHost, Logger, ConfigName + "_" + nameof(MetadataEnhanceOptions));
            IntroSkipStore = new IntroSkipOptionsStore(applicationHost, Logger, ConfigName + "_" + nameof(IntroSkipOptions));
            ExperienceEnhanceStore = new ExperienceEnhanceOptionsStore(applicationHost, Logger,
                ConfigName + "_" + nameof(ExperienceEnhanceOptions));
            // 网络与直链：新建 Tab，迁移时从主配置与元数据配置各取一块（见 NetworkProxyOptionsMigration）
            NetworkProxyStore = new NetworkProxyOptionsStore(applicationHost, Logger,
                ConfigName + "_" + nameof(NetworkProxyOptions));

            MigrateLegacyOptionGroups();
            MigrateChineseSearchOptions();
            EnsureChineseSearchUsable();
            InitializeOptionCache();

            // 方案 4A：对账并释放上一次进程遗留的 ISO 孤儿挂载（跨 Emby 重启泄漏的兜底）
            try
            {
                Common.IsoMounter.ReconcileOnStartup();
            }
            catch (Exception ex)
            {
                Logger.Warn($"ISO mount reconcile on startup failed: {ex.Message}");
            }

            // 插件界面语言固定「跟随客户端」：Resources.Culture 置空后，ResourceManager 会回退到
            // 请求线程的 CurrentUICulture，页面文案随客户端语言自动切换。
            Resources.Culture = null;

            if (MainOptionsStore.GetOptions().AboutOptions.DebugMode)
            {
                DebugMode = true;
                MainOptionsStore.GetOptions().AboutOptions.DebugMode = false;
                MainOptionsStore.SavePluginOptionsSuppress();
            }
            else if (Debugger.IsAttached)
            {
                DebugMode = true;
            }

            // 插件总开关（重启生效）：关闭时跳过补丁、API 对象与事件订阅，
            // 仅保留配置读写，使设置页仍可打开以重新启用。
            if (!MainOptionsStore.GetOptions().EnablePlugin)
            {
                Logger.Warn("StrmAssistant is disabled (EnablePlugin = false): " +
                            "no patches, scheduled tasks or web endpoints will run. " +
                            "Re-enable it and restart the server to take effect.");
                return;
            }

            if (IsModSupported)
            {
                Logger.Info("Initializing Harmony Mod patches...");

                try
                {
                    PatchManager.Initialize();

                    // 检查补丁状态并记录详细信息
                    var modSuccess = PatchManager.IsModSuccess();
                    if (!modSuccess)
                    {
                        Logger.Warn("Harmony Mod initialization completed with some failures. Check logs above for details.");
                    }
                    else
                    {
                        Logger.Info("Harmony Mod initialization completed successfully.");
                    }

                    // 在调试模式下输出详细诊断
                    if (DebugMode)
                    {
                        Logger.Debug(PatchManager.GetDiagnosticReport());
                    }
                }
                catch (Exception ex)
                {
                    // 兜底：补丁初始化异常不得导致插件加载失败、设置页无法打开
                    Logger.Error($"PatchManager initialization failed: {ex.Message}");
                    Logger.Error(ex.ToString());
                }
            }
            else
            {
                Logger.Info("Harmony Mod is not supported in this environment. Using reflection-based approach.");
            }
            
            // 执行启动健康检查
            try
            {
                HealthCheck.Initialize(Logger);
                var healthResult = HealthCheck.Instance.PerformHealthCheck();
                Logger.Info($"Startup Health Check: {healthResult.OverallStatus}");
                
                if (healthResult.OverallStatus != HealthStatus.Healthy)
                {
                    if (DebugMode)
                    {
                        Logger.Debug(healthResult.ToString());
                    }
                    else
                    {
                        Logger.Warn($"Health issues detected. Enable DebugMode for details.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Health check failed: {ex.Message}");
            }
            
            // 初始化优化建议系统
            try
            {
                OptimizationAdvisor.Initialize(Logger);
                var suggestions = OptimizationAdvisor.Instance.GetSuggestions();
                
                if (suggestions.Count > 0 && DebugMode)
                {
                    var criticalSuggestions = suggestions.Where(s => 
                        s.Priority == OptimizationPriority.Critical || s.Priority == OptimizationPriority.High).ToList();
                    
                    if (criticalSuggestions.Any())
                    {
                        Logger.Warn($"Found {criticalSuggestions.Count} optimization suggestions. Use OptimizationAdvisor for details.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Optimization advisor initialization failed: {ex.Message}");
            }
            
            // 启动定期性能报告（仅在DebugMode下，每60分钟一次）
            // 启动定期内存清理器（按配置启停，防止 Emby 内存持续增长）
            try
            {
                var memOpts = MainOptionsStore.GetOptions().Memory;
                MemoryCleaner.ApplySettings(Logger, memOpts.EnableMemoryCleanup, memOpts.MemoryCleanupIntervalMinutes);
            }
            catch (Exception ex)
            {
                Logger.Warn($"MemoryCleaner initialization failed: {ex.Message}");
            }

            if (DebugMode)
            {
                try
                {
                    PerformanceReporter.Initialize(Logger, intervalMinutes: 60);
                    Logger.Debug("PerformanceReporter started - Reports every 60 minutes");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"PerformanceReporter initialization failed: {ex.Message}");
                }
            }

            LibraryApi = new LibraryApi(libraryManager, providerManager, fileSystem, mediaMountManager, userManager);
            MediaInfoApi = new MediaInfoApi(libraryManager, fileSystem, providerManager, mediaSourceManager,
                itemRepository, jsonSerializer, libraryMonitor);
            ChapterApi = new ChapterApi(libraryManager, itemRepository);
            IntroDbApi = new IntroDbApi(httpClient, jsonSerializer);
            ImagePostProcessor = new ImagePostProcessor(ffmpegManager, providerManager, fileSystem);
            DoubanAssist = new global::StrmAssistant.Provider.Douban.DoubanAssist(httpClient, jsonSerializer,
                providerManager, fileSystem);
            FingerprintApi = new FingerprintApi(libraryManager, fileSystem, applicationPaths, ffmpegManager,
                mediaEncoder, mediaMountManager, jsonSerializer, itemRepository, serverApplicationHost);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, userManager, sessionManager);
            AutoMarkWatched = new AutoMarkWatched(libraryManager, userManager, sessionManager, userDataManager);
            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            SubtitleApi = new SubtitleApi(libraryManager, fileSystem, mediaProbeManager, localizationManager,
                itemRepository);
            MetadataApi = new MetadataApi(libraryManager, fileSystem, configurationManager, localizationManager,
                jsonSerializer, httpClient);
            VideoThumbnailApi = new VideoThumbnailApi(libraryManager, fileSystem, imageExtractionManager, itemRepository,
                mediaMountManager, serverApplicationPaths, libraryMonitor, ffmpegManager);
            ShortcutMenuHelper.Initialize(configurationManager);

            FfmpegPathOverride.Initialize(ffmpegManager, Logger);
            var mediaInfoOptions = MediaInfoExtractStore.GetOptions();
            FfmpegPathOverride.Apply(mediaInfoOptions.IsoTool.CustomProbePath, mediaInfoOptions.IsoTool.CustomEncoderPath);

            var metadataCacheOptions = MetadataEnhanceStore.GetOptions();
            if (metadataCacheOptions.MetadataCache.IsMetadataCacheEnabled)
            {
                var (cacheOk, cacheError) =
                    LocalMetadataCache.ValidateAndInitialize(metadataCacheOptions.MetadataCache.MetadataCacheRootFolder);
                Logger.Info(cacheOk
                    ? $"MetadataCache - initialized at {LocalMetadataCache.Root} (mode={metadataCacheOptions.MetadataCache.MetadataCacheMode})"
                    : $"MetadataCache - root invalid ({cacheError}): {metadataCacheOptions.MetadataCache.MetadataCacheRootFolder}");

                // 启动时清被动缓存：TMDB 侧改了数据后，避免一直命中旧缓存
                if (cacheOk && metadataCacheOptions.MetadataCache.ClearRecentTmdbCacheOnStartup)
                {
                    var removed = LocalMetadataCache.ClearPassiveCache();

                    Logger.Info(removed >= 0
                        ? $"MetadataCache - passive cache cleared on startup ({removed} file(s) removed)"
                        : "MetadataCache - passive cache clear skipped: cache root not initialized");
                }
            }

            if (MainOptionsStore.GetOptions().TaskConcurrency.CatchupMode) QueueManager.Initialize();
            if (IntroSkipStore.GetOptions().Playback.EnableIntroSkip) PlaySessionMonitor.Initialize();
            if (ExperienceEnhanceStore.GetOptions().Playback.EnableAutoMarkWatched) AutoMarkWatched.Initialize();
            MemoryGuard.Apply(Logger,
                MainOptionsStore.GetOptions().Memory.EnableMemoryAutoRestart,
                MainOptionsStore.GetOptions().Memory.MemoryAutoRestartThresholdMB);

            MigrateImageCaptureExcludeContainers();

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _userManager.UserCreated += OnUserCreated;
            _userManager.UserDeleted += OnUserDeleted;
            _userManager.UserConfigurationUpdated += OnUserConfigurationUpdated;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;
        }

        /// <summary>
        /// Restore persisted legacy groups before any cache or background feature reads the options.
        /// </summary>
        private void MigrateLegacyOptionGroups()
        {
            // 拍平旧版容器：TaskConcurrency / Memory 原本包在 GeneralOptions 里、
            // ChineseSearch 包在 ModOptions 里，页面上会多出一层只有标题的容器（任务与内存 / 搜索）。
            // 这里把已有配置提到顶层并落盘，之后 JSON 里不再有这两个节点。
            var flattened = false;

            if (MainOptionsStore.GetOptions().GeneralOptions != null)
            {
                var legacyGeneral = MainOptionsStore.GetOptions().GeneralOptions;
                MainOptionsStore.GetOptions().TaskConcurrency =
                    legacyGeneral.TaskConcurrency ?? new TaskConcurrencyOptions();
                MainOptionsStore.GetOptions().Memory = legacyGeneral.Memory ?? new MemoryOptions();
                MainOptionsStore.GetOptions().GeneralOptions = null;
                flattened = true;
            }

            if (MainOptionsStore.GetOptions().ModOptions != null)
            {
                MainOptionsStore.GetOptions().ChineseSearch =
                    MainOptionsStore.GetOptions().ModOptions.ChineseSearch ?? new ChineseSearchOptions();
                MainOptionsStore.GetOptions().ModOptions = null;
                flattened = true;
            }

            if (flattened)
            {
                MainOptionsStore.SavePluginOptionsSuppress();
                Logger.Info("PluginOptions - flattened GeneralOptions/ModOptions containers into top-level groups");
            }

            // 旧版分钟级「追更最小等待」→ 秒级（一次性换算并落盘，避免两份值长期并存）。
            // 注意：选项的 Initialize() 只在设置页加载时调用，启动阶段必须自己处理。
            var taskConcurrency = MainOptionsStore.GetOptions().TaskConcurrency;
            if (taskConcurrency.CatchupMinWaitSeconds <= 0 && taskConcurrency.CatchupMinWaitMinutes > 0)
            {
                taskConcurrency.CatchupMinWaitSeconds = Math.Min(7200, taskConcurrency.CatchupMinWaitMinutes * 60);
                taskConcurrency.CatchupMinWaitMinutes = 0;
                MainOptionsStore.SavePluginOptionsSuppress();

                Logger.Info(
                    $"TaskConcurrency - migrated CatchupMinWaitMinutes to CatchupMinWaitSeconds ({taskConcurrency.CatchupMinWaitSeconds} s)");
            }

        }

        private void MigrateImageCaptureExcludeContainers()
        {
            try
            {
                const string legacyDefault = "MpegTs,Ts,M2Ts";
                var mediaInfoOptions = MediaInfoExtractStore.GetOptions();

                if (!string.Equals(mediaInfoOptions.ImageCapture.ImageCaptureExcludeMediaContainers, legacyDefault,
                        StringComparison.Ordinal)) return;

                mediaInfoOptions.ImageCapture.ImageCaptureExcludeMediaContainers = string.Empty;
                MediaInfoExtractStore.SetOptions(mediaInfoOptions);

                Logger.Info("MediaInfoExtract - Migrated ImageCaptureExcludeMediaContainers: ts/m2ts no longer excluded by default");
            }
            catch (Exception e)
            {
                Logger.Warn("MediaInfoExtract - Exclude containers migration failed: " + e.Message);
            }
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            if (_libraryManager.IsScanRunning) return;

            var options = ExperienceEnhanceStore.GetOptions();

            var introSkipOptions = IntroSkipStore.GetOptions();

            if (introSkipOptions.Scrape.EnableIntroDbScrape && introSkipOptions.Scrape.EnableAutomaticIntroScraping)
            {
                if (e.Argument.Item is Series refreshedSeries)
                {
                    _ = BackgroundTaskRunner.Run("IntroDb.ScrapeSeries", () => IntroDbApi.ScrapeSeriesAsync(refreshedSeries, CancellationToken.None));
                }
                else if (e.Argument.Item is Movie refreshedMovie)
                {
                    _ = BackgroundTaskRunner.Run("IntroDb.ScrapeMovie", () => IntroDbApi.ScrapeMovieAsync(refreshedMovie, CancellationToken.None));
                }
            }

            var mediaInfoOptions = MediaInfoExtractStore.GetOptions();
            var imageCaptureOptions = mediaInfoOptions.ImageCapture;
            var postProcessMode = imageCaptureOptions.ImageCapturePostProcessMode;

            if (imageCaptureOptions.EnableImageCapture && !string.IsNullOrWhiteSpace(postProcessMode))
            {
                var refreshedItem = e.Argument.Item;

                _ = BackgroundTaskRunner.Run("ImagePostProcess.ProcessItem", () => ImagePostProcessor.ProcessItem(refreshedItem, postProcessMode));
            }

            if (MetadataEnhanceStore.GetOptions().DoubanAssist.EnableDoubanAssist)
            {
                if (e.Argument.Item is Movie doubanMovie)
                {
                    _ = BackgroundTaskRunner.Run("Douban.Apply.Movie", () => DoubanAssist.ApplyAsync(doubanMovie, CancellationToken.None));
                }
                else if (e.Argument.Item is Series doubanSeries)
                {
                    _ = BackgroundTaskRunner.Run("Douban.Apply.Series", () => DoubanAssist.ApplyAsync(doubanSeries, CancellationToken.None));
                }
                else if (e.Argument.Item is Season doubanSeason)
                {
                    _ = BackgroundTaskRunner.Run("Douban.Apply.Season", () => DoubanAssist.ApplyAsync(doubanSeason, CancellationToken.None));
                }
            }

            if (options.Merge.MergeMultiVersion && e.Argument.Item.IsTopParent)
            {
                var library = e.Argument.CollectionFolders.OfType<CollectionFolder>().FirstOrDefault();

                if (library != null && (library.CollectionType == CollectionType.Movies.ToString() ||
                                        library.CollectionType == CollectionType.TvShows.ToString() &&
                                        options.Merge.MergeSeriesPreference ==
                                        VersionMergeOptions.MergeSeriesScopeOption.GlobalScope ||
                                        library.CollectionType is null))
                {
                    MergeMultiVersionTask.CurrentScanLibrary.Value = library;

                    var mergeMoviesTask = _taskManager.ScheduledTasks.FirstOrDefault(t =>
                        t.ScheduledTask is MergeMultiVersionTask);

                    if (mergeMoviesTask != null)
                    {
                        _ = _taskManager.Execute(mergeMoviesTask, new TaskOptions());
                    }
                }
            }
        }

        private void OnUserCreated(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserDeleted(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserConfigurationUpdated(object sender, GenericEventArgs<User> e)
        {
            if (e.Argument.Policy.IsAdministrator) LibraryApi.FetchAdminOrderedViews();
        }

        private void OnLibraryOptionsUpdated(object sender, GenericEventArgs<Tuple<CollectionFolder, LibraryOptions>> e)
        {
            var library = e.Argument.Item1;

            if (!LibraryApi.ExcludedCollectionTypes.Contains(library.CollectionType))
            {
                LibraryApi.UpdateLibraryPathsInScope();

                if (library.CollectionType == CollectionType.TvShows.ToString() || library.CollectionType is null)
                {
                    PlaySessionMonitor.UpdateLibraryPathsInScope();
                    FingerprintApi.UpdateLibraryPathsInScope();

                    if (IntroSkipStore.GetOptions().Fingerprint.UnlockIntroSkip)
                        FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength();

                    if (ExperienceEnhanceStore.GetOptions().Merge.MergeMultiVersion)
                        LibraryApi.EnsureLibraryEnabledAutomaticSeriesGrouping();
                }
            }
        }

        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                // 合集变更通知：条目加入合集
                if (ExperienceEnhanceStore.GetOptions().Notification.EnhanceNotificationSystem &&
                    (e.Parent ?? e.Item?.Parent) is BoxSet boxsetAdded)
                {
                    NotificationApi.BoxsetUpdateSendNotification(boxsetAdded, e.Item, true);
                }

                var deserializeResult = false;

                if (e.Item is Video && MediaInfoExtractStore.GetOptions().MediaInfoPersist.PersistMediaInfoMode !=
                    PersistMediaInfoOption.None.ToString())
                {
                    deserializeResult = LibraryApi.HasMediaInfo(e.Item);

                    var directoryService = new DirectoryService(Logger, _fileSystem);

                    if (!deserializeResult)
                    {
                        // 上面刚查过 HasMediaInfo(e.Item)==false，传 skipHasMediaInfoCheck=true 省掉重复的 DB 查询
                        deserializeResult = await MediaInfoApi.DeserializeMediaInfo(e.Item, directoryService,
                            "OnItemAdded Restore", true, true).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = MediaInfoApi.SerializeMediaInfo(e.Item.InternalId, directoryService, true,
                            "OnItemAdded Overwrite").ConfigureAwait(false);
                    }
                }

                if ((e.Item is Video || e.Item is Audio) && MainOptionsStore.PluginOptions.TaskConcurrency.CatchupMode)
                {
                    if (IntroSkipStore.IntroSkipOptions.Fingerprint.UnlockIntroSkip &&
                        IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                        e.Item is Episode && FingerprintApi.IsLibraryInScope(e.Item) &&
                        (!deserializeResult || FingerprintApi.IsExtractNeeded(e.Item)))
                    {
                        QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                    }
                    else
                    {
                        if (IsCatchupTaskSelected(CatchupTask.MediaInfo) && !deserializeResult)
                        {
                            QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                        }

                        if (IsCatchupTaskSelected(CatchupTask.IntroSkip) &&
                            e.Item is Episode && PlaySessionMonitor.IsLibraryInScope(e.Item))
                        {
                            if (!deserializeResult)
                            {
                                QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                            }
                            else if (e.Item is Episode episode && ChapterApi.SeasonHasIntroCredits(episode))
                            {
                                QueueManager.IntroSkipItemQueue.Enqueue(episode);
                            }
                        }
                    }

                    if (IsCatchupTaskSelected(CatchupTask.EpisodeRefresh) && e.Item is Episode ep &&
                        LibraryApi.IsPremiereDateInScope(ep, DateTimeOffset.UtcNow.AddDays(-90), false))
                    {
                        QueueManager.EpisodeRefreshItemQueue.Enqueue(ep);
                    }
                }

                if (e.Item is Movie || e.Item is Series || e.Item is Episode)
                {
                    NotificationApi.FavoritesUpdateSendNotification(e.Item);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackProgressEventArgs e)
        {
            if (e.Item is Video && MediaInfoExtractStore.MediaInfoExtractOptions.ExtractionPerformance.ExclusiveExtract &&
                IsExclusiveFeatureSelected(ExclusiveControl.ExtractAlternative))
            {
                var targetItem = MediaInfoApi.GetItemByMediaSourceId(e.Item, e.MediaSourceId);

                if (targetItem != null)
                {
                    var refreshOptions = MediaInfoApi.GetMediaInfoRefreshOptions();

                    MediaInfoApi.QueueRefreshAlternateVersions(targetItem, refreshOptions, false);
                }
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (MetadataEnhanceStore.GetOptions().PersonEnhance.EnhanceMovieDbPerson &&
                (e.UpdateReason & (ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataImport)) != 0)
            {
                if (e.Item is Season season && season.IndexNumber > 0)
                {
                    MetadataApi.UpdateSeriesPeople(season.Parent as Series);
                }
                else if (e.Item is Series series)
                {
                    MetadataApi.UpdateSeriesPeople(series);
                }
            }

            // 增强通知系统：元数据/图像更新通知（发给管理员）
            if (ExperienceEnhanceStore.GetOptions().Notification.EnhanceNotificationSystem &&
                (e.Item is Video || e.Item is Audio))
            {
                if ((e.UpdateReason & ItemUpdateType.ImageUpdate) != 0)
                {
                    NotificationApi.ImageUpdateSendNotification(e.Item);
                }
                else if ((e.UpdateReason &
                          (ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit |
                           ItemUpdateType.MetadataImport)) != 0)
                {
                    var trackedFields = ExperienceEnhanceStore.GetOptions().Notification.MetadataUpdateTrackedFields;
                    if (MatchesTrackedFields(e, trackedFields))
                    {
                        NotificationApi.MetadataUpdateSendNotification(e.Item,
                            string.Format(Resources.Notification_MetadataUpdate_EventDescription.Replace("\\n",
                                Environment.NewLine), e.Item.Name, e.Item.Path,
                            DescribeUpdateReason(e.UpdateReason)));
                    }
                }
            }
        }

        /// <summary>
        /// MetadataUpdateTrackedFields 过滤：逗号分隔的字段名（如 Overview,CommunityRating），
        /// 为空表示全部通知。若 Emby 提供 ItemChangeEventArgs.MetadataChangedFields 则按其过滤，
        /// 否则退化为 UpdateReason 粒度（始终命中）。
        /// </summary>
        private static bool MatchesTrackedFields(ItemChangeEventArgs e, string trackedFields)
        {
            var tracked = (trackedFields ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)
                .ToArray();

            if (tracked.Length == 0) return true;

            try
            {
                var property = typeof(ItemChangeEventArgs).GetProperty("MetadataChangedFields",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(e) is IEnumerable<string> changed)
                {
                    var changedFields = changed as string[] ?? changed.ToArray();
                    if (changedFields.Length == 0) return true;

                    return changedFields.Any(c => tracked.Any(t =>
                        string.Equals(t, c, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception)
            {
                // 字段不可用则不阻断通知
            }

            return true;
        }

        private static string DescribeUpdateReason(ItemUpdateType reason)
        {
            if ((reason & ItemUpdateType.MetadataDownload) != 0) return "MetadataDownload";
            if ((reason & ItemUpdateType.MetadataEdit) != 0) return "MetadataEdit";
            if ((reason & ItemUpdateType.MetadataImport) != 0) return "MetadataImport";
            if ((reason & ItemUpdateType.ImageUpdate) != 0) return "ImageUpdate";
            return reason.ToString();
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            // 合集变更通知：条目移出合集
            if (ExperienceEnhanceStore.GetOptions().Notification.EnhanceNotificationSystem &&
                (e.Parent ?? e.Item?.Parent) is BoxSet boxsetRemoved)
            {
                NotificationApi.BoxsetUpdateSendNotification(boxsetRemoved, e.Item, false);
            }

            if (e.Item is Video && MediaInfoExtractStore.GetOptions().MediaInfoPersist.PersistMediaInfoMode !=
                PersistMediaInfoOption.Restore.ToString())
            {
                var directoryService = new DirectoryService(Logger, _fileSystem);
                MediaInfoApi.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
            }

            if (e.Item is CollectionFolder library)
            {
                if (library.CollectionType == CollectionType.BoxSets.ToString() &&
                    !ExperienceEnhanceStore.GetOptions().LibraryOrder.NoBoxsetsAutoCreation)
                {
                    PatchManager.NoBoxsetsAutoCreation.Patch();
                    ExperienceEnhanceStore.GetOptions().LibraryOrder.NoBoxsetsAutoCreation = true;
                    ExperienceEnhanceStore.SavePluginOptionsSuppress();
                }

                if (!LibraryApi.ExcludedCollectionTypes.Contains(library.CollectionType))
                {
                    LibraryApi.UpdateLibraryPathsInScope();

                    if (library.CollectionType == CollectionType.TvShows.ToString() || library.CollectionType is null)
                    {
                        PlaySessionMonitor.UpdateLibraryPathsInScope();
                        FingerprintApi.UpdateLibraryPathsInScope();
                    }
                }
            }
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.UserData.IsFavorite)
            {
                if (MainOptionsStore.PluginOptions.TaskConcurrency.CatchupMode &&
                    IntroSkipStore.IntroSkipOptions.Fingerprint.UnlockIntroSkip && IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                    FingerprintApi.IsLibraryInScope(e.Item) && (e.Item is Series || e.Item is Episode))
                {
                    QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                }
                else if (MainOptionsStore.PluginOptions.TaskConcurrency.CatchupMode &&
                         IsCatchupTaskSelected(CatchupTask.MediaInfo))
                {
                    QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                }
            }
        }

        public override void OnUninstalling()
        {
            if (MainOptionsStore.GetOptions().ChineseSearch.EnhanceChineseSearch)
            {
                _ = NotificationApi.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.Uninstall_Warning}", 10000);
            }

            MemoryCleaner.DisposeInstance();
            Common.IsoMounter.DismountAll();

            base.OnUninstalling();
        }

        /// <summary>
        /// 版本标记文件的格式版本。
        /// <para>
        /// 1（旧，隐式）：文件内只写版本号。历史编号方案并不统一——日期式 <c>26.3.25</c>、
        /// 三段式 <c>3.0.55</c>（实为 <c>3.0.0.55</c> 的截断写法）、四段式 <c>3.0.0.55</c>。
        /// </para>
        /// <para>
        /// 2：<c>格式号|版本号</c>，版本号与 Releases tag 一致为 4 段式（如 <c>2|3.0.0.55</c>）。
        /// 格式号不同即说明编号方案已变更，此时两个版本号<b>不可比</b>
        /// （按 <see cref="Version"/> 语义 <c>3.0.0.55 &lt; 3.0.55</c>，会得出「降级」的假结论），
        /// 因此只重置基线，既不判升级也不判降级。
        /// </para>
        /// <para>
        /// 3：<c>格式号|索引方案号|版本号</c>（如 <c>3|2|3.0.0.65</c>）。
        /// 只有**索引方案号**（<see cref="EnhanceChineseSearch.IndexSchemeVersion"/>）变化时
        /// 才自动关闭功能；同一方案内的版本升级保持原状 —— 纯 UI / 文案升级不该把功能关掉。
        /// 读到格式 2 及更早的标记（没有方案号）时只重置基线，不执行自动关闭。
        /// </para>
        /// </summary>
        private const int VersionMarkerFormat = 3;

        /// <summary>
        /// 启动兜底：分词器扩展不可用时关掉中文搜索。
        /// <para>
        /// 索引一旦建立在插件自带分词器上，扩展加载不上就会让**每次元数据写入整体回滚**。
        /// 这种情况与版本号无关（换架构、插件目录不完整、Emby 内部变化都会触发），
        /// 所以单独校验一次：宁可关掉功能，也不能带着坏索引继续跑。
        /// </para>
        /// </summary>
        private void EnsureChineseSearchUsable()
        {
            try
            {
                var options = MainOptionsStore.GetOptions();
                if (!options.ChineseSearch.EnhanceChineseSearch) return;

                if (EnhanceChineseSearch.IsTokenizerAvailable()) return;

                options.ChineseSearch.EnhanceChineseSearch = false;
                options.ChineseSearch.EnhanceChineseSearchRestore = true;
                MainOptionsStore.SavePluginOptionsSuppress();

                Logger.Error(
                    "EnhanceChineseSearch has been disabled: tokenizer extension is not available " +
                    "(missing file, unrecognized SHA-1, or unsupported architecture). " +
                    "Index restore to default tokenizer queued. Fix the tokenizer, then re-enable.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"EnsureChineseSearchUsable failed: {ex.Message}");
            }
        }

        private void MigrateChineseSearchOptions()
        {
            try
            {
                var configDir = ApplicationPaths.PluginConfigurationsPath;
                Directory.CreateDirectory(configDir);

                var legacyMarkerPath = Path.Combine(configDir, $"{Name}_chinese_search_migrated_2_2_0.flag");
                var versionMarkerPath = Path.Combine(configDir, $"{Name}_chinese_search_last_seen_version.txt");

                var currentVersionText = CurrentVersion ?? "0.0.0.0";
                if (!Version.TryParse(currentVersionText, out var currentVersion))
                {
                    currentVersion = new Version(0, 0, 0, 0);
                }

                var hasLegacyMarker = File.Exists(legacyMarkerPath);
                var hasVersionMarker = File.Exists(versionMarkerPath);

                // 标记文件格式见 ChineseSearchMarker：1（隐式版本号）/ 2（格式|版本）/ 3（格式|方案|版本）。
                var marker = Common.ChineseSearchMarker.Parse(
                    hasVersionMarker ? File.ReadAllText(versionMarkerPath) : null);
                var markerFormat = marker.Format;
                var previousVersion = marker.Version;
                var previousIndexScheme = marker.IndexScheme;

                // 标记格式变更 → 版本号不可比，只重置基线（既不判升级也不判降级，也不告警）。
                var formatChanged = hasVersionMarker && markerFormat != VersionMarkerFormat;

                // 只有索引方案号真的变了才需要关功能。格式 2 及更早的标记没有方案号，
                // 视为未知 → 不关（避免老用户升级时功能被莫名关掉）。
                var indexSchemeChanged = marker.HasIndexScheme &&
                                         previousIndexScheme != EnhanceChineseSearch.IndexSchemeVersion;

                void WriteVersionMarker()
                {
                    File.WriteAllText(versionMarkerPath,
                        Common.ChineseSearchMarker.Format(EnhanceChineseSearch.IndexSchemeVersion, currentVersion));
                }

                void DisableForIndexRestore(string reason)
                {
                    var options = MainOptionsStore.GetOptions();
                    var changed = false;

                    if (options.ChineseSearch.EnhanceChineseSearch)
                    {
                        options.ChineseSearch.EnhanceChineseSearch = false;

                        // 关闭时**排程还原**：把 fts_search9 重建回 Emby 默认分词器（unicode61）。
                        // 否则会留下「索引用 simple + 选项已关闭」的半吊状态：索引永久依赖插件自带的
                        // libsimple 扩展，一旦扩展加载不上（插件目录不完整 / 架构不符 / Emby 内部变化），
                        // 每次元数据写入都会整体回滚。还原后索引不再依赖扩展，关闭就是真的关闭。
                        options.ChineseSearch.EnhanceChineseSearchRestore = true;
                        changed = true;
                    }
                    else if (options.ChineseSearch.EnhanceChineseSearchRestore)
                    {
                        options.ChineseSearch.EnhanceChineseSearchRestore = false;
                        changed = true;
                    }

                    if (!changed)
                    {
                        Logger.Info($"EnhanceChineseSearch: {reason}, but the option is already off; nothing to do.");
                        return;
                    }

                    MainOptionsStore.SavePluginOptionsSuppress();
                    Logger.Info(
                        $"EnhanceChineseSearch has been disabled ({reason}). " +
                        "Index restore to default tokenizer queued (runs shortly after startup). " +
                        "User must re-enable it manually to trigger rebuild.");
                }

                Logger.Info(
                    $"EnhanceChineseSearch upgrade-check: current={currentVersion}, previous={(previousVersion?.ToString() ?? "<none>")}, " +
                    $"markerFormat={markerFormat}, previousIndexScheme={previousIndexScheme}, " +
                    $"indexScheme={EnhanceChineseSearch.IndexSchemeVersion}, indexSchemeChanged={indexSchemeChanged}, " +
                    $"formatChanged={formatChanged}, legacyMarker={hasLegacyMarker}, versionMarker={hasVersionMarker}");

                // 首次安装 / 标记格式变更 / 旧格式（没有方案号）：只写基线，不执行自动禁用。
                if (previousVersion is null || formatChanged)
                {
                    WriteVersionMarker();

                    if (!hasLegacyMarker)
                    {
                        File.WriteAllText(legacyMarkerPath, DateTime.UtcNow.ToString("O"));
                    }

                    Logger.Info(formatChanged
                        ? $"EnhanceChineseSearch upgrade-check: marker format changed ({markerFormat} -> {VersionMarkerFormat}, previous={previousVersion}); baseline reset, no auto-disable."
                        : "EnhanceChineseSearch upgrade-check: baseline initialized, no auto-disable on first run.");
                    return;
                }

                // 索引方案变了：旧索引与新代码不匹配，必须关掉并重建。
                if (indexSchemeChanged)
                {
                    DisableForIndexRestore(
                        $"index scheme changed {previousIndexScheme} -> {EnhanceChineseSearch.IndexSchemeVersion} " +
                        $"({previousVersion} -> {currentVersion})");
                    WriteVersionMarker();
                    return;
                }

                if (currentVersion < previousVersion)
                {
                    Logger.Warn(
                        $"EnhanceChineseSearch upgrade-check: detected downgrade {previousVersion} -> {currentVersion} " +
                        "within the same index scheme; keep current option state.");
                }
                else if (currentVersion > previousVersion)
                {
                    Logger.Info(
                        $"EnhanceChineseSearch upgrade-check: upgraded {previousVersion} -> {currentVersion} " +
                        "with the same index scheme; option state kept, no rebuild needed.");
                }

                WriteVersionMarker();

                WriteVersionMarker();

                if (!hasLegacyMarker)
                {
                    File.WriteAllText(legacyMarkerPath, DateTime.UtcNow.ToString("O"));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to migrate Chinese search options: {ex.Message}");
                if (DebugMode)
                {
                    Logger.Debug(ex.StackTrace);
                }
            }
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description => "Extract MediaInfo and Enable IntroSkip";

        public override Guid Id => _id;

        /// <summary>
        /// 插件要求的最低 Emby 版本，<see cref="Initialize"/> 的准入检查使用。
        /// </summary>
        public const string MinRequiredEmbyVersion = "4.9.1.0";

        /// <summary>
        /// 品牌区「Emby Server_vx.x.x.x +」对外展示的基准版本（与准入检查解耦）。
        /// </summary>
        public const string AdvertisedEmbyVersion = "4.10.2.0";

        public sealed override string Name => "StrmAssistantPlus";

        /// <summary>
        /// 配置文件命名前缀（保持 StrmAssistant 不变以延续旧版配置）。
        /// </summary>
        public string ConfigName => "StrmAssistant";

        public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        public string UserAgent => $"{Name}/{CurrentVersion}";

        /// <summary>
        /// 插件界面语言：固定跟随客户端，即当前请求线程的文化。
        /// </summary>
        public CultureInfo DefaultUICulture => CultureInfo.CurrentUICulture;

        public bool DebugMode;

        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public bool SupportsHarmony => RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Properties.thumb.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(GetPluginInfo(), _libraryManager, MainOptionsStore,
                            MediaInfoExtractStore, MetadataEnhanceStore, IntroSkipStore, ExperienceEnhanceStore,
                            NetworkProxyStore, _taskManager, _sessionManager)
                    };
                }

                return _pages.AsReadOnly();
            }
        }
    }
}
