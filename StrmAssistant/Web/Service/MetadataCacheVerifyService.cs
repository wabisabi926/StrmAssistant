using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// 元数据缓存对账：只读扫描 cache/override/patches，报告文件数、过期项、
    /// 无效 JSON、媒体库里不存在的 TMDB 目录，以及库里没有被动缓存的条目。
    /// 不删除、不修改任何文件。
    /// </summary>
    public class MetadataCacheVerifyService : BaseApiService
    {
        private const int MaxSamples = 50;

        public class ScopeInfo
        {
            public int Folders { get; set; }
            public int Files { get; set; }
            public long Bytes { get; set; }
            public int ExpiredFiles { get; set; }
            public int InvalidFiles { get; set; }
            public List<string> Orphans { get; set; } = new List<string>();
            public List<string> ExpiredSamples { get; set; } = new List<string>();
            public List<string> InvalidSamples { get; set; } = new List<string>();
        }

        public class MetadataCacheVerifyResponse
        {
            public bool Enabled { get; set; }
            public string Root { get; set; }
            public int TtlDays { get; set; }
            public int LibraryMovieIds { get; set; }
            public int LibrarySeriesIds { get; set; }
            public ScopeInfo Cache { get; set; } = new ScopeInfo();
            public ScopeInfo Override { get; set; } = new ScopeInfo();
            public ScopeInfo Patches { get; set; } = new ScopeInfo();
            public List<string> MissingMovieCache { get; set; } = new List<string>();
            public List<string> MissingSeriesCache { get; set; } = new List<string>();
            public string Error { get; set; }
        }

        public object Get(GetMetadataCacheVerify request)
        {
            var response = new MetadataCacheVerifyResponse();

            try
            {
                var options = Plugin.Instance.MetadataEnhanceStore.GetOptions().MetadataCache;
                var root = LocalMetadataCache.Root ?? options.MetadataCacheRootFolder;

                response.TtlDays = options.MetadataCacheTtlDays;
                response.Root = root;
                response.Enabled = options.IsMetadataCacheEnabled &&
                                   !string.IsNullOrWhiteSpace(root) &&
                                   Directory.Exists(root);
                if (!response.Enabled) return response;

                var movieIds = GetTmdbIds(typeof(Movie));
                var seriesIds = GetTmdbIds(typeof(Series));
                response.LibraryMovieIds = movieIds.Count;
                response.LibrarySeriesIds = seriesIds.Count;

                var now = DateTime.UtcNow;
                response.Cache = ScanScope(root, "cache", true, response.TtlDays, now, movieIds, seriesIds);
                response.Override = ScanScope(root, "override", false, 0, now, movieIds, seriesIds);
                response.Patches = ScanScope(root, "patches", false, 0, now, movieIds, seriesIds);

                var cacheMovieIds = GetFolderIds(root, "cache", "tmdb-movies2");
                var cacheSeriesIds = GetFolderIds(root, "cache", "tmdb-tv");
                response.MissingMovieCache = movieIds
                    .Where(id => !cacheMovieIds.Contains(id))
                    .Take(MaxSamples)
                    .ToList();
                response.MissingSeriesCache = seriesIds
                    .Where(id => !cacheSeriesIds.Contains(id))
                    .Take(MaxSamples)
                    .ToList();
            }
            catch (Exception e)
            {
                response.Error = e.Message;
            }

            return response;
        }

        private static ScopeInfo ScanScope(string root, string scope, bool isPassive, int ttlDays,
            DateTime now, HashSet<string> movieIds, HashSet<string> seriesIds)
        {
            var info = new ScopeInfo();
            var scopePath = Path.Combine(root, scope);
            if (!Directory.Exists(scopePath)) return info;

            foreach (var typeDir in new[] { "tmdb-movies2", "tmdb-tv", "tmdb-collections" })
            {
                var typePath = Path.Combine(scopePath, typeDir);
                if (!Directory.Exists(typePath)) continue;

                foreach (var folder in Directory.EnumerateDirectories(typePath))
                {
                    var id = Path.GetFileName(folder);
                    info.Folders++;

                    if (typeDir == "tmdb-movies2" && !movieIds.Contains(id))
                    {
                        AddSample(info.Orphans, $"{scope}/{typeDir}/{id}");
                    }
                    else if (typeDir == "tmdb-tv" && !seriesIds.Contains(id))
                    {
                        AddSample(info.Orphans, $"{scope}/{typeDir}/{id}");
                    }

                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        info.Files++;

                        try
                        {
                            info.Bytes += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // 文件可能在扫描时被删除
                        }

                        if (isPassive && ttlDays > 0)
                        {
                            try
                            {
                                if (LocalMetadataCache.IsExpired(File.GetLastWriteTimeUtc(file), now, ttlDays))
                                {
                                    info.ExpiredFiles++;
                                    AddSample(info.ExpiredSamples, $"{scope}/{typeDir}/{id}/{Path.GetFileName(file)}");
                                }
                            }
                            catch
                            {
                                // 时间戳读取失败时不计入过期
                            }
                        }

                        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            using var _ = JsonDocument.Parse(File.ReadAllText(file));
                        }
                        catch
                        {
                            info.InvalidFiles++;
                            AddSample(info.InvalidSamples, $"{scope}/{typeDir}/{id}/{Path.GetFileName(file)}");
                        }
                    }
                }
            }

            return info;
        }

        private static HashSet<string> GetFolderIds(string root, string scope, string typeDir)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var path = Path.Combine(root, scope, typeDir);
            if (!Directory.Exists(path)) return ids;

            foreach (var folder in Directory.EnumerateDirectories(path))
            {
                var id = Path.GetFileName(folder);
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }

            return ids;
        }

        private static HashSet<string> GetTmdbIds(Type itemType)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var items = Plugin.LibraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { itemType.Name }
            });

            foreach (var item in items)
            {
                if (item.ProviderIds == null) continue;
                foreach (var pair in item.ProviderIds)
                {
                    if (string.Equals(pair.Key, "Tmdb", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        ids.Add(pair.Value.Trim());
                    }
                }
            }

            return ids;
        }

        private static void AddSample(List<string> list, string value)
        {
            if (list.Count < MaxSamples) list.Add(value);
        }
    }
}
