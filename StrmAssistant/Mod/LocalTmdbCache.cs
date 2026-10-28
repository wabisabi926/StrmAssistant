using HarmonyLib;
using MediaBrowser.Common.Net;
using StrmAssistant.Common;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 本地 TMDB 缓存（v2，语义化）：在 MovieDb 的元数据 HTTP 唯一出口
    /// <c>MovieDbProviderBase.GetMovieDbResponse(HttpRequestOptions)</c> 上按 TMDB 实体读写磁盘缓存。
    ///
    /// 读取优先级（见 <see cref="LocalMetadataCache"/>）：
    ///   1. override 覆盖缓存（恒最高优先级，用户自定义）
    ///   2. cache 被动缓存（仅「优先使用缓存 / 仅用覆盖缓存」模式读取）
    ///   3. 在线请求（未命中时）
    /// 得到基础数据后叠加 patches/ 合并补丁（部分字段覆盖）。
    /// 写入：仅「优先使用缓存 / 在线并缓存」模式把最终结果写入 cache 被动缓存。
    ///
    /// 仅当 <see cref="Options.MetadataEnhanceOptions.IsMetadataCacheEnabled"/> 为真时才打补丁，
    /// 默认（模式 None）完全不介入，保证零行为变化。
    /// 图像 API（GetImageResponse，不带 language 参数）留待后续。
    /// </summary>
    public class LocalTmdbCache : PatchBase<LocalTmdbCache>
    {
        private static MethodInfo _getMovieDbResponse;

        public LocalTmdbCache()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().MetadataCache.IsMetadataCacheEnabled)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                var movieDbAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "MovieDb");
                if (movieDbAssembly == null)
                {
                    Plugin.Instance.Logger.Info("LocalTmdbCache - MovieDb plugin not loaded, skip");
                    return;
                }

                var providerBase = movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _getMovieDbResponse = providerBase?.GetMethod("GetMovieDbResponse",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new[] { typeof(HttpRequestOptions) }, null);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"LocalTmdbCache: GetMovieDbResponse={_getMovieDbResponse != null}");
                }

                if (_getMovieDbResponse == null)
                {
                    Plugin.Instance.Logger.Warn(
                        "LocalTmdbCache - MovieDbProviderBase.GetMovieDbResponse not found; local TMDB cache inactive");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("LocalTmdbCache init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getMovieDbResponse,
                prefix: nameof(GetMovieDbResponsePrefix), postfix: nameof(GetMovieDbResponsePostfix));
        }

        [HarmonyPrefix]
        private static bool GetMovieDbResponsePrefix(HttpRequestOptions options, ref Task<HttpResponseInfo> __result)
        {
            try
            {
                if (options?.Url is not { Length: > 0 } url) return true;
                if (!Plugin.Instance.MetadataEnhanceStore.GetOptions().MetadataCache.IsMetadataCacheReadEnabled) return true;
                if (!LocalMetadataCache.TryParseEntity(url, out var entry)) return true;

                byte[] bytes;

                // P1：override 覆盖缓存（无论模式，只要读取开启即优先）
                if (LocalMetadataCache.TryReadOverride(entry, out bytes))
                {
                    bytes = LocalMetadataCache.ApplyPatches(entry, bytes);
                    __result = Task.FromResult(LocalMetadataCache.BuildResponse(bytes, url));
                    Plugin.Instance.Logger.Debug(
                        $"LocalTmdbCache - OVERRIDE HIT {entry.RelativePath} ({bytes.Length} bytes)");
                    return false;
                }

                // P2：被动缓存
                if (LocalMetadataCache.TryReadPassive(entry, out bytes))
                {
                    bytes = LocalMetadataCache.ApplyPatches(entry, bytes);
                    __result = Task.FromResult(LocalMetadataCache.BuildResponse(bytes, url));
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug(
                            $"LocalTmdbCache - CACHE HIT {entry.RelativePath} ({bytes.Length} bytes)");
                    }

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("LocalTmdbCache prefix failed: " + ex.Message);
                return true;
            }
        }

        [HarmonyPostfix]
        private static void GetMovieDbResponsePostfix(ref Task<HttpResponseInfo> __result, HttpRequestOptions options)
        {
            try
            {
                if (__result == null || options?.Url is not { Length: > 0 } url) return;
                if (!Plugin.Instance.MetadataEnhanceStore.GetOptions().MetadataCache.IsMetadataCacheWriteEnabled) return;
                if (!LocalMetadataCache.TryParseEntity(url, out var entry)) return;

                __result = CacheAndReturn(__result, entry);
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("LocalTmdbCache postfix failed: " + ex.Message);
            }
        }

        private static async Task<HttpResponseInfo> CacheAndReturn(Task<HttpResponseInfo> inner,
            LocalMetadataCache.CacheEntry entry)
        {
            var response = await inner.ConfigureAwait(false);

            try
            {
                if (response?.Content != null)
                {
                    var bytes = LocalMetadataCache.ReadAll(response.Content);
                    if (bytes is { Length: > 0 })
                    {
                        // 先应用合并补丁，再把最终结果存入被动缓存
                        bytes = LocalMetadataCache.ApplyPatches(entry, bytes);
                        LocalMetadataCache.WritePassive(entry, bytes);
                        // 原流已被读空，返回一个新的可读流供调用方反序列化
                        response.Content = new MemoryStream(bytes, false);

                        if (Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug(
                                $"LocalTmdbCache - STORE {entry.RelativePath} ({bytes.Length} bytes)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("LocalTmdbCache cache-write failed: " + ex.Message);
            }

            return response;
        }
    }
}
