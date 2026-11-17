using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;
using System.Reflection;

namespace StrmAssistant.Options
{
    public class AboutOptions : EditableOptionsBase
    {
        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public override string EditorTitle => Resources.AboutOptions_EditorTitle_About;

        public GenericItemList VersionInfoList { get; set; } = new GenericItemList();

        [Browsable(false)]
        public bool DebugMode { get; set; } = false;

        [Browsable(false)]
        public string GitHubToken { get; set; } = string.Empty;

        [Browsable(false)]
        public string GitHubProxy { get; set; } = string.Empty;
        
        /// <summary>
        /// 显示用版本号：返回完整四段程序集版本（如 4.0.1.0）。
        /// 供品牌区（BrandOptions）与关于页共用。
        ///
        /// 历史：早期版本第四位恒非零（3.0.0.65 等），曾把结尾 ".0" 砍掉让显示更短；
        /// 自 4.0.1.0 起版本体系改为第四位归零（补丁号在第三位），再砍 ".0" 会把
        /// 4.0.1.0 显示成 4.0.1，与 tag/CHANGELOG/Release 名称不一致，故不再裁剪。
        /// </summary>
        internal static string GetVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        }

        public void Initialize()
        {
            VersionInfoList.Clear();

            // 版本号已移至基础设置页顶部的品牌区（BrandOptions），此处仅保留链接。

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Repo_Link,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant",
                });

            VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Wiki_Link,
                    Icon = IconNames.menu_book,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/sjtuross/StrmAssistant/wiki",
                });
        }
    }
}
