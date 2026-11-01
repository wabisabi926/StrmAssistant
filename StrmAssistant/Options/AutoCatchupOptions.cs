using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 提取 · 自动补漏分组：扫库结束后自动补提取缺失项，并对失败条目做有限次重试。
    /// 只处理「增量缺失」的条目（<c>IsExtractNeeded</c>），不会重复探测已有媒体信息的条目。
    /// </summary>
    public class AutoCatchupOptions : EditableOptionsBase
    {
        [DisplayNameL("MediaInfoExtractOptions_Group_AutoCatchup", typeof(Resources))]
        public override string EditorTitle => Resources.MediaInfoExtractOptions_Group_AutoCatchup;

        [DisplayNameL("MediaInfoExtractOptions_AutoExtractOnScan_Auto_Extract_After_Library_Scan",
            typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_AutoExtractOnScan_When_enabled__items_missing_media_info_are_extracted_automatically_after_each_library_scan__Default_is_ON_",
            typeof(Resources))]
        [Required]
        public bool AutoExtractOnScan { get; set; } = true;

        [DisplayNameL("MediaInfoExtractOptions_AutoRetryFailed_Auto_Retry_Failed_Items", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_AutoRetryFailed_Failed_or_unreachable_items_are_recorded_and_retried_on_the_next_scan__Default_is_ON_",
            typeof(Resources))]
        [Required]
        public bool AutoRetryFailed { get; set; } = true;

        [DisplayNameL("MediaInfoExtractOptions_MaxRetryAttempts_Max_Retry_Attempts", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_MaxRetryAttempts_Maximum_retry_attempts_per_item__items_exceeding_this_limit_are_removed_from_the_queue__Default_is_3_",
            typeof(Resources))]
        [Required, MinValue(1), MaxValue(10)]
        [VisibleCondition(nameof(AutoRetryFailed), SimpleCondition.IsTrue)]
        public int MaxRetryAttempts { get; set; } = 3;
    }
}
