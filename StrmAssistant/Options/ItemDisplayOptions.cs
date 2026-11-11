using Emby.Media.Common.Extensions;
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
    /// 体验增强 · 显示优化分组：详情页人物列表、分集标题、缺集视图、剧集卡片上的显示效果。
    /// 作用面是「打开一个条目时看到什么」。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，<c>IsModSupported</c> 必须留在本类内。
    /// </summary>
    public class ItemDisplayOptions : EditableOptionsBase
    {
        [DisplayNameL("ItemDisplayOptions_EditorTitle_Item_Display", typeof(Resources))]
        public override string EditorTitle => Resources.ItemDisplayOptions_EditorTitle_Item_Display;

        [DisplayNameL("ModOptions_HidePersonNoImage_Hide_Person_without_Image", typeof(Resources))]
        [DescriptionL("ModOptions_HidePersonNoImage_Hide_person_without_image__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool HidePersonNoImage { get; set; } = false;

        public enum HidePersonOption
        {
            [DescriptionL("HidePersonOption_NoImage_NoImage", typeof(Resources))]
            NoImage,
            [DescriptionL("HidePersonOption_ActorOnly_ActorOnly", typeof(Resources))]
            ActorOnly,
            [DescriptionL("HidePersonOption_NonChinese_NonChinese", typeof(Resources))]
            NonChinese
        }

        [Browsable(false)]
        public List<EditorSelectOption> HidePersonOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(HidePersonOptionList))]
        [VisibleCondition(nameof(HidePersonNoImage), SimpleCondition.IsTrue)]
        public string HidePersonPreference { get; set; } = HidePersonOption.NoImage.ToString();

        [DisplayNameL("UIFunctionOptions_BeautifyMissingMetadata_Beautify_Missing_Metadata", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_BeautifyMissingMetadata_Beautify_missing_metadata_for_episode_display__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool BeautifyMissingMetadata { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_EnhanceMissingEpisodes_Missing_Episodes_Enhanced", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_EnhanceMissingEpisodes_Add_MovieDb_support_for_the_missing_episodes_function__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnhanceMissingEpisodes { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_ReplaceUnplayedWithTotal_Show_Total_Episodes", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_ReplaceUnplayedWithTotal_Show_total_episode_count_instead_of_unplayed_count_on_series_cards__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool ReplaceUnplayedWithTotal { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_ShowCollectionsFromSeasons_Show_Collections_from_Seasons", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_ShowCollectionsFromSeasons_Show_collections_for_TV_show_considering_its_seasons__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool ShowCollectionsFromSeasons { get; set; } = false;

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public void Initialize()
        {
            HidePersonOptionList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(HidePersonOption)))
            {
                HidePersonOptionList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }
        }
    }
}
