using MediaBrowser.Controller.Entities.TV;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class MediaInfoPrefetchTests
    {
        private static Episode Episode(int season, int index) =>
            new Episode { ParentIndexNumber = season, IndexNumber = index };

        [Fact]
        public void PicksNextEpisodeInSameSeason()
        {
            var next = MediaInfoPrefetch.PickNext(
                new[] { Episode(0, 9), Episode(1, 1), Episode(1, 2), Episode(1, 3) }, 1, 1);

            Assert.NotNull(next);
            Assert.Equal(1, next.ParentIndexNumber);
            Assert.Equal(2, next.IndexNumber);
        }

        [Fact]
        public void PicksNextSeasonWhenCurrentSeasonEnds()
        {
            var next = MediaInfoPrefetch.PickNext(
                new[] { Episode(1, 1), Episode(1, 2), Episode(2, 1) }, 1, 2);

            Assert.NotNull(next);
            Assert.Equal(2, next.ParentIndexNumber);
            Assert.Equal(1, next.IndexNumber);
        }

        [Fact]
        public void DoesNotPickEarlierSeason()
        {
            var next = MediaInfoPrefetch.PickNext(
                new[] { Episode(1, 1), Episode(2, 1), Episode(2, 2) }, 2, 2);

            Assert.Null(next);
        }

        [Fact]
        public void ContinuesSpecialsWhenCurrentIsSpecial()
        {
            var next = MediaInfoPrefetch.PickNext(
                new[] { Episode(0, 5), Episode(0, 6), Episode(1, 1) }, 0, 5);

            Assert.NotNull(next);
            Assert.Equal(0, next.ParentIndexNumber);
            Assert.Equal(6, next.IndexNumber);
        }

        [Fact]
        public void ReturnsNullAtEndOfSeries()
        {
            Assert.Null(MediaInfoPrefetch.PickNext(
                new[] { Episode(1, 1), Episode(1, 2) }, 1, 2));
        }
    }
}
