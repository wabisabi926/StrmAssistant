using StrmAssistant.Options;
using StrmAssistant.Properties;
using System.Globalization;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 社区评分来源的默认值与解析规则。
    /// 这些是「不写库」的纯逻辑，必须保证：默认不干预、非法值回退为不干预、豆瓣不视为统一。
    /// </summary>
    public class CommunityRatingOptionsTests
    {
        [Fact]
        public void Defaults_DoNotTouchCommunityRating()
        {
            var options = new CommunityRatingOptions();

            Assert.Equal(CommunityRatingSourceOption.None, options.ParsedSource);
            Assert.False(options.IsUnificationEnabled);
            Assert.Equal(5, options.CommunityRatingMinVotes);
            Assert.False(options.CommunityRatingClearWhenMissing);
        }

        [Theory]
        [InlineData("Tmdb", CommunityRatingSourceOption.Tmdb, true)]
        [InlineData("IMDb", CommunityRatingSourceOption.Imdb, true)]
        [InlineData("douban", CommunityRatingSourceOption.Douban, false)]
        [InlineData("", CommunityRatingSourceOption.None, false)]
        [InlineData("garbage", CommunityRatingSourceOption.None, false)]
        public void ParsedSource_IsCaseInsensitiveAndSafe(string raw, CommunityRatingSourceOption expected,
            bool enabled)
        {
            var options = new CommunityRatingOptions { CommunityRatingSource = raw };

            Assert.Equal(expected, options.ParsedSource);
            Assert.Equal(enabled, options.IsUnificationEnabled);
        }

        [Theory]
        [InlineData(CommunityRatingSourceOption.None, false, false)]
        [InlineData(CommunityRatingSourceOption.Tmdb, false, false)]
        [InlineData(CommunityRatingSourceOption.Imdb, false, false)]
        [InlineData(CommunityRatingSourceOption.Douban, false, true)]
        [InlineData(CommunityRatingSourceOption.Tmdb, true, true)]
        public void DoubanDataLayer_ActivatesForDoubanSourceOrCapability(CommunityRatingSourceOption source,
            bool capability, bool expected)
        {
            var options = new MetadataEnhanceOptions
            {
                CommunityRating = { CommunityRatingSource = source.ToString() },
                DoubanAssist = { DoubanFetchRating = capability }
            };

            Assert.Equal(source == CommunityRatingSourceOption.Douban, options.IsDoubanRatingSource);
            Assert.Equal(expected, options.IsDoubanDataEnabled);
        }

        [Fact]
        public void DoubanDataLayer_ActivatesForAnyMetadataCapability()
        {
            Assert.True(new MetadataEnhanceOptions { DoubanAssist = { DoubanFillOverview = true } }.IsDoubanDataEnabled);
            Assert.True(new MetadataEnhanceOptions { DoubanAssist = { DoubanCastChinese = true } }.IsDoubanDataEnabled);
            Assert.True(new MetadataEnhanceOptions { DoubanAssist = { DoubanPosterFallback = true } }.IsDoubanDataEnabled);
            Assert.True(new MetadataEnhanceOptions { DoubanAssist = { DoubanRatingToKodi = true } }.IsDoubanDataEnabled);
            Assert.False(new MetadataEnhanceOptions().IsDoubanDataEnabled);
        }

        [Fact]
        public void MdbListKey_LivesOnCommunityRatingAndDoesNotActivateDoubanLayer()
        {
            var options = new MetadataEnhanceOptions
            {
                CommunityRating = { MdbListApiKey = "test-key" }
            };

            // 凭据属于外部评分源：不影响豆瓣数据层，也不会让豆瓣能力隐式开启。
            Assert.Equal("test-key", options.CommunityRating.MdbListApiKey);
            Assert.False(options.IsDoubanDataEnabled);
            Assert.False(options.DoubanAssist.EnableMdbListCriticRating);
        }

        [Theory]
        [InlineData("")]
        [InlineData("zh-CN")]
        [InlineData("zh-Hant")]
        public void NewResourceKeys_AreLocalized(string cultureName)
        {
            var culture = new CultureInfo(cultureName);
            var keys = new[]
            {
                "MetadataEnhanceOptions_Group_CommunityRating",
                "MetadataEnhanceOptions_CommunityRatingSource_Community_Rating_Source",
                "MetadataEnhanceOptions_CommunityRatingSource_Description",
                "CommunityRatingSourceOption_None_Do_Not_Interfere",
                "CommunityRatingSourceOption_Tmdb_TMDb",
                "CommunityRatingSourceOption_Imdb_IMDb_via_MdbList",
                "CommunityRatingSourceOption_Douban_Douban",
                "MetadataEnhanceOptions_CommunityRatingMinVotes_Minimum_Votes",
                "MetadataEnhanceOptions_CommunityRatingMinVotes_Description",
                "MetadataEnhanceOptions_CommunityRatingClearWhenMissing_Clear_When_No_Rating",
                "MetadataEnhanceOptions_CommunityRatingClearWhenMissing_Description",
                "MetadataEnhanceOptions_UnifyCommunityRatingButton_Unify_Now",
                "MetadataEnhanceOptions_UnifyCommunityRatingButton_Description",
                "UnifyCommunityRatingTask_Name_Unify_Community_Rating",
                "UnifyCommunityRatingTask_Description_Unify_CommunityRating_to_the_configured_source"
            };

            foreach (var key in keys)
            {
                Assert.False(string.IsNullOrEmpty(Resources.ResourceManager.GetString(key, culture)), key);
            }
        }
    }
}
