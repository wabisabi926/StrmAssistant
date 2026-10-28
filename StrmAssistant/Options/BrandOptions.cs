using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 品牌信息：组头显示插件名；版本行用 LabelItem 渲染为区块内全宽文本，
    /// 不套列表项，因而没有左侧缩进。
    /// </summary>
    public class BrandOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Assistant", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Assistant;

        [DisplayName("")]
        public LabelItem PluginVersion => new LabelItem(
            "版本：v" + AboutOptions.GetVersion() +
            " 💎 Support " + Plugin.AdvertisedEmbyVersion + " +");
    }
}
