using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 本地 TMDB 缓存键解析（回归 F6）。
    ///
    /// 旧正则没有锚定结尾：<c>/3/movie/123</c> 与 <c>/3/movie/123/credits</c> 都会落到
    /// <c>tmdb-movies2/123/all.json</c>，详情响应被当成演职员响应喂给 Emby（反之亦然）。
    /// 另外 <c>?append_to_response=...</c> 不参与缓存键，同一文件会承载多种 JSON 结构。
    /// </summary>
    public class MetadataCacheKeyTests
    {
        private const string Host = "https://api.themoviedb.org";

        [Theory]
        [InlineData("/3/movie/123", "tmdb-movies2", "123", "all.json")]
        [InlineData("/3/movie/123/", "tmdb-movies2", "123", "all.json")]
        [InlineData("/3/collection/77", "tmdb-collections", "77", "all.json")]
        [InlineData("/3/tv/456", "tmdb-tv", "456", "series.json")]
        [InlineData("/3/tv/456/season/2", "tmdb-tv", "456", "season-2.json")]
        [InlineData("/3/tv/456/season/2/episode/3", "tmdb-tv", "456", "season-2-episode-3.json")]
        public void Parses_CanonicalEntityPaths(string path, string typeDir, string id, string fileName)
        {
            Assert.True(LocalMetadataCache.TryParseEntity(Host + path, out var entry));
            Assert.Equal(typeDir, entry.TypeDir);
            Assert.Equal(id, entry.TmdbId);
            Assert.Equal(fileName, entry.FileName);
        }

        [Theory]
        [InlineData("/3/movie/123/credits")]          // 旧实现会误命中 all.json
        [InlineData("/3/movie/123/images")]
        [InlineData("/3/movie/123/external_ids")]
        [InlineData("/3/movie/123/reviews")]
        [InlineData("/3/person/31")]
        [InlineData("/3/search/movie")]
        [InlineData("/3/configuration")]
        [InlineData("/3/movie/abc")]                   // 非数字 id
        public void Rejects_SubResourcesAndNonEntities(string path)
        {
            Assert.False(LocalMetadataCache.TryParseEntity(Host + path, out var entry));
            Assert.Null(entry);
        }

        [Fact]
        public void Isolates_AppendToResponse()
        {
            Assert.True(LocalMetadataCache.TryParseEntity(Host + "/3/movie/123", out var plain));
            Assert.True(LocalMetadataCache.TryParseEntity(
                Host + "/3/movie/123?append_to_response=credits,images", out var withAppend));

            Assert.Equal(plain.TypeDir, withAppend.TypeDir);
            Assert.Equal(plain.TmdbId, withAppend.TmdbId);
            Assert.NotEqual(plain.FileName, withAppend.FileName);
            Assert.EndsWith(".json", withAppend.FileName);
        }

        [Fact]
        public void Isolates_Language()
        {
            Assert.True(LocalMetadataCache.TryParseEntity(Host + "/3/movie/123?language=zh-CN", out var zh));
            Assert.True(LocalMetadataCache.TryParseEntity(Host + "/3/movie/123?language=en-US", out var en));

            Assert.NotEqual(zh.FileName, en.FileName);
        }

        [Fact]
        public void Ignores_NonStructuralQueryParameters()
        {
            // api_key / page 不影响实体响应结构，不应改变缓存键
            Assert.True(LocalMetadataCache.TryParseEntity(Host + "/3/movie/123", out var plain));
            Assert.True(LocalMetadataCache.TryParseEntity(
                Host + "/3/movie/123?api_key=deadbeef&page=2", out var withNoise));

            Assert.Equal(plain.FileName, withNoise.FileName);
        }

        [Fact]
        public void Is_Stable_ForEquivalentQueryOrdering()
        {
            Assert.True(LocalMetadataCache.TryParseEntity(
                Host + "/3/movie/123?append_to_response=credits&language=zh-CN", out var first));
            Assert.True(LocalMetadataCache.TryParseEntity(
                Host + "/3/movie/123?language=zh-CN&append_to_response=credits", out var second));

            Assert.Equal(first.FileName, second.FileName);
        }
    }
}
