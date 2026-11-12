using System;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Provider.Douban
{
    public enum DoubanRatingSource
    {
        None,
        Subject,
        Snapshot,
        LegacyImdb
    }

    /// <summary>评分快照视图：由调用方从持久化快照中读出，解析器本身不访问文件。</summary>
    public sealed class DoubanSnapshotView
    {
        public double Rating { get; set; }
        public long? Votes { get; set; }
        public DateTime? CheckedAt { get; set; }
    }

    public sealed class DoubanRatingResolution
    {
        public DoubanRatingClient.DoubanLightStatus Status { get; set; }
        public double Rating { get; set; }
        public long? Votes { get; set; }
        public DoubanRatingSource Source { get; set; }

        /// <summary>legacy 兜底分应记入的快照键：有豆瓣 ID 记豆瓣 ID，否则记 IMDb 键。</summary>
        public string SnapshotKey { get; set; }
    }

    public static class DoubanRatingResolver
    {
        public static async Task<DoubanRatingResolution> ResolveAsync(
            string doubanId,
            string imdbId,
            double? subjectRating,
            long? subjectVotes,
            Func<string, DoubanSnapshotView> tryGetSnapshot,
            Func<string, CancellationToken, Task<DoubanRatingClient.DoubanLightRating>> fetchLegacy,
            TimeSpan snapshotWindow,
            DateTime utcNow,
            CancellationToken cancellationToken)
        {
            if (subjectRating is > 0)
            {
                return new DoubanRatingResolution
                {
                    Status = DoubanRatingClient.DoubanLightStatus.Ok,
                    Rating = subjectRating.Value,
                    Votes = subjectVotes,
                    Source = DoubanRatingSource.Subject,
                    SnapshotKey = doubanId
                };
            }

            var imdbKey = GetImdbSnapshotKey(imdbId);

            if (imdbKey == null)
            {
                return new DoubanRatingResolution
                {
                    Status = string.IsNullOrEmpty(doubanId)
                        ? DoubanRatingClient.DoubanLightStatus.NoId
                        : DoubanRatingClient.DoubanLightStatus.NoRating,
                    Source = DoubanRatingSource.None
                };
            }

            var snapshot = tryGetSnapshot(imdbKey);

            if (snapshot != null && snapshot.Rating > 0 && snapshot.CheckedAt.HasValue &&
                utcNow - snapshot.CheckedAt.Value < snapshotWindow)
            {
                return new DoubanRatingResolution
                {
                    Status = DoubanRatingClient.DoubanLightStatus.Ok,
                    Rating = snapshot.Rating,
                    Votes = snapshot.Votes,
                    Source = DoubanRatingSource.Snapshot,
                    SnapshotKey = imdbKey
                };
            }

            var legacy = await fetchLegacy(imdbId, cancellationToken).ConfigureAwait(false);

            if (legacy == null || legacy.Status == DoubanRatingClient.DoubanLightStatus.Failed)
            {
                return new DoubanRatingResolution
                {
                    Status = DoubanRatingClient.DoubanLightStatus.Failed,
                    Source = DoubanRatingSource.None
                };
            }

            if (legacy.Status != DoubanRatingClient.DoubanLightStatus.Ok)
            {
                return new DoubanRatingResolution
                {
                    Status = DoubanRatingClient.DoubanLightStatus.NoRating,
                    Source = DoubanRatingSource.None
                };
            }

            return new DoubanRatingResolution
            {
                Status = DoubanRatingClient.DoubanLightStatus.Ok,
                Rating = legacy.Rating,
                Votes = legacy.Votes,
                Source = DoubanRatingSource.LegacyImdb,
                SnapshotKey = string.IsNullOrEmpty(doubanId) ? imdbKey : doubanId
            };
        }

        public static string GetImdbSnapshotKey(string imdbId)
        {
            return string.IsNullOrWhiteSpace(imdbId)
                ? null
                : "imdb:" + imdbId.Trim().ToLowerInvariant();
        }
    }
}
