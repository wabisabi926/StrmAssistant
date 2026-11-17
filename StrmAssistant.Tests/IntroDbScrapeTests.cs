using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IntroDbScrapeTests
    {
        private static List<IntroDbApi.IntroDbSegment> Seg(long? start, long? end) =>
            new List<IntroDbApi.IntroDbSegment> { new IntroDbApi.IntroDbSegment { start_ms = start, end_ms = end } };

        private static IntroDbApi.SourceResult Source(string provider, bool intro, bool credits) => new IntroDbApi.SourceResult
        {
            Provider = provider,
            Intro = intro ? Seg(1000, 2000) : null,
            Credits = credits ? Seg(3000, 4000) : null
        };

        [Theory]
        [InlineData("{\"error\":\"No submissions for this episode\",\"code\":\"no_submissions\"}", "no_submissions")]
        [InlineData("{\"error\":\"Episode not found\",\"code\":\"episode_not_found\",\"details\":\"S1E2\"}", "episode_not_found")]
        [InlineData("{\"error\":\"Media not found\",\"code\":\"media_not_found\"}", "media_not_found")]
        [InlineData("{\"code\":\" NO_SUBMISSIONS \"}", "no_submissions")]
        [InlineData("{\"code\":\"specific_media_usage_limit_exceeded\"}", "specific_media_usage_limit_exceeded")]
        public void ParseNoDataCode_ReadsTopLevelCode(string body, string expected)
        {
            Assert.Equal(expected, IntroDbApi.ParseNoDataCode(body));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Not Found")]
        [InlineData("{}")]
        [InlineData("{\"error\":\"No submissions\"}")]
        [InlineData("{\"code\":123}")]
        [InlineData("{\"error\":{\"code\":\"no_submissions\"}}")]
        [InlineData("[1,2]")]
        public void ParseNoDataCode_ReturnsNullWhenNoUsableCode(string body)
        {
            Assert.Null(IntroDbApi.ParseNoDataCode(body));
        }

        [Fact]
        public void ResolveResults_PrefersCompleteSourceOverMerging()
        {
            var results = new List<IntroDbApi.SourceResult>
            {
                Source("IntroDb", false, true),
                Source("Tidb", true, true)
            };

            var merged = IntroDbApi.ResolveResults(results, out var introSource, out var creditsSource);

            Assert.Equal("Tidb", introSource);
            Assert.Equal("Tidb", creditsSource);
            Assert.Same(results[1].Intro, merged.intro);
            Assert.Same(results[1].Credits, merged.credits);
        }

        [Fact]
        public void ResolveResults_FirstCompleteSourceWins()
        {
            var results = new List<IntroDbApi.SourceResult>
            {
                Source("IntroDb", true, true),
                Source("Tidb", true, true)
            };

            var merged = IntroDbApi.ResolveResults(results, out var introSource, out var creditsSource);

            Assert.Equal("IntroDb", introSource);
            Assert.Equal("IntroDb", creditsSource);
            Assert.Same(results[0].Intro, merged.intro);
        }

        [Fact]
        public void ResolveResults_MergesWhenNoSourceComplete()
        {
            var results = new List<IntroDbApi.SourceResult>
            {
                Source("IntroDb", false, true),
                Source("Tidb", true, false)
            };

            var merged = IntroDbApi.ResolveResults(results, out var introSource, out var creditsSource);

            Assert.Equal("Tidb", introSource);
            Assert.Equal("IntroDb", creditsSource);
            Assert.Same(results[1].Intro, merged.intro);
            Assert.Same(results[0].Credits, merged.credits);
        }

        [Fact]
        public void ResolveResults_ReturnsNullWhenNoData()
        {
            var results = new List<IntroDbApi.SourceResult> { Source("IntroDb", false, false) };

            var merged = IntroDbApi.ResolveResults(results, out var introSource, out var creditsSource);

            Assert.Null(merged);
            Assert.Null(introSource);
            Assert.Null(creditsSource);
        }

        [Fact]
        public void MaxStartDeltaMs_ReportsLargestFieldDelta()
        {
            var results = new List<IntroDbApi.SourceResult>
            {
                new IntroDbApi.SourceResult { Provider = "IntroDb", Intro = Seg(0, 47000), Credits = Seg(1360000, null) },
                new IntroDbApi.SourceResult { Provider = "Tidb", Intro = Seg(500, 47500), Credits = Seg(2783000, null) }
            };

            Assert.Equal(1423000, IntroDbApi.MaxStartDeltaMs(results));
        }

        [Fact]
        public void OrderProviders_MoviePutsTidbFirst()
        {
            var ordered = IntroDbApi.OrderProviders(new List<string> { "IntroDb", "Tidb" }, true);

            Assert.Equal(new[] { "Tidb", "IntroDb" }, ordered);
        }

        [Fact]
        public void OrderProviders_TvKeepsConfiguredOrder()
        {
            var ordered = IntroDbApi.OrderProviders(new List<string> { "IntroDb", "Tidb" }, false);

            Assert.Equal(new[] { "IntroDb", "Tidb" }, ordered);
        }

        [Fact]
        public void ParseUnifiedProviders_KeepsExplicitSingleSource()
        {
            var providers = IntroDbApi.ParseUnifiedProviders("IntroDb");

            Assert.Single(providers);
            Assert.Equal("IntroDb", providers[0]);
        }

        [Fact]
        public void ParseUnifiedProviders_UsesDefaultWhenEmpty()
        {
            var providers = IntroDbApi.ParseUnifiedProviders(string.Empty);

            Assert.Equal(new[] { "IntroDb", "Tidb" }, providers);
        }

        [Fact]
        public void BuildTheIntroDbUrl_KeepsTmdbAndAddsTvdbEpisodeIdAsPin()
        {
            // v4 要求媒体 id + season/episode；单集 TVDB id 只能作为附加提示，不能单独使用。
            var url = IntroDbApi.BuildTheIntroDbUrl("1399", "121361", "5577121", "tt0944947", 1, 2, 3_600_000);

            Assert.Equal(
                "https://api.theintrodb.org/v4/media?tmdb_id=1399&season=1&episode=2&tvdb_episode_id=5577121&duration_ms=3600000",
                url);
        }

        [Fact]
        public void BuildTheIntroDbUrl_MovieIgnoresTvdbEpisodeId()
        {
            var url = IntroDbApi.BuildTheIntroDbUrl("603", "169", "5577121", "tt0133093", null, null, null);

            Assert.DoesNotContain("tvdb_episode_id", url);
        }

        [Fact]
        public void BuildTheIntroDbUrl_FallsBackToTmdb()
        {
            var url = IntroDbApi.BuildTheIntroDbUrl("1399", "121361", null, "tt0944947", 1, 2, null);

            Assert.Equal(
                "https://api.theintrodb.org/v4/media?tmdb_id=1399&season=1&episode=2",
                url);
        }

        [Fact]
        public void BuildTheIntroDbUrl_FallsBackToTvdbWhenNoTmdb()
        {
            var url = IntroDbApi.BuildTheIntroDbUrl(null, "121361", null, "tt0944947", 1, 2, null);

            Assert.Equal(
                "https://api.theintrodb.org/v4/media?tvdb_id=121361&season=1&episode=2",
                url);
        }

        [Fact]
        public void BuildTheIntroDbUrl_FallsBackToImdb()
        {
            var url = IntroDbApi.BuildTheIntroDbUrl(null, null, null, "tt0944947", 1, 2, null);

            Assert.Equal(
                "https://api.theintrodb.org/v4/media?imdb_id=tt0944947&season=1&episode=2",
                url);
        }

        [Fact]
        public void BuildTheIntroDbUrl_MovieUsesTmdbWithoutType()
        {
            var url = IntroDbApi.BuildTheIntroDbUrl("603", "169", null, "tt0133093", null, null, null);

            Assert.Equal("https://api.theintrodb.org/v4/media?tmdb_id=603", url);
            Assert.DoesNotContain("type=", url);
            Assert.DoesNotContain("season", url);
        }

        [Fact]
        public void BuildTheIntroDbUrl_ReturnsNullWhenNoIdAvailable()
        {
            Assert.Null(IntroDbApi.BuildTheIntroDbUrl(null, null, null, null, 1, 2, null));
        }

        [Fact]
        public void BuildTheIntroDbUrl_OmitsNonPositiveDuration()
        {
            var url = IntroDbApi.BuildTheIntroDbUrl("603", null, null, null, null, null, 0);

            Assert.DoesNotContain("duration_ms", url);
        }

        [Fact]
        public void TheIntroDbBaseUrl_IsV4()
        {
            Assert.EndsWith("/v4", IntroDbApi.TheIntroDbBaseUrl);
        }

        [Fact]
        public void PickLongestSegment_UsesEndOnlySegment()
        {
            var segments = new List<IntroDbApi.IntroDbSegment>
            {
                new IntroDbApi.IntroDbSegment { start_ms = 0, end_ms = 40000 },
                new IntroDbApi.IntroDbSegment { start_ms = null, end_ms = 90000 }
            };

            var picked = ChapterApi.PickLongestSegment(segments);

            Assert.NotNull(picked);
            Assert.Null(picked.start_ms);
            Assert.Equal(90000, picked.end_ms);
        }

        [Fact]
        public void PickLongestSegment_DropsSegmentsWithoutAnyBoundary()
        {
            var segments = new List<IntroDbApi.IntroDbSegment>
            {
                new IntroDbApi.IntroDbSegment()
            };

            Assert.Null(ChapterApi.PickLongestSegment(segments));
        }

        [Fact]
        public void GetIntroMarkerTicks_UsesRuntimeWhenEndMissing()
        {
            var segment = new IntroDbApi.IntroDbSegment { start_ms = 1000, end_ms = null };

            var (start, end) = ChapterApi.GetIntroMarkerTicks(segment, 1_000_000_000);

            Assert.Equal(10_000_000, start);
            Assert.Equal(1_000_000_000, end);
        }

        [Fact]
        public void GetIntroMarkerTicks_ClampsEndToRuntime()
        {
            var segment = new IntroDbApi.IntroDbSegment { start_ms = 0, end_ms = 60000 };

            var (_, end) = ChapterApi.GetIntroMarkerTicks(segment, 50_000_000);

            Assert.Equal(50_000_000, end);
        }

        [Fact]
        public void GetIntroMarkerTicks_MovieEndOnlyStartsAtZero()
        {
            var segment = new IntroDbApi.IntroDbSegment { start_ms = null, end_ms = 90000 };

            var (start, end) = ChapterApi.GetIntroMarkerTicks(segment, null);

            Assert.Equal(0, start);
            Assert.Equal(900_000_000, end);
        }
    }
}
