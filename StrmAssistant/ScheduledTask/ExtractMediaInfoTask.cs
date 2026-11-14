using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.MediaInfoExtractOptions;

namespace StrmAssistant.ScheduledTask
{
    public class ExtractMediaInfoTask : IScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            _logger.Info("MediaInfoExtract - Scheduled Task Execute");

            var maxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.MaxConcurrentCount;
            _logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
            var cooldownSeconds = maxConcurrentCount == 1
                ? Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.CooldownDurationSeconds
                : (int?)null;
            if (cooldownSeconds.HasValue) _logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);

            var persistMediaInfoMode = Plugin.Instance.MediaInfoExtractStore.GetOptions().MediaInfoPersist.PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var timeoutMs = Plugin.Instance.MediaInfoExtractStore.GetOptions().ExtractionPerformance.MediaInfoExtractionTimeoutMs;
            var imageMultiplier = Plugin.Instance.MediaInfoExtractStore.GetOptions().ExtractionPerformance.ImageExtractionTimeoutMultiplier;
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().ImageCapture.EnableImageCapture;
            _logger.Info("Enable Image Capture: " + enableImageCapture);
            var enableIntroSkip = Plugin.Instance.IntroSkipStore.GetOptions().Playback.EnableIntroSkip;
            _logger.Info("Intro Skip Enabled: " + enableIntroSkip);

            if (timeoutMs > 0) _logger.Info("Media Info Extraction Timeout (ms): " + timeoutMs +
                                            ", Image Extraction Timeout Multiplier: " + imageMultiplier);

            var items = Plugin.LibraryApi.FetchPreExtractTaskItems();

            if (items.Count > 0) IsRunning = true;

            double total = items.Count;
            var index = 0;
            var current = 0;
            var skip = 0;

            var tasks = new List<Task>();

            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        await QueueManager.MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        QueueManager.MasterSemaphore.Release();
                        _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                        return;
                    }

                    var taskIndex = ++index;
                    var taskItem = item;
                    var task = Task.Run(async () =>
                    {
                        bool? result = null;
                        CancellationTokenSource timeoutCts = null;

                        try
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                                return;
                            }

                            var itemToken = cancellationToken;
                            if (timeoutMs > 0)
                            {
                                // 需要截图的条目按倍数放大超时（截图耗时显著高于纯探测）
                                var effectiveTimeout = timeoutMs;
                                if (enableImageCapture && !taskItem.HasImage(ImageType.Primary, 0))
                                {
                                    effectiveTimeout = timeoutMs * Math.Max(1, imageMultiplier);
                                }

                                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                timeoutCts.CancelAfter(effectiveTimeout);
                                itemToken = timeoutCts.Token;
                            }

                            result = await Plugin.LibraryApi
                                .OrchestrateMediaInfoProcessAsync(taskItem, "MediaInfoExtract Task", itemToken)
                                .ConfigureAwait(false);

                            if (result is null)
                            {
                                if (!mediaInfoRestoreMode)
                                {
                                    _logger.Info(
                                        $"MediaInfoExtract - Item skipped or non-existent: {taskItem.Name} - {taskItem.Path}");
                                    MediaInfoRetryQueue.RecordFailure(taskItem.InternalId, "skipped or non-existent");
                                }

                                Interlocked.Increment(ref skip);
                                return;
                            }

                            if (result is true)
                            {
                                MediaInfoRetryQueue.RecordSuccess(taskItem.InternalId);
                            }

                            if (enableIntroSkip && taskItem is Episode episode &&
                                Plugin.PlaySessionMonitor.IsLibraryInScope(episode) &&
                                Plugin.ChapterApi.SeasonHasIntroCredits(episode))
                            {
                                QueueManager.IntroSkipItemQueue.Enqueue(episode);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                _logger.Warn($"MediaInfoExtract - Item timed out ({timeoutMs}ms): {taskItem.Name} - {taskItem.Path}");
                            }
                            else
                            {
                                _logger.Info($"MediaInfoExtract - Item cancelled: {taskItem.Name} - {taskItem.Path}");
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.Error($"MediaInfoExtract - Item failed: {taskItem.Name} - {taskItem.Path}");
                            _logger.Error(e.Message);
                            _logger.Debug(e.StackTrace);
                            MediaInfoRetryQueue.RecordFailure(taskItem.InternalId, e.Message);
                        }
                        finally
                        {
                            timeoutCts?.Dispose();

                            // 强制重新提取的条目：本次已探测过，清除标记避免后续任务重复处理
                            if (LibraryApi.IsForceReExtract(taskItem.InternalId))
                            {
                                LibraryApi.ClearForceReExtract(taskItem.InternalId);
                                _logger.Info($"MediaInfoExtract - Force re-extract completed: {taskItem.Name}");
                            }

                            if (result is true && cooldownSeconds.HasValue)
                            {
                                try
                                {
                                    await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }

                            QueueManager.MasterSemaphore.Release();

                            var currentCount = Interlocked.Increment(ref current);
                            progress.Report(currentCount / total * 100);

                            if (!mediaInfoRestoreMode)
                            {
                                _logger.Info(
                                    $"MediaInfoExtract - Progress {currentCount}/{total} - Task {taskIndex}: {taskItem.Path}");
                            }
                        }
                    }, CancellationToken.None);
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                progress.Report(100.0);
                _logger.Info($"MediaInfoExtract - Number of items skipped: {skip}");
                MediaInfoRetryQueue.Save(true);
                _logger.Info("MediaInfoExtract - Scheduled Task Complete");
            }
            finally
            {
                // 取消路径（含 WaitAsync 抛出）必须复位，否则自动补漏会一直误判任务在跑
                if (items.Count > 0) IsRunning = false;
            }
        }

        public string Category =>
            Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                Plugin.Instance.DefaultUICulture);

        public string Key => "MediaInfoExtractTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExtractMediaInfoTask_Description_Extracts_media_info_from_videos_and_audios",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString("ExtractMediaInfoTask_Name_Extract_MediaInfo",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
