using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 片头片尾 · 在线刮削分组：从 TheIntroDB / TIDB 拉取片头片尾标记（只补缺失）。
    /// 注意：<see cref="VisibleConditionAttribute"/> 的目标属性按**同级路径**解析，
    /// 因此 <c>EnableIntroDbScrape</c> 与其余参数必须留在同一组。
    /// </summary>
    public class IntroSkipScrapeOptions : EditableOptionsBase
    {
        [DisplayNameL("IntroSkipOptions_Group_Scrape", typeof(Resources))]
        public override string EditorTitle => Resources.IntroSkipOptions_Group_Scrape;

        [DisplayNameL("IntroSkipOptions_EnableIntroDbScrape_IntroDb_Intro_Credits_Scraper", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_EnableIntroDbScrape_Scrape_intro_credits_markers_from_TheIntroDB_when_refreshing_series_or_movies__fill_missing_only__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableIntroDbScrape { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_EnableAutomaticIntroScraping_Enable_Automatic_Scraping", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_EnableAutomaticIntroScraping_Scrape_intro_credits_markers_for_new_content__May_slow_down_library_scans__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnableIntroDbScrape), SimpleCondition.IsTrue)]
        public bool EnableAutomaticIntroScraping { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> LibraryList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_ScrapeLibraryScope_Scrape_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_ScrapeLibraryScope_TV_and_mixed_libraries_to_scrape__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [VisibleCondition(nameof(EnableIntroDbScrape), SimpleCondition.IsTrue)]
        public string LibraryScope { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_IncludeMovies_Include_Movies", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IncludeMovies_Also_scrape_movies__movies_only_get_chapter_markers_and_do_not_participate_in_skip__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnableIntroDbScrape), SimpleCondition.IsTrue)]
        public bool IncludeMovies { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_IntroDbApiKey_Api_Key", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroDbApiKey_Optional_TheIntroDB_API_key_sent_via_Bearer_auth_to_raise_the_daily_request_limit__Default_is_empty_", typeof(Resources))]
        [VisibleCondition(nameof(EnableIntroDbScrape), SimpleCondition.IsTrue)]
        public string IntroDbApiKey { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_TidbApiKey_TIDB_API_Key", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_TidbApiKey_API_key_increases_daily_usage_limits__Default_is_empty_", typeof(Resources))]
        [VisibleCondition(nameof(EnableIntroDbScrape), SimpleCondition.IsTrue)]
        public string TidbApiKey { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_UnifiedIntroDbProviders_Unified_IntroDb_Providers", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UnifiedIntroDbProviders_Tried_in_order_until_one_succeeds__Default_is_IntroDb_Tidb_", typeof(Resources))]
        [VisibleCondition(nameof(EnableIntroDbScrape), SimpleCondition.IsTrue)]
        public string UnifiedIntroDbProviders { get; set; } = "IntroDb,Tidb";

        public void Initialize(ILibraryManager libraryManager)
        {
            LibraryList.Clear();

            foreach (var item in libraryManager.GetVirtualFolders())
            {
                if (item.CollectionType == CollectionType.TvShows.ToString() || item.CollectionType is null)
                {
                    LibraryList.Add(new EditorSelectOption
                    {
                        Value = item.ItemId,
                        Name = item.Name,
                        IsEnabled = true,
                    });
                }
            }
        }
    }
}
