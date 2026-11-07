using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Core;
using StrmAssistant.Mod;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.UIBaseClasses.Views;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
{
    internal class HomePageView : PluginPageView
    {
        private const string RunScheduledTaskCommand = "RunScheduledTask";
        private const string StopSessionCommand = "StopSession";

        private readonly PluginInfo _pluginInfo;
        private readonly PluginOptionsStore _store;
        private readonly ITaskManager _taskManager;
        private readonly ISessionManager _sessionManager;

        public HomePageView(PluginInfo pluginInfo, PluginOptionsStore store,
            ITaskManager taskManager, ISessionManager sessionManager)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _store = store;
            _taskManager = taskManager;
            _sessionManager = sessionManager;
            ContentData = store.GetOptions();

            PluginOptions.Initialize();
            PluginOptions.TaskConcurrency.Initialize();
            PluginOptions.ChineseSearch.Initialize();
            PluginOptions.AboutOptions.Initialize();
            PluginOptions.ScheduledTasks.Initialize();
            PluginOptions.Sessions.Initialize();

            PopulateScheduledTasks();
            PopulateSessions();
            PopulateOverview();
        }

        public PluginOptions PluginOptions => ContentData as PluginOptions;

        /// <summary>
        /// 生成「计划任务」列表：只列本插件自己的任务（按程序集过滤），
        /// 每行带「执行」按钮，并显示上次执行的状态、耗时与时间。
        /// </summary>
        private void PopulateScheduledTasks()
        {
            var list = PluginOptions.ScheduledTasks.TaskList;
            list.Clear();

            try
            {
                // 按程序集过滤：第三方插件可能有同名 Key 的任务，既不能被列进来，更不能被误执行
                var pluginAssembly = typeof(Plugin).Assembly;

                var workers = _taskManager.ScheduledTasks
                    .Where(t => t?.ScheduledTask != null && t.ScheduledTask.GetType().Assembly == pluginAssembly)
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();

                if (workers.Count == 0)
                {
                    list.Add(new GenericListItem
                    {
                        PrimaryText = Resources.ScheduledTasks_None,
                        Icon = IconNames.schedule
                    });
                    return;
                }

                foreach (var worker in workers)
                {
                    list.Add(BuildTaskItem(worker));
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"HomePage - 生成计划任务列表失败: {e.Message}");
                list.Add(new GenericListItem
                {
                    PrimaryText = e.Message,
                    Icon = IconNames.warning,
                    Status = ItemStatus.Failed
                });
            }
        }

        private static GenericListItem BuildTaskItem(IScheduledTaskWorker worker)
        {
            var isRunning = worker.State != TaskState.Idle;

            var item = new GenericListItem
            {
                PrimaryText = worker.Name,
                Icon = IconNames.schedule,
                Status = isRunning ? ItemStatus.InProgress : MapCompletion(worker.LastExecutionResult),
                Button1 = new ButtonItem(Resources.ScheduledTasks_Run_Run)
                {
                    Icon = IconNames.play_arrow,
                    Data1 = RunScheduledTaskCommand,
                    // 框架把按钮的 Data2 作为 itemId 回传给 RunCommand，这里放任务 Key
                    Data2 = worker.ScheduledTask.Key,
                    IsEnabled = !isRunning
                }
            };

            if (isRunning)
            {
                var percent = worker.CurrentProgress ?? 0;
                item.SecondaryText = string.Format(Resources.ScheduledTasks_Status_Running, (int)percent);
                item.HasPercentage = percent > 0;
                item.PercentComplete = (int)percent;
            }
            else
            {
                item.SecondaryText = DescribeLastRun(worker.LastExecutionResult);
            }

            return item;
        }

        private static ItemStatus MapCompletion(TaskResult result)
        {
            if (result == null || result.StartTimeUtc == default) return ItemStatus.Unavailable;

            switch (result.Status)
            {
                case TaskCompletionStatus.Completed:
                    return ItemStatus.Succeeded;
                case TaskCompletionStatus.Failed:
                case TaskCompletionStatus.Aborted:
                    return ItemStatus.Failed;
                case TaskCompletionStatus.Cancelled:
                    return ItemStatus.Warning;
                default:
                    return ItemStatus.Unknown;
            }
        }

        private static string DescribeLastRun(TaskResult result)
        {
            if (result == null || result.StartTimeUtc == default) return Resources.ScheduledTasks_NeverRun;

            var duration = result.EndTimeUtc > result.StartTimeUtc
                ? result.EndTimeUtc - result.StartTimeUtc
                : TimeSpan.Zero;

            var text = string.Format(Resources.ScheduledTasks_LastRun,
                GetStatusText(result.Status), FormatDuration(duration),
                result.EndTimeUtc.ToLocalTime().ToString("MM-dd HH:mm"));

            if (result.Status == TaskCompletionStatus.Failed && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                var error = result.ErrorMessage.Trim().Replace(Environment.NewLine, " ");
                if (error.Length > 80) error = error.Substring(0, 80) + "\u2026";
                text += " \u00b7 " + error;
            }

            return text;
        }

        private static string GetStatusText(TaskCompletionStatus status)
        {
            switch (status)
            {
                case TaskCompletionStatus.Completed:
                    return Resources.ScheduledTasks_Status_Completed;
                case TaskCompletionStatus.Failed:
                    return Resources.ScheduledTasks_Status_Failed;
                case TaskCompletionStatus.Cancelled:
                    return Resources.ScheduledTasks_Status_Cancelled;
                case TaskCompletionStatus.Aborted:
                    return Resources.ScheduledTasks_Status_Aborted;
                default:
                    return status.ToString();
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return string.Format(Resources.ScheduledTasks_Duration_Hours,
                    (int)duration.TotalHours, duration.Minutes);

            if (duration.TotalMinutes >= 1)
                return string.Format(Resources.ScheduledTasks_Duration_Minutes,
                    (int)duration.TotalMinutes, duration.Seconds);

            return string.Format(Resources.ScheduledTasks_Duration_Seconds,
                Math.Max(0, (int)duration.TotalSeconds));
        }

        private void RunScheduledTask(string key)
        {
            try
            {
                var pluginAssembly = typeof(Plugin).Assembly;
                var worker = _taskManager.ScheduledTasks.FirstOrDefault(t =>
                    t?.ScheduledTask != null &&
                    t.ScheduledTask.GetType().Assembly == pluginAssembly &&
                    string.Equals(t.ScheduledTask.Key, key, StringComparison.OrdinalIgnoreCase));

                if (worker == null)
                {
                    Notify(string.Format(Resources.ScheduledTasks_NotFound, key));
                    return;
                }

                if (worker.State != TaskState.Idle)
                {
                    Notify(string.Format(Resources.ScheduledTasks_AlreadyRunning, worker.Name));
                    return;
                }

                _taskManager.Execute(worker, new TaskOptions()).FireAndForget(Plugin.Instance.Logger);
                Notify(string.Format(Resources.ScheduledTasks_Started, worker.Name));
                Plugin.Instance.Logger.Info($"HomePage - 手动执行计划任务: {worker.Name} ({key})");
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"HomePage - 执行计划任务失败: {e.Message}");
                Notify(e.Message);
            }
        }

        private static void Notify(string message) =>
            Plugin.NotificationApi?.SendMessageToAdmins(
                $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {message}", 8000);

        /// <summary>
        /// 生成「活动会话」列表：谁在播什么，播放中的行带「停止播放」按钮。
        /// </summary>
        private void PopulateSessions()
        {
            var list = PluginOptions.Sessions.SessionList;
            list.Clear();

            try
            {
                var sessions = _sessionManager.Sessions?
                    .Where(s => s != null && !string.IsNullOrEmpty(s.Id))
                    .OrderByDescending(s => s.NowPlayingItem != null)
                    .ThenBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (sessions == null || sessions.Count == 0)
                {
                    list.Add(new GenericListItem
                    {
                        PrimaryText = Resources.Sessions_None,
                        Icon = IconNames.devices
                    });
                    return;
                }

                foreach (var session in sessions)
                {
                    list.Add(BuildSessionItem(session));
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"HomePage - 生成会话列表失败: {e.Message}");
            }
        }

        private static GenericListItem BuildSessionItem(SessionInfo session)
        {
            var playing = session.NowPlayingItem;
            var who = string.IsNullOrEmpty(session.UserName) ? session.DeviceName : session.UserName;
            var client = string.IsNullOrEmpty(session.Client) ? session.DeviceName : session.Client;

            var item = new GenericListItem
            {
                PrimaryText = string.Join(" \u00b7 ",
                    new[] { who, client }.Where(x => !string.IsNullOrEmpty(x)).Distinct()),
                Icon = IconNames.devices,
                Status = playing != null ? ItemStatus.InProgress : ItemStatus.None
            };

            if (playing == null)
            {
                item.SecondaryText = string.Format(Resources.Sessions_Idle,
                    session.LastActivityDate.ToLocalTime().ToString("MM-dd HH:mm"));
                return item;
            }

            var title = playing.SeriesName != null &&
                        playing.ParentIndexNumber.HasValue && playing.IndexNumber.HasValue
                ? $"{playing.SeriesName} S{playing.ParentIndexNumber:00}E{playing.IndexNumber:00}"
                : playing.Name;

            item.SecondaryText = string.Format(
                session.PlayState != null && session.PlayState.IsPaused
                    ? Resources.Sessions_Paused
                    : Resources.Sessions_Playing,
                title);

            item.Button1 = new ButtonItem(Resources.Sessions_Stop_Stop_Playback)
            {
                Icon = IconNames.stop,
                Data1 = StopSessionCommand,
                Data2 = session.Id
            };

            return item;
        }

        private void StopSession(string sessionId)
        {
            try
            {
                var session = _sessionManager.Sessions?
                    .FirstOrDefault(s => string.Equals(s?.Id, sessionId, StringComparison.OrdinalIgnoreCase));

                if (session == null)
                {
                    Notify(Resources.Sessions_NotFound);
                    return;
                }

                if (session.NowPlayingItem == null)
                {
                    Notify(Resources.Sessions_NotPlaying);
                    return;
                }

                var request = new PlaystateRequest
                {
                    Command = PlaystateCommand.Stop,
                    ControllingUserId = session.UserId
                };

                _sessionManager.SendPlaystateCommand(null, session.Id, request, CancellationToken.None)
                    .FireAndForget(Plugin.Instance.Logger);

                Notify(string.Format(Resources.Sessions_Stopped, session.NowPlayingItem.Name));
                Plugin.Instance.Logger.Info(
                    $"HomePage - 停止播放: {session.UserName}/{session.Client} ({session.NowPlayingItem.Name})");
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"HomePage - 停止播放失败: {e.Message}");
                Notify(e.Message);
            }
        }

        /// <summary>
        /// 生成「运行概览」：内存 / 任务 / 会话 / 队列 / 索引的实时状态，位于页面最顶部。
        /// </summary>
        private void PopulateOverview()
        {
            var list = PluginOptions.Overview.OverviewList;
            list.Clear();

            try
            {
                using var process = Process.GetCurrentProcess();
                var workingSetMb = process.WorkingSet64 / 1024 / 1024;
                var gcHeapMb = GC.GetTotalMemory(false) / 1024 / 1024;

                var memoryOptions = _store.GetOptions().Memory;
                var threshold = memoryOptions.MemoryAutoRestartThresholdMB;

                var memoryItem = new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_Memory, workingSetMb, gcHeapMb),
                    Icon = IconNames.memory
                };

                if (memoryOptions.EnableMemoryAutoRestart && threshold > 0)
                {
                    var percent = Math.Min(100, (int)(workingSetMb * 100L / threshold));
                    memoryItem.HasPercentage = true;
                    memoryItem.PercentComplete = percent;
                    memoryItem.SecondaryText = string.Format(
                        Resources.Overview_Memory_Threshold, threshold, percent);
                    memoryItem.Status = percent >= 90
                        ? ItemStatus.Failed
                        : percent >= 75
                            ? ItemStatus.Warning
                            : ItemStatus.Succeeded;
                }

                list.Add(memoryItem);

                // 内存超限倒计时：这是唯一会「自己重启服务器」的状态，必须让用户看见
                if (MemoryGuard.IsRestartPending && MemoryGuard.RestartAtUtc.HasValue)
                {
                    var seconds = Math.Max(0,
                        (int)(MemoryGuard.RestartAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);

                    list.Add(new GenericListItem
                    {
                        PrimaryText = string.Format(Resources.RuntimeStatus_RestartCountdown,
                            MemoryGuard.ThresholdMb, seconds),
                        Icon = IconNames.warning,
                        Status = ItemStatus.Failed
                    });
                }

                var runningTasks = _taskManager.ScheduledTasks
                    .Where(t => t.State == TaskState.Running)
                    .Select(t => t.ScheduledTask.Name)
                    .ToList();

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_RunningTasks,
                        runningTasks.Count, runningTasks.Count > 0 ? string.Join(", ", runningTasks) : "-"),
                    Icon = IconNames.schedule
                });

                var sessions = _sessionManager.Sessions?
                    .Where(s => !string.IsNullOrEmpty(s.Client))
                    .ToList();
                var playingCount = sessions?.Count(s => s.FullNowPlayingItem != null) ?? 0;

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_Sessions,
                        sessions?.Count ?? 0,
                        playingCount,
                        sessions is { Count: > 0 }
                            ? string.Join(", ", sessions.Take(3).Select(s => $"{s.Client}/{s.UserName}"))
                            : "-"),
                    Icon = IconNames.devices
                });

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_SearchIndex,
                        EnhanceChineseSearch.GetSearchIndexStatus()),
                    Icon = IconNames.search
                });

                var processing = new List<string>();
                if (QueueManager.IsMediaInfoProcessTaskRunning)
                {
                    processing.Add(Resources.CatchupTask_MediaInfo_MediaInfo);
                }

                if (QueueManager.IsEpisodeRefreshProcessTaskRunning)
                {
                    processing.Add(Resources.CatchupTask_EpisodeRefresh_EpisodeRefresh);
                }

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_ProcessingTasks,
                        processing.Count > 0 ? string.Join(", ", processing) : "-"),
                    Icon = IconNames.pending_actions
                });

                var mediaInfoQueue = QueueManager.MediaInfoExtractItemQueue.Count;
                var fingerprintQueue = QueueManager.FingerprintItemQueue.Count;
                var introQueue = QueueManager.IntroSkipItemQueue.Count;
                var episodeRefreshQueue = QueueManager.EpisodeRefreshItemQueue.Count;

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_PendingQueue,
                        mediaInfoQueue + fingerprintQueue + introQueue + episodeRefreshQueue,
                        mediaInfoQueue, fingerprintQueue, introQueue, episodeRefreshQueue),
                    Icon = IconNames.queue
                });

                var conflicts = ConflictDetector.GetConflictingAssemblies();
                if (conflicts.Length > 0)
                {
                    list.Add(new GenericListItem
                    {
                        PrimaryText = string.Format(Resources.RuntimeStatus_ConflictPlugin,
                            string.Join(", ", conflicts)),
                        Icon = IconNames.warning
                    });
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "DisclaimerDialog":
                    var disclaimerDialog = new DisclaimerDialogView(_pluginInfo);
                    return Task.FromResult<IPluginUIView>(disclaimerDialog);

                case RunScheduledTaskCommand:
                    RunScheduledTask(itemId);
                    PopulateScheduledTasks();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case StopSessionCommand:
                    StopSession(itemId);
                    PopulateSessions();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case "CleanMemoryNow":
                    MemoryCleaner.RequestCleanup("手动清理（选项页）");
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case "RefreshStatus":
                    PopulateOverview();
                    PopulateScheduledTasks();
                    PopulateSessions();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case "ClearMetadataCache":
                {
                    var removed = LocalMetadataCache.ClearPassiveCache();

                    Plugin.Instance.Logger.Info($"HomePage - 手动清理元数据被动缓存: {removed} 个文件");

                    Plugin.NotificationApi?.SendMessageToAdmins(
                        removed >= 0
                            ? string.Format(Resources.GeneralOptions_ClearMetadataCacheResult_Cleared, removed)
                            : Resources.GeneralOptions_ClearMetadataCacheResult_NotInitialized,
                        8000);

                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);
                }

                case "ScheduleIndexRebuild":
                    EnhanceChineseSearch.ScheduleIndexRebuild();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case "OptimizeSearchIndex":
                {
                    var optimizeResult = EnhanceChineseSearch.OptimizeSearchIndex();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    Plugin.NotificationApi?.SendMessageToAdmins(
                        optimizeResult.HasValue
                            ? string.Format(Resources.GeneralOptions_OptimizeSearchIndex_Reclaimed,
                                optimizeResult.Value.ReclaimedPages, optimizeResult.Value.ReclaimedBytes)
                            : Resources.GeneralOptions_OptimizeSearchIndex_Failed,
                        8000);
                    return Task.FromResult<IPluginUIView>(this);
                }

                case "RestartServer":
                    Task.Run(async () =>
                    {
                        Plugin.NotificationApi?.SendMessageToAdmins(
                            $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] " +
                            Resources.RuntimeStatus_ServerRestarting, 10000);
                        await Task.Delay(3000).ConfigureAwait(false);
                        Plugin.Instance.ApplicationHost.Restart();
                    }).FireAndForget(Plugin.Instance.Logger);
                    return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ContentData is PluginOptions options)
            {
                options.ValidateOrThrow();
            }

            _store.SetOptions(PluginOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
