using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 本地元数据缓存模式。
    /// </summary>
    public enum MetadataCacheOption
    {
        [DescriptionL("MetadataCacheOption_None_None", typeof(Resources))]
        None,
        [DescriptionL("MetadataCacheOption_PreferCacheThenOnline_PreferCacheThenOnline", typeof(Resources))]
        PreferCacheThenOnline,
        [DescriptionL("MetadataCacheOption_PreferOnlineWithCache_PreferOnlineWithCache", typeof(Resources))]
        PreferOnlineWithCache,
        [DescriptionL("MetadataCacheOption_UseOverrideCacheOnly_UseOverrideCacheOnly", typeof(Resources))]
        UseOverrideCacheOnly
    }

    /// <summary>
    /// 元数据增强 · 本地元数据缓存分组：缓存模式与缓存根目录。
    /// </summary>
    public class MetadataCacheOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_MetadataCache", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_MetadataCache;

        [Browsable(false)]
        public List<EditorRadioOption> MetadataCacheModeList { get; set; } = new List<EditorRadioOption>();

        [DisplayNameL("MetadataEnhanceOptions_MetadataCacheMode_Build_Local_Metadata_Cache", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MetadataCacheMode_Description", typeof(Resources))]
        [SelectItemsSource(nameof(MetadataCacheModeList))]
        [SelectShowRadioGroup]
        public string MetadataCacheMode { get; set; } = MetadataCacheOption.None.ToString();

        [DisplayNameL("MetadataEnhanceOptions_MetadataCacheRootFolder_Metadata_Cache_Root_Folder", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MetadataCacheRootFolder_Description", typeof(Resources))]
        [EditFolderPicker]
        [VisibleCondition(nameof(MetadataCacheMode), ValueCondition.IsNotEqual, MetadataCacheOption.None)]
        public string MetadataCacheRootFolder { get; set; } = string.Empty;

        /// <summary>被动缓存有效期；0 表示永不过期。只影响 cache，不影响 override/patches。</summary>
        [DisplayNameL("MetadataEnhanceOptions_MetadataCacheTtlDays_Cache_TTL_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MetadataCacheTtlDays_Description", typeof(Resources))]
        [Required, MinValue(0), MaxValue(3650)]
        [VisibleCondition(nameof(MetadataCacheMode), ValueCondition.IsNotEqual, MetadataCacheOption.None)]
        public int MetadataCacheTtlDays { get; set; } = 30;

        /// <summary>
        /// 启动时清空被动缓存（<c>&lt;root&gt;\cache</c>），保留 override 与 patches。
        /// 场景：TMDB 侧修正了某部片的信息，希望下次刮削立刻拿到新数据，而不是等缓存过期。
        /// </summary>
        [DisplayNameL("MetadataEnhanceOptions_ClearRecentTmdbCacheOnStartup_Clear_TMDB_Cache_On_Startup", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ClearRecentTmdbCacheOnStartup_Description", typeof(Resources))]
        [VisibleCondition(nameof(MetadataCacheMode), ValueCondition.IsNotEqual, MetadataCacheOption.None)]
        public bool ClearRecentTmdbCacheOnStartup { get; set; } = false;

        /// <summary>
        /// 图像缺失时可跳过的图像类型（逗号分隔，如 Logo,Backdrop）：只要提供了主海报就不再请求图像 API。
        /// </summary>
        [Browsable(false)]
        public string MetadataCacheOptionalImageTypes { get; set; } = string.Empty;

        private MetadataCacheOption ParsedMetadataCacheMode =>
            Enum.TryParse<MetadataCacheOption>(MetadataCacheMode, true, out var m) ? m : MetadataCacheOption.None;

        /// <summary>缓存总开关：模式非 None 且已指定根目录。</summary>
        [Browsable(false)]
        public bool IsMetadataCacheEnabled =>
            ParsedMetadataCacheMode != MetadataCacheOption.None && !string.IsNullOrWhiteSpace(MetadataCacheRootFolder);

        /// <summary>是否允许从缓存读取（缓存优先 / 仅用缓存）。</summary>
        [Browsable(false)]
        public bool IsMetadataCacheReadEnabled =>
            IsMetadataCacheEnabled &&
            (ParsedMetadataCacheMode == MetadataCacheOption.PreferCacheThenOnline ||
             ParsedMetadataCacheMode == MetadataCacheOption.UseOverrideCacheOnly);

        /// <summary>命中未缓存时是否写回缓存（缓存优先 / 在线并缓存）。</summary>
        [Browsable(false)]
        public bool IsMetadataCacheWriteEnabled =>
            IsMetadataCacheEnabled &&
            (ParsedMetadataCacheMode == MetadataCacheOption.PreferCacheThenOnline ||
             ParsedMetadataCacheMode == MetadataCacheOption.PreferOnlineWithCache);

        public void Initialize()
        {
            MetadataCacheModeList.Clear();
            MetadataCacheModeList = new List<EditorRadioOption>
            {
                new EditorRadioOption
                {
                    Value = MetadataCacheOption.None,
                    PrimaryText = Utility.GetEnumDescription(MetadataCacheOption.None)
                },
                new EditorRadioOption
                {
                    Value = MetadataCacheOption.PreferCacheThenOnline,
                    PrimaryText = Utility.GetEnumDescription(MetadataCacheOption.PreferCacheThenOnline)
                },
                new EditorRadioOption
                {
                    Value = MetadataCacheOption.PreferOnlineWithCache,
                    PrimaryText = Utility.GetEnumDescription(MetadataCacheOption.PreferOnlineWithCache)
                },
                new EditorRadioOption
                {
                    Value = MetadataCacheOption.UseOverrideCacheOnly,
                    PrimaryText = Utility.GetEnumDescription(MetadataCacheOption.UseOverrideCacheOnly)
                }
            };
        }

        protected override void Validate(ValidationContext context)
        {
            if (string.Equals(MetadataCacheMode, MetadataCacheOption.None.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string cacheError = null;

            if (string.IsNullOrWhiteSpace(MetadataCacheRootFolder))
            {
                // 场景：选了缓存模式但还没指定目录 —— 给出「必填」提示，而非误导性的「不存在或不可写」
                cacheError = Resources.MetadataCacheRootFolderRequired;
            }
            else
            {
                var (ok, code) = LocalMetadataCache.Validate(MetadataCacheRootFolder);
                if (!ok)
                {
                    cacheError = code == "ROOT_NOT_ALLOWED"
                        ? Resources.MetadataCacheRootFolderNotAllowed
                        : Resources.MetadataCacheRootFolderNotExist;
                }
            }

            if (cacheError != null)
            {
                context.AddValidationError(cacheError);
            }
        }
    }
}
