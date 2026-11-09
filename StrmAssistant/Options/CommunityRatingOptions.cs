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
    /// 社区评分来源。写入的是 Emby 唯一的 <c>CommunityRating</c> 字段，
    /// 因此同一时间只能有一个来源生效（互斥）。
    /// </summary>
    public enum CommunityRatingSourceOption
    {
        [DescriptionL("CommunityRatingSourceOption_None_Do_Not_Interfere", typeof(Resources))]
        None,
        [DescriptionL("CommunityRatingSourceOption_Tmdb_TMDb", typeof(Resources))]
        Tmdb,
        [DescriptionL("CommunityRatingSourceOption_Imdb_IMDb_via_MdbList", typeof(Resources))]
        Imdb,
        [DescriptionL("CommunityRatingSourceOption_Douban_Douban", typeof(Resources))]
        Douban
    }

    /// <summary>
    /// 元数据增强 · 社区评分分组：把 CommunityRating 统一为 TMDB / IMDb（或豆瓣）。
    ///
    /// 背景：Emby 的 CommunityRating 是单字段，刮削器、豆瓣覆盖、NFO、历史数据都往里写，
    /// 谁最后写谁生效且不记录来源，于是同一库里会混着不同来源的分数。这里提供一个唯一写入口。
    /// </summary>
    public class CommunityRatingOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_CommunityRating", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_CommunityRating;

        [Browsable(false)]
        public List<EditorSelectOption> CommunityRatingSourceList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_CommunityRatingSource_Community_Rating_Source", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_CommunityRatingSource_Description", typeof(Resources))]
        [SelectItemsSource(nameof(CommunityRatingSourceList))]
        public string CommunityRatingSource { get; set; } = CommunityRatingSourceOption.None.ToString();

        /// <summary>TMDB 票数低于该值视为分数不可信（冷门片 1 票也能算出 10.0）。0 表示不限制。</summary>
        [DisplayNameL("MetadataEnhanceOptions_CommunityRatingMinVotes_Minimum_Votes", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_CommunityRatingMinVotes_Description", typeof(Resources))]
        [Required, MinValue(0), MaxValue(1000000)]
        public int CommunityRatingMinVotes { get; set; } = 5;

        /// <summary>所选来源没有评分时，是否清空原有社区评分（多半来自其它来源）。</summary>
        [DisplayNameL("MetadataEnhanceOptions_CommunityRatingClearWhenMissing_Clear_When_No_Rating", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_CommunityRatingClearWhenMissing_Description", typeof(Resources))]
        public bool CommunityRatingClearWhenMissing { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_UnifyCommunityRatingButton_Unify_Now", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_UnifyCommunityRatingButton_Description", typeof(Resources))]
        public ButtonItem UnifyCommunityRatingButton =>
            new ButtonItem(Resources.MetadataEnhanceOptions_UnifyCommunityRatingButton_Unify_Now)
            {
                Icon = IconNames.refresh, Data1 = "UnifyCommunityRating",
                ConfirmationPrompt = ParsedSource == CommunityRatingSourceOption.Douban
                    ? Resources.MetadataEnhanceOptions_UnifyCommunityRatingButton_Douban_Confirmation
                    : Resources.AreYouSureToContinue
            };

        [Browsable(false)]
        public CommunityRatingSourceOption ParsedSource =>
            Enum.TryParse<CommunityRatingSourceOption>(CommunityRatingSource, true, out var source)
                ? source
                : CommunityRatingSourceOption.None;

        /// <summary>是否由本分组接管 CommunityRating（豆瓣仍走原来的豆瓣覆盖开关）。</summary>
        [Browsable(false)]
        public bool IsUnificationEnabled =>
            ParsedSource == CommunityRatingSourceOption.Tmdb || ParsedSource == CommunityRatingSourceOption.Imdb;

        public void Initialize()
        {
            CommunityRatingSourceList.Clear();

            foreach (Enum item in Enum.GetValues(typeof(CommunityRatingSourceOption)))
            {
                CommunityRatingSourceList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }
        }
    }
}
