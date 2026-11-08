using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 提取 · HDR 标注分组。
    /// <para>
    /// 这两个功能都只是「读探测结果、给视频流打扩展标签」，与 ISO 提取没有依赖关系。
    /// 此前它们沿用 <c>EnableIsoExtraction</c>，导致用户关掉 ISO 提取后标签功能静默失效
    /// （设置页上看不出关联），故独立成组。
    /// </para>
    /// <para>
    /// 注意：补丁在模块构造时按开关状态应用，<b>修改后需重启服务器</b>才生效（与插件总开关一致）。
    /// </para>
    /// </summary>
    public class HdrTaggingOptions : EditableOptionsBase
    {
        [DisplayNameL("MediaInfoExtractOptions_Group_HdrTagging", typeof(Resources))]
        public override string EditorTitle => Resources.MediaInfoExtractOptions_Group_HdrTagging;

        /// <summary>总开关：关闭后两个标注补丁都不打。</summary>
        [DisplayNameL("MediaInfoExtractOptions_EnableHdrTagging_Enable_HDR_tagging", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_EnableHdrTagging_Tag_HDR_Vivid_and_dual_layer_Dolby_Vision_Profile_7__Requires_a_server_restart__Default_is_OFF_", typeof(Resources))]
        public bool EnableHdrTagging { get; set; } = false;

        /// <summary>双层杜比视界：把 1080p 增强层标注为 Profile 7.6。</summary>
        [DisplayNameL("MediaInfoExtractOptions_EnableDolbyVisionProfileTagging_Tag_dual_layer_Dolby_Vision_as_Profile_7", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_EnableDolbyVisionProfileTagging_Marks_the_1080p_enhancement_layer_as_Profile_7_6__Keep_ON_unless_your_player_mishandles_it_", typeof(Resources))]
        public bool EnableDolbyVisionProfileTagging { get; set; } = true;

        /// <summary>HDR Vivid：CUVA 动态元数据 → 画质标签显示 HDR Vivid。</summary>
        [DisplayNameL("MediaInfoExtractOptions_EnableHdrVividTagging_Show_HDR_Vivid_label", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_EnableHdrVividTagging_Detects_CUVA_dynamic_metadata_and_shows_the_video_range_label_as_HDR_Vivid_", typeof(Resources))]
        public bool EnableHdrVividTagging { get; set; } = true;

        /// <summary>
        /// HDR 静态元数据补采：把 ffprobe side_data 里的 MaxCLL / MaxFALL / 母版色域与亮度
        /// 写进视频轨的 Title。仅补信息，不改变播放与画质判定。
        /// </summary>
        [DisplayNameL("MediaInfoExtractOptions_CaptureHdrStaticMetadata_Capture_HDR_static_metadata", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CaptureHdrStaticMetadata_Read_MaxCLL_MaxFALL_and_mastering_display_from_probe_side_data_and_write_them_to_the_video_track_title__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableHdrTagging), SimpleCondition.IsTrue)]
        public bool CaptureHdrStaticMetadata { get; set; } = false;
    }
}
