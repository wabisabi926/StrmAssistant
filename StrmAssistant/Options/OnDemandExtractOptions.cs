using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 提取 · 按需分组：打开详情页缺流时提取，播放时预取下一集。
    /// 两项都只把条目放进现有媒体信息队列，不走全库扫描。
    /// </summary>
    public class OnDemandExtractOptions : EditableOptionsBase
    {
        [DisplayNameL("MediaInfoExtractOptions_Group_OnDemand", typeof(Resources))]
        public override string EditorTitle => Resources.MediaInfoExtractOptions_Group_OnDemand;

        [DisplayNameL("MediaInfoExtractOptions_ExtractOnBrowse_Extract_When_Opening_Details",
            typeof(Resources))]
        [DescriptionL(
            "MediaInfoExtractOptions_ExtractOnBrowse_Extract_in_the_background_when_opening_a_detail_page_for_an_item_without_media_info__Default_is_OFF_",
            typeof(Resources))]
        [Required]
        public bool ExtractOnBrowse { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_EnableMediaInfoPreload_Preload_Next_Episode_During_Playback",
            typeof(Resources))]
        [DescriptionL(
            "MediaInfoExtractOptions_EnableMediaInfoPreload_Extract_media_info_for_the_next_episode_in_the_background_while_playing__Default_is_OFF_",
            typeof(Resources))]
        [Required]
        public bool EnableMediaInfoPreload { get; set; } = false;
    }
}
