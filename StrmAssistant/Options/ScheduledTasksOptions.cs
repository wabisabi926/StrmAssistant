using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 首页 · 常用任务。
    ///
    /// 不再罗列全部计划任务——Emby 自带的「计划任务」页已经完整覆盖那一屏，
    /// 插件里再画一遍 13 行既难看，又要跟着任务增减维护。这里只把最常手动触发的几项
    /// 做成一排按钮：按钮组只占一行，页面不会被撑长。
    /// 按钮的 <c>Data2</c> 是任务 Key，由 <c>HomePageView</c> 按「Key + 程序集」双重校验后执行。
    /// </summary>
    public class ScheduledTasksOptions : EditableOptionsBase
    {
        [DisplayNameL("ScheduledTasks_EditorTitle_Common_Tasks", typeof(Resources))]
        public override string EditorTitle => Resources.ScheduledTasks_EditorTitle_Common_Tasks;

        public ButtonItem RebuildUserDataKeysButton =>
            new ButtonItem(Resources.ScheduledTasks_Button_Rebuild_Keys)
            {
                Icon = IconNames.schedule, Data1 = "RunScheduledTask", Data2 = "RebuildUserDataKeysTask"
            };

        public ButtonItem ExtractMediaInfoButton =>
            new ButtonItem(Resources.ScheduledTasks_Button_Extract_MediaInfo)
            {
                Icon = IconNames.info, Data1 = "RunScheduledTask", Data2 = "MediaInfoExtractTask"
            };

        public ButtonItem MergeMultiVersionButton =>
            new ButtonItem(Resources.ScheduledTasks_Button_Merge_MultiVersion)
            {
                Icon = IconNames.queue, Data1 = "RunScheduledTask", Data2 = "MergeMultiVersionTask"
            };

        public ButtonItem RefreshEpisodesButton =>
            new ButtonItem(Resources.ScheduledTasks_Button_Refresh_Episodes)
            {
                Icon = IconNames.autorenew, Data1 = "RunScheduledTask", Data2 = "EpisodeRefreshTask"
            };
    }
}
