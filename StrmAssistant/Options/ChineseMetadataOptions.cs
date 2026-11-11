using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 元数据增强 · 中文元数据分组：MovieDb / Tvdb 的中文回退语言、非回退语言屏蔽等。
    /// 注意：<see cref="EnabledConditionAttribute"/> / <see cref="VisibleConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，因此本组所需的 helper（<c>IsMovieDbPluginLoaded</c> 等）必须留在本类内。
    /// </summary>
    public class ChineseMetadataOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_ChineseMetadata", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_ChineseMetadata;

        [DisplayNameL("ModOptions_ChineseMovieDb_Chinese_MovieDb", typeof(Resources))]
        [DescriptionL("ModOptions_ChineseMovieDb_Optimize_MovieDb_for_Chinese_metadata__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool ChineseMovieDb { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> LanguageList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("ModOptions_FallbackLanguages_Fallback_Languages", typeof(Resources))]
        [DescriptionL("ModOptions_FallbackLanguages_Fallback_languages__Default_is_zh_SG_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LanguageList))]
        [VisibleCondition(nameof(ChineseMovieDb), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string FallbackLanguages { get; set; } = "zh-sg";

        [DisplayNameL("MetadataEnhanceOptions_ChineseTvdb_Customize_Tvdb_Fallback_Language", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ChineseTvdb_Try_to_get_Chinese_or_Japanese_metadata_from_Tvdb__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsTvdbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool ChineseTvdb { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> TvdbLanguageList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("ModOptions_TvdbFallbackLanguages_TVDB_Fallback_Languages", typeof(Resources))]
        [DescriptionL("ModOptions_FallbackLanguages_Fallback_languages__Default_is_zh_SG_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(TvdbLanguageList))]
        [VisibleCondition(nameof(ChineseTvdb), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsTvdbPluginLoaded), SimpleCondition.IsTrue)]
        public string TvdbFallbackLanguages { get; set; } = "zhtw,yue";

        [DisplayNameL("MetadataEnhanceOptions_BlockNonFallbackLanguage_Block_Non_Fallback_Language", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_BlockNonFallbackLanguage_Block_English_such_non_fallback_language_in_overview__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(ShowBlockNonFallbackLanguage), SimpleCondition.IsTrue)]
        public bool BlockNonFallbackLanguage { get; set; } = false;

        [Browsable(false)]
        public bool ShowBlockNonFallbackLanguage =>
            ChineseMovieDb && IsMovieDbPluginLoaded || ChineseTvdb && IsTvdbPluginLoaded;

        [DisplayNameL("MetadataEnhanceOptions_PreferTraditionalChinese_Prefer_Traditional_Chinese", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_PreferTraditionalChinese_Convert_simplified_Chinese_overviews_from_the_MovieDb_provider_to_traditional_on_refresh__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool PreferTraditionalChinese { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DefaultNewLibraryTmdb_Default_New_Library_Tmdb", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DefaultNewLibraryTmdb_Prefer_TheMovieDb_over_Tvdb_as_the_default_fetcher_when_creating_new_libraries__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool DefaultNewLibraryTmdb { get; set; } = false;

        [Browsable(false)]
        public bool IsMovieDbPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MovieDb") && IsModSupported;

        [Browsable(false)]
        public bool IsTvdbPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Tvdb") && IsModSupported;

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public void Initialize()
        {
            LanguageList.Clear();

            foreach (var language in LanguageUtility.MovieDbFallbackLanguages)
            {
                LanguageList.Add(new EditorSelectOption
                {
                    Value = language.ToLowerInvariant(),
                    Name = language,
                    IsEnabled = true
                });
            }

            TvdbLanguageList.Clear();

            foreach (var language in LanguageUtility.TvdbFallbackLanguages)
            {
                TvdbLanguageList.Add(new EditorSelectOption
                {
                    Value = language,
                    Name = language,
                    IsEnabled = true
                });
            }
        }
    }
}
