using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 片源体检 · 声明级。纯只读：不探测媒体、不发网络请求、不修改任何条目数据，
    /// 只把「文件名宣称」与「已提取的媒体信息」做交叉比对，结果写到插件配置目录。
    ///
    /// 「字幕语言未识别」这条规则与「规范化音轨字幕语言」形成闭环：
    /// 先跑本任务看基线 → 开规范化并跑一次 → 再体检，该项应归零。
    /// </summary>
    public class MediaHealthCheckTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public MediaHealthCheckTask(IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
        }

        public string Key => "MediaHealthCheckTask";

        public string Name => Resources.ResourceManager.GetString(
            "MediaHealthCheckTask_Name_Media_Health_Check", Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "MediaHealthCheckTask_Description_Scan_media_info_for_declaration_mismatches",
            Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString(
            "PluginOptions_EditorTitle_Strm_Assistant", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return Task.CompletedTask;

            _logger.Info("MediaHealth - Scheduled Task Execute");
            progress.Report(0);

            var items = Plugin.LibraryApi.FetchPostExtractTaskItems(false);
            _logger.Info("MediaHealth - Number of items: " + items.Count);

            var directoryService = new DirectoryService(_logger, _fileSystem);
            var report = new MediaHealthReport { GeneratedAt = DateTimeOffset.Now };
            var itemsWithIssues = 0;
            var infoIssues = 0;

            var index = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                index++;
                if (items.Count > 0 && (index % 10 == 0 || index == items.Count))
                {
                    progress.Report(index / (double)items.Count * 100);
                }

                try
                {
                    var jsonPath = MediaInfoApi.GetMediaInfoJsonReadPath(item, directoryService);
                    if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                    {
                        report.SkippedItems++;
                        continue;
                    }

                    var input = ParseInput(item.Name, item.Path, jsonPath);
                    report.ItemsChecked++;

                    var issues = MediaHealthChecker.Check(input);
                    infoIssues += issues.Count(i => i.Severity == MediaHealthSeverity.Info);

                    // 提示级只汇总计数：全库「无字幕」若逐条列出会把报告刷爆
                    var reportable = issues.Where(i => i.Severity != MediaHealthSeverity.Info).ToList();
                    if (reportable.Count == 0) continue;

                    itemsWithIssues++;
                    report.Issues.AddRange(reportable);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.Debug($"MediaHealth - {item.Name}: {e.Message}");
                }
            }

            report.ItemsWithIssues = itemsWithIssues;
            report.Critical = report.Issues.Count(i => i.Severity == MediaHealthSeverity.Critical);
            report.Warning = report.Issues.Count(i => i.Severity == MediaHealthSeverity.Warning);
            report.Info = infoIssues;

            progress.Report(100);

            string message;
            if (report.ItemsChecked == 0)
            {
                // 空跑不覆盖上一份有效报告，否则下次对比会丢掉基线
                message = Resources.Health_Notify_NoData;
            }
            else
            {
                var saved = MediaHealthService.Save(report);
                message = saved
                    ? string.Format(Resources.Health_Notify_Completed,
                        report.Critical, report.Warning, report.Info, report.SkippedItems,
                        MediaHealthService.MarkdownPath)
                    : Resources.Health_Notify_WriteFailed;
            }

            _logger.Info("MediaHealth - " + message);
            Plugin.NotificationApi?.SendMessageToAdmins(
                $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {message}", 12000);

            return Task.CompletedTask;
        }

        private static MediaHealthInput ParseInput(string itemName, string itemPath, string jsonPath)
        {
            var input = new MediaHealthInput
            {
                ItemName = itemName,
                FileName = string.IsNullOrEmpty(itemPath) ? string.Empty : Path.GetFileName(itemPath)
            };

            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));

            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                return input;

            if (!document.RootElement[0].TryGetProperty("MediaSourceInfo", out var mediaSource) ||
                mediaSource.ValueKind != JsonValueKind.Object)
                return input;

            if (mediaSource.TryGetProperty("RunTimeTicks", out var runtime) &&
                runtime.ValueKind == JsonValueKind.Number &&
                runtime.TryGetInt64(out var ticks))
            {
                input.RunTimeTicks = ticks;
            }

            if (mediaSource.TryGetProperty("MediaStreams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    input.Streams.Add(new MediaHealthStream
                    {
                        Type = GetString(stream, "Type"),
                        Codec = GetString(stream, "Codec"),
                        Width = GetInt(stream, "Width"),
                        Height = GetInt(stream, "Height"),
                        BitDepth = GetInt(stream, "BitDepth"),
                        ColorTransfer = GetString(stream, "ColorTransfer"),
                        ExtendedVideoType = GetString(stream, "ExtendedVideoType"),
                        Language = GetString(stream, "Language"),
                        Title = GetString(stream, "Title"),
                        IsExternal = GetBool(stream, "IsExternal"),
                        IsForced = GetBool(stream, "IsForced")
                    });
                }
            }

            return input;
        }

        private static string GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int? GetInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var result)
                ? result
                : (int?)null;

        private static bool GetBool(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
