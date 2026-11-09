using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 社区评分统一：把 <see cref="BaseItem.CommunityRating"/> 固定为某一个来源（TMDB / IMDb）。
    ///
    /// 为什么需要它：Emby 只有一个 CommunityRating 字段，刮削器、豆瓣覆盖、NFO、历史数据都往里写，
    /// 谁最后写谁生效，且 Emby 不记录来源（官方文档明确说明无法识别）。本服务是唯一写入口：
    /// 按配置的来源取分、写入，并在 ProviderIds 留下来源标记，供前端与 Kodi 判断。
    ///
    /// 与豆瓣评分互斥：来源选 TMDB / IMDb 时，豆瓣评分只写 <c>DoubanRating</c>（若开启同步到 Kodi），
    /// 不再修改 <c>CommunityRating</c>；来源选豆瓣时由 <c>DoubanAssist</c> 接管主评分。
    /// </summary>
    public class CommunityRatingService
    {
        /// <summary>来源标记：tmdb / imdb。Emby 本身不记录来源，只能自己写。</summary>
        public const string SourceProviderId = "CommunityRatingSource";

        /// <summary>各来源的分数快照，供 Kodi 皮肤并存读取，与主评分互不抢占。</summary>
        public const string TmdbRatingProviderId = "TmdbRating";

        public const string ImdbRatingProviderId = "ImdbRating";

        private const string MdbListBaseUrl = "https://api.mdblist.com";
        private const int TmdbRequestIntervalMs = 120;
        private const int MdbListRequestIntervalMs = 1100;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromHours(1);
        private static readonly ConcurrentDictionary<string, RatingCacheEntry> Cache =
            new ConcurrentDictionary<string, RatingCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim TmdbThrottle = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim MdbListThrottle = new SemaphoreSlim(1, 1);
        private static DateTimeOffset _lastTmdbRequest = DateTimeOffset.MinValue;
        private static DateTimeOffset _lastMdbListRequest = DateTimeOffset.MinValue;

        private static ILogger Logger => Plugin.Instance?.Logger;

        private sealed class RatingCacheEntry
        {
            public double? Value { get; set; }
            public int Votes { get; set; }
            public LookupStatus Status { get; set; }
            public DateTimeOffset Expiry { get; set; }
        }

        private sealed class TmdbRatingResponse
        {
            public double? vote_average { get; set; }
            public int? vote_count { get; set; }
        }

        private sealed class MdbListResponse
        {
            public List<MdbListRating> ratings { get; set; }
        }

        private sealed class MdbListRating
        {
            public string source { get; set; }
            public double? value { get; set; }
        }

        private enum LookupStatus
        {
            Ok,
            NoId,
            NoRating,
            LowVotes,
            NotFound,
            Failed
        }

        private sealed class RatingLookup
        {
            public LookupStatus Status { get; set; }
            public double Value { get; set; }
            public int Votes { get; set; }
            public string Detail { get; set; }
        }

        /// <summary>当前是否配置了 TMDB / IMDb 统一（豆瓣与「不干预」都返回 false）。</summary>
        public static bool IsUnificationEnabled => GetRootOptions()?.CommunityRating?.IsUnificationEnabled == true;

        public static CommunityRatingOptions GetOptions() => GetRootOptions()?.CommunityRating;

        private static MetadataEnhanceOptions GetRootOptions()
        {
            try
            {
                return Plugin.Instance?.MetadataEnhanceStore?.GetOptions();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按当前配置扫描全部电影与剧集。apply=false 时只统计，不写库。</summary>
        public async Task<CommunityRatingUnifyReport> UnifyAllAsync(bool apply, IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var root = GetRootOptions();
            var options = root?.CommunityRating;
            var report = new CommunityRatingUnifyReport
            {
                Source = options?.ParsedSource ?? CommunityRatingSourceOption.None,
                Applied = apply
            };

            if (options == null || !options.IsUnificationEnabled)
            {
                Logger?.Info("CommunityRating - skipped: source is not TMDB/IMDb");
                progress?.Report(100);
                return report;
            }

            if (options.ParsedSource == CommunityRatingSourceOption.Imdb &&
                string.IsNullOrWhiteSpace(options.MdbListApiKey))
            {
                Logger?.Warn("CommunityRating - skipped: IMDb source requires MdbList API key");
                progress?.Report(100);
                report.Failed = 1;
                return report;
            }

            if (Plugin.LibraryManager == null)
            {
                Logger?.Warn("CommunityRating - skipped: library manager not ready");
                progress?.Report(100);
                return report;
            }

            var items = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { nameof(Movie), nameof(Series) }
            });

            report.Total = items.Length;
            Logger?.Info(
                $"CommunityRating - {(apply ? "apply" : "dry-run")} source={options.ParsedSource} items={items.Length} minVotes={options.CommunityRatingMinVotes}");

            return await UnifyAsync(items, apply, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>对给定条目批量统一评分。</summary>
        public async Task<CommunityRatingUnifyReport> UnifyAsync(IReadOnlyList<BaseItem> items, bool apply,
            IProgress<double> progress, CancellationToken cancellationToken)
        {
            var root = GetRootOptions();
            var options = root?.CommunityRating;
            var report = new CommunityRatingUnifyReport
            {
                Source = options?.ParsedSource ?? CommunityRatingSourceOption.None,
                Applied = apply,
                Total = items?.Count ?? 0
            };

            if (options == null || !options.IsUnificationEnabled || items == null || items.Count == 0)
            {
                progress?.Report(100);
                return report;
            }

            progress?.Report(0);
            var index = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                index++;
                if (index % 5 == 0 || index == items.Count)
                {
                    progress?.Report(index / (double)items.Count * 100);
                }

                try
                {
                    var outcome = await UnifyItemCoreAsync(item, options, apply, cancellationToken)
                        .ConfigureAwait(false);
                    report.Record(outcome, item);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    report.Failed++;
                    if (report.Failed <= 10)
                    {
                        Logger?.Warn($"CommunityRating - {item.Name} ({item.InternalId}): {e.Message}");
                    }
                }
            }

            progress?.Report(100);
            return report;
        }

        /// <summary>刷新元数据后对单条生效；失败不影响刷新流程。</summary>
        public async Task<bool> UnifyItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var root = GetRootOptions();
            var options = root?.CommunityRating;
            if (options == null || !options.IsUnificationEnabled || item == null) return false;

            try
            {
                var outcome = await UnifyItemCoreAsync(item, options, true, cancellationToken)
                    .ConfigureAwait(false);
                return outcome == CommunityRatingUnifyOutcome.Updated ||
                       outcome == CommunityRatingUnifyOutcome.Cleared;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger?.Debug($"CommunityRating - unify item failed for {item.Name}: {e.Message}");
                return false;
            }
        }

        private async Task<CommunityRatingUnifyOutcome> UnifyItemCoreAsync(BaseItem item,
            CommunityRatingOptions options, bool apply, CancellationToken cancellationToken)
        {
            if (item == null || item.IsVirtualItem) return CommunityRatingUnifyOutcome.NoId;

            if (IsRatingLocked(item)) return CommunityRatingUnifyOutcome.Locked;

            var lookup = await LookupAsync(item, options, cancellationToken).ConfigureAwait(false);

            switch (lookup.Status)
            {
                case LookupStatus.NoId:
                    return CommunityRatingUnifyOutcome.NoId;
                case LookupStatus.Failed:
                    return CommunityRatingUnifyOutcome.Failed;
                case LookupStatus.NotFound:
                case LookupStatus.NoRating:
                    if (!options.CommunityRatingClearWhenMissing) return CommunityRatingUnifyOutcome.NoRating;
                    return apply ? ClearRating(item) : CommunityRatingUnifyOutcome.Cleared;
                case LookupStatus.LowVotes:
                    return CommunityRatingUnifyOutcome.LowVotes;
            }

            if (!apply) return CommunityRatingUnifyOutcome.Updated;

            return ApplyRating(item, lookup, options.ParsedSource);
        }

        private async Task<RatingLookup> LookupAsync(BaseItem item,
            CommunityRatingOptions options, CancellationToken cancellationToken)
        {
            switch (options.ParsedSource)
            {
                case CommunityRatingSourceOption.Tmdb:
                    return await LookupTmdbAsync(item, options, cancellationToken).ConfigureAwait(false);
                case CommunityRatingSourceOption.Imdb:
                    return await LookupImdbAsync(item, options.MdbListApiKey, cancellationToken)
                        .ConfigureAwait(false);
                default:
                    return new RatingLookup { Status = LookupStatus.NoId, Detail = "source not set" };
            }
        }

        private async Task<RatingLookup> LookupTmdbAsync(BaseItem item, CommunityRatingOptions options,
            CancellationToken cancellationToken)
        {
            var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return new RatingLookup { Status = LookupStatus.NoId, Detail = "missing Tmdb id" };
            }

            var mediaType = item is Movie ? "movie" : item is Series ? "tv" : null;
            if (mediaType == null)
            {
                return new RatingLookup { Status = LookupStatus.NoId, Detail = "unsupported type" };
            }

            var cacheKey = $"tmdb:{mediaType}:{tmdbId.Trim()}";
            if (TryGetCache(cacheKey, out var cached)) return cached;

            var apiKey = AltMovieDbConfig.CurrentMovieDbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new RatingLookup { Status = LookupStatus.Failed, Detail = "missing Tmdb api key" };
            }

            var baseUrl = AltMovieDbConfig.CurrentMovieDbApiUrl;
            var bearer = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
            var url = $"{baseUrl}/3/{mediaType}/{Uri.EscapeDataString(tmdbId.Trim())}";
            if (!bearer) url += $"?api_key={Uri.EscapeDataString(apiKey)}";

            var requestOptions = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance?.UserAgent
            };

            if (bearer)
            {
                requestOptions.RequestHeaders["Authorization"] = "Bearer " + apiKey;
            }

            TmdbRatingResponse payload;

            try
            {
                await ThrottleAsync(TmdbThrottle, () => _lastTmdbRequest, value => _lastTmdbRequest = value,
                    TmdbRequestIntervalMs, cancellationToken).ConfigureAwait(false);

                using var response = await Plugin.HttpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return StoreFailure(cacheKey, LookupStatus.NotFound, "Tmdb 404");
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return StoreFailure(cacheKey, LookupStatus.Failed, $"Tmdb HTTP {(int)response.StatusCode}");
                }

                await using var stream = response.Content;
                payload = Plugin.JsonSerializer.DeserializeFromStream<TmdbRatingResponse>(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return StoreFailure(cacheKey, LookupStatus.Failed, "Tmdb request failed: " + e.Message);
            }

            if (payload == null || !payload.vote_average.HasValue)
            {
                return StoreFailure(cacheKey, LookupStatus.NoRating, "Tmdb no rating");
            }

            var votes = payload.vote_count ?? 0;
            if (votes <= 0)
            {
                return StoreFailure(cacheKey, LookupStatus.NoRating, "Tmdb no votes");
            }

            if (options.CommunityRatingMinVotes > 0 && votes < options.CommunityRatingMinVotes)
            {
                return StoreFailure(cacheKey, LookupStatus.LowVotes,
                    $"Tmdb votes {votes} < {options.CommunityRatingMinVotes}");
            }

            var lookup = new RatingLookup
            {
                Status = LookupStatus.Ok,
                Value = Math.Round(payload.vote_average.Value, 1),
                Votes = votes,
                Detail = $"tmdb {votes} votes"
            };

            Cache[cacheKey] = new RatingCacheEntry
            {
                Status = lookup.Status,
                Value = lookup.Value,
                Votes = votes,
                Expiry = DateTimeOffset.UtcNow.Add(CacheDuration)
            };

            return lookup;
        }

        private async Task<RatingLookup> LookupImdbAsync(BaseItem item, string apiKey,
            CancellationToken cancellationToken)
        {
            var imdbId = item.GetProviderId(MetadataProviders.Imdb);
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                return new RatingLookup { Status = LookupStatus.NoId, Detail = "missing Imdb id" };
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new RatingLookup { Status = LookupStatus.Failed, Detail = "missing MdbList api key" };
            }

            var cacheKey = $"imdb:{imdbId.Trim()}";
            if (TryGetCache(cacheKey, out var cached)) return cached;

            var url =
                $"{MdbListBaseUrl}/imdb/{Uri.EscapeDataString(imdbId.Trim())}?apikey={Uri.EscapeDataString(apiKey.Trim())}&format=json";

            var requestOptions = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance?.UserAgent
            };

            MdbListResponse payload;

            try
            {
                await ThrottleAsync(MdbListThrottle, () => _lastMdbListRequest, value => _lastMdbListRequest = value,
                    MdbListRequestIntervalMs, cancellationToken).ConfigureAwait(false);

                using var response = await Plugin.HttpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return StoreFailure(cacheKey, LookupStatus.NotFound, "MdbList 404");
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return StoreFailure(cacheKey, LookupStatus.Failed, $"MdbList HTTP {(int)response.StatusCode}");
                }

                await using var stream = response.Content;
                payload = Plugin.JsonSerializer.DeserializeFromStream<MdbListResponse>(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return StoreFailure(cacheKey, LookupStatus.Failed, "MdbList request failed: " + e.Message);
            }

            var imdb = payload?.ratings?
                .FirstOrDefault(r => r.value.HasValue &&
                                     (r.source ?? string.Empty).IndexOf("imdb", StringComparison.OrdinalIgnoreCase) >= 0)
                ?.value;

            if (!imdb.HasValue)
            {
                return StoreFailure(cacheKey, LookupStatus.NoRating, "MdbList no IMDb rating");
            }

            var lookup = new RatingLookup
            {
                Status = LookupStatus.Ok,
                Value = Math.Round(imdb.Value, 1),
                Detail = "imdb via MdbList"
            };

            Cache[cacheKey] = new RatingCacheEntry
            {
                Status = lookup.Status,
                Value = lookup.Value,
                Expiry = DateTimeOffset.UtcNow.Add(CacheDuration)
            };

            return lookup;
        }

        private static CommunityRatingUnifyOutcome ApplyRating(BaseItem item, RatingLookup lookup,
            CommunityRatingSourceOption source)
        {
            var changed = false;
            var target = (float)lookup.Value;

            if (!item.CommunityRating.HasValue || Math.Abs(item.CommunityRating.Value - target) > 0.05f)
            {
                item.CommunityRating = target;
                changed = true;
            }

            var sourceText = source == CommunityRatingSourceOption.Tmdb ? "tmdb" : "imdb";
            if (!string.Equals(item.GetProviderId(SourceProviderId), sourceText,
                    StringComparison.OrdinalIgnoreCase))
            {
                item.ProviderIds[SourceProviderId] = sourceText;
                changed = true;
            }

            var ratingKey = source == CommunityRatingSourceOption.Tmdb ? TmdbRatingProviderId : ImdbRatingProviderId;
            var ratingText = lookup.Value.ToString("0.#", CultureInfo.InvariantCulture);
            if (!string.Equals(item.GetProviderId(ratingKey), ratingText, StringComparison.Ordinal))
            {
                item.ProviderIds[ratingKey] = ratingText;
                changed = true;
            }

            // 切换主评分来源时清掉另一来源的快照，避免 Kodi/前端读到旧值。
            // DoubanRating 是独立的第三评分载体，不在这里清理。
            var staleKey = source == CommunityRatingSourceOption.Tmdb
                ? ImdbRatingProviderId
                : TmdbRatingProviderId;
            if (item.ProviderIds.Remove(staleKey)) changed = true;

            if (!changed) return CommunityRatingUnifyOutcome.Same;

            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            // 主评分已被 TMDB / IMDb 接管，豆瓣快照的「已应用」标记必须复位，
            // 否则前端仍会凭旧标记显示豆瓣图标。
            Plugin.DoubanAssist?.InvalidateRatingApplied(item);
            return CommunityRatingUnifyOutcome.Updated;
        }

        private static CommunityRatingUnifyOutcome ClearRating(BaseItem item)
        {
            var changed = false;

            if (item.CommunityRating.HasValue)
            {
                item.CommunityRating = null;
                changed = true;
            }

            // 只清主评分及其来源快照；DoubanRating 是独立的第三评分载体，
            // 由「同步豆瓣评分到 Kodi」开关管理，不随主评分清空。
            foreach (var key in new[] { SourceProviderId, TmdbRatingProviderId, ImdbRatingProviderId })
            {
                if (item.ProviderIds.Remove(key)) changed = true;
            }

            if (!changed) return CommunityRatingUnifyOutcome.Same;

            item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            Plugin.DoubanAssist?.InvalidateRatingApplied(item);
            return CommunityRatingUnifyOutcome.Cleared;
        }

        internal static bool IsRatingLocked(BaseItem item)
        {
            if (item.IsLocked) return true;

            var lockedFields = item.LockedFields;
            return lockedFields != null && lockedFields.Any(f =>
                string.Equals(f.ToString(), "CommunityRating", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryGetCache(string key, out RatingLookup lookup)
        {
            lookup = null;

            if (!Cache.TryGetValue(key, out var entry) || entry.Expiry <= DateTimeOffset.UtcNow) return false;

            lookup = new RatingLookup
            {
                Status = entry.Status,
                Value = entry.Value ?? 0,
                Votes = entry.Votes
            };

            return true;
        }

        private static RatingLookup StoreFailure(string cacheKey, LookupStatus status, string detail)
        {
            // 请求失败（网络 / 401 / 429）不缓存：用户可能刚补上 key，等一小时才恢复体验很差。
            if (status != LookupStatus.Failed)
            {
                Cache[cacheKey] = new RatingCacheEntry
                {
                    Status = status,
                    Expiry = DateTimeOffset.UtcNow.Add(FailureCacheDuration)
                };
            }

            return new RatingLookup { Status = status, Detail = detail };
        }

        private static async Task ThrottleAsync(SemaphoreSlim gate, Func<DateTimeOffset> read,
            Action<DateTimeOffset> write, int intervalMs, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var wait = TimeSpan.FromMilliseconds(intervalMs) - (DateTimeOffset.UtcNow - read());
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                write(DateTimeOffset.UtcNow);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    internal enum CommunityRatingUnifyOutcome
    {
        Updated,
        Same,
        Cleared,
        Locked,
        NoId,
        NoRating,
        LowVotes,
        Failed
    }

    /// <summary>统一评分的统计结果；<see cref="Samples"/> 只保留前若干条变化，避免日志爆炸。</summary>
    public sealed class CommunityRatingUnifyReport
    {
        private const int MaxSamples = 20;

        public CommunityRatingSourceOption Source { get; set; }
        public bool Applied { get; set; }
        public int Total { get; set; }
        public int Updated { get; set; }
        public int Same { get; set; }
        public int Cleared { get; set; }
        public int Locked { get; set; }
        public int NoId { get; set; }
        public int NoRating { get; set; }
        public int LowVotes { get; set; }
        public int Failed { get; set; }
        public List<string> Samples { get; } = new List<string>();

        internal void Record(CommunityRatingUnifyOutcome outcome, BaseItem item)
        {
            switch (outcome)
            {
                case CommunityRatingUnifyOutcome.Updated:
                    Updated++;
                    break;
                case CommunityRatingUnifyOutcome.Same:
                    Same++;
                    break;
                case CommunityRatingUnifyOutcome.Cleared:
                    Cleared++;
                    break;
                case CommunityRatingUnifyOutcome.Locked:
                    Locked++;
                    break;
                case CommunityRatingUnifyOutcome.NoId:
                    NoId++;
                    break;
                case CommunityRatingUnifyOutcome.NoRating:
                    NoRating++;
                    break;
                case CommunityRatingUnifyOutcome.LowVotes:
                    LowVotes++;
                    break;
                default:
                    Failed++;
                    break;
            }

            if (outcome == CommunityRatingUnifyOutcome.Updated || outcome == CommunityRatingUnifyOutcome.Cleared)
            {
                if (Samples.Count < MaxSamples)
                {
                    Samples.Add(
                        $"{(outcome == CommunityRatingUnifyOutcome.Cleared ? "clear" : "update")} | {item.Name} | {item.InternalId}");
                }
            }
        }

        public string BuildSummary()
        {
            return string.Format(
                "CommunityRating - {0} source={1} total={2} {3}={4} same={5} cleared={6} locked={7} noId={8} noRating={9} lowVotes={10} failed={11}",
                Applied ? "apply" : "dry-run", Source, Total, Applied ? "updated" : "wouldUpdate", Updated,
                Same, Cleared, Locked, NoId, NoRating, LowVotes, Failed);
        }
    }
}
