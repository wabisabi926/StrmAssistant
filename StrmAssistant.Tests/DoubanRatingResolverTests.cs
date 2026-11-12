using StrmAssistant.Provider.Douban;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StrmAssistant.Tests
{
    public class DoubanRatingResolverTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Window = TimeSpan.FromDays(7);

        private static DoubanRatingClient.DoubanLightRating Legacy(
            double rating, int votes = 0,
            DoubanRatingClient.DoubanLightStatus status = DoubanRatingClient.DoubanLightStatus.Ok)
        {
            return new DoubanRatingClient.DoubanLightRating
            {
                Status = status,
                Rating = rating,
                Votes = votes
            };
        }

        private static Func<string, CancellationToken, Task<DoubanRatingClient.DoubanLightRating>> Fetch(
            DoubanRatingClient.DoubanLightRating result, Action<string> onCall = null)
        {
            return (imdbId, _) =>
            {
                onCall?.Invoke(imdbId);
                return Task.FromResult(result);
            };
        }

        private static Task<DoubanRatingClient.DoubanLightRating> MustNotFetch(string imdbId,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("legacy API should not be called");
        }

        [Fact]
        public async Task SubjectRating_WinsOverSnapshotAndLegacy()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                "1234567", "tt1234567", 8.7, 1000,
                _ => new DoubanSnapshotView { Rating = 9.9, Votes = 1, CheckedAt = Now.AddDays(-1) },
                MustNotFetch, Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.Ok, resolution.Status);
            Assert.Equal(DoubanRatingSource.Subject, resolution.Source);
            Assert.Equal(8.7, resolution.Rating);
            Assert.Equal(1000, resolution.Votes);
            Assert.Equal("1234567", resolution.SnapshotKey);
        }

        [Fact]
        public async Task MissingSubjectRating_UsesFreshSnapshot()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                "1234567", "tt1234567", null, null,
                _ => new DoubanSnapshotView { Rating = 9.1, Votes = 42, CheckedAt = Now.AddDays(-1) },
                MustNotFetch, Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.Ok, resolution.Status);
            Assert.Equal(DoubanRatingSource.Snapshot, resolution.Source);
            Assert.Equal(9.1, resolution.Rating);
            Assert.Equal(42, resolution.Votes);
            Assert.Equal("imdb:tt1234567", resolution.SnapshotKey);
        }

        [Fact]
        public async Task ExpiredSnapshot_FetchesLegacy()
        {
            var called = false;

            var resolution = await DoubanRatingResolver.ResolveAsync(
                null, "tt1234567", null, null,
                _ => new DoubanSnapshotView { Rating = 9.1, Votes = 42, CheckedAt = Now.AddDays(-8) },
                Fetch(Legacy(8.4, 50), _ => called = true), Window, Now, CancellationToken.None);

            Assert.True(called);
            Assert.Equal(DoubanRatingClient.DoubanLightStatus.Ok, resolution.Status);
            Assert.Equal(DoubanRatingSource.LegacyImdb, resolution.Source);
            Assert.Equal(8.4, resolution.Rating);
            Assert.Equal(50, resolution.Votes);
            Assert.Equal("imdb:tt1234567", resolution.SnapshotKey);
        }

        [Fact]
        public async Task SnapshotWithoutCheckedAt_IsTreatedAsExpired()
        {
            var called = false;

            var resolution = await DoubanRatingResolver.ResolveAsync(
                null, "tt1234567", null, null,
                _ => new DoubanSnapshotView { Rating = 9.1, Votes = 42 },
                Fetch(Legacy(8.4, 50), _ => called = true), Window, Now, CancellationToken.None);

            Assert.True(called);
            Assert.Equal(DoubanRatingSource.LegacyImdb, resolution.Source);
        }

        [Fact]
        public async Task LegacySuccess_WithDoubanId_RecordsUnderSubject()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                "1234567", "tt1234567", null, null,
                _ => null, Fetch(Legacy(8.4, 50)), Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.Ok, resolution.Status);
            Assert.Equal(DoubanRatingSource.LegacyImdb, resolution.Source);
            Assert.Equal("1234567", resolution.SnapshotKey);
        }

        [Fact]
        public async Task LegacySuccess_WithoutDoubanId_RecordsUnderImdbKey()
        {
            string seenImdbId = null;

            var resolution = await DoubanRatingResolver.ResolveAsync(
                null, "TT1234567", null, null,
                _ => null, Fetch(Legacy(8.4, 50), id => seenImdbId = id), Window, Now,
                CancellationToken.None);

            Assert.Equal("TT1234567", seenImdbId);
            Assert.Equal("imdb:tt1234567", resolution.SnapshotKey);
        }

        [Fact]
        public async Task LegacyFailure_ReturnsFailed()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                "1234567", "tt1234567", null, null,
                _ => null, Fetch(Legacy(0, 0, DoubanRatingClient.DoubanLightStatus.Failed)),
                Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.Failed, resolution.Status);
            Assert.Equal(DoubanRatingSource.None, resolution.Source);
        }

        [Fact]
        public async Task LegacyNoRating_ReturnsNoRating()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                "1234567", "tt1234567", null, null,
                _ => null, Fetch(Legacy(0, 0, DoubanRatingClient.DoubanLightStatus.NoRating)),
                Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.NoRating, resolution.Status);
            Assert.Equal(DoubanRatingSource.None, resolution.Source);
        }

        [Fact]
        public async Task MissingBothIds_ReturnsNoId_WithoutFetch()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                null, null, null, null,
                _ => null, MustNotFetch, Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.NoId, resolution.Status);
            Assert.Equal(DoubanRatingSource.None, resolution.Source);
        }

        [Fact]
        public async Task DoubanIdWithoutImdb_ReturnsNoRating_WithoutFetch()
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                "1234567", null, null, null,
                _ => null, MustNotFetch, Window, Now, CancellationToken.None);

            Assert.Equal(DoubanRatingClient.DoubanLightStatus.NoRating, resolution.Status);
            Assert.Equal(DoubanRatingSource.None, resolution.Source);
        }
    }
}
