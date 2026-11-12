﻿﻿﻿using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.GenericEdit;
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

        /// <summary>社区评分：把 CommunityRating 统一为 TMDB / IMDb / 豆瓣（互斥）。</summary>
        [DisplayNameL("MetadataEnhanceOptions_Group_CommunityRating", typeof(Resources))]
        public CommunityRatingOptions CommunityRating { get; set; } = new CommunityRatingOptions();

        /// <summary>豆瓣与附加评分：每个能力独立勾选，社区评分由「社区评分来源」决定。</summary>
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

        /// <summary>社区评分来源是否为豆瓣；为 true 时豆瓣评分直接接管社区评分，无需额外开关。</summary>
        [Browsable(false)]
        public bool IsDoubanRatingSource => CommunityRating?.ParsedSource == CommunityRatingSourceOption.Douban;

        /// <summary>
        /// 是否需要豆瓣数据层（关联 ID / subject 抓取 / 缓存）：
        /// 来源=豆瓣，或任意豆瓣能力（徽章评分 / Kodi 第三评分 / 简介 / 演员 / 海报）开启。
        /// 全部关闭时不会产生任何豆瓣请求。
        /// </summary>
        [Browsable(false)]
        public bool IsDoubanDataEnabled =>
            IsDoubanRatingSource ||
            DoubanAssist?.DoubanFetchRating == true ||
            DoubanAssist?.DoubanRatingToKodi == true ||
            DoubanAssist?.DoubanFillOverview == true ||
            DoubanAssist?.DoubanCastChinese == true ||
            DoubanAssist?.DoubanPosterFallback == true;

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
        /// MovieDb 已知适配区间下限（已适配 1.8.6/.7/.8、1.9.1.0、1.9.2.0、1.9.9.0、2.0.0.0）。
        /// 低于该下限时，中文刮削/图像参数补丁可能因内部 API 变动而失效。
        /// 注意：这里只做“低于下限”的告警，高版本是否兼容由 ChineseMovieDb 的缺方法清单判定。
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
                // 2.0.x 单列一档：PRO 3.0.0.57 明确适配到 2.0.1.0，与 1.9.9.0 混在一档会掩盖差异
                if (v >= new Version("2.0.0.0")) return "2.0.x";
                if (v >= new Version("1.9.9.0")) return "1.9.9.0";
                if (v >= new Version("1.9.2.0")) return "1.9.2.0-1.9.8.x";
                if (v >= new Version("1.9.0.0")) return "1.9.x";
                if (v >= new Version("1.8.6")) return "1.8.6-1.8.x";
                return "<1.8.6";
            }
        }

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = base.CreateEditContainer();
            if (container is EditObjectContainer editContainer)
            {
                EditorGroupHelper.Rebuild(editContainer,
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_ChineseMetadata,
                        "ChineseMetadata", "ChineseMetadata"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_CommunityRating,
                        "CommunityRating", "CommunityRating"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_DoubanAssist,
                        "DoubanAssist", "DoubanAssist"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_PosterPerson,
                        "PosterPerson", "PosterSort", "PersonEnhance"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_ScrapeEpisodeGroup,
                        "ScrapeEpisodeGroup", "ScrapeOptimize", "EpisodeGroup"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_EpisodeRefresh,
                        "EpisodeRefresh", "MetadataRefresh"),
                    new EditorGroupSpec(Resources.MetadataEnhanceOptions_Group_MetadataCache,
                        "MetadataCache", "MetadataCache"));

                // 清除标语是破坏性开关，放在人物增强之后、演员刷新偏好之前。
                EditorGroupHelper.MoveItemBefore(editContainer, "PosterPerson",
                    nameof(PosterSortOptions.ClearTagline), nameof(PersonEnhanceOptions.RefreshPersonMode));
            }

            return container;
        }

        public void Initialize()
        {
            ChineseMetadata.Initialize();
            PersonEnhance.Initialize();
            CommunityRating.Initialize();
            MetadataRefresh.Initialize();
            MetadataCache.Initialize();
        }

        protected override void Validate(ValidationContext context)
        {
            // UI 上 MetadataCache 已独立分组，但 Emby 仍不会自动递归校验；
            // 这里直接调用子对象的校验逻辑，校验规则本身不变。
            MetadataCache.ValidateOptions(context);
        }
    }
}
