using StrmAssistant.Common;
using System.Collections.Generic;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IntroDbScrapeTests
    {
        [Fact]
        public void EffectiveCredits_PrefersCreditsOverOutro()
        {
            var credits = new List<IntroDbApi.IntroDbSegment>
            {
                new IntroDbApi.IntroDbSegment { start_ms = 1000, end_ms = 2000 }
            };
            var outro = new List<IntroDbApi.IntroDbSegment>
            {
                new IntroDbApi.IntroDbSegment { start_ms = 3000, end_ms = 4000 }
            };

            var media = new IntroDbApi.IntroDbMedia { credits = credits, outro = outro };

            Assert.Same(credits, media.EffectiveCredits);
        }

        [Fact]
        public void EffectiveCredits_FallsBackToOutro()
        {
            var outro = new List<IntroDbApi.IntroDbSegment>
            {
                new IntroDbApi.IntroDbSegment { start_ms = 3000, end_ms = 4000 }
            };

            var media = new IntroDbApi.IntroDbMedia { outro = outro };

            Assert.Same(outro, media.EffectiveCredits);
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
        public void GetIntroMarkerTicks_ConvertsStartAndEndMilliseconds()
        {
            var segment = new IntroDbApi.IntroDbSegment { start_ms = 0, end_ms = 51849 };

            var (start, end) = ChapterApi.GetIntroMarkerTicks(segment, null);

            Assert.Equal(0, start);
            Assert.Equal(518_490_000, end);
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
