﻿﻿﻿using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Provider.Douban
{
    /// <summary>
    /// 豆瓣数据层：按 IMDb（Wikidata P4529 解析）或标题匹配豆瓣条目，负责
    /// ① 建立 ProviderIds["Douban"] 关联；② 预取/缓存 subject 数据；③ 按「社区评分来源」
    /// 写入社区评分；④ 按开关写 Kodi 第三评分；⑤ 可选补全简介/演员/海报。
    /// 带 7 天文件缓存、2 秒频控与风控熔断。
    /// </summary>
    public class DoubanAssist
    {
        private const string BaseUrl = "https://movie.douban.com";

        private const string SuggestUrl = BaseUrl + "/j/subject_suggest";
        private const string SparqlUrl = "https://query.wikidata.org/sparql";
        private const string DoubanProviderId = DoubanConstants.ProviderId;

        // 豆瓣开放平台 V2 API：配置 DoubanAccessToken 后优先走官方接口，
        // 可绕过网页端风控，且返回结构化 JSON。未配置 Token 时回退到 movie.douban.com 网页刮削。
        private const string DoubanV2BaseUrl = "https://api.douban.com/v2";
        private const string DoubanV2MoviePath = "/movie/{0}";
        private const string DoubanV2TvPath = "/tv/{0}";

        private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan BreakerCooldown = TimeSpan.FromHours(6);
        private const int BreakerThreshold = 5;

        private class CacheEntry
        {
            public DateTime Expiry { get; set; }
            public object Value { get; set; }
        }

        public class DoubanSuggest
        {
            public string id { get; set; }
            public string title { get; set; }
            public string year { get; set; }
            public string type { get; set; }
            public string img { get; set; }
        }

        public class DoubanCelebrity
        {
            public string ChineseName { get; set; }
            public string ForeignName { get; set; }
            public string Role { get; set; }
        }

        private class SparqlResponse
        {
            public SparqlResults results { get; set; }
        }

        private class SparqlResults
        {
            public SparqlBinding[] bindings { get; set; }
        }

        private class SparqlBinding
        {
            public SparqlValue doubanId { get; set; }
        }

        private class SparqlValue
        {
            public string value { get; set; }
        }

        private static readonly Regex RatingRegex =
            new Regex(@"property=""v:average"">\s*([\d.]+)\s*<", RegexOptions.Compiled);
        private static readonly Regex VotesRegex =
            new Regex(@"property=""v:votes"">\s*(\d+)\s*<", RegexOptions.Compiled);
        private static readonly Regex SummaryRegex =
            new Regex(@"property=""v:summary""[^>]*>([\s\S]*?)</span>", RegexOptions.Compiled);
        private static readonly Regex GenreRegex =
            new Regex(@"property=""v:genre"">([^<]+)<", RegexOptions.Compiled);
        private static readonly Regex InfoRegex =
            new Regex(@"id=""info"">([\s\S]*?)</div>", RegexOptions.Compiled);
        private static readonly Regex CountryRegex =
            new Regex(@"制片国家/地区[^<]*</span>\s*([^<\r\n]+)|制片国家/地区:</span?\s*([^<\r\n]+)", RegexOptions.Compiled);
        private static readonly Regex LanguageRegex =
            new Regex(@"语言[^<]*</span>\s*([^<\r\n]+)|语言:</span?\s*([^<\r\n]+)", RegexOptions.Compiled);
        private static readonly Regex StripTagsRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex CelebrityNameRegex =
            new Regex(@"class=""name""[^>]*>\s*<a href=""[^""]*celebrity/\d+/?""[^>]*>([^<]+)</a>\s*(?:/\s*([^<]*?))?\s*</span>",
                RegexOptions.Compiled);
        private static readonly Regex CelebrityRoleRegex = new Regex(@"饰[:：]?\s*([^<]+)", RegexOptions.Compiled);

        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly DoubanRatingService _ratingService;

        private readonly string _cachePath;
        private readonly string _retryPath;
        private readonly string _seasonsPath;
        private readonly object _cacheLock = new object();
        private readonly object _retryLock = new object();
        private readonly object _seasonsLock = new object();
        private readonly object _requestLock = new object();
        private readonly ConcurrentDictionary<long, byte> _inFlight = new ConcurrentDictionary<long, byte>();

        private Dictionary<string, CacheEntry> _cache;
        private Dictionary<string, RetryEntry> _retry;

        /// <summary>
        /// 季级映射表：剧集豆瓣 ID → { 季号 → 季豆瓣 ID }。
        ///
        /// 豆瓣把每个季拆成独立 subject（如「庆余年 第二季」），剧集条目本身只有一个 ID，
        /// 季条目必须另行解析。若每个季都靠标题搜索，既慢又易错配（不同剧同名季）。
        /// 这里按剧集缓存映射：同一剧集的季条目直接查表，未命中才搜索并回填；
        /// 批量建立时按剧集聚合季号集合，减少重复查询。
        /// </summary>
        private Dictionary<string, Dictionary<string, SeasonMapEntry>> _seasons;

        private DateTime _lastRequestTime = DateTime.MinValue;
        private DateTime _breakerUntil = DateTime.MinValue;
        private int _consecutiveBlocked;

        /// <summary>单条条目最多重试次数，超过后从队列移除，避免永久占用扫库任务。</summary>
        private const int MaxRetryAttempts = 3;

        /// <summary>「扫库后填充豆瓣 ID」的失败重试记录，按条目 ID 索引，落盘跨进程保留。</summary>
        private class RetryEntry
        {
            public int Attempts { get; set; }
            public string LastError { get; set; }
            public DateTime LastAt { get; set; }
        }

        /// <summary>季映射表的一条记录：季豆瓣 ID + 建立时间（用于过期判定）。</summary>
        private class SeasonMapEntry
        {
            public string SubjectId { get; set; }
            public DateTime Updated { get; set; }
        }

        /// <summary>季映射的存活期：与豆瓣条目缓存一致（7 天）。过期后重新解析，避免季被改名/下架后长期错配。</summary>
        private static readonly TimeSpan SeasonMapTtl = TimeSpan.FromDays(7);

        /// <summary>
        /// 单次「扫库后填充豆瓣 ID」的统计。仅在该任务运行期间非空，
        /// 用于任务结束时输出一行分布日志，回答「为什么有些条目没有豆瓣评分」。
        /// </summary>
        private class PopulateStats
        {
            public int Total;
            public int Matched;
            public int NoMatch;
            public int SkippedLocked;
            public int SkippedLanguage;
            public int RatingApplied;
            public int RatingSkippedLookback;
            public int RatingSkippedSource;
        }

        private PopulateStats _populateStats;

        public DoubanAssist(IHttpClient httpClient, IJsonSerializer jsonSerializer,
            IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _cachePath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                "strmassistant_douban_cache.json");
            _retryPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                "strmassistant_douban_retry.json");
            _seasonsPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                "strmassistant_douban_seasons.json");

            _ratingService = new DoubanRatingService(jsonSerializer, FetchSubjectAsync,
                (item, suggestType, seasonNumber, ct) => ApplyAsync(item, suggestType, seasonNumber, ct),
                () => IsBreakerEngaged());
        }

        /// <summary>
        /// 按条目 ID 列表解析豆瓣评分快照（经条目上的 Douban ProviderId 关联），供徽章端点使用。
        /// </summary>
        public List<DoubanRatingInfo> GetRatings(IEnumerable<string> itemIds) =>
            _ratingService.GetRatings(itemIds);

        /// <summary>按当前用户可见性过滤的评分快照查询。</summary>
        public List<DoubanRatingInfo> GetRatings(IEnumerable<string> itemIds, User user) =>
            _ratingService.GetRatings(itemIds, user);

        /// <summary>按 IMDb ID 列表解析豆瓣评分（Kodi 侧用外部 ID 定位）。</summary>
        public List<DoubanImdbRatingInfo> GetRatingsByImdb(IEnumerable<string> imdbIds, User user) =>
            _ratingService.GetRatingsByImdb(imdbIds, user);

        /// <summary>条目/ProviderIds 变化后让 IMDb 索引下次重建。</summary>
        public void InvalidateImdbIndex() => _ratingService.InvalidateImdbIndex();

        /// <summary>详情页 / 开始播放的按需评分刷新。</summary>
        public bool TryScheduleRatingRefresh(BaseItem item, string origin) =>
            _ratingService.TryScheduleRatingRefresh(item, origin);

        /// <summary>本地修复 ProviderIds["DoubanRating"]，不发豆瓣请求。</summary>
        public void RepairRatingProviderIds(IEnumerable<BaseItem> items) =>
            _ratingService.RepairRatingProviderIds(items);

        /// <summary>社区评分被其它来源接管时复位豆瓣应用标记。</summary>
        public void InvalidateRatingApplied(BaseItem item) => _ratingService.InvalidateRatingApplied(item);

        /// <summary>强制把评分快照落盘。</summary>
        public void FlushRatings() => _ratingService.FlushRatings();

        /// <summary>加载失败重试队列（仅在持有 <see cref="_retryLock"/> 时调用）。</summary>
        private void EnsureRetryLoaded()
        {
            if (_retry != null) return;

            _retry = new Dictionary<string, RetryEntry>();

            try
            {
                if (File.Exists(_retryPath))
                {
                    var persisted = _jsonSerializer.DeserializeFromFile<Dictionary<string, RetryEntry>>(_retryPath);
                    if (persisted != null) _retry = persisted;
                }
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Retry queue load failed: {0}", e.Message);
            }
        }

        private void SaveRetry()
        {
            lock (_retryLock)
            {
                if (_retry == null) return;

                try
                {
                    _jsonSerializer.SerializeToFile(_retry, _retryPath);
                }
                catch (Exception e)
                {
                    _logger.Debug("DoubanAssist - Retry queue save failed: {0}", e.Message);
                }
            }
        }

        /// <summary>
        /// 记录一次失败。累计达到 <see cref="MaxRetryAttempts"/> 后移出队列，
        /// 避免标题始终匹配不上的条目每次扫库都被重跑。
        /// </summary>
        private void RecordRetry(long itemId, string error)
        {
            lock (_retryLock)
            {
                EnsureRetryLoaded();

                var key = itemId.ToString();

                if (_retry.TryGetValue(key, out var entry))
                {
                    entry.Attempts++;
                    entry.LastError = error;
                    entry.LastAt = DateTime.UtcNow;

                    if (entry.Attempts >= MaxRetryAttempts) _retry.Remove(key);
                }
                else
                {
                    _retry[key] = new RetryEntry { Attempts = 1, LastError = error, LastAt = DateTime.UtcNow };
                }
            }
        }

        private void RemoveRetry(long itemId)
        {
            lock (_retryLock)
            {
                EnsureRetryLoaded();
                _retry.Remove(itemId.ToString());
            }
        }

        // ===== 季级映射（剧集豆瓣 ID → 季号 → 季豆瓣 ID）=====

        private void EnsureSeasonsLoaded()
        {
            if (_seasons != null) return;

            _seasons = new Dictionary<string, Dictionary<string, SeasonMapEntry>>();

            try
            {
                if (File.Exists(_seasonsPath))
                {
                    var persisted =
                        _jsonSerializer.DeserializeFromFile<Dictionary<string, Dictionary<string, SeasonMapEntry>>>(
                            _seasonsPath);
                    if (persisted != null) _seasons = persisted;
                }
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Season map load failed: {0}", e.Message);
            }
        }

        private void SaveSeasons()
        {
            try
            {
                _jsonSerializer.SerializeToFile(_seasons, _seasonsPath);
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Season map save failed: {0}", e.Message);
            }
        }

        /// <summary>查季映射：命中且未过期才返回，过期视为未命中（触发重新解析）。</summary>
        private string TryGetSeasonSubject(string seriesSubjectId, int seasonNumber)
        {
            if (string.IsNullOrEmpty(seriesSubjectId)) return null;

            lock (_seasonsLock)
            {
                EnsureSeasonsLoaded();

                if (!_seasons.TryGetValue(seriesSubjectId, out var bySeason)) return null;
                if (!bySeason.TryGetValue(seasonNumber.ToString(), out var entry)) return null;
                if (string.IsNullOrEmpty(entry?.SubjectId)) return null;

                if (DateTime.UtcNow - entry.Updated > SeasonMapTtl)
                {
                    _logger.Debug("DoubanAssist - Season map expired for series {0} S{1}", seriesSubjectId,
                        seasonNumber);
                    return null;
                }

                return entry.SubjectId;
            }
        }

        /// <summary>
        /// 清空季映射表（设置页按钮调用）。用于季条目匹配错误后立即重试，
        /// 无需等待 7 天 TTL 过期或手工删除 json 文件。
        /// </summary>
        public int ClearSeasonMap()
        {
            lock (_seasonsLock)
            {
                EnsureSeasonsLoaded();

                var seriesCount = _seasons.Count;
                var seasonCount = _seasons.Values.Sum(m => m.Count);

                _seasons.Clear();
                SaveSeasons();

                _logger.Info("DoubanAssist - Season map cleared: {0} series, {1} season(s)",
                    seriesCount, seasonCount);

                return seasonCount;
            }
        }

        /// <summary>写入季映射并落盘（条目数很少，不做写盘节流）。</summary>
        private void RememberSeason(string seriesSubjectId, int seasonNumber, string seasonSubjectId)        {
            if (string.IsNullOrEmpty(seriesSubjectId) || string.IsNullOrEmpty(seasonSubjectId)) return;

            lock (_seasonsLock)
            {
                EnsureSeasonsLoaded();

                if (!_seasons.TryGetValue(seriesSubjectId, out var bySeason))
                {
                    bySeason = new Dictionary<string, SeasonMapEntry>();
                    _seasons[seriesSubjectId] = bySeason;
                }

                var key = seasonNumber.ToString();
                var existing = bySeason.TryGetValue(key, out var e) ? e : null;

                // 值未变则只刷新时间戳，避免反复写盘
                if (existing != null && existing.SubjectId == seasonSubjectId &&
                    DateTime.UtcNow - existing.Updated < TimeSpan.FromHours(1))
                {
                    return;
                }

                bySeason[key] = new SeasonMapEntry
                {
                    SubjectId = seasonSubjectId,
                    Updated = DateTime.UtcNow
                };

                SaveSeasons();
                _logger.Info("DoubanAssist - Season map updated: series {0} S{1} -> {2}", seriesSubjectId,
                    seasonNumber, seasonSubjectId);
            }
        }

        /// <summary>
        /// 电影刷新完成后调用：匹配豆瓣条目并填充缺失字段。重复触发、锁定条目与非中文库直接跳过。
        /// </summary>
        public Task ApplyAsync(Movie movie, CancellationToken cancellationToken)
        {
            return ApplyAsync(movie, "movie", null, cancellationToken);
        }

        /// <summary>
        /// 剧集刷新完成后调用：匹配豆瓣剧集条目并填充缺失字段。
        /// </summary>
        public Task ApplyAsync(Series series, CancellationToken cancellationToken)
        {
            return ApplyAsync(series, "tv", null, cancellationToken);
        }

        /// <summary>
        /// 季刷新完成后调用：按「剧名 第N季」匹配豆瓣季条目（阿拉伯/中文数字各试一次）。
        /// </summary>
        public Task ApplyAsync(Season season, CancellationToken cancellationToken)
        {
            return ApplyAsync(season, "tv", season?.IndexNumber, cancellationToken);
        }

        /// <summary>
        /// 扫库后补全豆瓣 ID：只处理**缺失 ID** 的条目。
        /// 已有 ID 的条目跳过——它们的评分刷新由条目事件与 BuildDoubanCacheTask 负责，
        /// 否则每次扫库都要把整库重跑一遍。上次失败的条目会被优先重试。
        /// </summary>
        public async Task PopulateDoubanIdsAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            // 扫库后 ProviderIds 可能已变化，IMDb 索引下次重建
            InvalidateImdbIndex();

            var scanItems = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                // Season 必须包含：豆瓣把季拆成独立 subject，季条目需要单独匹配（季级映射表的数据来源）。
                IncludeItemTypes = new[] { nameof(Movie), nameof(Series), nameof(Season) }
            }).ToList();

            // 本地自愈：按评分快照补齐（全量替换元数据后）或清理（关闭开关后）DoubanRating，不发豆瓣请求。
            RepairRatingProviderIds(scanItems);

            var missing = scanItems
                .Where(i => string.IsNullOrEmpty(i.GetProviderId(DoubanProviderId))).ToList();

            // 上次失败的条目优先重试；顺带清理已从库中消失或已补上 ID 的记录
            List<BaseItem> retryItems;

            lock (_retryLock)
            {
                EnsureRetryLoaded();

                retryItems = _retry.Keys
                    .Select(key => long.TryParse(key, out var id) ? Plugin.LibraryManager.GetItemById(id) : null)
                    .Where(i => i != null && string.IsNullOrEmpty(i.GetProviderId(DoubanProviderId)))
                    .ToList();

                var alive = new HashSet<string>(retryItems.Select(i => i.InternalId.ToString()));

                foreach (var key in _retry.Keys.Where(k => !alive.Contains(k)).ToList())
                {
                    _retry.Remove(key);
                }
            }

            var queue = retryItems.Concat(missing)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            _logger.Info("DoubanPopulate - Candidates: {0} (missingId={1}, retry={2})",
                queue.Count, missing.Count, retryItems.Count);

            if (queue.Count == 0)
            {
                _logger.Info("DoubanPopulate - Nothing to do");
                progress?.Report(100.0);
                return;
            }

            var stats = new PopulateStats { Total = queue.Count };
            _populateStats = stats;

            var index = 0;

            try
            {
                foreach (var item in queue)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // 季条目必须带季号走季分支，否则会匹配到剧集主条目（豆瓣季是独立 subject）。
                        var seasonNumber = (item as Season)?.IndexNumber;

                        await ApplyAsync(item, item is Movie ? "movie" : "tv", seasonNumber, cancellationToken)
                            .ConfigureAwait(false);

                        // ApplyAsync 内部吞掉异常，因此以「最终是否拿到豆瓣 ID」判定成败
                        if (string.IsNullOrEmpty(item.GetProviderId(DoubanProviderId)))
                        {
                            RecordRetry(item.InternalId, "no-match");
                        }
                        else
                        {
                            RemoveRetry(item.InternalId);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("DoubanPopulate - Cancelled");
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger.Warn("DoubanPopulate - Failed for {0}: {1}", item.Name, e.Message);
                        RecordRetry(item.InternalId, e.Message);
                    }
                    finally
                    {
                        index++;
                        progress?.Report(index * 100.0 / queue.Count);
                    }
                }
            }
            finally
            {
                _populateStats = null;
                FlushRatings();
                SaveRetry();

                // 一行分布日志：直接回答「为什么有些条目没有豆瓣评分」
                _logger.Info(
                    "DoubanPopulate - Summary: candidates={0} matched={1} noMatch={2} " +
                    "skipped[locked={3}, language={4}] ratingApplied={5} " +
                    "ratingSkipped[lookback={6}, sourceNotDouban={7}]",
                    stats.Total, stats.Matched, stats.NoMatch, stats.SkippedLocked, stats.SkippedLanguage,
                    stats.RatingApplied, stats.RatingSkippedLookback, stats.RatingSkippedSource);

                lock (_retryLock)
                {
                    _logger.Info("DoubanPopulate - Retry queue: pending={0}", _retry?.Count ?? 0);
                }
            }

            progress?.Report(100.0);
            _logger.Info("DoubanPopulate - Complete");
        }

        /// <summary>
        /// 全库同步豆瓣数据：逐条匹配豆瓣条目、写关联 ID、缓存 subject，
        /// 并按勾选应用徽章评分 / Kodi 第三评分 / 简介 / 演员 / 海报 / MdbList。
        /// 来源=豆瓣时 ApplyAsync 会同时写社区评分；来源不是豆瓣时不动社区评分。
        /// 全程复用 2 秒频控、6 小时熔断、缓存命中跳过。
        /// </summary>
        public async Task<DoubanDataSyncReport> SyncAllDoubanDataAsync(IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var report = new DoubanDataSyncReport();
            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();

            var doubanEnabled = options.IsDoubanDataEnabled;
            var mdbListOnly = !doubanEnabled &&
                              options.DoubanAssist.EnableMdbListCriticRating &&
                              !string.IsNullOrWhiteSpace(options.CommunityRating?.MdbListApiKey);

            if (!doubanEnabled && !mdbListOnly)
            {
                _logger.Warn("DoubanSync - skipped: no Douban capability or MdbList critic rating is enabled");
                progress?.Report(100);
                return report;
            }

            if (Plugin.LibraryManager == null)
            {
                _logger.Warn("DoubanSync - skipped: library manager not ready");
                progress?.Report(100);
                return report;
            }

            var items = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Movie), nameof(Series), nameof(Season) }
            });

            report.Total = items.Length;
            _logger.Info($"DoubanSync - items={items.Length} mode={(doubanEnabled ? "douban" : "mdblist")}");

            var index = 0;

            foreach (var item in items)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (doubanEnabled)
                    {
                        var suggestType = item is Movie ? "movie" : "tv";
                        var seasonNumber = (item as Season)?.IndexNumber;
                        await ApplyAsync(item, suggestType, seasonNumber, cancellationToken, true)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await Plugin.RatingUnifier
                            .UpdateMdbListCriticRatingAsync(item, options.CommunityRating?.MdbListApiKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var hasKey = doubanEnabled
                        ? !string.IsNullOrEmpty(item.GetProviderId(DoubanProviderId))
                        : !string.IsNullOrEmpty(item.GetProviderId(MetadataProviders.Imdb));

                    if (hasKey)
                    {
                        report.Matched++;
                    }
                    else
                    {
                        report.Skipped++;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("DoubanSync - cancelled");
                    throw;
                }
                catch (Exception e)
                {
                    report.Failed++;
                    if (report.Failed <= 10)
                    {
                        _logger.Warn($"DoubanSync - {item.Name}: {e.Message}");
                    }
                }
                finally
                {
                    index++;
                    progress?.Report(items.Length == 0 ? 100 : index * 100.0 / items.Length);
                }
            }

            _logger.Info(report.BuildSummary());
            return report;
        }

        /// <summary>全库统一社区评分（手动任务；处理全库，不受评分回看期限制）。</summary>
        public Task<DoubanUnifyReport> UnifyRatingsAsync(IProgress<double> progress,
            CancellationToken cancellationToken) =>
            _ratingService.UnifyRatingsAsync(progress, cancellationToken);

        /// <summary>
        /// 解析条目生效的元数据语言：条目级为空时回退到所属媒体库的 LibraryOptions。
        /// （Emby 4.10 的 item.PreferredMetadataLanguage 不再继承库设置，实测返回空。）
        /// </summary>
        private static string ResolveMetadataLanguage(BaseItem item)
        {
            if (!string.IsNullOrEmpty(item.PreferredMetadataLanguage)) return item.PreferredMetadataLanguage;

            try
            {
                return BaseItem.LibraryManager.GetLibraryOptions(item)?.PreferredMetadataLanguage;
            }
            catch
            {
                return null;
            }
        }

        private async Task ApplyAsync(BaseItem item, string suggestType, int? seasonNumber,
            CancellationToken cancellationToken, bool ignoreRatingLookback = false)
        {
            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();

            if (!options.IsDoubanDataEnabled || item == null) return;

            // 社区评分由「社区评分来源」决定；本方法只处理豆瓣相关能力，每个能力独立开关。
            var metadataEnabled = options.DoubanAssist.DoubanFillOverview ||
                                  options.DoubanAssist.DoubanCastChinese ||
                                  options.DoubanAssist.DoubanPosterFallback;

            var stats = _populateStats;

            if (item.IsLocked)
            {
                if (stats != null) Interlocked.Increment(ref stats.SkippedLocked);
                _logger.Debug("DoubanAssist - Skipped (reason=Locked): {0}", item.Name);
                return;
            }

            var metadataAllowed = metadataEnabled;

            if (metadataEnabled)
            {
                var language = ResolveMetadataLanguage(item);
                metadataAllowed = !string.IsNullOrEmpty(language) &&
                                  language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

                if (!metadataAllowed)
                {
                    if (stats != null) Interlocked.Increment(ref stats.SkippedLanguage);
                    _logger.Debug("DoubanAssist - Metadata skipped (reason=Language, lang={0}): {1}",
                        language ?? "(empty)", item.Name);
                }
            }

            if (!_inFlight.TryAdd(item.InternalId, 0)) return;

            try
            {
                var subject = await FindSubjectAsync(item, suggestType, seasonNumber, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    if (stats != null) Interlocked.Increment(ref stats.NoMatch);
                    _logger.Info("DoubanAssist - No douban match for: {0}", item.Name);
                    return;
                }

                if (stats != null) Interlocked.Increment(ref stats.Matched);
                _logger.Info("DoubanAssist - Matched {0} -> subject {1}", item.Name, subject.SubjectId);

                // 每个能力独立勾选，不再做「生效范围」二次门控。
                var allowNameOverview = options.DoubanAssist.DoubanFillOverview && metadataAllowed;
                var allowCast = options.DoubanAssist.DoubanCastChinese && metadataAllowed;
                var allowPoster = options.DoubanAssist.DoubanPosterFallback && metadataAllowed;

                var changed = false;

                if (!string.Equals(item.GetProviderId(DoubanProviderId), subject.SubjectId, StringComparison.Ordinal))
                {
                    item.ProviderIds[DoubanProviderId] = subject.SubjectId;
                    changed = true;
                }

                // 评分（社区评分 / Web 徽章 / Kodi 第三评分）由评分服务统一处理。
                var ratingResult = await _ratingService.ApplyToItemAsync(item, subject, ignoreRatingLookback,
                    cancellationToken).ConfigureAwait(false);
                changed = ratingResult.Changed || changed;

                if (stats != null)
                {
                    if (ratingResult.Applied) Interlocked.Increment(ref stats.RatingApplied);
                    if (ratingResult.SkippedLookback) Interlocked.Increment(ref stats.RatingSkippedLookback);
                    if (ratingResult.SkippedSource) Interlocked.Increment(ref stats.RatingSkippedSource);
                }

                if (allowNameOverview)
                {
                    if (string.IsNullOrWhiteSpace(item.Overview) && !string.IsNullOrWhiteSpace(subject.Summary))
                    {
                        item.Overview = subject.Summary;
                        changed = true;
                    }

                    foreach (var genre in subject.Genres)
                    {
                        if (!string.IsNullOrWhiteSpace(genre) && !item.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                        {
                            item.AddGenre(genre.Trim());
                            changed = true;
                        }
                    }

                    if (subject.Countries.Count > 0)
                    {
                        var locations = item.ProductionLocations?
                            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList() ?? new List<string>();

                        foreach (var country in subject.Countries)
                        {
                            var trimmed = country.Trim();

                            if (!string.IsNullOrWhiteSpace(trimmed) &&
                                !locations.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                            {
                                locations.Add(trimmed);
                                changed = true;
                            }
                        }

                        item.ProductionLocations = locations.ToArray();
                    }
                }

                // 影评人评分独立于豆瓣社区评分，但与豆瓣数据层共用同一次刷新；
                // 数据层未启用时由 Plugin 的独立 MdbList 钩子处理。
                if (options.DoubanAssist.EnableMdbListCriticRating &&
                    !string.IsNullOrWhiteSpace(options.CommunityRating?.MdbListApiKey) &&
                    (options.DoubanAssist.RatingUpdateLookbackDays <= 0 || !item.PremiereDate.HasValue ||
                     item.PremiereDate.Value >= DateTime.UtcNow.AddDays(
                         -options.DoubanAssist.RatingUpdateLookbackDays)))
                {
                    await Plugin.RatingUnifier
                        .UpdateMdbListCriticRatingAsync(item, options.CommunityRating?.MdbListApiKey, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (allowCast)
                {
                    changed = await ApplyCastChineseNamesAsync(item, subject.SubjectId, cancellationToken)
                        .ConfigureAwait(false) || changed;
                }

                if (allowPoster && !item.HasImage(ImageType.Primary) && suggestType == "movie")
                {
                    changed = await TrySetDoubanPosterAsync(item, subject.SubjectId, cancellationToken)
                        .ConfigureAwait(false) || changed;
                }

                if (changed)
                {
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    _logger.Info("DoubanAssist - Applied subject {0} to {1}", subject.SubjectId, item.Name);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Warn("DoubanAssist - Failed for {0}: {1}", item.Name, e.Message);
                _logger.Debug(e.StackTrace);
            }
            finally
            {
                _inFlight.TryRemove(item.InternalId, out _);
            }
        }

        /// <summary>
        /// 豆瓣演员中文名：覆盖主演（<see cref="PersonType.Actor"/>）与客串（<see cref="PersonType.GuestStar"/>），
        /// 仅替换当前为纯英文名的演员（匹配豆瓣外文名），已中文名的演员不动。
        /// </summary>
        private async Task<bool> ApplyCastChineseNamesAsync(BaseItem item, string subjectId,
            CancellationToken cancellationToken)
        {
            var people = Plugin.LibraryManager.GetItemPeople(item);

            if (people == null || people.Count == 0) return false;

            var celebrities = await FetchCelebritiesAsync(subjectId, cancellationToken).ConfigureAwait(false);

            if (celebrities == null || celebrities.Count == 0) return false;

            var changed = false;

            foreach (var person in people.Where(p =>
                         (p.Type == PersonType.Actor || p.Type == PersonType.GuestStar) &&
                         !string.IsNullOrWhiteSpace(p.Name)))
            {
                if (person.Name.Any(c => c > 0x7F)) continue;

                var entry = celebrities.FirstOrDefault(c =>
                    !string.IsNullOrEmpty(c.ForeignName) &&
                    string.Equals(c.ForeignName.Trim(), person.Name.Trim(), StringComparison.OrdinalIgnoreCase));

                if (entry == null || string.IsNullOrEmpty(entry.ChineseName) ||
                    !entry.ChineseName.Any(c => c > 0x7F)) continue;

                _logger.Info("DoubanAssist - Cast {0} -> {1} for {2}", person.Name, entry.ChineseName, item.Name);
                person.Name = entry.ChineseName;
                changed = true;
            }

            if (changed)
            {
                Plugin.LibraryManager.UpdatePeople(item, people);
            }

            return changed;
        }

        private async Task<List<DoubanCelebrity>> FetchCelebritiesAsync(string subjectId,
            CancellationToken cancellationToken)
        {
            var key = "celeb:" + subjectId;

            var cached = GetCache<List<DoubanCelebrity>>(key);
            if (cached != null) return cached;

            var html = await RequestAsync($"{BaseUrl}/subject/{subjectId}/celebrities", cancellationToken)
                .ConfigureAwait(false);
            if (html == null) return null;

            var result = new List<DoubanCelebrity>();
            var matches = CelebrityNameRegex.Matches(html).Cast<Match>().ToList();

            for (var i = 0; i < matches.Count; i++)
            {
                var celebrity = new DoubanCelebrity { ChineseName = matches[i].Groups[1].Value.Trim() };

                var foreign = matches[i].Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(foreign))
                {
                    celebrity.ForeignName = foreign.Trim();
                }

                var blockEnd = i + 1 < matches.Count ? matches[i + 1].Index : Math.Min(html.Length, matches[i].Index + 800);
                var roleMatch = CelebrityRoleRegex.Match(html, matches[i].Index, blockEnd - matches[i].Index);
                if (roleMatch.Success)
                {
                    celebrity.Role = roleMatch.Groups[1].Value.Trim();
                }

                result.Add(celebrity);
            }

            SetCache(key, result);

            return result;
        }

        /// <summary>
        /// 豆瓣海报兜底：条目无主图时，从 suggest 结果取海报（优先大图）写入 Primary。
        /// </summary>
        private async Task<bool> TrySetDoubanPosterAsync(BaseItem item, string subjectId,
            CancellationToken cancellationToken)
        {
            try
            {
                var searchTerm = item.OriginalTitle ?? item.Name;

                if (string.IsNullOrWhiteSpace(searchTerm)) return false;

                var suggestions = await GetSuggestionsAsync(searchTerm, cancellationToken).ConfigureAwait(false);
                var posterUrl = suggestions?
                    .Where(s => string.Equals(s.id, subjectId, StringComparison.Ordinal) && !string.IsNullOrEmpty(s.img))
                    .Select(s => s.img)
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(posterUrl)) return false;

                var largeUrl = posterUrl.Replace("/s_ratio_poster/", "/l_ratio_poster/", StringComparison.Ordinal);

                var tempPath = await DownloadToTempAsync(largeUrl, cancellationToken).ConfigureAwait(false)
                               ?? await DownloadToTempAsync(posterUrl, cancellationToken).ConfigureAwait(false);

                if (tempPath == null) return false;

                var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(item);
                var directoryService = new DirectoryService(_logger, _fileSystem);

                await _providerManager.SaveImage(item, libraryOptions, tempPath, ImageType.Primary, null,
                        Array.Empty<long>(), directoryService, false, cancellationToken)
                    .ConfigureAwait(false);

                _logger.Info("DoubanAssist - Poster set from subject {0} for {1}", subjectId, item.Name);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Warn("DoubanAssist - Poster fallback failed for {0}: {1}", item.Name, e.Message);
                return false;
            }
        }

        private async Task<string> DownloadToTempAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                var requestOptions = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    BufferContent = true,
                    UserAgent = Plugin.Instance.UserAgent
                };

                var cookie = Plugin.Instance.MetadataEnhanceStore.GetOptions().DoubanAssist.DoubanCookie;
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    requestOptions.RequestHeaders["Cookie"] = cookie.Trim();
                }

                using var response = await _httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK) return null;

                var tempPath = Path.Combine(Plugin.Instance.ApplicationPaths.TempDirectory,
                    "douban_" + Guid.NewGuid().ToString("N") + ".jpg");
                Directory.CreateDirectory(Plugin.Instance.ApplicationPaths.TempDirectory);

                await using var contentStream = response.Content;
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

                return tempPath;
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Image download failed for {0}: {1}", url, e.Message);
                return null;
            }
        }

        private async Task<DoubanSubject> FindSubjectAsync(BaseItem item, string suggestType, int? seasonNumber,
            CancellationToken cancellationToken)
        {
            var cachedId = item.GetProviderId(DoubanProviderId);
            if (!string.IsNullOrEmpty(cachedId))
            {
                _logger.Debug("DoubanAssist - match path=cached-id: {0} -> {1}", item.Name, cachedId);
                return await FetchSubjectAsync(cachedId, cancellationToken).ConfigureAwait(false);
            }

            if (seasonNumber.HasValue)
            {
                var season = item as Season;
                var seriesTitle = season?.Series != null
                    ? season.Series.OriginalTitle ?? season.Series.Name
                    : null;

                if (string.IsNullOrEmpty(seriesTitle)) return null;

                // ① 季映射表优先：父剧集已有豆瓣 ID 时直接查表。
                // 同一部剧的多个季只需解析一次，后续季条目零网络请求即可命中。
                var seriesSubjectId = season.Series.GetProviderId(DoubanProviderId);
                var mappedId = TryGetSeasonSubject(seriesSubjectId, seasonNumber.Value);

                if (!string.IsNullOrEmpty(mappedId))
                {
                    var byMap = await FetchSubjectAsync(mappedId, cancellationToken).ConfigureAwait(false);
                    if (byMap != null)
                    {
                        _logger.Info("DoubanAssist - match path=season-map: {0} S{1} -> {2}",
                            seriesTitle, seasonNumber.Value, mappedId);
                        return byMap;
                    }

                    // 映射指向的豆瓣条目已失效（下架/改 ID）→ 继续走搜索并回填
                    _logger.Info("DoubanAssist - Season map stale for series {0} S{1} -> {2}, searching again",
                        seriesSubjectId, seasonNumber.Value, mappedId);
                }

                var candidates = new List<string> { $"{seriesTitle} 第{seasonNumber.Value}季" };
                var chinese = ToChineseSeasonNumber(seasonNumber.Value);
                if (!chinese.Contains(seasonNumber.Value.ToString())) candidates.Add($"{seriesTitle} {chinese}");

                foreach (var query in candidates)
                {
                    // 传 seriesTitle 作为「剧名主体」校验基准：候选季标题必须与剧名一致，
                    // 否则豆瓣对「重器 第1季」返回的「大国重器 第一季」会被误当成本剧的季。
                    var subject = await SearchByTitleAsync(query, null, suggestType, seasonNumber, cancellationToken,
                            seriesTitle)
                        .ConfigureAwait(false);
                    if (subject != null)
                    {
                        RememberSeason(seriesSubjectId, seasonNumber.Value, subject.SubjectId);
                        _logger.Debug("DoubanAssist - match path=season-search ({0}) for {1} S{2}", query,
                            seriesTitle, seasonNumber.Value);
                        return subject;
                    }
                }

                // 第三策略：按剧名搜索，从全部影视建议里找标题带「目标季号」的条目。
                // 除季号外还必须校验「剧名主体」一致——豆瓣对「重器 第1季」只返回「大国重器 第一季」，
                // 仅靠季号会把不同作品当成同一部剧（剧名主体不同即放弃，交由第四策略回退剧集 ID）。
                var suggestions = await GetSuggestionsAsync(seriesTitle, cancellationToken).ConfigureAwait(false);

                var markers = BuildSeasonMarkers(seasonNumber.Value);
                var seriesKey = NormalizeTitle(seriesTitle);

                var seasonMatch = suggestions?
                    .Where(s => IsVideoSuggestType(s.type) && !string.IsNullOrEmpty(s.id) &&
                                !string.Equals(s.title, seriesTitle, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(s => s.title != null &&
                                         markers.Any(m => s.title.Contains(m, StringComparison.OrdinalIgnoreCase)) &&
                                         NormalizeTitle(s.title) == seriesKey);

                if (seasonMatch != null)
                {
                    var subject = await FetchSubjectAsync(seasonMatch.id, cancellationToken).ConfigureAwait(false);
                    if (subject != null) RememberSeason(seriesSubjectId, seasonNumber.Value, seasonMatch.id);
                    _logger.Debug("DoubanAssist - match path=season-scan ({0}) for {1} S{2}", seasonMatch.title,
                        seriesTitle, seasonNumber.Value);
                    return subject;
                }

                // 第四策略：单季剧在豆瓣没有独立季条目——剧集主条目本身就是第一季页面。
                // 仅对 S1 生效；S2+ 若搜不到就是真的没有（不能拿主条目冒充）。
                if (seasonNumber.Value == 1 && !string.IsNullOrEmpty(seriesSubjectId))
                {
                    var bySeries = await FetchSubjectAsync(seriesSubjectId, cancellationToken).ConfigureAwait(false);
                    if (bySeries != null)
                    {
                        RememberSeason(seriesSubjectId, 1, seriesSubjectId);
                        _logger.Info("DoubanAssist - Season S1 of {0} falls back to series subject {1}",
                            seriesTitle, seriesSubjectId);
                        return bySeries;
                    }
                }

                return null;
            }

            var imdbId = item.GetProviderId(MetadataProviders.Imdb);
            if (!string.IsNullOrEmpty(imdbId))
            {
                var doubanId = await ResolveDoubanIdByImdbAsync(imdbId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(doubanId))
                {
                    var byImdb = await FetchSubjectAsync(doubanId, cancellationToken).ConfigureAwait(false);
                    if (byImdb != null) return byImdb;

                    // IMDb 路径失败（404/风控）时回退标题搜索，而不是直接放弃
                }
            }

            var searchTerm = item.OriginalTitle ?? item.Name;
            if (string.IsNullOrWhiteSpace(searchTerm)) return null;

            return await SearchByTitleAsync(searchTerm, item.ProductionYear, suggestType, null, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 季数转中文序数（1→第一季、21→第二十一季），超出范围返回阿拉伯数字形式。
        /// </summary>
        private static string ToChineseSeasonNumber(int n)
        {
            if (n <= 0 || n > 99) return $"第{n}季";

            var digits = new[] { "", "一", "二", "三", "四", "五", "六", "七", "八", "九" };

            if (n < 10) return "第" + digits[n] + "季";
            if (n == 10) return "第十季";
            if (n < 20) return "第十" + digits[n - 10] + "季";

            var tens = n / 10;
            var ones = n % 10;

            return "第" + digits[tens] + "十" + (ones > 0 ? digits[ones] : string.Empty) + "季";
        }

        private async Task<DoubanSubject> SearchByTitleAsync(string title, int? year, string suggestType,
            int? seasonNumber, CancellationToken cancellationToken, string expectedSeriesTitle = null)
        {
            var suggestions = await GetSuggestionsAsync(title, cancellationToken).ConfigureAwait(false);

            // 豆瓣 suggest 把剧集和季也标成 movie（实测「庆余年 第二季」→ type=movie），
            // 原先按 suggestType 严格过滤会把剧集/季候选全部丢掉——这是「第 1 季」匹配全失败的根因。
            // 这里只排除非影视类型（人物/游戏/书籍等）。
            var matches = suggestions?
                .Where(s => !string.IsNullOrEmpty(s.id) && IsVideoSuggestType(s.type))
                .ToList();

            var match = matches?.FirstOrDefault();

            // 季级匹配：候选标题必须同时满足
            //   ① 带「目标季号」标记（避免第 2 季匹配到第一季/第三季）；
            //   ② 剧名主体一致（去掉季标记与标点后相等）。
            // ② 是必需的：豆瓣对「重器 第1季」只返回「大国重器 第一季」，
            // 仅校验季号会把完全不同的作品当成同一部剧。
            if (seasonNumber.HasValue && matches != null)
            {
                var markers = BuildSeasonMarkers(seasonNumber.Value);
                var seriesKey = string.IsNullOrEmpty(expectedSeriesTitle)
                    ? null
                    : NormalizeTitle(expectedSeriesTitle);

                match = matches.FirstOrDefault(s =>
                {
                    if (s.title == null) return false;
                    if (!markers.Any(m => s.title.Contains(m, StringComparison.OrdinalIgnoreCase))) return false;
                    return seriesKey == null || NormalizeTitle(s.title) == seriesKey;
                });

                if (match == null && seriesKey != null)
                {
                    _logger.Info(
                        "DoubanAssist - Season {0} of {1}: no candidate with matching title body (rejected unrelated titles)",
                        seasonNumber.Value, expectedSeriesTitle);
                }
            }

            if (match == null) return null;

            if (year.HasValue && !string.IsNullOrEmpty(match.year) &&
                int.TryParse(match.year, out var suggestYear) && Math.Abs(suggestYear - year.Value) > 1)
            {
                _logger.Debug("DoubanAssist - Year mismatch for {0}: douban {1} vs item {2}", title, suggestYear, year);
                return null;
            }

            return await FetchSubjectAsync(match.id, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 标题归一化：用于判断「候选标题主体」是否与剧名一致。
        /// 去掉季/部标记、空白与常见标点——「天才，女友」与「我的天才女友 第一季」归一化后不同，
        /// 会走 S1 回退（结果仍正确）；「庆余年」与「庆余年 第二季」归一化后相同，正常接受。
        /// </summary>
        private static string NormalizeTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var s = value.Trim();

            // 季/部标记（中英文）
            s = Regex.Replace(s, @"第[0-9一二三四五六七八九十百千]+季", string.Empty);
            s = Regex.Replace(s, @"第[0-9一二三四五六七八九十百千]+部", string.Empty);
            s = Regex.Replace(s, @"(?i)\bseason\s*\d+", string.Empty);
            s = Regex.Replace(s, @"(?i)\bpart\s*\d+", string.Empty);
            s = Regex.Replace(s, @"(?i)\bs\d{1,2}\b", string.Empty);

            // 空白与标点
            s = Regex.Replace(s, @"[\s\u3000]", string.Empty);
            s = Regex.Replace(s, @"[，。、·・:：;；\-—_~～!！?？\.\(\)（）\[\]【】《》<>""'“”‘’]", string.Empty);

            return s.Trim();
        }

        /// <summary>豆瓣 suggest 的影视类型：movie（含剧集/季）与 tv。其余（person/game/book 等）一律排除。</summary>
        private static bool IsVideoSuggestType(string type)
        {
            return string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 生成某季的标题标记集合，覆盖豆瓣常见命名：
        /// 「第2季」「第二季」「Season 2」「Part 2」「第2部」「第2章」。
        /// </summary>
        private static List<string> BuildSeasonMarkers(int seasonNumber)
        {
            var markers = new List<string>
            {
                $"第{seasonNumber}季",
                $"Season {seasonNumber}",
                $"Part {seasonNumber}",
                $"第{seasonNumber}部"
            };

            var chinese = ToChineseSeasonNumber(seasonNumber);
            if (!markers.Contains(chinese)) markers.Add(chinese);

            return markers;
        }

        private async Task<List<DoubanSuggest>> GetSuggestionsAsync(string query, CancellationToken cancellationToken)
        {
            var key = "suggest:" + query;

            var cached = GetCache<List<DoubanSuggest>>(key);
            if (cached != null) return cached;

            if (IsBreakerEngaged()) return null;

            var html = await RequestAsync($"{SuggestUrl}?q={Uri.EscapeDataString(query)}", cancellationToken)
                .ConfigureAwait(false);
            if (html == null) return null;

            try
            {
                var result = _jsonSerializer.DeserializeFromString<List<DoubanSuggest>>(html);
                SetCache(key, result);
                return result;
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Suggest parse failed: {0}", e.Message);
                return null;
            }
        }

        /// <summary>
        /// 通过 Wikidata SPARQL（IMDb P345 → 豆瓣 P4529）解析豆瓣条目 ID。
        /// </summary>
        private async Task<string> ResolveDoubanIdByImdbAsync(string imdbId, CancellationToken cancellationToken)
        {
            var key = "wd:" + imdbId;

            var cached = GetCache<string>(key);
            if (cached != null) return cached;

            var query = $"SELECT ?doubanId WHERE {{ ?item wdt:P345 \"{imdbId}\" ; wdt:P4529 ?doubanId. " +
                        "FILTER(ISLITERAL(?doubanId)) } LIMIT 1";
            var url = $"{SparqlUrl}?query={Uri.EscapeDataString(query)}&format=json";

            var json = await RequestAsync(url, cancellationToken).ConfigureAwait(false);
            if (json == null) return null;

            try
            {
                var response = _jsonSerializer.DeserializeFromString<SparqlResponse>(json);
                var doubanId = response?.results?.bindings?.FirstOrDefault()?.doubanId?.value;

                // 防御：只接受纯数字的豆瓣 subject id（过滤 blank-node/genid 等 URL 值）
                if (string.IsNullOrEmpty(doubanId) || !Regex.IsMatch(doubanId, @"^\d+$"))
                {
                    _logger.Info("DoubanAssist - Wikidata returned no valid douban id for {0}", imdbId);
                    return null;
                }

                SetCache(key, doubanId);

                return doubanId;
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Wikidata parse failed: {0}", e.Message);
                return null;
            }
        }

        private async Task<DoubanSubject> FetchSubjectAsync(string subjectId, CancellationToken cancellationToken)
        {
            var key = "subject:" + subjectId;

            var cached = GetCache<DoubanSubject>(key);
            if (cached != null)
            {
                // 快照文件与缓存文件生命周期独立，缓存命中时补记一次，
                // 避免快照被删/生成晚于缓存时 7 天内徽章一直缺失（RecordSubjectRating 自身幂等）。
                if (cached.Rating.HasValue)
                {
                    _ratingService.RecordSubjectRating(subjectId, cached.Rating.Value, cached.Votes);
                }

                return cached;
            }

            if (IsBreakerEngaged()) return null;

            // 优先走豆瓣开放平台 V2 API（若已配置 Access Token）。
            var accessToken = Plugin.Instance.MetadataEnhanceStore.GetOptions().DoubanAssist.DoubanAccessToken;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var apiSubject = await FetchSubjectViaApiAsync(subjectId, accessToken.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                if (apiSubject != null)
                {
                    SetCache(key, apiSubject);
                    if (apiSubject.Rating.HasValue)
                    {
                        _ratingService.RecordSubjectRating(subjectId, apiSubject.Rating.Value, apiSubject.Votes);
                    }

                    return apiSubject;
                }

                _logger.Info("DoubanAssist - V2 API failed for {0}, falling back to HTML scraping", subjectId);
            }

            var html = await RequestAsync($"{BaseUrl}/subject/{subjectId}/", cancellationToken).ConfigureAwait(false);
            if (html == null) return null;

            if (html.Contains("sec.douban.com", StringComparison.Ordinal) ||
                (!html.Contains("v:average", StringComparison.Ordinal) &&
                 !html.Contains("id=\"info\"", StringComparison.Ordinal)))
            {
                _logger.Info("DoubanAssist - Subject {0} blocked by douban risk control (sec page). " +
                             "Configure a logged-in DoubanCookie in settings to bypass.", subjectId);
                RegisterBlocked(subjectId);
                return null;
            }

            _consecutiveBlocked = 0;

            var subject = ParseSubject(subjectId, html);
            SetCache(key, subject);

            if (subject.Rating.HasValue)
            {
                _ratingService.RecordSubjectRating(subjectId, subject.Rating.Value, subject.Votes);
            }

            return subject;
        }

        /// <summary>
        /// 通过豆瓣开放平台 V2 API 拉取条目（需 Access Token）。
        /// 先按电影 /movie/{id} 尝试，失败再按剧集 /tv/{id} 尝试。任何异常均返回 null 以触发网页回退。
        /// </summary>
        private async Task<DoubanSubject> FetchSubjectViaApiAsync(string subjectId, string accessToken,
            CancellationToken cancellationToken)
        {
            var subject = await TryFetchV2Async(string.Format(DoubanV2MoviePath, subjectId), accessToken, cancellationToken)
                              .ConfigureAwait(false)
                          ?? await TryFetchV2Async(string.Format(DoubanV2TvPath, subjectId), accessToken, cancellationToken)
                              .ConfigureAwait(false);

            if (subject != null) subject.SubjectId = subjectId;
            return subject;
        }

        private async Task<DoubanSubject> TryFetchV2Async(string path, string accessToken,
            CancellationToken cancellationToken)
        {
            try
            {
                var requestOptions = new HttpRequestOptions
                {
                    Url = DoubanV2BaseUrl + path,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    BufferContent = true,
                    UserAgent = Plugin.Instance.UserAgent
                };
                requestOptions.RequestHeaders["Authorization"] = "Bearer " + accessToken;

                using var response = await _httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug("DoubanAssist - V2 {0} returned {1}", path, response.StatusCode);
                    return null;
                }

                await using var contentStream = response.Content;
                var payload = _jsonSerializer.DeserializeFromStream<DoubanV2Subject>(contentStream);
                if (payload == null || string.IsNullOrEmpty(payload.id)) return null;

                var subject = new DoubanSubject
                {
                    Summary = payload.intro
                };

                if (payload.rating != null && payload.rating.value > 0)
                {
                    subject.Rating = payload.rating.value;
                }

                if (payload.rating != null && payload.rating.count > 0)
                {
                    subject.Votes = payload.rating.count;
                }

                if (payload.genres != null)
                {
                    subject.Genres = payload.genres.Where(g => !string.IsNullOrWhiteSpace(g))
                        .Select(g => g.Trim()).ToList();
                }

                if (payload.countries != null)
                {
                    subject.Countries = payload.countries.Where(c => !string.IsNullOrWhiteSpace(c))
                        .Select(c => c.Trim()).ToList();
                }

                if (payload.languages != null)
                {
                    subject.Languages = payload.languages.Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim()).ToList();
                }

                return subject;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - V2 request failed for {0}: {1}", path, e.Message);
                return null;
            }
        }

        private class DoubanV2Subject
        {
            public string id { get; set; }
            public string intro { get; set; }
            public DoubanV2Rating rating { get; set; }
            public List<string> genres { get; set; }
            public List<string> countries { get; set; }
            public List<string> languages { get; set; }
        }

        private class DoubanV2Rating
        {
            public double value { get; set; }
            public long count { get; set; }
        }

        private static DoubanSubject ParseSubject(string subjectId, string html)
        {
            var subject = new DoubanSubject { SubjectId = subjectId };

            var ratingMatch = RatingRegex.Match(html);
            if (ratingMatch.Success && double.TryParse(ratingMatch.Groups[1].Value, out var rating))
            {
                subject.Rating = rating;
            }

            var votesMatch = VotesRegex.Match(html);
            if (votesMatch.Success && long.TryParse(votesMatch.Groups[1].Value, out var votes))
            {
                subject.Votes = votes;
            }

            var summaryMatch = SummaryRegex.Match(html);
            if (summaryMatch.Success)
            {
                subject.Summary = StripTagsRegex.Replace(summaryMatch.Groups[1].Value, string.Empty)
                    .Replace("&nbsp;", " ").Replace("\u00a0", " ").Trim();
            }

            foreach (Match m in GenreRegex.Matches(html))
            {
                subject.Genres.Add(m.Groups[1].Value.Trim());
            }

            var infoMatch = InfoRegex.Match(html);
            if (infoMatch.Success)
            {
                var info = StripTagsRegex.Replace(infoMatch.Groups[1].Value, string.Empty);

                var countryMatch = CountryRegex.Match(info);
                if (countryMatch.Success)
                {
                    var value = countryMatch.Groups[1].Success ? countryMatch.Groups[1].Value : countryMatch.Groups[2].Value;
                    subject.Countries = value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                }

                var languageMatch = LanguageRegex.Match(info);
                if (languageMatch.Success)
                {
                    var value = languageMatch.Groups[1].Success ? languageMatch.Groups[1].Value : languageMatch.Groups[2].Value;
                    subject.Languages = value.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                }
            }

            return subject;
        }

        private async Task<string> RequestAsync(string url, CancellationToken cancellationToken)
        {
            lock (_requestLock)
            {
                var wait = _lastRequestTime + RequestInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                _lastRequestTime = DateTime.UtcNow;
            }

            try
            {
                var requestOptions = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "text/html,application/json;q=0.9,*/*;q=0.8",
                    BufferContent = true,
                    UserAgent = Plugin.Instance.UserAgent
                };

                var cookie = Plugin.Instance.MetadataEnhanceStore.GetOptions().DoubanAssist.DoubanCookie;
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    requestOptions.RequestHeaders["Cookie"] = cookie.Trim();
                }

                using var response = await _httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Info("DoubanAssist - {0} returned {1}", url, response.StatusCode);
                    return null;
                }

                await using var contentStream = response.Content;
                using var reader = new StreamReader(contentStream);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Request failed for {0}: {1}", url, e.Message);
                return null;
            }
        }

        private bool IsBreakerEngaged()
        {
            if (_breakerUntil > DateTime.UtcNow)
            {
                _logger.Warn("DoubanAssist - Circuit breaker engaged until {0:HH:mm}, requests paused", _breakerUntil);
                return true;
            }

            return false;
        }

        private void RegisterBlocked(string subjectId)
        {
            _consecutiveBlocked++;

            _logger.Warn("DoubanAssist - Blocked by anti-crawler for subject {0} ({1}/{2})",
                subjectId, _consecutiveBlocked, BreakerThreshold);

            if (_consecutiveBlocked >= BreakerThreshold)
            {
                _breakerUntil = DateTime.UtcNow + BreakerCooldown;
                _consecutiveBlocked = 0;
                _logger.Warn("DoubanAssist - Circuit breaker engaged for {0} hours, configure Cookie or pause scraping",
                    BreakerCooldown.TotalHours);
            }
        }

        private T GetCache<T>(string key) where T : class
        {
            lock (_cacheLock)
            {
                if (_cache == null) LoadCache();

                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.Expiry > DateTime.UtcNow) return entry.Value as T;
                    _cache.Remove(key);
                }
            }

            return null;
        }

        private void SetCache<T>(string key, T value) where T : class
        {
            lock (_cacheLock)
            {
                if (_cache == null) LoadCache();

                _cache[key] = new CacheEntry { Expiry = DateTime.UtcNow + CacheDuration, Value = value };
                SaveCache();
            }
        }

        private void LoadCache()
        {
            _cache = new Dictionary<string, CacheEntry>();

            try
            {
                if (File.Exists(_cachePath))
                {
                    var persisted = _jsonSerializer.DeserializeFromFile<Dictionary<string, CacheEntry>>(_cachePath);
                    if (persisted != null) _cache = persisted;
                }
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Cache load failed: {0}", e.Message);
            }
        }

        private void SaveCache()
        {
            try
            {
                var cutoff = DateTime.UtcNow;

                foreach (var expired in _cache.Where(kv => kv.Value.Expiry <= cutoff)
                             .Select(kv => kv.Key).ToList())
                {
                    _cache.Remove(expired);
                }

                _jsonSerializer.SerializeToFile(_cache, _cachePath);
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Cache save failed: {0}", e.Message);
            }
        }
    }

    /// <summary>豆瓣全库同步的统计结果；与 TMDB/IMDb 的统一报告分开，避免日志混淆。</summary>
    public sealed class DoubanDataSyncReport
    {
        public int Total { get; set; }
        public int Matched { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }

        public string BuildSummary()
        {
            return string.Format("DoubanSync - total={0} matched={1} skipped={2} failed={3}",
                Total, Matched, Skipped, Failed);
        }
    }
}
