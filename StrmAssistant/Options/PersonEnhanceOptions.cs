using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 人物刷新方式（可多选）。
    /// </summary>
    public enum RefreshPersonOption
    {
        [DescriptionL("RefreshPersonOption_Default_Default", typeof(Resources))]
        Default,
        [DescriptionL("RefreshPersonOption_FullRefresh_FullRefresh", typeof(Resources))]
        FullRefresh,
        [DescriptionL("RefreshPersonOption_NoAdult_NoAdult", typeof(Resources))]
        NoAdult,
        [DescriptionL("RefreshPersonOption_ExcludeIfChinese_ExcludeIfChinese", typeof(Resources))]
        ExcludeIfChinese,
        [DescriptionL("RefreshPersonOption_DownloadImagesInAdvance_DownloadImagesInAdvance", typeof(Resources))]
        DownloadImagesInAdvance,
        [DescriptionL("RefreshPersonOption_DefaultImageRefresh_DefaultImageRefresh", typeof(Resources))]
        DefaultImageRefresh,
        [DescriptionL("RefreshPersonOption_FullImageRefresh_FullImageRefresh", typeof(Resources))]
        FullImageRefresh
    }

    /// <summary>
    /// 元数据增强 · 人物增强分组：导入分季演员并更新剧集人物、人物刷新偏好。
    /// </summary>
    public class PersonEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_PersonEnhance", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_PersonEnhance;

        [DisplayNameL("MetadataEnhanceOptions_EnhanceMovieDbPerson_Enhance_MovieDb_Person", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnhanceMovieDbPerson_Import_season_cast_and_update_series_people__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool EnhanceMovieDbPerson { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> RefreshPersonOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonMode_Refresh_Person_Preferences", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonMode_By_default__refresh_metadata__30_days_old__excluding_adults_and_those_with_an_avatar_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(RefreshPersonOptionList))]
        [VisibleCondition(nameof(EnhanceMovieDbPerson), SimpleCondition.IsTrue)]
        public string RefreshPersonMode { get; set; } = RefreshPersonOption.Default.ToString();

        [Browsable(false)]
        public bool IsMovieDbPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MovieDb") && IsModSupported;

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public void Initialize()
        {
            RefreshPersonOptionList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(RefreshPersonOption)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                };

                RefreshPersonOptionList.Add(selectOption);
            }
        }
    }
}
