using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 扫库后自动补提取：库扫描完成后触发，处理「缺失媒体信息」的条目
    /// （含上次提取失败/文件暂不可达的重试项），使漏提取的条目无需手动跑任务即可补齐。
    /// 是否执行由「媒体信息提取 · 自动补漏」选项控制。
    /// </summary>
    public class AutoExtractMediaInfoTask : ILibraryPostScanTask
    {
        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!Plugin.IsPluginEnabled) return Task.CompletedTask;

            return Plugin.LibraryApi.AutoCatchupMediaInfoAsync(progress, cancellationToken);
        }
    }
}
