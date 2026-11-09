using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Provider.Douban
{
    /// <summary>
    /// 轻量豆瓣评分客户端：只按 IMDb ID 查评分，不抓 subject 页面、不写缓存文件。
    /// 接口（参考 TMDbHelper 的 DoubanAPI，2026-11-09 实测可用）：
    ///   POST https://api.douban.com/v2/movie/imdb/{id}?apikey=...（豆瓣公开 web apikey）
    /// 返回 rating.average / numRaters，只取评分与票数。
    /// 这是第三方接口，key 可能失效；失效时统一评分记 Failed/NoRating，
    /// 不回退网页刮削，避免把风控压力拉回来。豆瓣辅助的完整刮削链路不受影响。
    /// </summary>
    public class DoubanRatingClient
    {
        private const string LegacyUrlTemplate = "https://api.douban.com/v2/movie/imdb/{0}";
        private const string LegacyApiKey = "0ab215a8b1977939201640fa14c66bab";
        private const string CookieBid = "bid=J9zb1zA5sJc";
        private const int RequestIntervalMs = 1500;
        private const int JitterMaxMs = 1000;
        private const int BreakerThreshold = 5;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan MissCacheDuration = TimeSpan.FromHours(1);
        private static readonly TimeSpan BreakerCooldown = TimeSpan.FromMinutes(30);

        private static readonly string[] UserAgents =
        {
            "api-client/1 com.douban.frodo/7.22.0.beta9(231) Android/23 product/Mate 40 vendor/HUAWEI model/Mate 40 brand/HUAWEI  rom/android  network/wifi  platform/AndroidPad",
            "api-client/1 com.douban.frodo/7.18.0(230) Android/22 product/MI 9 vendor/Xiaomi model/MI 9 brand/Android  rom/miui6  network/wifi  platform/mobile nd/1",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        };

        private static readonly SemaphoreSlim ThrottleGate = new SemaphoreSlim(1, 1);
        private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
        private static int _consecutiveFailures;
        private static DateTimeOffset _breakerUntil = DateTimeOffset.MinValue;
        private static readonly ConcurrentDictionary<string, (double Rating, int Votes, DateTimeOffset Expiry)> Cache =
            new ConcurrentDictionary<string, (double, int, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);

        private static ILogger Logger => Plugin.Instance?.Logger;

        private sealed class LegacyResponse
        {
            public RatingDto rating { get; set; }
        }

        private sealed class RatingDto
        {
            // 豆瓣返回的 average 是字符串（如 "9.7"），必须按 string 接收再手动转换。
            public string average { get; set; }
            public int? numRaters { get; set; }
        }

        public enum DoubanLightStatus
        {
            Ok,
            NoId,
            NoRating,
            Failed
        }

        public sealed class DoubanLightRating
        {
            public DoubanLightStatus Status { get; set; }
            public double Rating { get; set; }
            public int Votes { get; set; }
        }

        /// <summary>按 IMDb ID 查豆瓣评分。Ok / NoId / NoRating / Failed。</summary>
        public async Task<DoubanLightRating> GetRatingAsync(string imdbId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imdbId) ||
                !imdbId.Trim().StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return new DoubanLightRating { Status = DoubanLightStatus.NoId };
            }

            var key = imdbId.Trim();
            if (Cache.TryGetValue(key, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
            {
                return new DoubanLightRating
                {
                    Status = DoubanLightStatus.Ok,
                    Rating = cached.Rating,
                    Votes = cached.Votes
                };
            }

            if (_breakerUntil > DateTimeOffset.UtcNow)
            {
                Logger?.Warn("DoubanLight - breaker engaged until {0}", _breakerUntil);
                return new DoubanLightRating { Status = DoubanLightStatus.Failed };
            }

            var result = await FetchLegacy(key, cancellationToken).ConfigureAwait(false);

            if (result.Status == DoubanLightStatus.Ok)
            {
                Cache[key] = (result.Rating, result.Votes, DateTimeOffset.UtcNow.Add(CacheDuration));
            }
            else if (result.Status == DoubanLightStatus.NoRating)
            {
                Cache[key] = (0, 0, DateTimeOffset.UtcNow.Add(MissCacheDuration));
            }

            return result;
        }

        private async Task<DoubanLightRating> FetchLegacy(string imdbId, CancellationToken cancellationToken)
        {
            await ThrottleAsync(cancellationToken).ConfigureAwait(false);

            var url = string.Format(CultureInfo.InvariantCulture, LegacyUrlTemplate,
                Uri.EscapeDataString(imdbId.Trim()));
            var requestOptions = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                RequestContentType = "application/x-www-form-urlencoded",
                RequestContent = ("apikey=" + LegacyApiKey).AsMemory(),
                BufferContent = true,
                ThrowOnErrorResponse = false,
                UserAgent = PickUserAgent()
            };
            requestOptions.RequestHeaders["Cookie"] = CookieBid;

            using var response = await Plugin.HttpClient.SendAsync(requestOptions, "POST").ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Logger?.Debug("DoubanLight - 404 for {0}", imdbId);
                return new DoubanLightRating { Status = DoubanLightStatus.NoRating };
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                RegisterFailure($"HTTP {(int)response.StatusCode}");
                return new DoubanLightRating { Status = DoubanLightStatus.Failed };
            }

            await using var stream = response.Content;
            string body;
            using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8, false, 4096, true))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var payload = Plugin.JsonSerializer.DeserializeFromString<LegacyResponse>(body);
            return ToResult(payload?.rating, "legacy", imdbId);
        }

        private static DoubanLightRating ToResult(RatingDto rating, string source, string imdbId)
        {
            if (rating == null || string.IsNullOrWhiteSpace(rating.average) ||
                !double.TryParse(rating.average, NumberStyles.Any, CultureInfo.InvariantCulture, out var avg) ||
                avg <= 0)
            {
                Logger?.Debug("DoubanLight - no rating for {0} ({1})", imdbId, source);
                return new DoubanLightRating { Status = DoubanLightStatus.NoRating };
            }

            ResetFailures();
            return new DoubanLightRating
            {
                Status = DoubanLightStatus.Ok,
                Rating = Math.Round(avg, 1),
                Votes = rating.numRaters ?? 0
            };
        }

        private static void RegisterFailure(string detail)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            Logger?.Debug("DoubanLight - failure {0}: {1}", failures, detail);

            if (failures >= BreakerThreshold)
            {
                _breakerUntil = DateTimeOffset.UtcNow.Add(BreakerCooldown);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                Logger?.Warn("DoubanLight - breaker engaged for {0} minutes after {1} failures",
                    BreakerCooldown.TotalMinutes, BreakerThreshold);
            }
        }

        private static void ResetFailures()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        private static string PickUserAgent()
        {
            return UserAgents[Random.Shared.Next(0, UserAgents.Length)];
        }

        private static async Task ThrottleAsync(CancellationToken cancellationToken)
        {
            await ThrottleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var wait = TimeSpan.FromMilliseconds(RequestIntervalMs) - (DateTimeOffset.UtcNow - _lastRequest);
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                wait += TimeSpan.FromMilliseconds(Random.Shared.Next(0, JitterMaxMs));

                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                _lastRequest = DateTimeOffset.UtcNow;
            }
            finally
            {
                ThrottleGate.Release();
            }
        }
    }
}
