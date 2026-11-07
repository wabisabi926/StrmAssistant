using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 片头片尾 · 播放与跳过分组：启用跳过、时长上限、生效范围（库/用户/客户端）、
    /// 跳过偏好与清除标记。
    /// 注意：<see cref="VisibleConditionAttribute"/> 的目标属性按**同级路径**解析，
    /// 因此 <c>EnableIntroSkip</c> 与所有依赖项必须留在同一组。
    /// </summary>
    public class IntroSkipPlaybackOptions : EditableOptionsBase
    {
        [DisplayNameL("IntroSkipOptions_Group_Playback", typeof(Resources))]
        public override string EditorTitle => Resources.IntroSkipOptions_Group_Playback;

        [DisplayNameL("PluginOptions_EnableIntroSkip_Enable_Intro_Skip__Experimental_", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableIntroSkip_Enable_intro_skip_and_credits_skip_for_episodes__Default_is_False_", typeof(Resources))]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_MaxIntroDurationSeconds", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_MaxIntroDurationSeconds_Description", typeof(Resources))]
        [MinValue(10), MaxValue(600)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayNameL("IntroSkipOptions_MaxCreditsDurationSeconds", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_MaxCreditsDurationSeconds_Description", typeof(Resources))]
        [MinValue(10), MaxValue(600)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        [DisplayNameL("IntroSkipOptions_MinOpeningPlotDurationSeconds", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_MinOpeningPlotDurationSeconds_Description", typeof(Resources))]
        [MinValue(30), MaxValue(120)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MinOpeningPlotDurationSeconds { get; set; } = 60;

        [Browsable(false)]
        public List<EditorSelectOption> LibraryList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryScope_TV_shows_library_scope_to_detect__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string LibraryScope { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> UserList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_UserScope_User_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UserScope_Users_allowed_to_detect__Blank_includes_all", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(UserList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string UserScope { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_ClientScope_Client_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_ClientScope_Allowed_clients__Default_is_Emby_Infuse_SenPlayer", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string ClientScope { get; set; } = "Emby,Infuse,SenPlayer";

        public enum IntroSkipPreference
        {
            [DescriptionL("IntroSkipControl_ResetAndOverwrite_ResetAndOverwrite", typeof(Resources))]
            ResetAndOverwrite,
            [DescriptionL("IntroSkipPreference_NoDetectionButReset_NoDetectionButReset", typeof(Resources))]
            NoDetectionButReset
        }

        [Browsable(false)]
        public List<EditorSelectOption> IntroSkipPreferenceList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_IntroSkipPreferences_IntroSkip_Preferences", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroSkipPreferences_Description", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(IntroSkipPreferenceList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string IntroSkipPreferences { get; set; } = string.Empty;

        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public ButtonItem ClearIntroButton =>
            new ButtonItem(
                Resources.ClearChapterMarkersTask_Description_Clears_behavior_based_intro_and_credits_markers)
            {
                Icon = IconNames.clear_all, Data1 = "ClearIntroCreditsMarkers",
                ConfirmationPrompt = Resources.AreYouSureToContinue
            };

        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public GenericItemList ClearIntroResult { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public SpacerItem ClearIntroResultSeparator { get; set; } = new SpacerItem(SpacerSize.Small);

        public void Initialize(ILibraryManager libraryManager)
        {
            LibraryList.Clear();
            UserList.Clear();
            IntroSkipPreferenceList.Clear();

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

            foreach (var user in LibraryApi.AllUsers)
            {
                UserList.Add(new EditorSelectOption
                {
                    Value = user.Key.InternalId.ToString(),
                    Name = (user.Value ? "\ud83d\udc51" : "\ud83d\udc64") + user.Key.Name,
                    IsEnabled = true,
                });
            }

            foreach (Enum item in Enum.GetValues(typeof(IntroSkipPreference)))
            {
                IntroSkipPreferenceList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = Utility.GetEnumDescription(item),
                    IsEnabled = true,
                });
            }
        }
    }
}
