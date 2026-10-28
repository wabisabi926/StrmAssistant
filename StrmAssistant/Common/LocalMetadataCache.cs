using MediaBrowser.Common.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 本地 TMDB 语义化缓存（v2）：把 MovieDb 的元数据请求按 TMDB 实体（电影/剧集/季/集/合集）
    /// 映射到统一目录布局，实现「override 优先 + 被动缓存」。
    ///
    /// 目录布局（可直接复用既有的 override 资源）：
    ///   &lt;root&gt;\override\tmdb-movies2\&lt;tmdbid&gt;\all.json          （覆盖缓存，恒 P1，用户自定义）
    ///   &lt;root&gt;\override\tmdb-tv\&lt;tmdbid&gt;\series.json
    ///   &lt;root&gt;\override\tmdb-tv\&lt;tmdbid&gt;\season-&lt;n&gt;.json
    ///   &lt;root&gt;\override\tmdb-tv\&lt;tmdbid&gt;\season-&lt;s&gt;-episode-&lt;e&gt;.json
    ///   &lt;root&gt;\override\tmdb-collections\&lt;tmdbid&gt;\all.json
    ///   &lt;root&gt;\cache\&lt;同上&gt;                                      （被动缓存，读写）
    ///   &lt;root&gt;\patches\&lt;同上&gt;                                    （合并补丁，v3 使用）
    ///
    /// URL 映射（MovieDb 元数据出口的 URL 特征）：
    ///   /3/movie/{id}                        -> tmdb-movies2 / all.json
    ///   /3/tv/{id}                           -> tmdb-tv / series.json
    ///   /3/tv/{id}/season/{n}                -> tmdb-tv / season-{n}.json
    ///   /3/tv/{id}/season/{s}/episode/{e}    -> tmdb-tv / season-{s}-episode-{e}.json
    ///   /3/collection/{id}                   -> tmdb-collections / all.json
    ///
    /// 注意：缓存按实体落盘、不区分 language，与用户固定的备选语言顺序一致；
    /// 若需多语言并存，需在 v3 引入语言后缀。
    /// </summary>
    public static class LocalMetadataCache
    {
        private static readonly object _lock = new object();
        private static string _root;

        /// <summary>
        /// 只匹配**完整**的实体资源路径（锚定结尾）。
        ///
        /// ⚠️ 不能只匹配前缀：/3/movie/123 与 /3/movie/123/credits 会落到同一个 all.json，
        /// 详情响应被当成演职员响应喂给 Emby（反之亦然）。未列入白名单的子资源一律放行、不缓存。
        /// </summary>
        private static readonly Regex EntityPath = new Regex(
            @"^/3/(movie|tv|collection)/(\d+)(?:/season/(\d+)(?:/episode/(\d+))?)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 影响响应结构的查询参数：不隔离会让同一个文件承载多种 JSON 结构。
        /// append_to_response 是 Emby 的 MovieDb provider 最常用的参数，必须进键。
        /// </summary>
        private static readonly string[] VariantQueryKeys =
        {
            "append_to_response", "language", "include_image_language"
        };

        public static string Root => _root;

        public sealed class CacheEntry
        {
            public string TypeDir { get; set; }
            public string TmdbId { get; set; }
            public string FileName { get; set; }

            public string RelativePath => Path.Combine(TypeDir, TmdbId, FileName);
        }

        /// <summary>
        /// 只校验（无副作用，不创建目录）。返回 ok 与错误码：
        /// EMPTY / INVALID / ROOT_NOT_ALLOWED / NOT_WRITABLE。
        /// 目录不存在但父目录存在时视为 ok（初始化阶段会自动创建）。
        /// </summary>
        public static (bool ok, string code) Validate(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return (false, "EMPTY");

            string full;
            try
            {
                full = Path.GetFullPath(root.Trim());
            }
            catch
            {
                return (false, "INVALID");
            }

            var trimmed = full.TrimEnd('\\', '/');
            if (string.IsNullOrEmpty(trimmed)) return (false, "INVALID");

            // 盘符根
            if (string.Equals(trimmed, Path.GetPathRoot(full)?.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
                return (false, "ROOT_NOT_ALLOWED");

            // Windows 系统目录
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(winDir) && trimmed.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
                return (false, "ROOT_NOT_ALLOWED");

            // Emby programdata 本身（不允许把数据目录当缓存根）
            try
            {
                var dataPath = Plugin.Instance?.ApplicationPaths?.ProgramDataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var dataFull = Path.GetFullPath(dataPath).TrimEnd('\\', '/');
                    if (string.Equals(trimmed, dataFull, StringComparison.OrdinalIgnoreCase))
                        return (false, "ROOT_NOT_ALLOWED");
                }
            }
            catch
            {
                // 忽略
            }

            if (Directory.Exists(full))
            {
                // 已存在：做一次轻量可写探测，失败则如实报告
                try
                {
                    var probe = Path.Combine(full, ".strmassistant_write_test");
                    File.WriteAllText(probe, string.Empty);
                    File.Delete(probe);
                }
                catch
                {
                    return (false, "NOT_WRITABLE");
                }

                return (true, null);
            }

            // 不存在：父目录必须存在（否则无法创建），初始化阶段会创建
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return (false, "INVALID");

            return (true, null);
        }

        /// <summary>
        /// 校验并初始化缓存根目录，创建所需子目录。返回是否可用及原因。
        /// </summary>
        public static (bool ok, string error) ValidateAndInitialize(string root)
        {
            try
            {
                var (ok, code) = Validate(root);
                if (!ok) return (false, code);

                var full = Path.GetFullPath(root.Trim());

                if (!Directory.Exists(full))
                    Directory.CreateDirectory(full);

                foreach (var scope in new[] { "cache", "override", "patches" })
                {
                    foreach (var type in new[] { "tmdb-movies2", "tmdb-tv", "tmdb-collections" })
                    {
                        Directory.CreateDirectory(Path.Combine(full, scope, type));
                    }
                }

                lock (_lock) { _root = full; }
                return (true, null);
            }
            catch (Exception e)
            {
                return (false, e.Message);
            }
        }

        public static void Reset()
        {
            lock (_lock) { _root = null; }
        }

        /// <summary>
        /// 清空**被动缓存**（<c>&lt;root&gt;\cache</c>），保留用户 <c>override</c> 与合并补丁 <c>patches</c>。
        ///
        /// 用途：TMDB 侧修正了某部片的信息后，希望下次刮削立刻拿到新数据，而不是等缓存命中旧值。
        /// 只删文件、顺带清理空目录，但保留 <c>cache</c> 根与三个类型目录（避免下次写入前目录缺失）。
        /// 返回删除的文件数；根目录未初始化返回 -1。
        /// </summary>
        public static int ClearPassiveCache()
        {
            var root = _root;
            if (string.IsNullOrEmpty(root)) return -1;

            var cacheDir = Path.Combine(root, "cache");
            if (!Directory.Exists(cacheDir)) return 0;

            var removed = 0;

            lock (_lock)
            {
                foreach (var file in Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception)
                    {
                        // 单个文件被占用：跳过，下次启动再试
                    }
                }

                // 由深到浅删空目录（字符串长度倒序近似层级倒序）
                foreach (var dir in Directory.EnumerateDirectories(cacheDir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                }
            }

            return removed;
        }

        /// <summary>
        /// 把 MovieDb 元数据请求 URL 解析为 TMDB 实体条目；非实体请求（人物/搜索/配置/子资源）返回 false。
        /// </summary>
        public static bool TryParseEntity(string url, out CacheEntry entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

                // 只认白名单内的完整资源路径；/credits、/images、/external_ids 等子资源一律放行
                var match = EntityPath.Match(uri.AbsolutePath);
                if (!match.Success) return false;

                var kind = match.Groups[1].Value.ToLowerInvariant();
                var id = match.Groups[2].Value;
                var season = match.Groups[3].Success ? match.Groups[3].Value : null;
                var episode = match.Groups[4].Success ? match.Groups[4].Value : null;

                string typeDir, fileName;
                switch (kind)
                {
                    case "movie":
                        typeDir = "tmdb-movies2";
                        fileName = "all.json";
                        break;
                    case "collection":
                        typeDir = "tmdb-collections";
                        fileName = "all.json";
                        break;
                    default: // tv
                        typeDir = "tmdb-tv";
                        if (episode != null && season != null)
                            fileName = $"season-{season}-episode-{episode}.json";
                        else if (season != null)
                            fileName = $"season-{season}.json";
                        else
                            fileName = "series.json";
                        break;
                }

                // 影响响应结构的参数必须隔离，否则 append_to_response 的多种结构会互相覆盖
                var variant = BuildVariantSuffix(uri.Query);
                if (variant.Length > 0)
                {
                    fileName = fileName.Substring(0, fileName.Length - ".json".Length) + "." + variant + ".json";
                }

                entry = new CacheEntry { TypeDir = typeDir, TmdbId = id, FileName = fileName };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把影响响应结构的查询参数规范化后取短哈希，作为文件名后缀。
        /// 只取白名单参数（api_key / page 等不影响实体响应结构的不参与），保证键稳定。
        /// </summary>
        private static string BuildVariantSuffix(string query)
        {
            if (string.IsNullOrEmpty(query)) return string.Empty;

            var parts = query.TrimStart('?')
                .Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('='))
                .Where(kv => kv.Length == 2)
                .Where(kv => VariantQueryKeys.Any(k => kv[0].Equals(k, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv[0].ToLowerInvariant() + "=" + Uri.UnescapeDataString(kv[1]).ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            if (parts.Count == 0) return string.Empty;

            using var sha = System.Security.Cryptography.SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("&", parts)));

            var sb = new StringBuilder(6);
            for (var i = 0; i < 3; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        private static string BuildPath(string scope, CacheEntry entry)
        {
            var root = _root;
            if (string.IsNullOrEmpty(root) || entry == null) return null;
            return Path.Combine(root, scope, entry.TypeDir, entry.TmdbId, entry.FileName);
        }

        public static string GetOverrideFilePath(CacheEntry entry) => BuildPath("override", entry);
        public static string GetPassiveFilePath(CacheEntry entry) => BuildPath("cache", entry);
        public static string GetPatchFilePath(CacheEntry entry) => BuildPath("patches", entry);

        private static bool TryRead(string path, out byte[] bytes)
        {
            bytes = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                bytes = File.ReadAllBytes(path);
                return bytes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryReadOverride(CacheEntry entry, out byte[] bytes) =>
            TryRead(GetOverrideFilePath(entry), out bytes);

        public static bool TryReadPassive(CacheEntry entry, out byte[] bytes) =>
            TryRead(GetPassiveFilePath(entry), out bytes);

        public static void WritePassive(CacheEntry entry, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            try
            {
                var path = GetPassiveFilePath(entry);
                if (string.IsNullOrEmpty(path)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                // 缓存写入失败不影响主流程
            }
        }

        public static byte[] ReadAll(Stream stream)
        {
            if (stream == null) return null;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// 合并补丁：若存在 <c>patches/&lt;type&gt;/&lt;tmdbid&gt;/&lt;file&gt;.json</c>，
        /// 则把其顶层字段合并到 base 之上（部分信息覆盖，如标题/简介）。无补丁或失败时原样返回。
        /// 顶层字段合并为幂等操作，故「读缓存后再合并」不会产生漂移。
        /// </summary>
        public static byte[] ApplyPatches(CacheEntry entry, byte[] baseBytes)
        {
            if (entry == null || baseBytes == null || baseBytes.Length == 0) return baseBytes;

            var patchPath = GetPatchFilePath(entry);

            try
            {
                if (string.IsNullOrEmpty(patchPath) || !File.Exists(patchPath)) return baseBytes;

                var serializer = Plugin.JsonSerializer;
                if (serializer == null) return baseBytes;

                var patchText = File.ReadAllText(patchPath);
                if (string.IsNullOrWhiteSpace(patchText)) return baseBytes;

                var baseObj = serializer.DeserializeFromString<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(baseBytes));
                var patchObj = serializer.DeserializeFromString<Dictionary<string, object>>(patchText);

                if (baseObj == null ||
                    patchObj == null) return baseBytes;

                foreach (var kv in patchObj)
                {
                    baseObj[kv.Key] = kv.Value;
                }

                var merged = serializer.SerializeToString(baseObj);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"LocalTmdbCache - PATCH applied ({patchObj.Count} field(s)) {entry.RelativePath}");
                }

                return Encoding.UTF8.GetBytes(merged);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("LocalTmdbCache patch merge failed: " + e.Message);
                return baseBytes;
            }
        }

        /// <summary>
        /// 用缓存的字节构造一个可供 MovieDb 消费的 HttpResponseInfo。
        /// </summary>
        public static HttpResponseInfo BuildResponse(byte[] bytes, string url)
        {
            return new HttpResponseInfo
            {
                Content = new MemoryStream(bytes, false),
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                ContentLength = bytes?.LongLength ?? 0,
                ResponseUrl = url
            };
        }
    }
}
