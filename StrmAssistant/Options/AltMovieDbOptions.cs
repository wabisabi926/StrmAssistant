using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using static StrmAssistant.Common.CommonUtility;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 元数据增强 · 备用 MovieDb 接口分组：自定义 TMDB API / 图片地址与 API Key。
    /// </summary>
    public class AltMovieDbOptions : EditableOptionsBase
    {
        [DisplayNameL("MetadataEnhanceOptions_Group_AltMovieDb", typeof(Resources))]
        public override string EditorTitle => Resources.MetadataEnhanceOptions_Group_AltMovieDb;

        [DisplayNameL("MetadataEnhanceOptions_EnableAltMovieDbUrl", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbConfig_Description", typeof(Resources))]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        [Required]
        public bool AltMovieDbConfig { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbApiUrl_Alternative_MovieDb_Api_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbApiUrl_Default_alternative_is_https___api_tmdb_org", typeof(Resources))]
        [VisibleCondition(nameof(AltMovieDbConfig), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string AltMovieDbApiUrl { get; set; } = "https://api.tmdb.org";

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbImageUrl_Alternative_MovieDb_Image_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbImageUrl_No_default_alternative__Provide_by_yourself_", typeof(Resources))]
        [VisibleCondition(nameof(AltMovieDbConfig), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string AltMovieDbImageUrl { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbApiKey_Alternative_MovieDb_Api_Key", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbApiKey_Provide_your_own_MovieDb_Api_Key__Blank_uses_system_default_", typeof(Resources))]
        [VisibleCondition(nameof(AltMovieDbConfig), SimpleCondition.IsTrue)]
        [EnabledCondition(nameof(IsMovieDbPluginLoaded), SimpleCondition.IsTrue)]
        public string AltMovieDbApiKey { get; set; } = string.Empty;

        [Browsable(false)]
        public bool IsMovieDbPluginLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "MovieDb") && IsModSupported;

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        protected override void Validate(ValidationContext context)
        {
            string errors = null;

            foreach (var (value, isValid, errorResource) in new (string, Func<string, bool>, string)[]
                     {
                         (AltMovieDbApiUrl, IsValidHttpUrl, Resources.InvalidAltMovieDbApiUrl),
                         (AltMovieDbImageUrl, IsValidHttpUrl, Resources.InvalidAltMovieDbImageUrl),
                         (AltMovieDbApiKey, IsValidMovieDbApiKey, Resources.InvalidAltMovieDbApiKey)
                     })
            {
                if (!string.IsNullOrWhiteSpace(value) && !isValid(value))
                {
                    errors = errors == null ? errorResource : $"{errors}; {errorResource}";
                }
            }

            if (!string.IsNullOrEmpty(errors))
            {
                context.AddValidationError(errors);
            }
        }
    }
}
