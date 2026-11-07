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
using StrmAssistant.Web.Service;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
        /// 刷新「运行概览」状态条：内存 / 任务 / 队列 / 索引，异常时追加冲突插件与重启倒计时。
        /// 只占一行，放在品牌之下、总开关之上。
        /// </summary>
        private void PopulateOverview()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var workingSetMb = process.WorkingSet64 / 1024 / 1024;

                var memoryOptions = _store.GetOptions().Memory;
                var threshold = memoryOptions.MemoryAutoRestartThresholdMB;
                var percent = threshold > 0
                    ? Math.Min(100, (int)(workingSetMb * 100L / threshold))
                    : 0;

                var runningTasks = _taskManager.ScheduledTasks.Count(t => t.State == TaskState.Running);

                var pendingQueue = QueueManager.MediaInfoExtractItemQueue.Count
                                   + QueueManager.FingerprintItemQueue.Count
                                   + QueueManager.IntroSkipItemQueue.Count
                                   + QueueManager.EpisodeRefreshItemQueue.Count;

                var conflicts = ConflictDetector.GetConflictingAssemblies();

                var text = string.Format(Resources.Overview_Status_Text,
                    workingSetMb, threshold, percent, runningTasks, pendingQueue,
                    EnhanceChineseSearch.GetSearchIndexStatus());

                if (conflicts.Length > 0)
                {
                    text += string.Format(Resources.Overview_Status_Conflict, string.Join(", ", conflicts));
                }

                if (DryRunMode.Enabled)
                {
                    text += Resources.Overview_Status_DryRun;
                }

                if (MemoryGuard.IsRestartPending && MemoryGuard.RestartAtUtc.HasValue)
                {
                    var seconds = Math.Max(0,
                        (int)(MemoryGuard.RestartAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);

                    text += string.Format(Resources.Overview_Status_Restart, seconds);
                }

                var status = MemoryGuard.IsRestartPending || conflicts.Length > 0 || percent >= 90
                    ? ItemStatus.Failed
                    : percent >= 75 || runningTasks > 0 || pendingQueue > 0 || DryRunMode.Enabled
                        ? ItemStatus.Warning
                        : ItemStatus.Succeeded;

                PluginOptions.OverviewStatus = new StatusItem(
                    Resources.Overview_Status_Caption, text, status);
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
                    if (DryRunMode.Skip("clear passive metadata cache", Plugin.Instance.Logger))
                    {
                        PopulateOverview();
                        RaiseUIViewInfoChanged();
                        return Task.FromResult<IPluginUIView>(this);
                    }

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

                case "ExportConfig":
                    HandleExportConfig();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case "ImportConfig":
                    HandleImportConfig();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

                case "VerifyMetadataCache":
                    HandleVerifyMetadataCache();
                    PopulateOverview();
                    RaiseUIViewInfoChanged();
                    return Task.FromResult<IPluginUIView>(this);

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

        private const string ConfigFileName = "StrmAssistant-Config.json";

        private string ResolveConfigFolder()
        {
            var folder = Plugin.Instance.MainOptionsStore.GetOptions().ConfigTransfer.ConfigTransferFolder;
            return string.IsNullOrWhiteSpace(folder)
                ? Plugin.Instance.ApplicationPaths.PluginConfigurationsPath
                : folder;
        }

        private void HandleExportConfig()
        {
            try
            {
                var folder = ResolveConfigFolder();
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, ConfigFileName);
                File.WriteAllText(path, ConfigTransferService.ExportJson(), new UTF8Encoding(false));

                var message = string.Format(Resources.GeneralOptions_ConfigTransfer_Exported, path);
                Plugin.Instance.Logger.Info("HomePage - " + message);
                Plugin.NotificationApi?.SendMessageToAdmins(message, 12000);
            }
            catch (Exception e)
            {
                HandleConfigTransferError(e);
            }
        }

        private void HandleImportConfig()
        {
            try
            {
                var path = Path.Combine(ResolveConfigFolder(), ConfigFileName);
                if (!File.Exists(path))
                {
                    var missing = string.Format(Resources.GeneralOptions_ConfigTransfer_FileMissing, path);
                    Plugin.NotificationApi?.SendMessageToAdmins(missing, 12000);
                    return;
                }

                var result = ConfigTransferService.ImportJson(File.ReadAllText(path, Encoding.UTF8));
                if (!string.IsNullOrEmpty(result.Error))
                {
                    HandleConfigTransferError(new InvalidOperationException(result.Error));
                    return;
                }

                var message = string.Format(Resources.GeneralOptions_ConfigTransfer_Imported,
                    result.ValuesRestored, result.Rejected.Count, result.Failed.Count, result.Mismatched.Count);
                Plugin.Instance.Logger.Info("HomePage - " + message);
                Plugin.NotificationApi?.SendMessageToAdmins(message, 12000);
            }
            catch (Exception e)
            {
                HandleConfigTransferError(e);
            }
        }

        private void HandleVerifyMetadataCache()
        {
            try
            {
                var result = MetadataCacheVerifyService.Run();
                var message = MetadataCacheVerifyService.BuildSummary(result);
                Plugin.Instance.Logger.Info("HomePage - " + message);
                Plugin.NotificationApi?.SendMessageToAdmins(message, 12000);
            }
            catch (Exception e)
            {
                HandleConfigTransferError(e);
            }
        }

        private void HandleConfigTransferError(Exception exception)
        {
            var message = string.Format(Resources.GeneralOptions_ConfigTransfer_Failed, exception.Message);
            Plugin.Instance.Logger.Error("HomePage - " + message);
            Plugin.NotificationApi?.SendMessageToAdmins(message, 12000);
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
