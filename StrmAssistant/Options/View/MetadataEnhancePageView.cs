﻿﻿﻿using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistant.Common;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.UIBaseClasses.Views;
using StrmAssistant.Properties;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
{
    internal class MetadataEnhancePageView : PluginPageView
    {
        private readonly MetadataEnhanceOptionsStore _store;

        public MetadataEnhancePageView(PluginInfo pluginInfo, MetadataEnhanceOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;
            ContentData = store.GetOptions();
            MetadataEnhanceOptions.Initialize();
        }

        public MetadataEnhanceOptions MetadataEnhanceOptions => ContentData as MetadataEnhanceOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ContentData is MetadataEnhanceOptions options)
            {
                options.ValidateOrThrow();
            }

            _store.SetOptions(MetadataEnhanceOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (commandId == "UnifyCommunityRating")
            {
                // 按钮依赖当前页面上的来源配置：先落盘，避免用到旧值。
                if (ContentData is MetadataEnhanceOptions options)
                {
                    _store.SetOptions(options);
                }

                var pageOptions = ContentData as MetadataEnhanceOptions;
                var source = pageOptions?.CommunityRating?.ParsedSource ?? CommunityRatingSourceOption.None;

                _ = BackgroundTaskRunner.Run("CommunityRating.Unify", async () =>
                {
                    try
                    {
                        // 豆瓣：条目分优先、IMDb API 兜底；带频控 + 熔断，不支持演练模式。
                        if (source == CommunityRatingSourceOption.Douban)
                        {
                            if (DryRunMode.Enabled)
                            {
                                Plugin.Instance.Logger.Info(
                                    "CommunityRating - Douban source does not support dry-run; disable Dry Run and run again");
                                return;
                            }

                            var doubanReport = await Plugin.DoubanAssist
                                .UnifyRatingsAsync(null, CancellationToken.None).ConfigureAwait(false);
                            Plugin.Instance.Logger.Info(doubanReport.BuildSummary());
                            Plugin.NotificationApi?.SendMessageToAdmins(
                                $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {doubanReport.BuildSummary()}",
                                15000);
                            return;
                        }

                        var apply = !DryRunMode.Enabled;
                        var report = await Plugin.RatingUnifier
                            .UnifyAllAsync(apply, null, CancellationToken.None).ConfigureAwait(false);

                        Plugin.Instance.Logger.Info(report.BuildSummary());
                        Plugin.NotificationApi?.SendMessageToAdmins(
                            $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {report.BuildSummary()}", 15000);
                    }
                    catch (Exception e)
                    {
                        Plugin.Instance.Logger.Warn("CommunityRating - manual unify failed: " + e.Message);
                    }
                });

                return Task.FromResult<IPluginUIView>(this);
            }

            if (commandId == "ClearSeasonMap")
            {
                // 季映射清理是毫秒级操作（只清内存字典 + 删 json），同步执行即可
                try
                {
                    var cleared = Plugin.DoubanAssist.ClearSeasonMap();
                    Plugin.Instance.Logger.Info(
                        $"MetadataEnhance - Clear season map requested from UI: {cleared} season(s) removed");
                }
                catch (System.Exception e)
                {
                    Plugin.Instance.Logger.Warn("MetadataEnhance - Clear season map failed: " + e.Message);
                }

                return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }
    }
}
