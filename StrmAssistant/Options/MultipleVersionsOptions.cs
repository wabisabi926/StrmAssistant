using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 多版本美化相关枚举。
    /// 用枚举而非字符串，Emby 的 GenericEdit 才会渲染成下拉框，并通过
    /// <see cref="DescriptionLAttribute"/> 显示本地化标签。
    /// 枚举成员名与旧版本的字符串取值完全一致，因此历史 JSON 配置可原样反序列化。
    /// </summary>
    public static class MultipleVersionsOptions
    {
        /// <summary>
        /// 版本标签来源：默认（保持 Emby 原始标签）/ 画质（分辨率）/ 画质+体积（分辨率 + 编码 + 码率 + 体积）/ 文件名。
        /// </summary>
        public enum MultiVersionDisplayOption
        {
            [DescriptionL("MultiVersionDisplayOption_SystemDefault_SystemDefault", typeof(Resources))]
            SystemDefault,

            [DescriptionL("MultiVersionDisplayOption_MediaInfo_MediaInfo", typeof(Resources))]
            MediaInfo,

            [DescriptionL("MultiVersionDisplayOption_MediaInfoExtended_MediaInfoExtended", typeof(Resources))]
            MediaInfoExtended,

            [DescriptionL("MultiVersionDisplayOption_FileNameWithoutExtension_FileNameWithoutExtension", typeof(Resources))]
            FileNameWithoutExtension
        }

        /// <summary>
        /// 版本排序方式：默认（Emby 原始顺序）/ 画质降序 / 画质升序 / 路径升序 / 路径降序。
        /// </summary>
        public enum MultiVersionSortOption
        {
            [DescriptionL("MultiVersionSortOption_SystemDefault_SystemDefault", typeof(Resources))]
            SystemDefault,

            [DescriptionL("MultiVersionSortOption_HighToLowQuality_HighToLowQuality", typeof(Resources))]
            HighToLowQuality,

            [DescriptionL("MultiVersionSortOption_LowToHighQuality_LowToHighQuality", typeof(Resources))]
            LowToHighQuality,

            [DescriptionL("MultiVersionSortOption_PathAscending_PathAscending", typeof(Resources))]
            PathAscending,

            [DescriptionL("MultiVersionSortOption_PathDescending_PathDescending", typeof(Resources))]
            PathDescending
        }
    }
}
