using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 扫库后自动填充豆瓣 ID：
    /// 库扫描完成后触发，为缺失/需更新的电影、剧集执行豆瓣匹配并写回
    /// ProviderIds["Douban"]，使新入库或历史遗漏的条目无需等待条目事件也能补全。
    /// </summary>
    public class TriggerPopulateDoubanIdTask : ILibraryPostScanTask
    {
        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!Plugin.Instance.MetadataEnhanceStore.GetOptions().DoubanAssist.EnableDoubanAssist)
            {
                return Task.CompletedTask;
            }

            return Plugin.DoubanAssist.PopulateDoubanIdsAsync(progress, cancellationToken);
        }
    }
}
