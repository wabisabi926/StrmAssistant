using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 首页 · 维护工具分组：清理内存 / 重建索引 / 优化索引 / 清理缓存 / 重启服务器。
    /// 只读的运行时状态已拆到 <see cref="OverviewOptions"/>（页面顶部）。
    /// 说明：这些项都是「操作类」，按统一排序规范放在设置项之后、危险操作居末。
    /// </summary>
    public class RuntimeStatusOptions : EditableOptionsBase
    {
        [DisplayNameL("GeneralOptions_Group_RuntimeStatus", typeof(Resources))]
        public override string EditorTitle => Resources.GeneralOptions_Group_RuntimeStatus;

        /// <summary>旧版配置迁移用；新位置见 <see cref="ConfigTransferOptions"/>。</summary>
        [Browsable(false)]
        public string ConfigTransferFolder { get; set; } = string.Empty;

        public ButtonItem VerifyMetadataCacheButton =>
            new ButtonItem(Resources.GeneralOptions_VerifyMetadataCache_Verify_Metadata_Cache)
            {
                Icon = IconNames.queue, Data1 = "VerifyMetadataCache"
            };

        /// <summary>
        /// 片源体检：只读，不探测媒体、不改条目数据，结果写插件配置目录。
        /// 与「校验元数据缓存」并列，属于同一类只读校验操作。
        /// </summary>
        public ButtonItem MediaHealthCheckButton =>
            new ButtonItem(Resources.Health_Button_Run)
            {
                Icon = IconNames.info, Data1 = "RunScheduledTask", Data2 = "MediaHealthCheckTask"
            };

        public ButtonItem ExportMediaHealthButton =>
            new ButtonItem(Resources.Health_Button_Export)
            {
                Icon = IconNames.queue, Data1 = "ExportMediaHealth"
            };

        /// <summary>上次体检摘要（只读），由 HomePageView.PopulateOverview 填充。</summary>
        public StatusItem MediaHealthStatus { get; set; } = new StatusItem();

        public ButtonItem CleanMemoryNowButton =>
            new ButtonItem(Resources.GeneralOptions_CleanMemoryNow_Clean_Memory_Now)
            {
                Icon = IconNames.memory, Data1 = "CleanMemoryNow"
            };

        public ButtonItem ScheduleIndexRebuildButton =>
            new ButtonItem(Resources.GeneralOptions_ScheduleIndexRebuild_Schedule_Search_Index_Rebuild)
            {
                Icon = IconNames.build, Data1 = "ScheduleIndexRebuild",
                ConfirmationPrompt = Resources.AreYouSureToContinue
            };

        public ButtonItem ClearMetadataCacheButton =>
            new ButtonItem(Resources.GeneralOptions_ClearMetadataCache_Clear_Metadata_Cache)
            {
                Icon = IconNames.delete_sweep, Data1 = "ClearMetadataCache",
                ConfirmationPrompt = Resources.AreYouSureToContinue
            };

        public ButtonItem OptimizeSearchIndexButton =>
            new ButtonItem(Resources.GeneralOptions_OptimizeSearchIndex_Optimize_Search_Index)
            {
                Icon = IconNames.cleaning_services, Data1 = "OptimizeSearchIndex"
            };

        public ButtonItem RestartServerButton =>
            new ButtonItem(Resources.GeneralOptions_RestartServer_Restart_Server)
            {
                Icon = IconNames.autorenew, Data1 = "RestartServer",
                ConfirmationPrompt = Resources.AreYouSureToContinue
            };
    }
}
