using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 提取 · ISO 与外部工具分组。
    /// <para>
    /// <c>EnableIsoExtraction</c> 与 <c>CustomProbePath</c> 是同一件事的两面：Windows 走系统挂载，
    /// Linux/Docker 下用进程内 UDF 选片、把正片样本交给 ffprobe（见 <c>Common/IsoMounter.cs</c>、
    /// <c>Common/LibraryApi.cs</c>），因此两者必须同组呈现，避免用户在页面两端分别配置。
    /// </para>
    /// <para>
    /// <c>CustomEncoderPath</c> 用于图像捕获与视频缩略图的 ffmpeg 编码（如带 libplacebo 的构建可修复 DV P5 绿屏），
    /// 与 ISO 无关，但同属"替换外部二进制"，故并入本组。
    /// </para>
    /// </summary>
    public class IsoToolOptions : EditableOptionsBase
    {
        [DisplayNameL("MediaInfoExtractOptions_Group_IsoTool", typeof(Resources))]
        public override string EditorTitle => Resources.MediaInfoExtractOptions_Group_IsoTool;

        [DisplayNameL("MediaInfoExtractOptions_EnableIsoExtraction_Enable_ISO_Extraction", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_EnableIsoExtraction_Include_ISO_images_in_media_info_extraction__Windows_native_OS_mount__Linux_Docker_in_process_UDF_read_with_custom_ffprobe__Default_is_OFF_", typeof(Resources))]
        public bool EnableIsoExtraction { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_CustomProbePath_Custom_Probe_Path", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CustomProbePath_Custom_ffprobe_binary_used_for_media_info_extraction__leave_empty_to_use_the_Emby_builtin__supports_rffmpeg_wrapper_for_distributed_extraction_", typeof(Resources))]
        [EditFilePicker]
        public string CustomProbePath { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_CustomEncoderPath_Custom_Encoder_Path", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CustomEncoderPath_Custom_ffmpeg_binary_used_for_image_capture_and_thumbnails__leave_empty_to_use_the_Emby_builtin__a_build_with_libplacebo_fixes_DV_P5_green_tint_", typeof(Resources))]
        [EditFilePicker]
        public string CustomEncoderPath { get; set; } = string.Empty;
    }
}
