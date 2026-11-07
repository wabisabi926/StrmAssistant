using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 首页 · 活动会话。
    ///
    /// 原来只有一行「活动会话：2（播放中 0）」，看得到有人在播却停不掉。
    /// 这里列出每个会话及其正在播放的条目，播放中的行带「停止播放」按钮。
    /// 列表内容全部在运行时生成，没有用户配置，不涉及配置迁移。
    /// </summary>
    public class SessionsOptions : EditableOptionsBase
    {
        [DisplayNameL("Sessions_EditorTitle_Active_Sessions", typeof(Resources))]
        public override string EditorTitle => Resources.Sessions_EditorTitle_Active_Sessions;

        [DisplayNameL("Sessions_List_Session_List", typeof(Resources))]
        [DescriptionL("Sessions_List_Session_List_Description", typeof(Resources))]
        public GenericItemList SessionList { get; set; } = new GenericItemList();

        public void Initialize()
        {
            SessionList.Clear();
        }
    }
}
