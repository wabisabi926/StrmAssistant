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
    /// 体验增强 · 媒体库排序分组：首页库卡片角标、合集库、条目自然排序、库顺序强制。
    /// 作用面是「媒体库与列表怎么组织」——比显示优化更外围，故排在它之后。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，<c>IsModSupported</c> 必须留在本类内。
    /// </summary>
    public class LibraryOrderOptions : EditableOptionsBase
    {
        [DisplayNameL("LibraryOrderOptions_EditorTitle_Library_and_Order", typeof(Resources))]
        public override string EditorTitle => Resources.LibraryOrderOptions_EditorTitle_Library_and_Order;

        [DisplayNameL("UIFunctionOptions_ShowLibraryItemCount_Show_Library_Item_Count", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_ShowLibraryItemCount_Show_item_count_badge_on_library_cards_on_the_home_screen__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool ShowLibraryItemCount { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_NoBoxsetsAutoCreation_Prevent_Boxsets_Auto_Creation", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_NoBoxsetsAutoCreation_Prevent_automatic_boxset_library_creation__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool NoBoxsetsAutoCreation { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_OptimizeMediaItemOrder_Optimize_Media_Item_Display_Order", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_OptimizeMediaItemOrder_Optimize_natural_sort__in_library_collection_order__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool OptimizeMediaItemOrder { get; set; } = false;

        public enum ItemOrderOption
        {
            [DescriptionL("ItemOrderOption_NaturalSort_NaturalSort", typeof(Resources))]
            NaturalSort,
            [DescriptionL("ItemOrderOption_ReverseSeasonOrder_ReverseSeasonOrder", typeof(Resources))]
            ReverseSeasonOrder,
            [DescriptionL("ItemOrderOption_ReverseEpisodeOrder_ReverseEpisodeOrder", typeof(Resources))]
            ReverseEpisodeOrder,
            [DescriptionL("ItemOrderOption_CollectionPerItemDateCreated_CollectionPerItemDateCreated", typeof(Resources))]
            CollectionPerItemDateCreated
        }

        [Browsable(false)]
        public List<EditorSelectOption> ItemOrderOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(ItemOrderOptionList))]
        [VisibleCondition(nameof(OptimizeMediaItemOrder), SimpleCondition.IsTrue)]
        public string OptimizeItemOrderPreferences { get; set; } = string.Empty;

        [DisplayNameL("UIFunctionOptions_EnforceLibraryOrder_Enforce_Library_Order", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_EnforceLibraryOrder_Enforce_library_order_per_the_first_admin__Default_if_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnforceLibraryOrder { get; set; } = false;

        public enum EnforceUserDisplayOption
        {
            [DescriptionL("EnforceUserDisplayOption_LibraryOrder_LibraryOrder", typeof(Resources))]
            LibraryOrder,
            [DescriptionL("EnforceUserDisplayOption_SeriesDisplay_SeriesDisplay", typeof(Resources))]
            SeriesDisplay
        }

        [Browsable(false)]
        public List<EditorSelectOption> EnforceUserDisplayOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("UIFunctionOptions_EnforceUserPreferencesScope_Enforce_User_Preferences_Scope", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_EnforceUserPreferencesScope_Which_of_the_first_admin_s_preferences_are_enforced_for_all_users__Default_is_LibraryOrder_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(EnforceUserDisplayOptionList))]
        [VisibleCondition(nameof(EnforceLibraryOrder), SimpleCondition.IsTrue)]
        public string EnforceUserPreferencesScope { get; set; } = EnforceUserDisplayOption.LibraryOrder.ToString();

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public void Initialize()
        {
            ItemOrderOptionList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(ItemOrderOption)))
            {
                ItemOrderOptionList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }

            EnforceUserDisplayOptionList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(EnforceUserDisplayOption)))
            {
                EnforceUserDisplayOptionList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }
        }
    }
}
