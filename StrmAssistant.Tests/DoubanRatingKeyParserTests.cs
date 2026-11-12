using StrmAssistant.Provider.Douban;
using Xunit;

namespace StrmAssistant.Tests
{
    public class DoubanRatingKeyParserTests
    {
        [Theory]
        [InlineData("tt0111161", DoubanRatingKeyType.Imdb, "tt0111161", "tt0111161", "imdb:tt0111161")]
        [InlineData(" IMDB:tt0111161 ", DoubanRatingKeyType.Imdb, "tt0111161", "tt0111161", "imdb:tt0111161")]
        [InlineData("tmdb:550", DoubanRatingKeyType.Tmdb, "550", "tmdb:550", "tmdb:550")]
        [InlineData(" DouBan:1292052 ", DoubanRatingKeyType.Douban, "1292052", "douban:1292052",
            "douban:1292052")]
        public void TryParse_AcceptsSupportedKeys(string raw, DoubanRatingKeyType type, string value,
            string canonicalKey, string indexKey)
        {
            Assert.True(DoubanRatingKeyParser.TryParse(raw, out var key));
            Assert.Equal(type, key.Type);
            Assert.Equal(value, key.Value);
            Assert.Equal(canonicalKey, key.CanonicalKey);
            Assert.Equal(indexKey, key.IndexKey);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("12345")]
        [InlineData("tt")]
        [InlineData("ttabc")]
        [InlineData("tmdb:")]
        [InlineData("tmdb:abc")]
        [InlineData("douban:12a")]
        [InlineData("imdb:")]
        [InlineData("foo:123")]
        public void TryParse_RejectsUnsupportedKeys(string raw)
        {
            Assert.False(DoubanRatingKeyParser.TryParse(raw, out _));
        }
    }
}
