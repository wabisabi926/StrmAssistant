using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 概览 · 配置迁移分组：导出/导入目录与操作，独立于维护/重启等危险操作。
    /// </summary>
    public class ConfigTransferOptions : EditableOptionsBase
    {
        [DisplayNameL("GeneralOptions_Group_ConfigTransfer", typeof(Resources))]
        public override string EditorTitle => Resources.GeneralOptions_Group_ConfigTransfer;

        [DisplayNameL("GeneralOptions_ConfigTransferFolder_Config_Transfer_Folder", typeof(Resources))]
        [DescriptionL("GeneralOptions_ConfigTransferFolder_Description", typeof(Resources))]
        [EditFolderPicker]
        public string ConfigTransferFolder { get; set; } = string.Empty;

        public ButtonItem ExportConfigButton =>
            new ButtonItem(Resources.GeneralOptions_ExportConfig_Export_Config)
            {
                Icon = IconNames.code, Data1 = "ExportConfig"
            };

        public ButtonItem ImportConfigButton =>
            new ButtonItem(Resources.GeneralOptions_ImportConfig_Import_Config)
            {
                Icon = IconNames.build, Data1 = "ImportConfig",
                ConfirmationPrompt = Resources.GeneralOptions_ImportConfig_Confirm
            };
    }
}
