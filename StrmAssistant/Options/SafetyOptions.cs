using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 体验增强 · 安全分组：演练模式只记录将要执行的操作，不写库、不删文件。
    /// </summary>
    public class SafetyOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_Group_Safety", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_Group_Safety;

        [DisplayNameL("ExperienceEnhanceOptions_DryRun_Dry_Run", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_DryRun_Description", typeof(Resources))]
        [Required]
        public bool DryRun { get; set; } = false;
    }
}
