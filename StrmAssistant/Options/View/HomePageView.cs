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
            PopulateOverview();
        }

        public PluginOptions PluginOptions => ContentData as PluginOptions;

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

                var processing = new List<string>();
                if (QueueManager.IsMediaInfoProcessTaskRunning)
                {
                    processing.Add(Resources.CatchupTask_MediaInfo_MediaInfo);
                }

                if (QueueManager.IsEpisodeRefreshProcessTaskRunning)
                {
                    processing.Add(Resources.CatchupTask_EpisodeRefresh_EpisodeRefresh);
                }

                var mediaInfoQueue = QueueManager.MediaInfoExtractItemQueue.Count;
                var fingerprintQueue = QueueManager.FingerprintItemQueue.Count;
                var introQueue = QueueManager.IntroSkipItemQueue.Count;
                var episodeRefreshQueue = QueueManager.EpisodeRefreshItemQueue.Count;
                var pendingQueue = mediaInfoQueue + fingerprintQueue + introQueue + episodeRefreshQueue;

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.Overview_Tasks_Queue,
                        runningTasks.Count, pendingQueue),
                    SecondaryText = string.Format(Resources.Overview_Tasks_Queue_Detail,
                        runningTasks.Count > 0 ? string.Join(", ", runningTasks) : "-",
                        processing.Count > 0 ? string.Join(", ", processing) : "-",
                        mediaInfoQueue, fingerprintQueue, introQueue, episodeRefreshQueue),
                    Icon = IconNames.queue,
                    Status = runningTasks.Count > 0 || pendingQueue > 0
                        ? ItemStatus.InProgress
                        : ItemStatus.None
                });

                list.Add(new GenericListItem
                {
                    PrimaryText = string.Format(Resources.RuntimeStatus_SearchIndex,
                        EnhanceChineseSearch.GetSearchIndexStatus()),
                    Icon = IconNames.search
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
