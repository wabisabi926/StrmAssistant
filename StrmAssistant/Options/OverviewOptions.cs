using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 首页 · 运行概览。
    ///
    /// 页面最顶部的只读指标区：内存、运行中的任务、活动会话、待处理队列、搜索索引、冲突插件。
    /// 之所以拆成独立分组放在配置项之前，是因为打开插件页的第一诉求是「现在怎么样」，
    /// 而不是先翻一堆开关。列表内容全部运行时生成，没有用户配置，不涉及配置迁移。
    /// </summary>
    public class OverviewOptions : EditableOptionsBase
    {
        [DisplayNameL("Overview_EditorTitle_Runtime_Overview", typeof(Resources))]
        public override string EditorTitle => Resources.Overview_EditorTitle_Runtime_Overview;

        [DisplayNameL("Overview_List_Overview", typeof(Resources))]
        public GenericItemList OverviewList { get; set; } = new GenericItemList();


        public void Initialize()
        {
            OverviewList.Clear();
        }
    }
}
