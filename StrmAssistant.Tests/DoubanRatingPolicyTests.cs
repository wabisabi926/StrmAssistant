using StrmAssistant.Provider.Douban;
using System;
using Xunit;

namespace StrmAssistant.Tests
{
    public class DoubanRatingPolicyTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset OldPremiere = Now.AddDays(-5000);
        private static readonly DateTimeOffset RecentPremiere = Now.AddDays(-30);

        [Fact]
        public void ManualSync_IgnoresLookback()
        {
            Assert.True(DoubanRatingPolicy.IsRatingUpdateAllowed(true, 365, OldPremiere, true, Now));
        }

        [Fact]
        public void FirstApplication_OnOldItem_IsAllowed()
        {
            Assert.True(DoubanRatingPolicy.IsRatingUpdateAllowed(false, 365, OldPremiere, false, Now));
        }

        [Fact]
        public void SubsequentUpdate_OnOldItem_IsBlocked()
        {
            Assert.False(DoubanRatingPolicy.IsRatingUpdateAllowed(false, 365, OldPremiere, true, Now));
        }

        [Fact]
        public void WithinLookback_IsAllowed()
        {
            Assert.True(DoubanRatingPolicy.IsRatingUpdateAllowed(false, 365, RecentPremiere, true, Now));
        }

        [Fact]
        public void ZeroLookback_AllowsEverything()
        {
            Assert.True(DoubanRatingPolicy.IsRatingUpdateAllowed(false, 0, OldPremiere, true, Now));
        }

        [Fact]
        public void MissingPremiereDate_IsAllowed()
        {
            Assert.True(DoubanRatingPolicy.IsRatingUpdateAllowed(false, 365, null, true, Now));
        }
    }
}
