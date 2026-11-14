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
    /// <summary>
    /// 规范化外挂音轨 / 字幕的语言标签。
    ///
    /// 解决的问题：Emby 用户偏好里选了「简体中文优先」，播放时却仍落到英文轨道。
    /// 根因是外挂字幕的语言由 Emby 按文件名推断，中文命名常落成 und，
    /// 而 Emby 的 MediaStreamSelector 正是拿 MediaStream.Language 做语言匹配。
    ///
    /// 本任务只改标签，不介入选轨逻辑；修好之后 Emby 自带的
    /// AudioLanguagePreference / SubtitleLanguagePreference / SubtitleMode 自然生效。
    /// 开关关闭时任务直接退出，行为与现状一致。
    /// </summary>
    public class NormalizeTrackLanguageTask : IScheduledTask
    {
        public string Key => "NormalizeTrackLanguageTask";

        public string Name => Resources.ResourceManager.GetString(
            "NormalizeTrackLanguageTask_Name_Normalize_Track_Language", Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "NormalizeTrackLanguageTask_Description_Fix_language_tags_on_external_audio_and_subtitles",
            Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString(
            "PluginOptions_EditorTitle_Strm_Assistant", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            var logger = Plugin.Instance.Logger;

            if (!SubtitleApi.IsLanguageNormalizeEnabled())
            {
                logger.Info("TrackLanguage - 开关未开启，任务跳过");
                progress.Report(100);
                return;
            }

            if (DryRunMode.Skip("normalize track language tags", logger))
            {
                progress.Report(100);
                return;
            }

            var persistMediaInfoMode =
                Plugin.Instance.MediaInfoExtractStore.GetOptions().MediaInfoPersist.PersistMediaInfoMode;
            logger.Info("TrackLanguage - Persist MediaInfo Mode: " + persistMediaInfoMode);
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();

            var items = Plugin.LibraryApi.FetchPostExtractTaskItems(false);
            logger.Info("TrackLanguage - Number of items: " + items.Count);

            var total = items.Count;
            var processed = 0;
            var changedItems = 0;
            var changedStreams = 0;
            var tasks = new List<Task>();

            foreach (var item in items)
            {
                try
                {
                    await QueueManager.Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    QueueManager.Tier2Semaphore.Release();
                    logger.Info("TrackLanguage - Scheduled Task Cancelled");
                    return;
                }

                var taskItem = item;
                var task = Task.Run(() =>
                {
                    try
                    {
                        var changed = Plugin.SubtitleApi.NormalizeTrackLanguages(taskItem, persistMediaInfo);
                        if (changed > 0)
                        {
                            Interlocked.Increment(ref changedItems);
                            Interlocked.Add(ref changedStreams, changed);
                            logger.Info($"TrackLanguage - {taskItem.Name}: 修正 {changed} 条轨道");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        logger.Info("TrackLanguage - Item cancelled: " + taskItem.Name);
                    }
                    catch (Exception e)
                    {
                        logger.Info("TrackLanguage - Item failed: " + taskItem.Name);
                        logger.Debug(e.Message);
                        logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        QueueManager.Tier2Semaphore.Release();

                        var current = Interlocked.Increment(ref processed);
                        progress.Report(total > 0 ? current / (double)total * 100 : 100);
                    }
                }, CancellationToken.None);

                tasks.Add(task);
                await Task.Delay(10).ConfigureAwait(false);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress.Report(100);

            // 一条通知即可：0 改动时不打扰管理员
            if (changedStreams > 0)
            {
                var message = string.Format(Resources.NormalizeTrackLanguageTask_Notify_Completed,
                    total, changedItems, changedStreams);
                Plugin.NotificationApi?.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {message}", 8000);
                logger.Info("TrackLanguage - " + message);
            }
            else
            {
                logger.Info($"TrackLanguage - 完成：检查 {total} 个条目，未发现需要修正的语言标签");
            }
        }
    }
}
