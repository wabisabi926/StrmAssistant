using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 首页 · 插件更新分组：只读状态、手动检查、自动下载与 GitHub 访问配置。
    /// 更新源固定为本仓库，不提供修改入口。
    /// </summary>
    public class PluginUpdateOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginUpdateOptions_EditorTitle_Plugin_Update", typeof(Resources))]
        public override string EditorTitle => Resources.PluginUpdateOptions_EditorTitle_Plugin_Update;

        /// <summary>上次检查摘要，由 HomePageView.PopulatePluginUpdate 填充。</summary>
        public StatusItem UpdateStatus { get; set; } = new StatusItem();

        public ButtonItem CheckUpdateButton =>
            new ButtonItem(Resources.PluginUpdateOptions_CheckUpdate_Check_for_Updates)
            {
                Icon = IconNames.refresh, Data1 = "RunScheduledTask", Data2 = "PluginUpdateTask"
            };

        [DisplayNameL("PluginUpdateOptions_AutoDownloadUpdate_Auto_Download_Updates", typeof(Resources))]
        [DescriptionL("PluginUpdateOptions_AutoDownloadUpdate_Description", typeof(Resources))]
        public bool AutoDownloadUpdate { get; set; } = false;

        [DisplayNameL("PluginUpdateOptions_GitHubToken_GitHub_Token", typeof(Resources))]
        [DescriptionL("PluginUpdateOptions_GitHubToken_Description", typeof(Resources))]
        public string GitHubToken { get; set; } = string.Empty;

        [DisplayNameL("PluginUpdateOptions_GitHubProxy_GitHub_Proxy", typeof(Resources))]
        [DescriptionL("PluginUpdateOptions_GitHubProxy_Description", typeof(Resources))]
        public string GitHubProxy { get; set; } = string.Empty;
    }
}
