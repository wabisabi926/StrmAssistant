using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 豆瓣辅助可改写的字段。
    /// </summary>
    public enum DoubanAssistScrapeOption
    {
        [DescriptionL("DoubanAssistScrapeOption_Rating_Rating", typeof(Resources))]
        Rating,
        [DescriptionL("DoubanAssistScrapeOption_Cast_Cast", typeof(Resources))]
        CastCharacter,
        [DescriptionL("DoubanAssistScrapeOption_NameOverview_NameOverview", typeof(Resources))]
        NameOverview,
        [DescriptionL("DoubanAssistScrapeOption_Poster_Poster", typeof(Resources))]
        Poster
    }

    /// <summary>
    /// 元数据增强 · 豆瓣辅助分组：豆瓣评分 / 演员 / 简介补全，以及 MdbList 影评人评分。
    /// </summary>
    public class DoubanAssistOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_DoubanAssist", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_DoubanAssist;

        [DisplayNameL("MetadataEnhanceOptions_EnableDoubanAssist_Douban_Assist_Scraping", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableDoubanAssist_After_movie_refresh_fill_missing_Chinese_overview_genres_locations_from_Douban__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableDoubanAssist { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanCookie_Douban_Cookie", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanCookie_Optional_Douban_cookie_for_anti_crawler_verification__Default_is_empty_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        public string DoubanCookie { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_DoubanAccessToken_Douban_Access_Token", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanAccessToken_Used_to_assist_in_retrieving_Douban_info__Default_is_BLANK_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        public string DoubanAccessToken { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_DoubanRatingOverride_Override_Rating_With_Douban", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanRatingOverride_Overwrite_CommunityRating_with_Douban_rating__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        [Required]
        public bool DoubanRatingOverride { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> DoubanAssistScrapeOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_DoubanAssistScrapeScope_Douban_Assist_Scope", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanAssistScrapeScope_Douban_assist_scope__Default_is_ALL_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(DoubanAssistScrapeOptionList))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        public string DoubanAssistScrapeScope { get; set; } =
            string.Join(",", Enum.GetNames(typeof(DoubanAssistScrapeOption)));

        [DisplayNameL("MetadataEnhanceOptions_MdbListApiKey_MdbList_Api_Key", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MdbListApiKey_Optional_MdbList_API_key_used_to_refresh_critic_ratings_Rotten_Tomatoes_Metacritic_alongside_Douban_rating_updates__Default_is_empty_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        public string MdbListApiKey { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_RatingUpdateLookbackDays_Rating_Update_Lookback_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RatingUpdateLookbackDays_Douban_rating_update_lookback_days_based_on_premiere_date__0_disables_the_limit__Default_is_365_days_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        [Required, MinValue(0)]
        public int RatingUpdateLookbackDays { get; set; } = 365;

        [DisplayNameL("MetadataEnhanceOptions_DoubanCastChinese_Douban_Cast_Chinese_Names", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanCastChinese_Replace_English_actor_names_with_Douban_Chinese_names_for_matched_items__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        [Required]
        public bool DoubanCastChinese { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanPosterFallback_Douban_Poster_Fallback", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanPosterFallback_Set_Douban_poster_as_primary_image_when_the_movie_has_none__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        [Required]
        public bool DoubanPosterFallback { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_ClearSeasonMapButton_Clear_Season_Map", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ClearSeasonMapButton_Remove_cached_series_season_mappings_so_season_items_are_matched_again__Useful_after_a_wrong_season_match_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanAssist), SimpleCondition.IsTrue)]
        public ButtonItem ClearSeasonMapButton =>
            new ButtonItem(Resources.MetadataEnhanceOptions_ClearSeasonMapButton_Clear_Season_Map)
            {
                Icon = IconNames.delete_sweep, Data1 = "ClearSeasonMap",
                ConfirmationPrompt = Resources.AreYouSureToContinue
            };

        public void Initialize()
        {
            // 豆瓣辅助生效范围 = 字段多选
            DoubanAssistScrapeOptionList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(DoubanAssistScrapeOption)))
            {
                DoubanAssistScrapeOptionList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }
        }
    }
}
