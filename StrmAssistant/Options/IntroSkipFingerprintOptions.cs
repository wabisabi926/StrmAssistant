using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Entities;
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
    /// 片头片尾 · 检测与指纹分组：解锁内置检测、指纹时长、并行计算、生效库范围与黑名单。
    /// 注意：<see cref="VisibleConditionAttribute"/> / <see cref="EnabledConditionAttribute"/> 的目标属性
    /// 按**同级路径**解析，<c>IsModSupported</c> 与 <c>Validate</c> 依赖的字段必须留在本类内。
    /// </summary>
    public class IntroSkipFingerprintOptions : EditableOptionsBase
    {
        [DisplayNameL("IntroSkipOptions_Group_Fingerprint", typeof(Resources))]
        public override string EditorTitle => Resources.IntroSkipOptions_Group_Fingerprint;

        [DisplayNameL("IntroSkipOptions_UnlockIntroSkip_Built_in_Intro_Skip_Enhanced", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UnlockIntroSkip_Unlock_Strm_support_for_built_in_intro_skip_detection", typeof(Resources))]
        [Required]
        public bool UnlockIntroSkip { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_IntroDetectionFingerprintMinutes_Intro_Detection_Fingerprint_Minutes", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroDetectionFingerprintMinutes_It_must_be_between_2_and_20__Default_is_10_", typeof(Resources))]
        [MinValue(2), MaxValue(20)]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        [DisplayNameL("IntroSkipOptions_ParallelMarkerComputation_Parallel_Marker_Computation", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_ParallelMarkerComputation_Bypass_the_master_concurrency_limit_for_fingerprint_extraction_and_parallelize_across_all_CPU_cores__Default_is_OFF_", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool ParallelMarkerComputation { get; set; } = false;

        [Browsable(false)]
        public List<EditorSelectOption> MarkerEnabledLibraryList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_MarkerEnabledLibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_MarkerEnabledLibraryScope_Intro_detection_enabled_library_scope__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(MarkerEnabledLibraryList))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public string MarkerEnabledLibraryScope { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_LibraryFingerprintMinutes_Per_Library_Fingerprint_Minutes", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryFingerprintMinutes_Overrides_for_specific_libraries_in_libraryId_minutes_format_separated_by_comma__falls_back_to_the_global_value__Default_is_EMPTY_", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public string LibraryFingerprintMinutes { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_BlacklistShows_Optional_Blacklist_Shows", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_BlacklistShows_List_of_Series_Id_or_Season_Id_separated_by_comma_or_semicolon__Default_is_EMPTY", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public string FingerprintBlacklistShows { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_BlacklistKeywords_Optional_Blacklist_Keywords", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_BlacklistKeywords_Skip_episodes_whose_name_contains_any_keyword_separated_by_comma_or_semicolon__Default_is_EMPTY", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public string FingerprintBlacklistKeywords { get; set; } = string.Empty;

        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public GenericItemList FingerprintBlacklistShowsResult { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public SpacerItem FingerprintBlacklistShowsResultSeparator { get; set; } = new SpacerItem(SpacerSize.Medium);

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        protected override void Validate(ValidationContext context)
        {
            if (MarkerEnabledLibraryList.All(o => o.Value == "-1") &&
                MarkerEnabledLibraryScope.Contains("-1"))
            {
                context.AddValidationError(Resources.InvalidMarkerEnabledLibraryScope);
            }

            ValidateShowIds(context, FingerprintBlacklistShows, Resources.InvalidShowSeasonIds);
        }

        public void ValidateShowIds(ValidationContext context, string showIds, string errorMessage)
        {
            if (!string.IsNullOrWhiteSpace(showIds))
            {
                var ids = showIds.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => id.Trim())
                    .ToArray();

                var allInvalidIds = new List<string>();

                var invalidIds = ids.Where(id => !long.TryParse(id, out _));
                allInvalidIds.AddRange(invalidIds);

                var validIds = ids.Where(id => long.TryParse(id, out _)).Select(long.Parse).ToArray();
                var items = Plugin.LibraryApi.GetItemsByIds(validIds);

                var missingItems = validIds.Where(id => items.All(item => item.InternalId != id));
                allInvalidIds.AddRange(missingItems.Select(id => id.ToString()));

                var invalidItemTypes = items.Where(i => !(i is Season || i is Series))
                    .Select(i => i.InternalId.ToString());
                allInvalidIds.AddRange(invalidItemTypes);

                if (allInvalidIds.Any())
                {
                    context.AddValidationError(string.Format(errorMessage, string.Join(", ", allInvalidIds)));
                }
            }
        }

        public void Initialize(ILibraryManager libraryManager)
        {
            MarkerEnabledLibraryList.Clear();

            MarkerEnabledLibraryList.Add(new EditorSelectOption
            {
                Value = "-1",
                Name = Resources.Favorites,
                IsEnabled = true
            });

            foreach (var item in libraryManager.GetVirtualFolders())
            {
                if (item.CollectionType == CollectionType.TvShows.ToString() || item.CollectionType is null)
                {
                    if (item.LibraryOptions.EnableMarkerDetection)
                    {
                        MarkerEnabledLibraryList.Add(new EditorSelectOption
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
}
