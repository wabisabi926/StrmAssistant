using StrmAssistant.Common;
using StrmAssistant.ScheduledTask;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IntroMarkerCleanupTests
    {
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void ShouldScrapeMovie_OnlySkipsWhenExternalIntroAndCreditsExist(
            bool hasNonDbIntro, bool hasCredits, bool expected)
        {
            Assert.Equal(expected, ScrapeIntroDbTask.ShouldScrapeMovie(hasNonDbIntro, hasCredits));
        }

        [Fact]
        public void ShouldIncludeAllClearLibraries_WhenAllScopesBlank()
        {
            Assert.True(ChapterApi.ShouldIncludeAllClearLibraries(new[] { string.Empty, "  ", null }));
        }

        [Theory]
        [InlineData("3", "", "")]
        [InlineData("", "7", "")]
        [InlineData("", "", "9")]
        [InlineData("3", "7", "9")]
        public void ShouldIncludeAllClearLibraries_WhenAnyScopeConfigured(
            string playback, string fingerprint, string scrape)
        {
            Assert.False(ChapterApi.ShouldIncludeAllClearLibraries(new[] { playback, fingerprint, scrape }));
        }
    }
}
