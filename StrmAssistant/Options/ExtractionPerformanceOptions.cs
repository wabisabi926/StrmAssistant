using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 提取 · 性能与独占分组：单项提取超时、图像超时倍数，以及独占提取（只允许本插件调用 ffprobe/ffmpeg）。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，<c>HasExtractionTimeout</c> / <c>IsModSupported</c> 必须留在本类内。
    /// </summary>
    public class ExtractionPerformanceOptions : EditableOptionsBase
    {
        [DisplayNameL("MediaInfoExtractOptions_Group_ExtractionPerformance", typeof(Resources))]
        public override string EditorTitle => Resources.MediaInfoExtractOptions_Group_ExtractionPerformance;

        [DisplayNameL("ModOptions_ExclusiveExtract_Exclusive_Extract", typeof(Resources))]
        [DescriptionL("ModOptions_ExclusiveExtract_Only_allow_this_plugin_to_extract_media_info__ffprobe__and_capture_image__ffmpeg___Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool ExclusiveExtract { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> ExclusiveControlList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MediaInfoExtractOptions_ExclusiveExtractFeatureControl_Exclusive_Extract_Feature_Control", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(ExclusiveControlList))]
        [VisibleCondition(nameof(ExclusiveExtract), SimpleCondition.IsTrue)]
        public string ExclusiveControlFeatures { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_MediaInfoExtractionTimeoutMs_Media_Info_Extraction_Timeout", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_MediaInfoExtractionTimeoutMs_Per_item_extraction_timeout_in_milliseconds__0_disables_the_timeout__Default_is_0_", typeof(Resources))]
        [Required, MinValue(0)]
        public int MediaInfoExtractionTimeoutMs { get; set; } = 0;

        [Browsable(false)]
        public bool HasExtractionTimeout => MediaInfoExtractionTimeoutMs > 0;

        [DisplayNameL("MediaInfoExtractOptions_ImageExtractionTimeoutMultiplier_Image_Extraction_Timeout_Multiplier", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageExtractionTimeoutMultiplier_Multiplier_applied_to_the_extraction_timeout_when_image_capture_runs_for_the_item__Default_is_1_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(10)]
        [VisibleCondition(nameof(HasExtractionTimeout), SimpleCondition.IsTrue)]
        public int ImageExtractionTimeoutMultiplier { get; set; } = 1;

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public void Initialize()
        {
            ExclusiveControlList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(MediaInfoExtractOptions.ExclusiveControl)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                };

                ExclusiveControlList.Add(selectOption);
            }
        }
    }
}
