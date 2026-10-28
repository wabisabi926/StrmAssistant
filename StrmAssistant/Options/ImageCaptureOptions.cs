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
    /// 提取 · 图像捕获分组：无主图视频的截帧、截帧位置与画面裁剪规则。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，本组所需的 helper 必须留在本类内。
    /// </summary>
    public class ImageCaptureOptions : EditableOptionsBase
    {
        [DisplayNameL("MediaInfoExtractOptions_Group_ImageCapture", typeof(Resources))]
        public override string EditorTitle => Resources.MediaInfoExtractOptions_Group_ImageCapture;

        [DisplayNameL("PluginOptions_EnableImageCapture_Enable_Image_Capture", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableImageCapture_Perform_image_capture_for_videos_without_primary_image__Default_is_False_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnableImageCapture { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_ImageCaptureOffset_Image_Capture_Offset", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageCaptureOffset_Image_capture_position_as_a_percentage_of_runtime__Default_is_10_", typeof(Resources))]
        [Required, MinValue(10), MaxValue(90)]
        [VisibleCondition(nameof(EnableImageCapture), SimpleCondition.IsTrue)]
        public int ImageCapturePosition { get; set; } = 10;

        [Browsable(false)]
        public List<EditorSelectOption> ImageCapturePostProcessModeList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MediaInfoExtractOptions_ImageCapturePostProcessMode_Image_Capture_Post_Processing", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageCapturePostProcessMode_Generate_additional_images_after_capture__Default_is_OFF_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(ImageCapturePostProcessModeList))]
        [VisibleCondition(nameof(EnableImageCapture), SimpleCondition.IsTrue)]
        public string ImageCapturePostProcessMode { get; set; } = string.Empty;

        // 3.0.0.56 前为 ForcePortraitPoster / EnsureEpisode16x9 两个独立开关，现已合并进
        // ImageCapturePostProcessMode。保留为隐藏属性仅供升级时一次性迁移读取，迁移后恒为 false。
        [Browsable(false)]
        public bool ForcePortraitPoster { get; set; } = false;

        [Browsable(false)]
        public bool EnsureEpisode16x9 { get; set; } = false;

        [Browsable(false)]
        public string ImageCaptureExcludeMediaContainers { get; set; } = string.Empty;

        /// <summary>截图后处理模式。</summary>
        public enum ImageCapturePostProcess
        {
            [DescriptionL("ImageCapturePostProcess_EnsureAspect16By9_Ensure_Aspect_16x9", typeof(Resources))]
            EnsureAspect16By9,

            [DescriptionL("ImageCapturePostProcess_GenerateBackdrop_Generate_Backdrop", typeof(Resources))]
            GenerateBackdrop,

            [DescriptionL("ImageCapturePostProcess_EnsureAspect2By3_Ensure_Aspect_2x3", typeof(Resources))]
            EnsureAspect2By3
        }

        public void Initialize()
        {
            ImageCapturePostProcessModeList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(ImageCapturePostProcess)))
            {
                ImageCapturePostProcessModeList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }
        }

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    }
}
