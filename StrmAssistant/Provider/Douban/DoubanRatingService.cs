using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Provider.Douban
{
    /// <summary>
    /// 豆瓣评分服务：评分快照、按条目/IMDb 查询、统一社区评分、Kodi 第三评分与本地修复。
    /// 元数据匹配与简介/演员/海报仍由 <see cref="DoubanAssist"/> 负责；
    /// 本服务通过委托获取 subject 并回调整条刷新，避免与元数据服务循环依赖。
    /// </summary>
    public sealed class DoubanRatingService
    {
        private static readonly TimeSpan ImdbRatingCacheDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan ItemKeyIndexTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RatingsSaveThrottle = TimeSpan.FromSeconds(10);

        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly DoubanRatingClient _ratingClient = new DoubanRatingClient();
        private readonly Func<string, CancellationToken, Task<DoubanSubject>> _fetchSubject;
        private readonly Func<BaseItem, string, int?, CancellationToken, Task> _applyItem;
        private readonly Func<bool> _isBreakerEngaged;

        private readonly string _ratingsPath;
        private readonly object _ratingsLock = new object();
        private readonly ConcurrentDictionary<long, byte> _refreshScheduled = new ConcurrentDictionary<long, byte>();

        private Dictionary<string, long> _itemKeyIndex;
        private DateTime _itemKeyIndexBuilt;
        private readonly object _itemKeyIndexLock = new object();

        private Dictionary<string, DoubanRatingSnapshot> _ratings;
        private DateTime _lastRatingsSave = DateTime.MinValue;

        public DoubanRatingService(IJsonSerializer jsonSerializer,
            Func<string, CancellationToken, Task<DoubanSubject>> fetchSubject,
            Func<BaseItem, string, int?, CancellationToken, Task> applyItem,
            Func<bool> isBreakerEngaged)
        {
            _logger = Plugin.Instance.Logger;
            _jsonSerializer = jsonSerializer;
            _fetchSubject = fetchSubject;
            _applyItem = applyItem;
            _isBreakerEngaged = isBreakerEngaged;
            _ratingsPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                "strmassistant_douban_ratings.json");
        }

        /// <summary>
        /// 按条目 ID 列表解析豆瓣评分快照（经条目上的 Douban ProviderId 关联），供徽章端点使用。
        /// </summary>
        public List<DoubanRatingInfo> GetRatings(IEnumerable<string> itemIds) => GetRatings(itemIds, null);

        /// <summary>
        /// 同上，但按 <paramref name="user"/> 的可见性过滤：外部端点必须传当前用户，
        /// 否则匿名/越权请求能拿到「内部 ItemId ↔ 豆瓣 SubjectId」的对应关系。
        /// 内部任务（如扫库填充）传 null 表示不做可见性过滤。
        /// </summary>
        public List<DoubanRatingInfo> GetRatings(IEnumerable<string> itemIds, User user)
        {
            var result = new List<DoubanRatingInfo>();

            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                foreach (var itemId in itemIds)
                {
                    try
                    {
                        var item = Plugin.LibraryManager.GetItemById(long.Parse(itemId));
                        if (item == null) continue;
                        if (user != null && !item.IsVisible(user)) continue;

                        var subjectId = item.GetProviderId(DoubanConstants.ProviderId);
                        var source = item.GetProviderId(CommunityRatingService.SourceProviderId);

                        DoubanRatingSnapshot snapshot = null;
                        if (!string.IsNullOrEmpty(subjectId))
                        {
                            _ratings.TryGetValue(subjectId, out snapshot);
                        }

                        // 没有豆瓣 ID 时，全库评分把结果记在 imdb:tt... 名下，这里按 IMDb 兜底查一次。
                        if (snapshot == null)
                        {
                            var imdbKey = GetImdbSnapshotKey(item.GetProviderId(MetadataProviders.Imdb));
                            if (!string.IsNullOrEmpty(imdbKey))
                            {
                                _ratings.TryGetValue(imdbKey, out snapshot);
                            }
                        }

                        // 既没有豆瓣条目、也没有显式来源：不是本模块关心的数据。
                        if (snapshot == null && string.IsNullOrEmpty(source)) continue;

                        // Applied 以显式来源优先；无来源标记时退回「数字相等」的旧数据推断。
                        var ratingMatches = snapshot != null && item.CommunityRating.HasValue &&
                                            Math.Abs(item.CommunityRating.Value - snapshot.Rating) <= 0.05;

                        var writtenSource = snapshot?.WrittenSource;
                        var applied = string.Equals(source, "douban", StringComparison.OrdinalIgnoreCase) ||
                                      string.IsNullOrEmpty(source) &&
                                      (string.Equals(writtenSource, "douban", StringComparison.OrdinalIgnoreCase) ||
                                       string.IsNullOrEmpty(writtenSource) && ratingMatches);

                        if (string.IsNullOrEmpty(source) && applied) source = "douban";

                        result.Add(new DoubanRatingInfo
                        {
                            ItemId = itemId,
                            SubjectId = subjectId,
                            Rating = snapshot?.Rating,
                            Votes = snapshot?.Votes,
                            Applied = applied,
                            Source = source,
                            CommunityRating = item.CommunityRating
                        });
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 按外部键列表解析豆瓣评分（Kodi 侧用 IMDb / TMDb / 豆瓣 ID 定位）。
        /// 支持 tt...、tmdb:123、douban:123；Emby 认识但没有豆瓣数据时返回 Rating=null。
        /// </summary>
        public List<DoubanImdbRatingInfo> GetRatingsByKeys(IEnumerable<string> keys, User user)
        {
            var result = new List<DoubanImdbRatingInfo>();
            var wanted = new List<DoubanRatingKey>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in keys ?? Enumerable.Empty<string>())
            {
                if (!DoubanRatingKeyParser.TryParse(raw, out var key)) continue;
                if (!seen.Add(key.CanonicalKey)) continue;

                wanted.Add(key);
                if (wanted.Count >= 80) break;
            }

            if (wanted.Count == 0) return result;

            var index = GetItemKeyIndex();

            foreach (var key in wanted)
            {
                if (!index.TryGetValue(key.IndexKey, out var itemId)) continue;

                var item = Plugin.LibraryManager.GetItemById(itemId);
                if (item == null) continue;
                if (user != null && !item.IsVisible(user)) continue;

                var imdb = item.GetProviderId(MetadataProviders.Imdb)?.Trim().ToLowerInvariant();
                var subjectId = item.GetProviderId(DoubanConstants.ProviderId);
                var source = item.GetProviderId(CommunityRatingService.SourceProviderId);
                double? rating = null;
                long? votes = null;
                var applied = false;

                if (!string.IsNullOrEmpty(subjectId))
                {
                    lock (_ratingsLock)
                    {
                        if (_ratings == null) LoadRatings();

                        if (_ratings.TryGetValue(subjectId, out var snapshot))
                        {
                            rating = snapshot.Rating;
                            votes = snapshot.Votes;

                            var ratingMatches = item.CommunityRating.HasValue &&
                                                Math.Abs(item.CommunityRating.Value - snapshot.Rating) <= 0.05;
                            var writtenSource = snapshot.WrittenSource;
                            applied = string.Equals(source, "douban", StringComparison.OrdinalIgnoreCase) ||
                                      string.IsNullOrEmpty(source) &&
                                      (string.Equals(writtenSource, "douban", StringComparison.OrdinalIgnoreCase) ||
                                       string.IsNullOrEmpty(writtenSource) && ratingMatches);
                        }
                    }
                }

                // 没有豆瓣 ID 或还没有 subject 快照时，按条目 IMDb key 读取全库评分落下的快照。
                if (rating == null)
                {
                    var imdbKey = GetImdbSnapshotKey(imdb);

                    if (!string.IsNullOrEmpty(imdbKey))
                    {
                        lock (_ratingsLock)
                        {
                            if (_ratings == null) LoadRatings();

                            if (_ratings.TryGetValue(imdbKey, out var imdbSnapshot))
                            {
                                rating = imdbSnapshot.Rating;
                                votes = imdbSnapshot.Votes;

                                var ratingMatches = item.CommunityRating.HasValue &&
                                                    Math.Abs(item.CommunityRating.Value - imdbSnapshot.Rating) <= 0.05;
                                var writtenSource = imdbSnapshot.WrittenSource;
                                applied = string.Equals(source, "douban", StringComparison.OrdinalIgnoreCase) ||
                                          string.IsNullOrEmpty(source) &&
                                          (string.Equals(writtenSource, "douban", StringComparison.OrdinalIgnoreCase) ||
                                           string.IsNullOrEmpty(writtenSource) && ratingMatches);
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(source) && applied) source = "douban";

                result.Add(new DoubanImdbRatingInfo
                {
                    Key = key.CanonicalKey,
                    KeyType = key.TypeName,
                    Imdb = imdb,
                    ItemId = itemId.ToString(),
                    SubjectId = subjectId,
                    Rating = rating,
                    Votes = votes,
                    Applied = applied,
                    Source = source,
                    CommunityRating = item.CommunityRating
                });
            }

            return result;
        }

        /// <summary>兼容旧调用：仅按 IMDb 查询。</summary>
        public List<DoubanImdbRatingInfo> GetRatingsByImdb(IEnumerable<string> imdbIds, User user) =>
            GetRatingsByKeys(imdbIds, user);

        /// <summary>外部键（imdb/tmdb/douban）→ 条目内部 ID 索引：10 分钟缓存，扫库后立即失效。</summary>
        private Dictionary<string, long> GetItemKeyIndex()
        {
            lock (_itemKeyIndexLock)
            {
                if (_itemKeyIndex != null && DateTime.UtcNow - _itemKeyIndexBuilt < ItemKeyIndexTtl)
                {
                    return _itemKeyIndex;
                }

                var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var items = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(Movie), nameof(Series), nameof(Episode), nameof(Season) }
                    });

                    // 同一外部 ID 可能有重复条目：优先保留带豆瓣 ID 的那条。
                    var withDouban = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    void Add(string indexKey, BaseItem item, bool hasDouban)
                    {
                        if (!index.ContainsKey(indexKey))
                        {
                            index[indexKey] = item.InternalId;
                            if (hasDouban) withDouban.Add(indexKey);
                        }
                        else if (hasDouban && !withDouban.Contains(indexKey))
                        {
                            index[indexKey] = item.InternalId;
                            withDouban.Add(indexKey);
                        }
                    }

                    foreach (var item in items)
                    {
                        var hasDouban = !string.IsNullOrEmpty(item.GetProviderId(DoubanConstants.ProviderId));

                        var imdb = item.GetProviderId(MetadataProviders.Imdb);
                        if (DoubanRatingKeyParser.TryParse(imdb, out var imdbKey))
                        {
                            Add(imdbKey.IndexKey, item, hasDouban);
                        }

                        var tmdb = item.GetProviderId(MetadataProviders.Tmdb);
                        if (!string.IsNullOrWhiteSpace(tmdb) &&
                            DoubanRatingKeyParser.TryParse("tmdb:" + tmdb.Trim(), out var tmdbKey))
                        {
                            Add(tmdbKey.IndexKey, item, hasDouban);
                        }

                        var douban = item.GetProviderId(DoubanConstants.ProviderId);
                        if (!string.IsNullOrWhiteSpace(douban) &&
                            DoubanRatingKeyParser.TryParse("douban:" + douban.Trim(), out var doubanKey))
                        {
                            Add(doubanKey.IndexKey, item, true);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug("DoubanAssist - Item key index build failed: {0}", e.Message);
                }

                _itemKeyIndex = index;
                _itemKeyIndexBuilt = DateTime.UtcNow;
                return index;
            }
        }

        /// <summary>条目/ProviderIds 变化后让外部键索引下次重建。</summary>
        public void InvalidateImdbIndex()
        {
            lock (_itemKeyIndexLock)
            {
                _itemKeyIndex = null;
            }
        }

        private static string GetImdbSnapshotKey(string imdbId)
        {
            return DoubanRatingResolver.GetImdbSnapshotKey(imdbId);
        }

        /// <summary>
        /// 统一评分解析：豆瓣条目分 → IMDb 快照 → legacy 兜底；legacy 成功时按解析出的键记快照。
        /// </summary>
        private async Task<DoubanRatingResolution> ResolveRatingAsync(BaseItem item, string doubanId,
            double? subjectRating, long? subjectVotes, CancellationToken cancellationToken)
        {
            var resolution = await DoubanRatingResolver.ResolveAsync(
                    doubanId,
                    item.GetProviderId(MetadataProviders.Imdb),
                    subjectRating,
                    subjectVotes,
                    key => TryGetSnapshotState(key, out var rating, out var votes, out var checkedAt)
                        ? new DoubanSnapshotView { Rating = rating, Votes = votes, CheckedAt = checkedAt }
                        : null,
                    (imdbId, token) => _ratingClient.GetRatingAsync(imdbId, token),
                    ImdbRatingCacheDuration,
                    DateTime.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);

            if (resolution.Status == DoubanRatingClient.DoubanLightStatus.Ok)
            {
                if (resolution.Source == DoubanRatingSource.LegacyImdb)
                {
                    RecordRatingSnapshot(resolution.SnapshotKey, resolution.Rating, resolution.Votes);
                }

                _logger.Debug("DoubanAssist - Rating {0} for {1} source={2}", resolution.Rating, item.Name,
                    resolution.Source);
            }

            return resolution;
        }

        /// <summary>
        /// 对单条应用评分：Kodi 第三评分、社区评分与来源标记；返回统计所需的结果。
        /// 元数据写入仍由调用方负责。
        /// </summary>
        public async Task<DoubanRatingApplyResult> ApplyToItemAsync(BaseItem item, DoubanSubject subject,
            bool ignoreRatingLookback, CancellationToken cancellationToken)
        {
            var result = new DoubanRatingApplyResult();
            if (item == null || subject == null) return result;

            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
            var isDoubanRatingSource = options.IsDoubanRatingSource;
            var needDoubanRating = isDoubanRatingSource ||
                                   options.DoubanAssist.DoubanFetchRating ||
                                   options.DoubanAssist.DoubanRatingToKodi;
            var doubanRating = subject.Rating;

            if (!doubanRating.HasValue && needDoubanRating)
            {
                var resolution = await ResolveRatingAsync(item, subject.SubjectId, subject.Rating,
                    subject.Votes, cancellationToken).ConfigureAwait(false);

                if (resolution.Status == DoubanRatingClient.DoubanLightStatus.Ok)
                {
                    doubanRating = resolution.Rating;
                }
            }

            var changed = SyncRatingProviderId(item, doubanRating,
                options.DoubanAssist.DoubanRatingToKodi);

            // 社区评分只由「社区评分来源」决定；字段被锁定时不写。
            if (isDoubanRatingSource && doubanRating.HasValue &&
                !CommunityRatingService.IsRatingLocked(item))
            {
                // 自动刷新受回看期限制；手动全库同步与首次写入不受限。
                var lookbackDays = options.DoubanAssist.RatingUpdateLookbackDays;
                var ratingAllowed = DoubanRatingPolicy.IsRatingUpdateAllowed(
                    ignoreRatingLookback, lookbackDays, item.PremiereDate,
                    HasRatingEverApplied(subject.SubjectId), DateTimeOffset.UtcNow);

                if (ratingAllowed)
                {
                    var previousRating = item.CommunityRating;

                    if (Math.Abs((previousRating ?? 0) - doubanRating.Value) > 0.05)
                    {
                        item.CommunityRating = (float)doubanRating.Value;
                        changed = true;
                        _logger.Info("DoubanAssist - Rating updated for {0}: {1} -> {2} (votes {3})",
                            item.Name, previousRating?.ToString("0.0") ?? "N/A", doubanRating.Value,
                            subject.Votes);
                    }

                    // 社区评分此刻归豆瓣所有：写来源标记，避免「同分不同源」误判。
                    if (!string.Equals(item.GetProviderId(CommunityRatingService.SourceProviderId), "douban",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        item.ProviderIds[CommunityRatingService.SourceProviderId] = "douban";
                        changed = true;
                    }

                    MarkRatingApplied(subject.SubjectId, true);
                    result.Applied = true;
                }
                else
                {
                    result.SkippedLookback = true;
                    _logger.Info("DoubanAssist - Rating skipped (reason=Lookback, {0}d): {1}", lookbackDays,
                        item.Name);
                }
            }
            else if (isDoubanRatingSource && doubanRating.HasValue)
            {
                _logger.Info("DoubanAssist - Rating skipped (reason=LockedField): {0}", item.Name);
            }
            else if (doubanRating.HasValue)
            {
                result.SkippedSource = true;
                _logger.Debug("DoubanAssist - Rating skipped (reason=SourceNotDouban): {0}", item.Name);
            }

            result.Changed = changed;
            return result;
        }

        /// <summary>记录 subject 评分快照（供元数据服务在抓取或缓存命中时调用）。</summary>
        public void RecordSubjectRating(string subjectId, double rating, long? votes)
        {
            RecordRatingSnapshot(subjectId, rating, votes);
        }

        /// <summary>
        /// 写入评分快照。key 可以是豆瓣 subject ID，也可以是 IMDb key。
        /// </summary>
        private void RecordRatingSnapshot(string key, double rating, long? votes)
        {
            if (string.IsNullOrEmpty(key)) return;

            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                var now = DateTime.UtcNow;

                if (_ratings.TryGetValue(key, out var existing))
                {
                    var ratingChanged = Math.Abs(existing.Rating - rating) >= 0.001 || existing.Votes != votes;

                    existing.Checked = now;

                    if (!ratingChanged)
                    {
                        // 评分未变也要落盘「检查时间」，否则按需刷新的冷却永远不推进；按节流窗口写盘。
                        if (now - _lastRatingsSave >= RatingsSaveThrottle)
                        {
                            _lastRatingsSave = now;
                            SaveRatings();
                        }

                        return;
                    }

                    existing.Rating = rating;
                    existing.Votes = votes;
                    existing.Updated = now;
                    // 豆瓣分变了，CommunityRating 还停在旧分：先置 false，等真正写入后再置 true。
                    existing.Applied = false;
                    existing.WrittenSource = null;

                    SaveRatings();
                    return;
                }

                _ratings[key] = new DoubanRatingSnapshot
                {
                    Rating = rating,
                    Votes = votes,
                    Updated = now,
                    Checked = now,
                    Applied = false,
                    WrittenSource = null
                };

                SaveRatings();
            }
        }

        /// <summary>距上次成功检查是否已超过按需刷新间隔。无快照（含首次）视为到期。</summary>
        private bool IsRatingCheckDue(string subjectId, int refreshDays)
        {
            if (refreshDays <= 0) return false;

            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                if (!_ratings.TryGetValue(subjectId, out var snapshot)) return true;

                var last = snapshot.Checked ?? (snapshot.Updated == default ? (DateTime?)null : snapshot.Updated);
                if (!last.HasValue) return true;

                return DateTime.UtcNow - last.Value >= TimeSpan.FromDays(refreshDays);
            }
        }

        private bool TryGetSnapshotRating(string subjectId, out double rating)
        {
            rating = 0;

            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                if (!_ratings.TryGetValue(subjectId, out var snapshot)) return false;

                rating = snapshot.Rating;
                return true;
            }
        }

        /// <summary>读取指定 key（豆瓣 ID 或 IMDb key）的完整快照状态。</summary>
        private bool TryGetSnapshotState(string key, out double rating, out long? votes, out DateTime? checkedAt)
        {
            rating = 0;
            votes = null;
            checkedAt = null;

            if (string.IsNullOrEmpty(key)) return false;

            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                if (!_ratings.TryGetValue(key, out var snapshot)) return false;

                rating = snapshot.Rating;
                votes = snapshot.Votes;
                checkedAt = snapshot.Checked ??
                            (snapshot.Updated == default ? (DateTime?)null : snapshot.Updated);

                return snapshot.Rating > 0;
            }
        }

        /// <summary>按豆瓣分数同步 <c>ProviderIds["DoubanRating"]</c>，返回是否修改了条目。</summary>
        private static bool SyncRatingProviderId(BaseItem item, double? rating, bool enabled)
        {
            if (enabled && rating.HasValue)
            {
                var value = rating.Value.ToString("0.#", CultureInfo.InvariantCulture);

                if (!string.Equals(item.GetProviderId(DoubanConstants.RatingProviderId), value, StringComparison.Ordinal))
                {
                    item.ProviderIds[DoubanConstants.RatingProviderId] = value;
                    return true;
                }

                return false;
            }

            if (!enabled && item.ProviderIds.ContainsKey(DoubanConstants.RatingProviderId))
            {
                item.ProviderIds.Remove(DoubanConstants.RatingProviderId);
                return true;
            }

            return false;
        }

        /// <summary>「无评分时清空」：只清社区评分及其来源快照，保留独立的 DoubanRating 第三评分。</summary>
        private static bool ClearMainRating(BaseItem item)
        {
            var changed = false;

            if (item.CommunityRating.HasValue)
            {
                item.CommunityRating = null;
                changed = true;
            }

            foreach (var key in new[]
                     {
                         CommunityRatingService.SourceProviderId,
                         CommunityRatingService.TmdbRatingProviderId,
                         CommunityRatingService.ImdbRatingProviderId
                     })
            {
                if (item.ProviderIds.Remove(key)) changed = true;
            }

            return changed;
        }

        /// <summary>
        /// 按需刷新入口（详情页 / 开始播放）：冷却到期才抓豆瓣，异步执行，不阻塞调用方。
        /// 只处理已有豆瓣 ID 的条目——缺 ID 的由扫库补全任务负责。
        /// </summary>
        public bool TryScheduleRatingRefresh(BaseItem item, string origin)
        {
            if (item == null) return false;

            // 剧集播放/详情页拿到的是 Episode，豆瓣评分挂在剧集上，向上归一到 Series。
            if (item is Episode episode && episode.Series != null) item = episode.Series;

            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();

            if (!options.IsDoubanDataEnabled) return false;
            if (options.DoubanAssist.DoubanRefreshIntervalDays <= 0) return false;
            if (item.IsLocked) return false;

            var subjectId = item.GetProviderId(DoubanConstants.ProviderId);
            if (string.IsNullOrEmpty(subjectId)) return false;

            if (!IsRatingCheckDue(subjectId, options.DoubanAssist.DoubanRefreshIntervalDays)) return false;
            if (_isBreakerEngaged()) return false;
            if (!_refreshScheduled.TryAdd(item.InternalId, 0)) return false;

            var suggestType = item is Movie ? "movie" : "tv";
            var seasonNumber = (item as Season)?.IndexNumber;

            _ = BackgroundTaskRunner.Run("Douban.Refresh", async () =>
            {
                try
                {
                    // 0–2 秒抖动，叠加请求频控，避免短时间内集中打豆瓣
                    await Task.Delay(Random.Shared.Next(0, 2000)).ConfigureAwait(false);
                    await _applyItem(item, suggestType, seasonNumber, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.Debug("DoubanAssist - On-demand refresh failed ({0}): {1}", origin, e.Message);
                }
                finally
                {
                    _refreshScheduled.TryRemove(item.InternalId, out _);
                }
            });

            _logger.Info("DoubanAssist - On-demand refresh scheduled ({0}): {1}", origin, item.Name);
            return true;
        }

        /// <summary>
        /// 本地修复 <c>ProviderIds["DoubanRating"]</c>：只读评分快照，不发任何豆瓣请求。
        /// </summary>
        public void RepairRatingProviderIds(IEnumerable<BaseItem> items)
        {
            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();

            if (!options.IsDoubanDataEnabled || items == null) return;

            var writeKey = options.DoubanAssist.DoubanRatingToKodi;
            var repaired = 0;
            var cleared = 0;

            foreach (var item in items)
            {
                try
                {
                    var current = item.GetProviderId(DoubanConstants.RatingProviderId);

                    if (!writeKey)
                    {
                        if (string.IsNullOrEmpty(current)) continue;

                        item.ProviderIds.Remove(DoubanConstants.RatingProviderId);
                        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                        cleared++;
                        continue;
                    }

                    var subjectId = item.GetProviderId(DoubanConstants.ProviderId);
                    var snapshotKey = !string.IsNullOrEmpty(subjectId)
                        ? subjectId
                        : GetImdbSnapshotKey(item.GetProviderId(MetadataProviders.Imdb));
                    if (string.IsNullOrEmpty(snapshotKey)) continue;
                    if (!TryGetSnapshotRating(snapshotKey, out var rating)) continue;

                    var desired = rating.ToString("0.#", CultureInfo.InvariantCulture);
                    if (string.Equals(current, desired, StringComparison.Ordinal)) continue;

                    item.ProviderIds[DoubanConstants.RatingProviderId] = desired;
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    repaired++;
                }
                catch (Exception e)
                {
                    _logger.Debug("DoubanAssist - Rating ProviderId repair failed for {0}: {1}", item.Name, e.Message);
                }
            }

            if (repaired > 0 || cleared > 0)
            {
                _logger.Info("DoubanAssist - Rating ProviderId repaired: {0}, cleared: {1}", repaired, cleared);
            }
        }

        /// <summary>
        /// 标记某豆瓣条目对应的 <c>CommunityRating</c> 是否已由豆瓣评分写入。
        /// 全量填充时逐条调用，写盘按时间节流，收尾由 <see cref="FlushRatings"/> 兜底。
        /// </summary>
        private void MarkRatingApplied(string subjectId, bool applied)
        {
            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                if (!_ratings.TryGetValue(subjectId, out var snapshot)) return;

                var writtenSource = applied ? "douban" : null;
                var everChanged = applied && !snapshot.EverApplied;

                if (applied)
                {
                    snapshot.EverApplied = true;
                }

                if (!everChanged && snapshot.Applied == applied && snapshot.WrittenSource == writtenSource) return;

                snapshot.Applied = applied;
                snapshot.WrittenSource = writtenSource;

                if (DateTime.UtcNow - _lastRatingsSave < RatingsSaveThrottle) return;
                _lastRatingsSave = DateTime.UtcNow;
                SaveRatings();
            }
        }

        /// <summary>快照是否曾被豆瓣写入过社区评分；旧版快照用 Applied/WrittenSource 兼容推断。</summary>
        private bool HasRatingEverApplied(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            lock (_ratingsLock)
            {
                if (_ratings == null) LoadRatings();

                if (!_ratings.TryGetValue(key, out var snapshot)) return false;

                return snapshot.EverApplied || snapshot.Applied == true ||
                       string.Equals(snapshot.WrittenSource, "douban", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>社区评分被 TMDB / IMDb 接管或清空时，复位豆瓣快照的应用标记。</summary>
        public void InvalidateRatingApplied(BaseItem item)
        {
            if (item == null) return;

            var subjectId = item.GetProviderId(DoubanConstants.ProviderId);
            if (!string.IsNullOrEmpty(subjectId)) MarkRatingApplied(subjectId, false);

            var imdbKey = GetImdbSnapshotKey(item.GetProviderId(MetadataProviders.Imdb));
            if (!string.IsNullOrEmpty(imdbKey)) MarkRatingApplied(imdbKey, false);
        }

        /// <summary>强制把评分快照落盘（批量任务收尾用）。</summary>
        public void FlushRatings()
        {
            lock (_ratingsLock)
            {
                if (_ratings == null) return;

                SaveRatings();
                _lastRatingsSave = DateTime.UtcNow;
            }
        }

        private void LoadRatings()
        {
            _ratings = new Dictionary<string, DoubanRatingSnapshot>();

            try
            {
                if (File.Exists(_ratingsPath))
                {
                    var persisted = _jsonSerializer.DeserializeFromFile<Dictionary<string, DoubanRatingSnapshot>>(_ratingsPath);
                    if (persisted != null) _ratings = persisted;
                }
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Ratings load failed: {0}", e.Message);
            }
        }

        private void SaveRatings()
        {
            try
            {
                _jsonSerializer.SerializeToFile(_ratings, _ratingsPath);
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanAssist - Ratings save failed: {0}", e.Message);
            }
        }

        /// <summary>
        /// 全库统一社区评分：优先豆瓣条目分，缺失时回退 IMDb 第三方 API。
        /// 手动任务处理全库，不受评分回看期限制。
        /// </summary>
        public async Task<DoubanUnifyReport> UnifyRatingsAsync(IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var report = new DoubanUnifyReport();
            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();

            if (!options.IsDoubanRatingSource)
            {
                _logger.Warn("DoubanUnify - skipped: community rating source is not Douban");
                progress?.Report(100);
                return report;
            }

            if (Plugin.LibraryManager == null)
            {
                _logger.Warn("DoubanUnify - skipped: library manager not ready");
                progress?.Report(100);
                return report;
            }

            var toKodi = options.DoubanAssist.DoubanRatingToKodi;

            var items = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Movie), nameof(Series) }
            });

            report.Total = items.Length;
            _logger.Info($"DoubanUnify - items={items.Length}");

            var index = 0;

            try
            {
                foreach (var item in items)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        index++;
                        progress?.Report(items.Length == 0 ? 100 : index * 100.0 / items.Length);

                        if (CommunityRatingService.IsRatingLocked(item))
                        {
                            report.Locked++;
                            continue;
                        }

                        var doubanId = item.GetProviderId(DoubanConstants.ProviderId);

                        DoubanSubject subject = null;

                        // ① 条目本身已匹配豆瓣：优先用豆瓣条目分（subject 缓存命中时不发请求）。
                        if (!string.IsNullOrEmpty(doubanId))
                        {
                            subject = await _fetchSubject(doubanId, cancellationToken).ConfigureAwait(false);
                        }

                        // ② 条目无评分（或没有豆瓣 ID）：统一走 IMDb 快照 / legacy 兜底。
                        var resolution = await ResolveRatingAsync(item, doubanId, subject?.Rating, subject?.Votes,
                            cancellationToken).ConfigureAwait(false);

                        if (resolution.Status == DoubanRatingClient.DoubanLightStatus.Failed)
                        {
                            report.Failed++;
                            continue;
                        }

                        var rating = resolution.Status == DoubanRatingClient.DoubanLightStatus.Ok
                            ? resolution.Rating
                            : (double?)null;
                        var snapshotKey = resolution.Status == DoubanRatingClient.DoubanLightStatus.Ok
                            ? resolution.SnapshotKey
                            : null;

                        if (!rating.HasValue)
                        {
                            if (options.CommunityRating.CommunityRatingClearWhenMissing && ClearMainRating(item))
                            {
                                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                                report.Cleared++;
                            }
                            else if (resolution.Status == DoubanRatingClient.DoubanLightStatus.NoId)
                            {
                                report.NoId++;
                            }
                            else
                            {
                                report.NoRating++;
                            }

                            continue;
                        }

                        var target = (float)Math.Round(rating.Value, 1);
                        var changed = false;

                        if (!item.CommunityRating.HasValue ||
                            Math.Abs(item.CommunityRating.Value - target) > 0.05f)
                        {
                            item.CommunityRating = target;
                            changed = true;
                        }

                        if (!string.Equals(item.GetProviderId(CommunityRatingService.SourceProviderId), "douban",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            item.ProviderIds[CommunityRatingService.SourceProviderId] = "douban";
                            changed = true;
                        }

                        if (SyncRatingProviderId(item, rating, toKodi))
                        {
                            changed = true;
                        }

                        if (!string.IsNullOrEmpty(snapshotKey))
                        {
                            MarkRatingApplied(snapshotKey, true);
                        }

                        if (!changed)
                        {
                            report.Same++;
                            continue;
                        }

                        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                        report.Updated++;

                        if (report.Samples.Count < 20)
                        {
                            report.Samples.Add($"update | {item.Name} | {item.InternalId}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("DoubanUnify - cancelled");
                        throw;
                    }
                    catch (Exception e)
                    {
                        report.Failed++;
                        if (report.Failed <= 10)
                        {
                            _logger.Warn($"DoubanUnify - {item.Name}: {e.Message}");
                        }
                    }
                }
            }
            finally
            {
                FlushRatings();
            }

            _logger.Info(report.BuildSummary());
            return report;
        }
    }

    /// <summary>单条评分应用结果，供元数据服务更新扫库统计。</summary>
    public sealed class DoubanRatingApplyResult
    {
        public bool Changed { get; set; }
        public bool Applied { get; set; }
        public bool SkippedLookback { get; set; }
        public bool SkippedSource { get; set; }
    }
}
