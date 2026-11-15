using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Unified IntroDb API：IntroDb（api.introdb.app，IMDb 定位，片尾字段名 outro）
    /// 与 Tidb（api.theintrodb.org/v4，TVDB/TMDB 定位，片尾字段名 credits）。
    /// 决策策略为“完整源优先”：按顺序查询，第一个同时提供片头与片尾的源整条采用并停止；
    /// 只有所有源都不完整时才做字段级拼接（并记录混源信息）。
    /// 数据写入为带 #DB 后缀的章节标记，仅填充缺失标记或更新自身 #DB 标记。
    /// </summary>
    public class IntroDbApi
    {
        private const string IntroDbAppBaseUrl = "https://api.introdb.app";

        /// <summary>TheIntroDB 自 v4 起为唯一维护面，v2/v3 已冻结并会被移除。</summary>
        internal const string TheIntroDbBaseUrl = "https://api.theintrodb.org/v4";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

        // v4 限流：/media 匿名 30 次/10 秒、认证 120 次/10 秒；introdb.app 未公布限额。
        // 取保守间隔：匿名 400ms（25 次/10s）、认证 200ms（50 次/10s），两源共用一条节流时间线。
        private static readonly TimeSpan IntroDbAppRequestInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan TheIntroDbRequestIntervalAnonymous = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan TheIntroDbRequestIntervalWithKey = TimeSpan.FromMilliseconds(200);

        /// <summary>命中 429 后的冷却窗口，避免在限流期间继续打点。</summary>
        private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(10);

        /// <summary>某源连续失败（超时/429/5xx）达到该次数后，本轮任务剩余部分不再请求它。</summary>
        private const int SourceFailureThreshold = 5;

        /// <summary>混源拼接时两源同字段起点差异超过该值（毫秒）才升级为 Warn。</summary>
        internal const long MixedSourceWarnThresholdMs = 30_000;

        public class IntroDbSegment
        {
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
        }

        /// <summary>统一后的查询结果：片头 + 片尾。</summary>
        public class IntroDbMedia
        {
            public List<IntroDbSegment> intro { get; set; }
            public List<IntroDbSegment> credits { get; set; }
        }

        /// <summary>单个源的查询结果。</summary>
        internal sealed class SourceResult
        {
            public string Provider { get; set; }
            public List<IntroDbSegment> Intro { get; set; }
            public List<IntroDbSegment> Credits { get; set; }

            public bool HasIntro => Intro != null && Intro.Count > 0;
            public bool HasCredits => Credits != null && Credits.Count > 0;
            public bool HasAny => HasIntro || HasCredits;
            public bool IsComplete => HasIntro && HasCredits;
        }

        /// <summary>introdb.app /segments：intro/outro 是单对象（不是数组）。</summary>
        internal sealed class IntroDbAppResponse
        {
            public IntroDbAppSegment intro { get; set; }
            public IntroDbAppSegment outro { get; set; }
        }

        internal sealed class IntroDbAppSegment
        {
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
        }

        /// <summary>TheIntroDB v4 /media：intro/credits 为数组。</summary>
        internal sealed class TheIntroDbResponse
        {
            public List<IntroDbSegment> intro { get; set; }
            public List<IntroDbSegment> credits { get; set; }
        }

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private List<string> _scrapeLibraryPaths = new List<string>();

        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private readonly ConcurrentDictionary<string, (DateTime Expiry, SourceResult Data)> _cache =
            new ConcurrentDictionary<string, (DateTime, SourceResult)>();

        private readonly ConcurrentDictionary<long, byte> _scrapingItems =
            new ConcurrentDictionary<long, byte>();

        // 本次任务周期的请求结果统计：源站覆盖不全/网络抖动时逐集打日志会刷屏，
        // 逐集降为 Debug，任务收尾聚合一条（LogRequestSummary）。
        private int _noDataCount;
        private int _mixedCount;
        private readonly ConcurrentDictionary<string, int> _requestFailures =
            new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, int> _requestCounts =
            new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures =
            new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, byte> _disabledSources =
            new ConcurrentDictionary<string, byte>();
        private bool _firstFailureLogged;

        private DateTime _lastRequestTime = DateTime.MinValue;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private int _rateLimitLogged;

        // 节流需跨请求串行，否则并发刮削时时间线互相覆盖，实际速率仍会超限。
        private readonly SemaphoreSlim _throttleLock = new SemaphoreSlim(1, 1);

        public IntroDbApi(IHttpClient httpClient, IJsonSerializer jsonSerializer, ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _libraryManager = libraryManager;

            UpdateLibraryPathsInScope();
        }

        public IReadOnlyList<string> ScrapeLibraryPaths => _scrapeLibraryPaths;

        /// <summary>
        /// 刷新刮削范围的媒体库路径。空范围 = 全部剧集/混合库；电影库不参与范围。
        /// </summary>
        public void UpdateLibraryPathsInScope()
        {
            if (_libraryManager == null) return;

            var scope = Plugin.Instance?.IntroSkipStore?.GetOptions()?.Scrape?.LibraryScope;
            var validLibraryIds = Utility.GetValidLibraryIds(scope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.Contains(f.Id)))
                .ToList();

            _scrapeLibraryPaths = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item?.Path) &&
                   _scrapeLibraryPaths.Any(p => item.Path.StartsWith(p, PathComparison));
        }

        /// <summary>每轮任务开始时复位统计与降级状态。</summary>
        public void BeginRun()
        {
            _requestFailures.Clear();
            _requestCounts.Clear();
            _consecutiveFailures.Clear();
            _disabledSources.Clear();
            Interlocked.Exchange(ref _noDataCount, 0);
            Interlocked.Exchange(ref _mixedCount, 0);
            Interlocked.Exchange(ref _rateLimitLogged, 0);
            _firstFailureLogged = false;
        }

        /// <summary>
        /// 查询单个媒体的片头片尾数据。
        /// 决策：按顺序查询，遇到“片头+片尾都齐”的源即整条采用并停止；都不完整才字段拼接。
        /// 剧集顺序取配置（默认 IntroDb 先，省配额）；电影强制 TheIntroDB 先（有片长版本匹配）。
        /// 无数据返回 null。
        /// </summary>
        public async Task<IntroDbMedia> GetMediaInfoAsync(string tmdbId, int? season, int? episode,
            CancellationToken cancellationToken, string imdbId = null, long? durationMs = null,
            string tvdbId = null, string tvdbEpisodeId = null)
        {
            var providers = OrderProviders(
                ParseUnifiedProviders(Plugin.Instance.IntroSkipStore.GetOptions().Scrape.UnifiedIntroDbProviders),
                !season.HasValue);

            var results = new List<SourceResult>();

            foreach (var provider in providers)
            {
                if (_disabledSources.ContainsKey(provider))
                {
                    _logger.Debug("IntroDbScrape - {0} skipped (disabled for this run after repeated failures)",
                        provider);
                    continue;
                }

                var data = await QueryProviderAsync(provider, tmdbId, tvdbId, tvdbEpisodeId, imdbId, durationMs,
                    season, episode, cancellationToken).ConfigureAwait(false);

                if (data == null) continue;

                results.Add(data);

                if (data.IsComplete) break;
            }

            var merged = ResolveResults(results, out var introSource, out var creditsSource);

            LogMergeDecision(results, merged, introSource, creditsSource, tmdbId, season, episode);

            return merged;
        }

        /// <summary>
        /// 完整源优先：任一源同时提供片头与片尾时整条采用（按传入顺序取第一个），
        /// 否则按字段拼接（片头取第一个有片头的源，片尾同理）。
        /// </summary>
        internal static IntroDbMedia ResolveResults(IReadOnlyList<SourceResult> results,
            out string introSource, out string creditsSource)
        {
            introSource = null;
            creditsSource = null;

            if (results == null || results.Count == 0) return null;

            var complete = results.FirstOrDefault(r => r.IsComplete);
            if (complete != null)
            {
                introSource = complete.Provider;
                creditsSource = complete.Provider;

                return new IntroDbMedia
                {
                    intro = complete.Intro,
                    credits = complete.Credits
                };
            }

            var merged = new IntroDbMedia();

            foreach (var result in results)
            {
                if (merged.intro == null && result.HasIntro)
                {
                    merged.intro = result.Intro;
                    introSource = result.Provider;
                }

                if (merged.credits == null && result.HasCredits)
                {
                    merged.credits = result.Credits;
                    creditsSource = result.Provider;
                }
            }

            return merged.intro == null && merged.credits == null ? null : merged;
        }

        /// <summary>
        /// 解析配置的源顺序。空配置才用默认值；不再偷偷补上未配置的源（允许真正的单源）。
        /// </summary>
        internal static List<string> ParseUnifiedProviders(string raw)
        {
            var list = (raw ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Equals("IntroDb", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals("Tidb", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Equals("Tidb", StringComparison.OrdinalIgnoreCase) ? "Tidb" : "IntroDb")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
            {
                list.Add("IntroDb");
                list.Add("Tidb");
            }

            return list;
        }

        /// <summary>
        /// 电影把 TheIntroDB 提到最前：它支持 duration_ms 版本匹配，混源错位风险也集中在电影。
        /// 剧集保持配置顺序（默认 IntroDb 先，额度更宽）。
        /// </summary>
        internal static List<string> OrderProviders(List<string> providers, bool isMovie)
        {
            var list = providers?.ToList() ?? new List<string>();

            if (!isMovie || list.Count < 2) return list;

            var index = list.IndexOf("Tidb");
            if (index <= 0) return list;

            list.RemoveAt(index);
            list.Insert(0, "Tidb");

            return list;
        }

        /// <summary>
        /// 构造 TheIntroDB v4 的 /media 请求。
        /// v4 要求必须提供媒体 id（tmdb_id/tvdb_id/imdb_id 三选一）且 TV 必须带 season+episode；
        /// 实测单独传 tvdb_episode_id 会被拒（400），它只能作为附加的“精确定位”提示。
        /// 因此定位顺序为：tmdb_id（常用、快）→ tvdb_id → imdb_id，并在有单集 TVDB id 时附加 tvdb_episode_id。
        /// v4 不再接受 type 参数：无 season/episode 即电影。
        /// </summary>
        internal static string BuildTheIntroDbUrl(string tmdbId, string tvdbId, string tvdbEpisodeId, string imdbId,
            int? season, int? episode, long? durationMs)
        {
            var isEpisode = season.HasValue && episode.HasValue;

            string identity;

            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                identity = "tmdb_id=" + Uri.EscapeDataString(tmdbId.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(tvdbId))
            {
                identity = "tvdb_id=" + Uri.EscapeDataString(tvdbId.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(imdbId))
            {
                identity = "imdb_id=" + Uri.EscapeDataString(imdbId.Trim());
            }
            else
            {
                return null;
            }

            var url = $"{TheIntroDbBaseUrl}/media?{identity}";

            if (isEpisode)
            {
                url += $"&season={season.Value}&episode={episode.Value}";

                if (!string.IsNullOrWhiteSpace(tvdbEpisodeId))
                {
                    url += "&tvdb_episode_id=" + Uri.EscapeDataString(tvdbEpisodeId.Trim());
                }
            }

            if (durationMs.HasValue && durationMs.Value > 0)
            {
                url += $"&duration_ms={durationMs.Value}";
            }

            return url;
        }

        private async Task<SourceResult> QueryProviderAsync(string provider, string tmdbId, string tvdbId,
            string tvdbEpisodeId, string imdbId, long? durationMs, int? season, int? episode,
            CancellationToken cancellationToken)
        {
            string url;
            string apiKey;
            TimeSpan interval;

            if (provider == "IntroDb")
            {
                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    _logger.Debug("IntroDbScrape - IntroDb source skipped (no IMDb id)");
                    return null;
                }

                apiKey = Plugin.Instance.IntroSkipStore.GetOptions().Scrape.IntroDbApiKey;
                url = season.HasValue && episode.HasValue
                    ? $"{IntroDbAppBaseUrl}/segments?imdb_id={Uri.EscapeDataString(imdbId)}&season={season.Value}&episode={episode.Value}"
                    : $"{IntroDbAppBaseUrl}/segments?imdb_id={Uri.EscapeDataString(imdbId)}&is_movie=true";
                interval = IntroDbAppRequestInterval;
            }
            else
            {
                apiKey = Plugin.Instance.IntroSkipStore.GetOptions().Scrape.TidbApiKey;
                url = BuildTheIntroDbUrl(tmdbId, tvdbId, tvdbEpisodeId, imdbId, season, episode, durationMs);

                if (url == null) return null;

                interval = string.IsNullOrWhiteSpace(apiKey)
                    ? TheIntroDbRequestIntervalAnonymous
                    : TheIntroDbRequestIntervalWithKey;
            }

            if (_cache.TryGetValue(url, out var cached) && cached.Expiry > DateTime.UtcNow)
            {
                return cached.Data;
            }

            await ThrottleAsync(interval).ConfigureAwait(false);

            _requestCounts.AddOrUpdate(provider, 1, (_, c) => c + 1);

            var requestOptions = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                requestOptions.RequestHeaders["Authorization"] = "Bearer " + apiKey.Trim();
            }

            try
            {
                using var response = await _httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    HandleNoData(provider, url);
                    return null;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    HandleRateLimit(provider, url);
                    return null;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    RecordSourceFailure(provider);
                    RecordRequestFailure("status " + (int)response.StatusCode);
                    _logger.Debug("IntroDbScrape - {0} unexpected status {1} for {2}", provider,
                        (int)response.StatusCode, url);
                    return null;
                }

                await using var contentStream = response.Content;
                var result = ParseResponse(provider, contentStream);

                if (result == null || !result.HasAny)
                {
                    HandleNoData(provider, url);
                    return null;
                }

                _cache[url] = (DateTime.UtcNow + CacheDuration, result);
                ResetSourceFailures(provider);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Emby 的 HttpClient 对非 2xx 是抛 HttpException，不是返回状态码。
                // 404 表示该媒体/该集没有提交，属正常“无数据”，不能算请求失败。
                HandleNoData(provider, url);
                return null;
            }
            catch (HttpException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
            {
                HandleRateLimit(provider, url);
                return null;
            }
            catch (HttpException e) when (e.StatusCode.HasValue)
            {
                RecordSourceFailure(provider);
                RecordRequestFailure("status " + (int)e.StatusCode.Value);
                _logger.Debug("IntroDbScrape - {0} unexpected status {1} for {2}", provider,
                    (int)e.StatusCode.Value, url);
                return null;
            }
            catch (Exception e)
            {
                // 超时/连接失败等网络异常：首条保留 Warn 提示用户，后续逐集降为 Debug，
                // 任务收尾时由 LogRequestSummary 聚合输出，避免整季刷屏。
                var reason = ClassifyRequestFailure(e);
                RecordSourceFailure(provider);
                RecordRequestFailure(reason);

                if (!_firstFailureLogged)
                {
                    _firstFailureLogged = true;
                    _logger.Warn("IntroDbScrape - {0} request failed for {1}: {2} (further failures will be aggregated)",
                        provider, url, e.Message);
                }
                else
                {
                    _logger.Debug("IntroDbScrape - {0} request failed ({1}) for {2}", provider, reason, url);
                }

                return null;
            }
        }

        /// <summary>两个源的响应结构不同，分别反序列化后再归一。</summary>
        private SourceResult ParseResponse(string provider, Stream content)
        {
            if (provider == "IntroDb")
            {
                var dto = _jsonSerializer.DeserializeFromStream<IntroDbAppResponse>(content);

                return new SourceResult
                {
                    Provider = provider,
                    Intro = ToSegmentList(dto?.intro),
                    Credits = ToSegmentList(dto?.outro)
                };
            }

            var tidb = _jsonSerializer.DeserializeFromStream<TheIntroDbResponse>(content);

            return new SourceResult
            {
                Provider = provider,
                Intro = tidb?.intro,
                Credits = tidb?.credits
            };
        }

        private static List<IntroDbSegment> ToSegmentList(IntroDbAppSegment segment)
        {
            if (segment == null || (!segment.start_ms.HasValue && !segment.end_ms.HasValue)) return null;

            return new List<IntroDbSegment>
            {
                new IntroDbSegment { start_ms = segment.start_ms, end_ms = segment.end_ms }
            };
        }

        /// <summary>该媒体/该集在源站没有提交：按无数据处理并做负缓存，避免短期内重复请求。</summary>
        private void HandleNoData(string provider, string url)
        {
            _cache[url] = (DateTime.UtcNow + CacheDuration, null);
            Interlocked.Increment(ref _noDataCount);
            ResetSourceFailures(provider);
            _logger.Debug("IntroDbScrape - {0} has no data for {1}", provider, url);
        }

        /// <summary>命中 429：进入冷却窗口，首条 Warn，其余聚合到任务汇总。</summary>
        private void HandleRateLimit(string provider, string url)
        {
            _cooldownUntil = DateTime.UtcNow + RateLimitCooldown;
            RecordSourceFailure(provider);
            RecordRequestFailure("429");

            if (Interlocked.Exchange(ref _rateLimitLogged, 1) == 0)
            {
                _logger.Warn(
                    "IntroDbScrape - {0} rate limit reached (429), backing off {1}s; further 429s will be aggregated",
                    provider, (int)RateLimitCooldown.TotalSeconds);
            }
            else
            {
                _logger.Debug("IntroDbScrape - {0} rate limit (429) for {1}", provider, url);
            }
        }

        /// <summary>连续失败达到阈值后禁用该源，避免对着不可用/超额度的源空转整轮任务。</summary>
        private void RecordSourceFailure(string provider)
        {
            var count = _consecutiveFailures.AddOrUpdate(provider, 1, (_, c) => c + 1);

            if (count < SourceFailureThreshold) return;
            if (!_disabledSources.TryAdd(provider, 0)) return;

            _logger.Warn(
                "IntroDbScrape - {0} disabled for the rest of this run after {1} consecutive failures",
                provider, count);
        }

        private void ResetSourceFailures(string provider)
        {
            _consecutiveFailures.TryRemove(provider, out _);
        }

        private static string ClassifyRequestFailure(Exception e)
        {
            if (e is HttpException httpException && httpException.IsTimedOut) return "timeout";

            var msg = e.Message ?? string.Empty;
            if (msg.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0) return "timeout";
            if (msg.IndexOf("Cancelling connection", StringComparison.OrdinalIgnoreCase) >= 0) return "cancelled";
            if (msg.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("Connection refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("Connection reset", StringComparison.OrdinalIgnoreCase) >= 0) return "unreachable";
            return "other";
        }

        private void RecordRequestFailure(string reason)
        {
            _requestFailures.AddOrUpdate(reason, 1, (_, c) => c + 1);
        }

        /// <summary>混源拼接时记录来源；两源同字段偏差过大时升级为 Warn（通常是版本不一致）。</summary>
        private void LogMergeDecision(List<SourceResult> results, IntroDbMedia merged, string introSource,
            string creditsSource, string tmdbId, int? season, int? episode)
        {
            if (merged == null) return;
            if (introSource == null || creditsSource == null || introSource == creditsSource) return;

            Interlocked.Increment(ref _mixedCount);

            var delta = MaxStartDeltaMs(results);
            var label = season.HasValue ? $"S{season}E{episode}" : "movie";
            var target = $"tmdb={tmdbId} {label}";

            if (delta > MixedSourceWarnThresholdMs)
            {
                _logger.Warn(
                    "IntroDbScrape - Mixed sources for {0}: intro={1}, credits={2}, max start delta={3}s (possible version mismatch)",
                    target, introSource, creditsSource, delta / 1000);
            }
            else
            {
                _logger.Debug("IntroDbScrape - Mixed sources for {0}: intro={1}, credits={2}, delta={3}s",
                    target, introSource, creditsSource, delta / 1000);
            }
        }

        /// <summary>同一字段两源都提供时，取各自最长段的起点差值（毫秒）。</summary>
        internal static long MaxStartDeltaMs(IReadOnlyList<SourceResult> results)
        {
            if (results == null) return 0;

            return Math.Max(FieldDelta(results, true), FieldDelta(results, false));
        }

        private static long FieldDelta(IReadOnlyList<SourceResult> results, bool intro)
        {
            var starts = results
                .Where(r => r != null)
                .Select(r => intro ? LongestStart(r.Intro) : LongestStart(r.Credits))
                .Where(s => s.HasValue)
                .Select(s => s.Value)
                .ToList();

            return starts.Count < 2 ? 0 : starts.Max() - starts.Min();
        }

        private static long? LongestStart(List<IntroDbSegment> segments)
        {
            if (segments == null || segments.Count == 0) return null;

            var longest = segments
                .Where(s => s.start_ms.HasValue || s.end_ms.HasValue)
                .OrderByDescending(s => (s.end_ms ?? 0) - (s.start_ms ?? 0))
                .FirstOrDefault();

            return longest == null ? (long?)null : longest.start_ms ?? 0;
        }

        /// <summary>任务收尾时输出本次刮削的请求结果汇总。</summary>
        public void LogRequestSummary()
        {
            var noData = Interlocked.Exchange(ref _noDataCount, 0);
            if (noData > 0)
                _logger.Info("IntroDbScrape - Source coverage: no data for {0} episode request(s) (normal for partially covered sources)", noData);

            var mixed = Interlocked.Exchange(ref _mixedCount, 0);
            if (mixed > 0)
                _logger.Info("IntroDbScrape - Mixed-source merges: {0} (no source had both intro and credits)", mixed);

            if (!_requestCounts.IsEmpty)
            {
                var counts = string.Join(", ", _requestCounts.Select(kvp => string.Format("{0}={1}", kvp.Key, kvp.Value)));
                _logger.Info("IntroDbScrape - Requests: {0}", counts);
                _requestCounts.Clear();
            }

            if (_requestFailures.IsEmpty) return;

            var detail = string.Join(", ", _requestFailures.Select(kvp => string.Format("{0}={1}", kvp.Key, kvp.Value)));
            _logger.Info("IntroDbScrape - Request failures: {0}", detail);
            _requestFailures.Clear();
            _firstFailureLogged = false;
            Interlocked.Exchange(ref _rateLimitLogged, 0);
        }

        /// <summary>
        /// 刮削一部剧集的全部缺失片头片尾标记。返回写入条数。
        /// </summary>
        public async Task<int> ScrapeSeriesAsync(Series series, CancellationToken cancellationToken)
        {
            if (!_scrapingItems.TryAdd(series.InternalId, 0)) return 0;

            try
            {
                var tmdbId = series.GetProviderId(MetadataProviders.Tmdb);
                var tvdbId = series.GetProviderId(MetadataProviders.Tvdb);
                var imdbId = series.GetProviderId(MetadataProviders.Imdb);

                // 三种 id 都没有才放弃；只有 TMDB 不再作为硬门槛。
                if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(imdbId))
                {
                    _logger.Debug("IntroDbScrape - Series skipped (no TMDB/TVDB/IMDb id): {0}", series.Name);
                    return 0;
                }

                var episodes = series
                    .GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        Recursive = true,
                        IncludeItemTypes = new[] { nameof(Episode) }
                    })
                    .OfType<Episode>()
                    .Where(e => e.IndexNumber.HasValue)
                    .OrderBy(e => e.ParentIndexNumber ?? 0)
                    .ThenBy(e => e.IndexNumber ?? 0)
                    .ToList();

                var marked = 0;
                var consecutiveNotFound = 0;

                foreach (var episode in episodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 仅有 IntroDb 自身 #DB 标记的集允许重新刮削刷新；探测等外部来源不覆盖
                    if (Plugin.ChapterApi.HasNonDbIntro(episode) && Plugin.ChapterApi.HasCredits(episode)) continue;

                    if (consecutiveNotFound >= 50)
                    {
                        _logger.Warn("IntroDbScrape - Aborted after 50 consecutive items without data for {0}",
                            series.Name);
                        break;
                    }

                    var data = await GetMediaInfoAsync(tmdbId, episode.ParentIndexNumber, episode.IndexNumber,
                            cancellationToken,
                            imdbId: imdbId,
                            durationMs: episode.RunTimeTicks > 0 ? episode.RunTimeTicks / 10000 : null,
                            tvdbId: tvdbId,
                            tvdbEpisodeId: episode.GetProviderId(MetadataProviders.Tvdb))
                        .ConfigureAwait(false);

                    if (data == null)
                    {
                        consecutiveNotFound++;
                        continue;
                    }

                    consecutiveNotFound = 0;

                    if (Plugin.ChapterApi.UpdateIntroDbMarkers(episode, data.intro, data.credits))
                    {
                        marked++;
                    }
                }

                if (marked > 0)
                {
                    _logger.Info("IntroDbScrape - Marked {0} episode(s) for {1}", marked, series.Name);
                }
                else
                {
                    // 一行聚合：源站无数据时不再逐集刷 Warn，改为剧集级汇总
                    _logger.Info("IntroDbScrape - No markers for {0} ({1} episode(s) checked)",
                        series.Name, episodes.Count);
                }

                return marked;
            }
            finally
            {
                _scrapingItems.TryRemove(series.InternalId, out _);
            }
        }

        /// <summary>
        /// 刮削一部电影的缺失片头片尾标记。返回是否写入。
        /// </summary>
        public async Task<bool> ScrapeMovieAsync(Movie movie, CancellationToken cancellationToken)
        {
            if (!_scrapingItems.TryAdd(movie.InternalId, 0)) return false;

            try
            {
                var tmdbId = movie.GetProviderId(MetadataProviders.Tmdb);
                var tvdbId = movie.GetProviderId(MetadataProviders.Tvdb);
                var imdbId = movie.GetProviderId(MetadataProviders.Imdb);

                if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(imdbId))
                {
                    _logger.Debug("IntroDbScrape - Movie skipped (no TMDB/TVDB/IMDb id): {0}", movie.Name);
                    return false;
                }

                var data = await GetMediaInfoAsync(tmdbId, null, null, cancellationToken,
                        imdbId: imdbId,
                        durationMs: movie.RunTimeTicks > 0 ? movie.RunTimeTicks / 10000 : null,
                        tvdbId: tvdbId)
                    .ConfigureAwait(false);

                if (data == null) return false;

                return Plugin.ChapterApi.UpdateIntroDbMarkers(movie, data.intro, data.credits);
            }
            finally
            {
                _scrapingItems.TryRemove(movie.InternalId, out _);
            }
        }

        /// <summary>
        /// 全局请求节流：两源共用一条时间线，取当前请求所需的最小间隔。
        /// 命中 429 后进入冷却窗口，冷却期内的请求统一顺延。
        /// </summary>
        private async Task ThrottleAsync(TimeSpan interval)
        {
            await _throttleLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var target = _lastRequestTime + interval;
                if (_cooldownUntil > target) target = _cooldownUntil;

                var wait = target - DateTime.UtcNow;

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait).ConfigureAwait(false);
                }

                _lastRequestTime = DateTime.UtcNow;
            }
            finally
            {
                _throttleLock.Release();
            }
        }
    }
}
