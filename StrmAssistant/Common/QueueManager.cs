using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using StrmAssistant.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.TaskConcurrencyOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public static class QueueManager
    {
        private static readonly ILogger Logger;
        private static DateTime _mediaInfoProcessLastRunTime = DateTime.MinValue;
        private static DateTime _introSkipProcessLastRunTime = DateTime.MinValue;
        private static DateTime _fingerprintProcessLastRunTime = DateTime.MinValue;
        private static DateTime _episodeRefreshProcessLastRunTime = DateTime.MinValue;
        private static readonly TimeSpan ThrottleInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 批处理中 task 列表的剔除阈值：超过该数量就把已完成的 task 移除，
        /// 否则闭包捕获的 BaseItem 会一直活到 Task.WhenAll 返回，大批量刮削时 RSS 持续上涨。
        /// </summary>
        private const int TaskPruneThreshold = 100;

        /// <summary>
        /// 批内触发内存归还的间隔（每处理这么多条触发一次，实际是否执行由 MemoryCleaner 自身节流决定）。
        /// </summary>
        private const int MidBatchCleanupInterval = 500;

        private static readonly Random Random = new Random();
        private static readonly object ConcurrencySettingsLock = new object();
        private static int _currentMasterMaxConcurrentCount;
        private static int _currentTier2MaxConcurrentCount;

        public static CancellationTokenSource MediaInfoTokenSource;
        public static CancellationTokenSource IntroSkipTokenSource;
        public static CancellationTokenSource FingerprintTokenSource;
        public static CancellationTokenSource EpisodeRefreshTokenSource;
        public static AdjustableSemaphore MasterSemaphore;
        public static AdjustableSemaphore Tier2Semaphore;
        /// <summary>
        /// 并行声纹计算专用信号量（CPU 核数），仅在 ParallelMarkerComputation 开启时由
        /// ExtractIntroFingerprintTask 使用，绕过为 ffprobe 设计的 MasterSemaphore 限流。
        /// </summary>
        public static AdjustableSemaphore ParallelMarkerSemaphore;
        public static ConcurrentQueue<BaseItem> MediaInfoExtractItemQueue = new ConcurrentQueue<BaseItem>();
        public static ConcurrentQueue<Episode> IntroSkipItemQueue = new ConcurrentQueue<Episode>();
        public static ConcurrentQueue<BaseItem> FingerprintItemQueue = new ConcurrentQueue<BaseItem>();
        public static ConcurrentQueue<Episode> EpisodeRefreshItemQueue = new ConcurrentQueue<Episode>(); 
        public static Task MediaInfoProcessTask;
        public static Task FingerprintProcessTask;
        public static Task EpisodeRefreshProcessTask;

        public static bool IsMediaInfoProcessTaskRunning { get; private set; }
        public static bool IsEpisodeRefreshProcessTaskRunning { get; private set; }

        static QueueManager()
        {
            Logger = Plugin.Instance.Logger;
            _currentMasterMaxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.MaxConcurrentCount;
            _currentTier2MaxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.Tier2MaxConcurrentCount;

            MasterSemaphore = new AdjustableSemaphore(_currentMasterMaxConcurrentCount);
            Tier2Semaphore = new AdjustableSemaphore(_currentTier2MaxConcurrentCount);
            ParallelMarkerSemaphore = new AdjustableSemaphore(Math.Max(1, Environment.ProcessorCount));
        }

        public static void Initialize()
        {
            if (MediaInfoProcessTask is null)
            {
                MediaInfoExtractItemQueue.Clear();
                MediaInfoProcessTask = MediaInfo_ProcessItemQueueAsync()
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Debug(
                                $"(Trace) MediaInfo_ProcessItemQueueAsync terminated unexpectedly. Exception: {task.Exception?.Flatten()}");
                        }
                        else if (task.IsCanceled)
                        {
                            Logger.Debug("(Trace) MediaInfo_ProcessItemQueueAsync was canceled.");
                        }
                        else
                        {
                            Logger.Debug("(Trace) MediaInfo_ProcessItemQueueAsync completed successfully.");
                        }

                        MediaInfoProcessTask = null;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            if (FingerprintProcessTask is null)
            {
                FingerprintItemQueue.Clear();
                FingerprintProcessTask = Fingerprint_ProcessItemQueueAsync()
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Debug(
                                $"(Trace) Fingerprint_ProcessItemQueueAsync terminated unexpectedly. Exception: {task.Exception?.Flatten()}");
                        }
                        else if (task.IsCanceled)
                        {
                            Logger.Debug("(Trace) Fingerprint_ProcessItemQueueAsync was canceled.");
                        }
                        else
                        {
                            Logger.Debug("(Trace) Fingerprint_ProcessItemQueueAsync completed successfully.");
                        }

                        FingerprintProcessTask = null;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            if (EpisodeRefreshProcessTask is null)
            {
                EpisodeRefreshItemQueue.Clear();
                EpisodeRefreshProcessTask = EpisodeRefresh_ProcessItemQueueAsync()
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Debug(
                                $"(Trace) EpisodeRefresh_ProcessItemQueueAsync terminated unexpectedly. Exception: {task.Exception?.Flatten()}");
                        }
                        else if (task.IsCanceled)
                        {
                            Logger.Debug("(Trace) EpisodeRefresh_ProcessItemQueueAsync was canceled.");
                        }
                        else
                        {
                            Logger.Debug("(Trace) EpisodeRefresh_ProcessItemQueueAsync completed successfully.");
                        }

                        EpisodeRefreshProcessTask = null;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        public static void UpdateMasterSemaphore(int maxConcurrentCount)
        {
            lock (ConcurrencySettingsLock)
            {
                MasterSemaphore.Resize(maxConcurrentCount);
                _currentMasterMaxConcurrentCount = maxConcurrentCount;
            }
        }

        public static void UpdateTier2Semaphore(int maxConcurrentCount)
        {
            lock (ConcurrencySettingsLock)
            {
                Tier2Semaphore.Resize(maxConcurrentCount);
                _currentTier2MaxConcurrentCount = maxConcurrentCount;
            }
        }

        /// <summary>
        /// 追更最小等待：入库不足设定时间的条目重新入队，下个节流周期再试，
        /// 避免在初始刮削完成前就跑媒体信息提取/声纹/刷新。
        ///
        /// 粒度是**秒**。容差固定 1 秒：旧实现留 5 秒容差，在秒级设置下会把
        /// 5 秒以内的设置完全吃掉（等于设了没用）。
        /// </summary>
        private static List<T> FilterByCatchupMinWait<T>(List<T> items, Action<T> requeue) where T : BaseItem
        {
            var minWaitSeconds = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency
                .EffectiveCatchupMinWaitSeconds;

            if (minWaitSeconds <= 0 || items.Count == 0) return items;

            var minWait = TimeSpan.FromSeconds(minWaitSeconds);
            var tolerance = TimeSpan.FromSeconds(1);
            var ready = new List<T>();

            foreach (var item in items)
            {
                var wait = minWait - (DateTime.UtcNow - item.DateCreated);

                if (wait > tolerance) requeue(item);
                else ready.Add(item);
            }

            if (ready.Count < items.Count)
            {
                Logger.Info(
                    $"Catchup - {items.Count - ready.Count} item(s) within min wait time ({minWaitSeconds} s), deferred");
            }

            return ready;
        }

        public static async Task MediaInfo_ProcessItemQueueAsync()
        {
            Logger.Info("MediaInfo - ProcessItemQueueAsync Started");

            MediaInfoTokenSource = new CancellationTokenSource();
            var cancellationToken = MediaInfoTokenSource.Token;

            var dequeueItems = new List<BaseItem>();
            var deferredItems = new List<BaseItem>();
            var tasks = new List<Task>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _mediaInfoProcessLastRunTime;
                var remainingTime = ThrottleInterval - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }

                if (!MediaInfoExtractItemQueue.IsEmpty)
                {
                    var maxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.MaxConcurrentCount;
                    var currentQueueCount = MediaInfoExtractItemQueue.Count;
                    
                    dequeueItems.Clear();
                    deferredItems.Clear();

                    while (MediaInfoExtractItemQueue.TryDequeue(out var dequeueItem))
                    {
                        var library = dequeueItem.GetTopParent();
                        var progress = library?.GetRefreshProgress();

                        if (currentQueueCount < maxConcurrentCount && progress.HasValue)
                        {
                            deferredItems.Add(dequeueItem);
                        }
                        else
                        {
                            dequeueItems.Add(dequeueItem);
                        }
                    }

                    Logger.Info("MediaInfoExtract - Clear Item Queue Started");

                    var dedupQueueItems =
                        dequeueItems.GroupBy(i => i.InternalId).Select(g => g.First()).ToList();
                    var mediaInfoItems = FilterByCatchupMinWait(
                        Plugin.LibraryApi.FetchExtractQueueItems(dedupQueueItems),
                        i => MediaInfoExtractItemQueue.Enqueue(i));

                    if (mediaInfoItems.Count > 0)
                    {
                        Logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
                        var cooldownSeconds = maxConcurrentCount == 1
                            ? Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.CooldownDurationSeconds
                            : (int?)null;
                        if (cooldownSeconds.HasValue)
                            Logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);
                        Logger.Info("Tier2 Max Concurrent Count: " +
                                    Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.Tier2MaxConcurrentCount);

                        IsMediaInfoProcessTaskRunning = true;

                        foreach (var item in mediaInfoItems)
                        {
                            var taskItem = item;

                            try
                            {
                                await MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                break;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                MasterSemaphore.Release();
                                Logger.Info("MediaInfoExtract - Item Cancelled: " + taskItem.Name + " - " +
                                            taskItem.Path);
                                break;
                            }

                            var task = Task.Run(async () =>
                            {
                                bool? result = null;

                                try
                                {
                                    // 取消判断必须放在 delegate 内部：
                                    // Task.Run 若带一个已取消的令牌，delegate 可能完全不执行，
                                    // 其 finally 随之消失 → 许可永久丢失（容量为 1 时后续任务全部卡住）。
                                    if (cancellationToken.IsCancellationRequested) return;
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        Logger.Info("MediaInfoExtract - Item cancelled: " + taskItem.Name + " - " +
                                                    taskItem.Path);
                                        return;
                                    }

                                    result = await Plugin.LibraryApi
                                        .OrchestrateMediaInfoProcessAsync(taskItem, "MediaInfoExtract Catchup",
                                            cancellationToken).ConfigureAwait(false);

                                    if (result is null)
                                    {
                                        Logger.Info("MediaInfoExtract - Item skipped or non-existent: " + taskItem.Name + " - " +
                                                    taskItem.Path);
                                        return;
                                    }

                                    if (IsCatchupTaskSelected(CatchupTask.IntroSkip) &&
                                        taskItem is Episode episode && Plugin.PlaySessionMonitor.IsLibraryInScope(episode))
                                    {
                                        IntroSkipItemQueue.Enqueue(episode);
                                    }

                                    Logger.Info("MediaInfoExtract - Item processed: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (OperationCanceledException)
                                {
                                    Logger.Info("MediaInfoExtract - Item cancelled: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (Exception e)
                                {
                                    Logger.Error("MediaInfoExtract - Item failed: " + taskItem.Name + " - " +
                                                 taskItem.Path);
                                    Logger.Error(e.Message);
                                    Logger.Debug(e.StackTrace);
                                }
                                finally
                                {
                                    if (result is true && cooldownSeconds.HasValue)
                                    {
                                        try
                                        {
                                            await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken)
                                                .ConfigureAwait(false);
                                        }
                                        catch
                                        {
                                            // ignored
                                        }
                                    }

                                    MasterSemaphore.Release();
                                }
                            }, CancellationToken.None);   // 不用取消令牌调度：delegate 必须执行才能释放许可
                            tasks.Add(task);

                            // 周期性剔除已完成的 task，释放闭包捕获的 BaseItem 引用，
                            // 避免大批量刮削期间整批引用堆积导致 RSS 持续上涨
                            if (tasks.Count >= TaskPruneThreshold)
                            {
                                tasks.RemoveAll(t => t.IsCompleted);
                            }

                            // 中段尝试一次内存归还（受 MemoryCleaner 自身节流约束，不会过频）
                            if (tasks.Count > 0 && tasks.Count % MidBatchCleanupInterval == 0)
                            {
                                MemoryCleaner.RequestCleanup("MediaInfoExtract Mid-Batch");
                            }
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                        tasks.Clear();

                        // 显式释放本批所有元素，下一批之前不再钉住引用
                        mediaInfoItems.Clear();
                        dedupQueueItems.Clear();

                        IsMediaInfoProcessTaskRunning = false;

                        // 一批刮削完成后，触发一次内存归还
                        MemoryCleaner.RequestCleanup("MediaInfoExtract Batch");
                    }

                    Logger.Info("MediaInfoExtract - Clear Item Queue Stopped");
                }

                if (deferredItems.Count > 0)
                {
                    Logger.Info("MediaInfoExtract - Enqueue Deferred Count: " + deferredItems.Count);
                        
                    foreach (var item in deferredItems)
                    {
                        MediaInfoExtractItemQueue.Enqueue(item);
                    }
                }

                _mediaInfoProcessLastRunTime = DateTime.UtcNow;
            }

            if (MediaInfoExtractItemQueue.IsEmpty)
            {
                Logger.Info("MediaInfo - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("MediaInfo - ProcessItemQueueAsync Cancelled");
            }
        }

        public static async Task Fingerprint_ProcessItemQueueAsync()
        {
            Logger.Info("Fingerprint - ProcessItemQueueAsync Started");

            FingerprintTokenSource = new CancellationTokenSource();
            var cancellationToken = FingerprintTokenSource.Token;

            var dequeueItems = new List<BaseItem>();
            var deferredItems = new List<BaseItem>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _fingerprintProcessLastRunTime;
                var remainingTime = ThrottleInterval - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }

                if (!FingerprintItemQueue.IsEmpty)
                {
                    var maxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.MaxConcurrentCount;
                    var currentQueueCount = FingerprintItemQueue.Count;
                    
                    dequeueItems.Clear();
                    deferredItems.Clear();

                    while (FingerprintItemQueue.TryDequeue(out var dequeueItem))
                    {
                        var library = dequeueItem.GetTopParent();
                        var progress = library?.GetRefreshProgress();

                        if (currentQueueCount < maxConcurrentCount && progress.HasValue)
                        {
                            deferredItems.Add(dequeueItem);
                        }
                        else
                        {
                            dequeueItems.Add(dequeueItem);
                        }
                    }

                    Logger.Info("IntroFingerprintExtract - Clear Item Queue Started");

                    var episodes = Plugin.FingerprintApi.FetchFingerprintQueueItems(dequeueItems);

                    if (IsCatchupTaskSelected(CatchupTask.MediaInfo, CatchupTask.IntroSkip))
                    {
                        var episodeIds = episodes.Select(e => e.InternalId).ToHashSet();
                        var mediaInfoItems = dequeueItems.Where(i => !episodeIds.Contains(i.InternalId));

                        foreach (var item in mediaInfoItems)
                        {
                            MediaInfoExtractItemQueue.Enqueue(item);
                        }
                    }

                    episodes = FilterByCatchupMinWait(episodes, i => FingerprintItemQueue.Enqueue(i));

                    if (episodes.Count > 0)
                    {
                        Logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
                        var cooldownSeconds = maxConcurrentCount == 1
                            ? Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.CooldownDurationSeconds
                            : (int?)null;
                        if (cooldownSeconds.HasValue)
                            Logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);
                        var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().ImageCapture.EnableImageCapture;

                        var groupedBySeason = episodes.GroupBy(e => e.Season).ToList();
                        var seasonTasks = new List<Task>();
                        
                        IsMediaInfoProcessTaskRunning = true;

                        foreach (var season in groupedBySeason)
                        {
                            var taskSeason = season.Key;

                            if (cancellationToken.IsCancellationRequested)
                            {
                                Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name + " - " +
                                            taskSeason.Path);
                                break;
                            }

                            var episodeTasks = new List<Task>();
                            var seasonSkip = false;

                            foreach (var episode in season)
                            {
                                var taskItem = episode;

                                try
                                {
                                    await MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                                }
                                catch
                                {
                                    break;
                                }

                                if (cancellationToken.IsCancellationRequested)
                                {
                                    MasterSemaphore.Release();
                                    break;
                                }

                                var task = Task.Run(async () =>
                                {
                                    bool? result1 = null;
                                    Tuple<string, bool> result2 = null;

                                    try
                                    {
                                        if (cancellationToken.IsCancellationRequested)
                                        {
                                            Logger.Info("IntroFingerprintExtract - Episode cancelled: " +
                                                        taskItem.Name + " - " + taskItem.Path);
                                            return;
                                        }

                                        if (Plugin.LibraryApi.IsExtractNeeded(taskItem, enableImageCapture))
                                        {
                                            result1 = await Plugin.LibraryApi.OrchestrateMediaInfoProcessAsync(taskItem,
                                                    "IntroFingerprintExtract Catchup", cancellationToken)
                                                .ConfigureAwait(false);

                                            if (result1 is null)
                                            {
                                                Logger.Info(
                                                    "IntroFingerprintExtract - Episode skipped or non-existent: " +
                                                    taskItem.Name + " - " + taskItem.Path);
                                                seasonSkip = true;
                                                return;
                                            }
                                        }

                                        var dateCreated = taskItem.DateCreated;
                                        taskItem.DateCreated = new DateTimeOffset(
                                            new DateTime(dateCreated.Year, dateCreated.Month, dateCreated.Day,
                                                dateCreated.Hour, dateCreated.Minute, dateCreated.Second),
                                            dateCreated.Offset);

                                        result2 = await Plugin.FingerprintApi
                                            .CreateTitleFingerprint(taskItem, cancellationToken)
                                            .ConfigureAwait(false);

                                        Logger.Info("IntroFingerprintExtract - Episode processed: " + taskItem.Name +
                                                    " - " + taskItem.Path);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Logger.Info("IntroFingerprintExtract - Episode cancelled: " + taskItem.Name +
                                                    " - " + taskItem.Path);
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.Error("IntroFingerprintExtract - Episode failed: " + taskItem.Name +
                                                     " - " + taskItem.Path);
                                        Logger.Error(e.Message);
                                        Logger.Debug(e.StackTrace);
                                    }
                                    finally
                                    {
                                        if ((result1 is true || result2?.Item2 is true) && cooldownSeconds.HasValue)
                                        {
                                            try
                                            {
                                                await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken)
                                                    .ConfigureAwait(false);
                                            }
                                            catch
                                            {
                                                // ignored
                                            }
                                        }

                                        MasterSemaphore.Release();
                                    }
                                }, cancellationToken);
                                episodeTasks.Add(task);
                            }

                            if (seasonSkip)
                            {
                                await Task.WhenAll(episodeTasks).ConfigureAwait(false);

                                Logger.Info("IntroFingerprintExtract - Season skipped: " + taskSeason.Name + " - " +
                                            taskSeason.Path);
                            }
                            else
                            {
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name +
                                                " - " + taskSeason.Path);
                                    break;
                                }

                                var seasonTask = Task.Run(async () =>
                                {
                                    await Task.WhenAll(episodeTasks).ConfigureAwait(false);

                                    try
                                    {
                                        await Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                        return;
                                    }

                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        Tier2Semaphore.Release();
                                        Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name +
                                                    " - " + taskSeason.Path);
                                        return;
                                    }

                                    try
                                    {
                                        await Plugin.FingerprintApi
                                            .UpdateIntroMarkerForSeason(taskSeason, cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name +
                                                    " - " + taskSeason.Path);
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.Error("IntroFingerprintExtract - Season failed: " + taskSeason.Name +
                                                     " - " + taskSeason.Path);
                                        Logger.Error(e.Message);
                                        Logger.Debug(e.StackTrace);
                                    }
                                    finally
                                    {
                                        Tier2Semaphore.Release();
                                    }
                                }, cancellationToken);
                                seasonTasks.Add(seasonTask);

                                // 大型剧集库下 seasonTasks 也会累积，闭包会钉住整季 Episode 引用
                                if (seasonTasks.Count >= TaskPruneThreshold)
                                {
                                    seasonTasks.RemoveAll(t => t.IsCompleted);
                                }
                            }
                        }
                        await Task.WhenAll(seasonTasks).ConfigureAwait(false);
                        seasonTasks.Clear();
                        episodes.Clear();
                        dequeueItems.Clear();

                        IsMediaInfoProcessTaskRunning = false;

                        // 指纹提取通常伴随大量 PCM/JSON 缓冲，结束后立即触发清理
                        MemoryCleaner.RequestCleanup("IntroFingerprintExtract Batch");
                    }

                    Logger.Info("IntroFingerprintExtract - Clear Item Queue Stopped");
                }

                if (deferredItems.Count > 0)
                {
                    Logger.Info("IntroFingerprintExtract - Enqueue Deferred Count: " + deferredItems.Count);

                    foreach (var item in deferredItems)
                    {
                        FingerprintItemQueue.Enqueue(item);
                    }
                }

                _fingerprintProcessLastRunTime = DateTime.UtcNow;
            }

            if (FingerprintItemQueue.IsEmpty)
            {
                Logger.Info("Fingerprint - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("Fingerprint - ProcessItemQueueAsync Cancelled");
            }
        }

        public static async Task IntroSkip_ProcessItemQueueAsync()
        {
            Logger.Info("IntroSkip - ProcessItemQueueAsync Started");

            IntroSkipTokenSource = new CancellationTokenSource();
            var cancellationToken = IntroSkipTokenSource.Token;

            var dequeueItems = new List<Episode>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _introSkipProcessLastRunTime;
                var remainingTime = ThrottleInterval - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }

                if (!IntroSkipItemQueue.IsEmpty)
                {
                    dequeueItems.Clear();
                    while (IntroSkipItemQueue.TryDequeue(out var dequeueItem))
                    {
                        dequeueItems.Add(dequeueItem);
                    }

                    if (dequeueItems.Count > 0)
                    {
                        Logger.Info("IntroSkip - Clear Item Queue Started");

                        Plugin.ChapterApi.PopulateIntroCredits(dequeueItems);

                        Logger.Info("IntroSkip - Clear Item Queue Stopped");
                    }
                }
                _introSkipProcessLastRunTime = DateTime.UtcNow;
            }

            if (IntroSkipItemQueue.IsEmpty)
            {
                Logger.Info("IntroSkip - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("IntroSkip - ProcessItemQueueAsync Cancelled");
            }
        }

        public static async Task EpisodeRefresh_ProcessItemQueueAsync()
        {
            Logger.Info("EpisodeRefresh - ProcessItemQueueAsync Started");

            EpisodeRefreshTokenSource = new CancellationTokenSource();
            var cancellationToken = EpisodeRefreshTokenSource.Token;

            var dequeueItems = new List<Episode>();
            var tasks = new List<Task>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _episodeRefreshProcessLastRunTime;
                var remainingTime = ThrottleInterval - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }

                if (!EpisodeRefreshItemQueue.IsEmpty)
                {
                    dequeueItems.Clear();

                    while (EpisodeRefreshItemQueue.TryDequeue(out var dequeueItem))
                    {
                        dequeueItems.Add(dequeueItem);
                    }

                    Logger.Info("EpisodeRefresh - Clear Item Queue Started");


                    var itemsToRefresh = FilterByCatchupMinWait(
                        Plugin.LibraryApi.FetchEpisodeRefreshQueueItems(dequeueItems),
                        i => EpisodeRefreshItemQueue.Enqueue(i));

                    if (itemsToRefresh.Count > 0)
                    {
                        var tier2MaxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.Tier2MaxConcurrentCount;
                        Logger.Info("Tier2 Max Concurrent Count: " + tier2MaxConcurrentCount);

                        IsEpisodeRefreshProcessTaskRunning = true;

                        foreach (var item in itemsToRefresh)
                        {
                            var taskItem = item;

                            try
                            {
                                await Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                break;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                Tier2Semaphore.Release();
                                Logger.Info("EpisodeRefresh - Item Cancelled: " + taskItem.Name + " - " +
                                            taskItem.Path);
                                break;
                            }

                            var task = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(
                                            Random.Next(0,
                                                Math.Max(0, tier2MaxConcurrentCount - Tier2Semaphore.CurrentCount) *
                                                MetadataApi.RequestIntervalMs), cancellationToken)
                                        .ConfigureAwait(false);

                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        Logger.Info("EpisodeRefresh - Item cancelled: " + taskItem.Name + " - " +
                                                    taskItem.Path);
                                        return;
                                    }

                                    EnableItemExclusiveFeatures(taskItem.InternalId, ExclusiveControl.CatchAllBlock,
                                        ExclusiveControl.IgnoreExtSubChange);

                                    await Plugin.LibraryApi.RefreshEpisodeMetadata(taskItem, cancellationToken)
                                        .ConfigureAwait(false);

                                    Logger.Info("EpisodeRefresh - Item processed: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (OperationCanceledException)
                                {
                                    Logger.Info("EpisodeRefresh - Item cancelled: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (Exception e)
                                {
                                    Logger.Error("EpisodeRefresh - Item failed: " + taskItem.Name + " - " +
                                                 taskItem.Path);
                                    Logger.Error(e.Message);
                                    Logger.Debug(e.StackTrace);
                                }
                                finally
                                {
                                    Tier2Semaphore.Release();
                                    ClearItemExclusiveFeatures(taskItem.InternalId);
                                }
                            }, cancellationToken);
                            tasks.Add(task);

                            // 周期性剔除已完成的 task，释放它们捕获的 Episode 闭包
                            if (tasks.Count >= TaskPruneThreshold)
                            {
                                tasks.RemoveAll(t => t.IsCompleted);
                            }

                            if (tasks.Count > 0 && tasks.Count % MidBatchCleanupInterval == 0)
                            {
                                MemoryCleaner.RequestCleanup("EpisodeRefresh Mid-Batch");
                            }
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                        tasks.Clear();
                        itemsToRefresh.Clear();

                        IsEpisodeRefreshProcessTaskRunning = false;

                        MemoryCleaner.RequestCleanup("EpisodeRefresh Batch");
                    }

                    Logger.Info("EpisodeRefresh - Clear Item Queue Stopped");
                }

                _episodeRefreshProcessLastRunTime = DateTime.UtcNow;
            }

            if (EpisodeRefreshItemQueue.IsEmpty)
            {
                Logger.Info("EpisodeRefresh - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("EpisodeRefresh - ProcessItemQueueAsync Cancelled");
            }
        }

        public static void Dispose()
        {
            MediaInfoTokenSource?.Cancel();
            MediaInfoTokenSource?.Dispose();
            MediaInfoTokenSource = null;

            IntroSkipTokenSource?.Cancel();
            IntroSkipTokenSource?.Dispose();
            IntroSkipTokenSource = null;

            FingerprintTokenSource?.Cancel();
            FingerprintTokenSource?.Dispose();
            FingerprintTokenSource = null;

            EpisodeRefreshTokenSource?.Cancel();
            EpisodeRefreshTokenSource?.Dispose();
            EpisodeRefreshTokenSource = null;
        }
    }
}
