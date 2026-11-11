using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 体验增强 · 多版本合并分组：同一文件夹内多版本自动合并、合并范围与分组上限、拆分操作。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，<c>IsModSupported</c> 必须留在本类内。
    /// </summary>
    public class VersionMergeOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_Group_VersionMerge", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_Group_VersionMerge;

        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMoviesScopeOption
        {
            [DescriptionL("MergeScopeOption_FolderScope_FolderScope", typeof(Resources))]
            FolderScope,
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [DisplayNameL("ExperienceEnhanceOptions_MergeMoviePreferences_Movie_Merge_Preference", typeof(Resources))]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMoviesScopeOption MergeMoviesPreference { get; set; } = MergeMoviesScopeOption.FolderScope;

        public enum MergeSeriesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [DisplayNameL("ExperienceEnhanceOptions_MergeSeriesPreferences_Series_Merge_Preference", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_MergeSeriesPreferences_Series_merge_works_across_libraries_only_in_GlobalScope", typeof(Resources))]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public MergeSeriesScopeOption MergeSeriesPreference { get; set; } = MergeSeriesScopeOption.LibraryScope;

        [DisplayNameL("ExperienceEnhanceOptions_FolderVideoGroupingLimit_Folder_Video_Grouping_Limit", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_FolderVideoGroupingLimit_Maximum_number_of_videos_in_the_same_folder_eligible_for_multi_version_merge__8_is_the_Emby_default__Default_is_8_", typeof(Resources))]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [Required]
        public int FolderVideoGroupingLimit { get; set; } = 8;

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public ButtonItem SplitMoviesButton =>
            new ButtonItem(
                Resources.ExperienceEnhanceOptions_SplitMovieButton_Split_multi_version_movies_in_all_libraries)
            {
                Icon = IconNames.clear_all, Data1 = "SplitMovies",
                ConfirmationPrompt = Resources.ExperienceEnhanceOptions_SplitMovieButton_Confirmation
            };

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public GenericItemList SplitMoviesProgress { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public SpacerItem SplitMovieProgressSeparator { get; set; } = new SpacerItem(SpacerSize.Small);

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    }
}
