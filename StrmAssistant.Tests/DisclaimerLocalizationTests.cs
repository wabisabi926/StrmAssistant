using System.Globalization;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using Xunit;

namespace StrmAssistant.Tests
{
    public class DisclaimerLocalizationTests
    {
        private static readonly string[] Keys =
        {
            "DisclaimerDialog_EditorTitle_Disclaimer",
            "DisclaimerDialog_Statement_Disclaimer",
            "DisclaimerDialog_UsageNoticeCaption_TermsOfUse",
            "DisclaimerDialog_LegalUse_Title",
            "DisclaimerDialog_LegalUse_Description",
            "DisclaimerDialog_License_Title",
            "DisclaimerDialog_License_Description",
            "DisclaimerDialog_NoProprietaryComponents_Title",
            "DisclaimerDialog_NoProprietaryComponents_Description",
            "DisclaimerDialog_FeatureLimits_Title",
            "DisclaimerDialog_FeatureLimits_Description",
            "DisclaimerDialog_UserResponsibility_Title",
            "DisclaimerDialog_UserResponsibility_Description",
            "DisclaimerDialog_DisclaimerCaption_LimitationOfLiability",
            "DisclaimerDialog_Liability_Description",
            "DisclaimerDialog_Contact_Description"
        };

        [Theory]
        [InlineData("en-US")]
        [InlineData("zh-CN")]
        [InlineData("zh-Hant")]
        public void AllDisclaimerKeys_AreTranslated(string cultureName)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);

            foreach (var key in Keys)
            {
                var value = Resources.ResourceManager.GetString(key, culture);
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is missing for {cultureName}");
            }
        }

        [Fact]
        public void DialogContent_UsesLocalizedResources()
        {
            var dialog = new DisclaimerDialog();

            Assert.Equal(Resources.DisclaimerDialog_EditorTitle_Disclaimer, dialog.EditorTitle);
            Assert.Equal(Resources.DisclaimerDialog_Statement_Disclaimer, dialog.Statement.Text);
            Assert.Equal(Resources.DisclaimerDialog_UsageNoticeCaption_TermsOfUse,
                dialog.UsageNoticeCaption.Caption);
            Assert.Equal(Resources.DisclaimerDialog_DisclaimerCaption_LimitationOfLiability,
                dialog.DisclaimerCaption.Caption);
            Assert.Equal(5, dialog.UsageNoticeList.Count);
            Assert.Equal(2, dialog.DisclaimerList.Count);

            foreach (var item in dialog.UsageNoticeList)
            {
                Assert.False(string.IsNullOrWhiteSpace(item.PrimaryText));
                Assert.False(string.IsNullOrWhiteSpace(item.SecondaryText));
            }

            foreach (var item in dialog.DisclaimerList)
            {
                Assert.False(string.IsNullOrWhiteSpace(item.SecondaryText));
            }
        }
    }
}
