using Emby.Web.GenericEdit;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 网络与直链设置根节点（第 6 个 Tab）。
    /// 收纳三块「对外连接 / 地址替换」类配置：
    /// 网络（代理与本地发现）、影音直链（302 重定向）、TMDB 替换（自定义 API 地址）。
    /// 三者原先分别散落在首页与元数据增强页，集中后低频配置不再干扰日常功能页。
    /// </summary>
    public class NetworkProxyOptions : EditableOptionsBase
    {
        [DisplayNameL("NetworkProxyOptions_EditorTitle_Network_Direct_Link", typeof(Resources))]
        public override string EditorTitle => Resources.NetworkProxyOptions_EditorTitle_Network_Direct_Link;

        /// <summary>网络：代理服务器、代理模式、刮削白名单、本地发现地址。</summary>
        [DisplayNameL("NetworkOptions_EditorTitle_Network", typeof(Resources))]
        public NetworkOptions Network { get; set; } = new NetworkOptions();

        /// <summary>影音直链：STRM / LiveTV 302 重定向与客户端黑名单。</summary>
        [DisplayNameL("DirectLinkOptions_EditorTitle_Direct_Link", typeof(Resources))]
        public DirectLinkOptions DirectLink { get; set; } = new DirectLinkOptions();

        /// <summary>TMDB 替换：自定义 TMDB API / 图片地址与 API Key。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_AltMovieDb", typeof(Resources))]
        public AltMovieDbOptions TmdbReplacement { get; set; } = new AltMovieDbOptions();

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = base.CreateEditContainer();
            if (container is EditObjectContainer editContainer)
            {
                EditorGroupHelper.Rebuild(editContainer,
                    new EditorGroupSpec(Resources.NetworkProxyOptions_Group_NetworkDirectLink,
                        "NetworkDirectLink", "Network", "DirectLink"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_AltMovieDb,
                        "TmdbReplacement", "TmdbReplacement"));
            }

            return container;
        }

        public void Initialize()
        {
            Network.Initialize();
        }
    }
}
