using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 首页 · 计划任务。
    ///
    /// 只列**本插件自己的**计划任务（按程序集过滤），每行带「执行」按钮，
    /// 免除「设置页 → Emby 自带计划任务页 → 找任务 → 点运行」的三跳。
    /// 列表内容全部在运行时生成，没有用户配置，因此不涉及配置迁移。
    /// </summary>
    public class ScheduledTasksOptions : EditableOptionsBase
    {
        [DisplayNameL("ScheduledTasks_EditorTitle_Scheduled_Tasks", typeof(Resources))]
        public override string EditorTitle => Resources.ScheduledTasks_EditorTitle_Scheduled_Tasks;

        [DisplayNameL("ScheduledTasks_List_Task_List", typeof(Resources))]
        [DescriptionL("ScheduledTasks_List_Task_List_Description", typeof(Resources))]
        public GenericItemList TaskList { get; set; } = new GenericItemList();

        public void Initialize()
        {
            TaskList.Clear();
        }
    }
}
