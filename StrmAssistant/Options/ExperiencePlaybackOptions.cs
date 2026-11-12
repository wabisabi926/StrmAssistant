using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 体验增强 · 播放与清理分组：自动标记已看、深度删除、字幕路径优化。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，<c>IsModSupported</c> 必须留在本类内。
    /// </summary>
    public class ExperiencePlaybackOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_Group_Playback", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_Group_Playback;

        [DisplayNameL("ExperienceEnhanceOptions_EnableAutoMarkWatched_Auto_Mark_Previous_Episodes_Watched", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnableAutoMarkWatched_Automatically_mark_previous_episodes_as_watched_when_current_episode_reaches_the_progress_threshold__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableAutoMarkWatched { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_AutoMarkWatchedThreshold_Marked_Progress_Threshold", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_AutoMarkWatchedThreshold_Playback_progress_percentage_of_current_episode_required_to_mark_previous_episodes_as_watched__5_to_100__Default_is_90_", typeof(Resources))]
        [VisibleCondition(nameof(EnableAutoMarkWatched), SimpleCondition.IsTrue)]
        [Required]
        public int AutoMarkWatchedThreshold { get; set; } = 90;

        [DisplayNameL("ExperienceEnhanceOptions_AutoMarkWatchedExcludeFavorites_Exclude_Favorite_Episodes", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_AutoMarkWatchedExcludeFavorites_Favorite_episodes_will_not_be_automatically_marked_as_watched__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableAutoMarkWatched), SimpleCondition.IsTrue)]
        [Required]
        public bool AutoMarkWatchedExcludeFavorites { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_OptimizeSubtitle_Optimize_Subtitle", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_OptimizeSubtitle_Resolve_strm_media_path_to_local_mount_for_open_subtitles_hash_and_filename_search__requires_open_subtitles_plugin__Default_is_OFF_", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool OptimizeSubtitle { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_NormalizeTrackLanguage_Normalize_Track_Language", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_NormalizeTrackLanguage_Fix_undetermined_language_tags_on_external_audio_and_subtitles__Simplified_and_Traditional_Chinese_are_shown_in_titles__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool NormalizeTrackLanguage { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_LibraryScopePlayProgress_Title", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_LibraryScopePlayProgress_Description", typeof(Resources))]
        [Required]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool LibraryScopePlayProgress { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_EnableDeepDelete_Enable_Deep_Delete", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnableDeepDelete_Attempt_to_cascade_delete_the_underlying_file_of_strm_or_symlink__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        [Required]
        public bool EnableDeepDelete { get; set; } = false;

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    }
}
