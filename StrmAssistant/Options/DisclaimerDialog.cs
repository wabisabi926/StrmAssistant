using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    public class DisclaimerDialog : EditableOptionsBase
    {
        public DisclaimerDialog()
        {
            UsageNoticeList.AddRange(new[]
            {
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerDialog_LegalUse_Title,
                    SecondaryText = Resources.DisclaimerDialog_LegalUse_Description,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerDialog_License_Title,
                    SecondaryText = Resources.DisclaimerDialog_License_Description,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerDialog_NoProprietaryComponents_Title,
                    SecondaryText = Resources.DisclaimerDialog_NoProprietaryComponents_Description,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerDialog_FeatureLimits_Title,
                    SecondaryText = Resources.DisclaimerDialog_FeatureLimits_Description,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerDialog_UserResponsibility_Title,
                    SecondaryText = Resources.DisclaimerDialog_UserResponsibility_Description,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                }
            });
            DisclaimerList.AddRange(new[]
            {
                new GenericListItem
                {
                    SecondaryText = Resources.DisclaimerDialog_Liability_Description,
                    ShowSecondaryFirst = true,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                },
                new GenericListItem
                {
                    SecondaryText = Resources.DisclaimerDialog_Contact_Description,
                    ShowSecondaryFirst = true,
                    Icon = IconNames.label_important,
                    IconMode = ItemListIconMode.SmallRegular,
                    Status = ItemStatus.Succeeded
                }
            });
        }

        public override string EditorTitle => Resources.DisclaimerDialog_EditorTitle_Disclaimer;

        public LabelItem Statement { get; set; } = new LabelItem(
            Resources.DisclaimerDialog_Statement_Disclaimer);

        public CaptionItem UsageNoticeCaption { get; set; } =
            new CaptionItem(Resources.DisclaimerDialog_UsageNoticeCaption_TermsOfUse);

        public GenericItemList UsageNoticeList { get; set; } = new GenericItemList();

        public CaptionItem DisclaimerCaption { get; set; } =
            new CaptionItem(Resources.DisclaimerDialog_DisclaimerCaption_LimitationOfLiability);

        public GenericItemList DisclaimerList { get; set; } = new GenericItemList();
    }
}
