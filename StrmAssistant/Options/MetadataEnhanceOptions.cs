using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 元数据增强设置根节点。
    /// 所有设置项按功能拆到 9 个分组类中，本类只负责：
    /// ① 声明分组（嵌套 <see cref="EditableOptionsBase"/> 会被 Emby 渲染成带 &lt;h2&gt; 小标题的分区）；
    /// ② 委派各分组的 <c>Initialize()</c>（框架不会自动调用嵌套对象）；
    /// ③ 委派各分组的 <c>Validate()</c>（框架不会自动递归校验）。
    /// 注意：分组的属性名与分组类名不同（避免实例成员遮蔽类型名导致初始化表达式无法解析）。
    /// </summary>
    public class MetadataEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_MetadataEnhanceOptions_Metadata_Enhance;

        /// <summary>中文元数据：MovieDb / Tvdb 中文回退语言、非回退语言屏蔽等。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_ChineseMetadata", typeof(Resources))]
        public ChineseMetadataOptions ChineseMetadata { get; set; } = new ChineseMetadataOptions();

        /// <summary>剧集组：MovieDb Episode Group 刮削与扩展信息。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_EpisodeGroup", typeof(Resources))]
        public EpisodeGroupOptions EpisodeGroup { get; set; } = new EpisodeGroupOptions();

        /// <summary>刮削优化：按季批量取集信息、跳过逐集回退与剧集图片请求。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_ScrapeOptimize", typeof(Resources))]
        public ScrapeOptimizeOptions ScrapeOptimize { get; set; } = new ScrapeOptimizeOptions();

        /// <summary>人物增强：分季演员导入、人物刷新偏好。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_PersonEnhance", typeof(Resources))]
        public PersonEnhanceOptions PersonEnhance { get; set; } = new PersonEnhanceOptions();

        /// <summary>豆瓣辅助：评分 / 演员 / 简介补全，MdbList 影评人评分。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_DoubanAssist", typeof(Resources))]
        public DoubanAssistOptions DoubanAssist { get; set; } = new DoubanAssistOptions();

        /// <summary>海报与排序：原语言海报、拼音排序名、NFO 增强导入、清除宣传语。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_PosterSort", typeof(Resources))]
        public PosterSortOptions PosterSort { get; set; } = new PosterSortOptions();

        /// <summary>元数据刷新：剧集刷新范围 / 回溯天数 / 生效媒体库。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_MetadataRefresh", typeof(Resources))]
        public MetadataRefreshOptions MetadataRefresh { get; set; } = new MetadataRefreshOptions();

        /// <summary>本地元数据缓存：缓存模式与缓存根目录。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_MetadataCache", typeof(Resources))]
        public MetadataCacheOptions MetadataCache { get; set; } = new MetadataCacheOptions();

        [Browsable(false)]
        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        /// <summary>
        /// MovieDb 插件版本（诊断用：内部 API 变动时据此判断 patch 兼容性）。
        /// </summary>
        [Browsable(false)]
        public string MovieDbPluginVersion
        {
            get
            {
                try
                {
                    return AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetName().Name == "MovieDb")
                        ?.GetName().Version?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// MovieDb 程序集版本（<see cref="Version"/> 形态）。null 表示未加载或无法解析。
        /// </summary>
        [Browsable(false)]
        public Version MovieDbVersion =>
            Version.TryParse(MovieDbPluginVersion, out var parsed) ? parsed : null;

        /// <summary>
        /// MovieDb 已知适配区间下限（已适配 1.8.6/.7/.8、1.9.1.0、1.9.2.0、1.9.9.0）。
        /// 低于该下限时，中文刮削/图像参数补丁可能因内部 API 变动而失效。
        /// </summary>
        [Browsable(false)]
        public bool IsMovieDbVersionCompatible =>
            MovieDbVersion == null || MovieDbVersion >= new Version("1.8.6");

        /// <summary>
        /// MovieDb 版本档位（用于日志与兼容性告警，非门禁——不因版本高而禁用补丁）。
        /// </summary>
        [Browsable(false)]
        public string MovieDbVersionTier
        {
            get
            {
                var v = MovieDbVersion;
                if (v == null) return "Unknown";
                if (v >= new Version("1.9.9.0")) return "1.9.9.0+";
                if (v >= new Version("1.9.2.0")) return "1.9.2.0-1.9.8.x";
                if (v >= new Version("1.9.0.0")) return "1.9.x";
                if (v >= new Version("1.8.6")) return "1.8.6-1.8.x";
                return "<1.8.6";
            }
        }

        public void Initialize()
        {
            ChineseMetadata.Initialize();
            PersonEnhance.Initialize();
            DoubanAssist.Initialize();
            MetadataRefresh.Initialize();
            MetadataCache.Initialize();
        }

        protected override void Validate(ValidationContext context)
        {
            ValidateChildGroup(context, nameof(MetadataCache));
        }
    }
}
