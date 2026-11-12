using StrmAssistant.Common;
using StrmAssistant.Options;
using StrmAssistant.Provider.Douban;
using System;
using System.Globalization;
using Xunit;

namespace StrmAssistant.Tests
{
    public class NotificationLocalizationTests
    {
        [Fact]
        public void CommunityRatingSummary_IsLocalized()
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
                var report = new CommunityRatingUnifyReport
                {
                    Applied = true,
                    Source = CommunityRatingSourceOption.Tmdb,
                    Total = 10,
                    Updated = 2,
                    Same = 7,
                    Failed = 1
                };

                var text = report.BuildUserSummary();

                Assert.Contains("社区评分", text);
                Assert.Contains("10", text);
                Assert.Contains("TMDB", text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        }

        [Fact]
        public void DoubanSummary_IsLocalized()
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
                var report = new DoubanUnifyReport
                {
                    Total = 10,
                    Updated = 2,
                    Same = 7,
                    Failed = 1
                };

                var text = report.BuildUserSummary();

                Assert.Contains("豆瓣评分", text);
                Assert.Contains("10", text);
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        }
    }
}
