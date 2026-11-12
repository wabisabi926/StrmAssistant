using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 元数据增强 · 豆瓣能力（单组展示）。
    /// UI 上字段按 评分输出 → 刷新参数 → 元数据 → 连接 排序，字段路径与保存位置不变。
    ///
    /// 设计原则：每个能力独立勾选，互不隐式依赖。
    /// - 社区评分由「社区评分来源」决定，本分组不参与。
    /// - 豆瓣数据层（关联 ID / subject 抓取 / 缓存）在这些勾选中任意一个开启时自动生效。
    /// </summary>
    public class DoubanAssistOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_DoubanAssist", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_DoubanAssist;

        [DisplayNameL("MetadataEnhanceOptions_DoubanFetchRating_Fetch_Douban_Rating", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanFetchRating_Description", typeof(Resources))]
        [Required]
        public bool DoubanFetchRating { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanRatingToKodi_Sync_Douban_Rating_to_Kodi", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanRatingToKodi_Description", typeof(Resources))]
        [Required]
        public bool DoubanRatingToKodi { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_EnableMdbListCriticRating_Update_Critic_Ratings", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableMdbListCriticRating_Description", typeof(Resources))]
        [Required]
        public bool EnableMdbListCriticRating { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanFillOverview_Fill_Overview_Genres_Productions", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanFillOverview_Description", typeof(Resources))]
        [Required]
        public bool DoubanFillOverview { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanCastChinese_Douban_Cast_Chinese_Names", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanCastChinese_Description", typeof(Resources))]
        [Required]
        public bool DoubanCastChinese { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanPosterFallback_Douban_Poster_Fallback", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanPosterFallback_Description", typeof(Resources))]
        [Required]
        public bool DoubanPosterFallback { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanRefreshIntervalDays_On_Demand_Refresh_Interval_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanRefreshIntervalDays_Description", typeof(Resources))]
        [Required, MinValue(0)]
        public int DoubanRefreshIntervalDays { get; set; } = 7;

        [DisplayNameL("MetadataEnhanceOptions_RatingUpdateLookbackDays_Rating_Update_Lookback_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RatingUpdateLookbackDays_Description", typeof(Resources))]
        [Required, MinValue(0)]
        public int RatingUpdateLookbackDays { get; set; } = 365;

        [DisplayNameL("MetadataEnhanceOptions_DoubanCookie_Douban_Cookie", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanCookie_Description", typeof(Resources))]
        public string DoubanCookie { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_DoubanAccessToken_Douban_Access_Token", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanAccessToken_Description", typeof(Resources))]
        public string DoubanAccessToken { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_ClearSeasonMapButton_Clear_Season_Map", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ClearSeasonMapButton_Description", typeof(Resources))]
        public ButtonItem ClearSeasonMapButton =>
            new ButtonItem(Resources.MetadataEnhanceOptions_ClearSeasonMapButton_Clear_Season_Map)
            {
                Icon = IconNames.delete_sweep, Data1 = "ClearSeasonMap",
                ConfirmationPrompt = Resources.AreYouSureToContinue
            };
    }
}
