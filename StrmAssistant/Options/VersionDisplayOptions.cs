using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 体验增强 · 多版本展示分组：详情页多版本的画质角标、标签来源与排序方式。
    /// 注意：<see cref="VisibleConditionAttribute"/> 的目标属性按**同级路径**解析。
    /// </summary>
    public class VersionDisplayOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_Group_VersionDisplay", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_Group_VersionDisplay;

        [DisplayNameL("ExperienceEnhanceOptions_EnableMultiVersionEnhance_Multi_Version_Quality_Badges", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnableMultiVersionEnhance_Show_quality_badges_and_sort_by_quality_on_multi_version_detail_pages__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableMultiVersionEnhance { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_EnableServerSideVersionLabel_Server_Side_Version_Label", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnableServerSideVersionLabel_Write_the_same_label_into_MediaSourceInfo_Name_so_Kodi_Apple_TV_and_mobile_apps_see_it_too__Only_applies_when_a_title_has_multiple_versions__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableMultiVersionEnhance), SimpleCondition.IsTrue)]
        [Required]
        public bool EnableServerSideVersionLabel { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_MultiVersionDisplayOption_Version_Display_Option", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_MultiVersionDisplayOption_Version_label_source__SystemDefault_MediaInfo_or_FileNameWithoutExtension__Default_is_MediaInfo_", typeof(Resources))]
        [VisibleCondition(nameof(EnableMultiVersionEnhance), SimpleCondition.IsTrue)]
        [Required]
        public MultipleVersionsOptions.MultiVersionDisplayOption MultiVersionDisplayOption { get; set; } =
            MultipleVersionsOptions.MultiVersionDisplayOption.MediaInfo;

        [DisplayNameL("ExperienceEnhanceOptions_MultiVersionSortOption_Version_Sort_Option", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_MultiVersionSortOption_Version_sort_order__SystemDefault_HighToLowQuality_LowToHighQuality_PathAscending_or_PathDescending__Default_is_SystemDefault_", typeof(Resources))]
        [VisibleCondition(nameof(EnableMultiVersionEnhance), SimpleCondition.IsTrue)]
        [Required]
        public MultipleVersionsOptions.MultiVersionSortOption MultiVersionSortOption { get; set; } =
            MultipleVersionsOptions.MultiVersionSortOption.SystemDefault;
    }
}
