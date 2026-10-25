using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 影音直链：视频/音乐 Strm 302 直链（含 LiveTV 频道），可选跟随网盘 302 跳转，
    /// 并支持按客户端黑名单回退 Emby 中转。保存选项后即时生效。
    /// </summary>
    public class StrmDirectRedirect : PatchBase<StrmDirectRedirect>
    {
        private static MethodInfo _processRequestMethod;
        private static MethodInfo _getStateMethod;
        private static MethodInfo _disposeStateMethod;

        private static Type _videoStreamRequestType;
        private static Type _liveStreamRequestType;
        private static Type _progressiveAudioRequestType;

        private static ISessionContext _sessionContext;

        private static bool _isEnabled;
        private static bool _follow302;
        private static string[] _videoClientBlacklist = Array.Empty<string>();
        private static string[] _audioClientBlacklist = Array.Empty<string>();

        public StrmDirectRedirect()
        {
            try
            {
                Initialize();

                ApplyOptions(GetDirectLinkOptions());

                if (_isEnabled && PatchTracker.FallbackPatchApproach != PatchApproach.None)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                // 兜底：单个模块初始化失败不得影响插件加载与设置页
                try
                {
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    Plugin.Instance?.Logger?.Warn($"StrmDirectRedirect 构造失败，已禁用该功能: {e.Message}");
                }
                catch
                {
                }
            }
        }

        private static Options.DirectLinkOptions GetDirectLinkOptions()
        {
            return Plugin.Instance?.NetworkProxyStore?.GetOptions()?.DirectLink
                   ?? new Options.DirectLinkOptions();
        }

        protected override void OnInitialize()
        {
            var streamRequestType = default(Type);
            var streamStateType = default(Type);

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var assembly = Assembly.Load("Emby.Server.MediaEncoding");

                var progressiveType = assembly?.GetType("Emby.Server.MediaEncoding.Api.Progressive.BaseProgressiveStreamingService", false);
                var baseStreamingType = assembly?.GetType("Emby.Server.MediaEncoding.Api.BaseStreamingService", false);
                streamRequestType = assembly?.GetType("Emby.Server.MediaEncoding.Api.StreamRequest", false);
                streamStateType = assembly?.GetType("Emby.Server.MediaEncoding.Api.StreamState", false);
                _videoStreamRequestType = assembly?.GetType("Emby.Server.MediaEncoding.Api.VideoStreamRequest", false);
                _liveStreamRequestType = FindType("MediaBrowser.Model.MediaInfo.LiveStreamRequest");
                _progressiveAudioRequestType = assembly?.GetType("Emby.Server.MediaEncoding.Api.Progressive.GetProgressiveAudioStream", false);

                if (progressiveType != null && streamRequestType != null)
                {
                    var parameterTypes = new[] { streamRequestType, typeof(bool) };
                    _processRequestMethod = progressiveType.GetMethod("ProcessRequest", flags, null, parameterTypes, null) ??
                                            progressiveType.GetMethods(flags).FirstOrDefault(m =>
                                                m.Name == "ProcessRequest" && !m.IsStatic &&
                                                HasParameterSignature(m, parameterTypes));
                }

                if (baseStreamingType != null && streamRequestType != null)
                {
                    var parameterTypes = new[] { streamRequestType, typeof(bool), typeof(CancellationToken) };
                    _getStateMethod = baseStreamingType.GetMethod("GetState", flags, null, parameterTypes, null) ??
                                      baseStreamingType.GetMethods(flags).FirstOrDefault(m =>
                                          m.Name == "GetState" && !m.IsStatic &&
                                          HasParameterSignature(m, parameterTypes));
                }

                _disposeStateMethod = streamStateType?.GetMethod("Dispose", flags, null,
                    new[] { typeof(bool), typeof(bool) }, null);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"StrmDirectRedirect 初始化异常: {e.Message}");
            }

            if (_processRequestMethod == null || _getStateMethod == null || _disposeStateMethod == null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                Plugin.Instance.Logger.Warn("StrmDirectRedirect: 未命中 ProcessRequest/GetState/StreamState.Dispose，影音直链不可用");
                return;
            }

            Plugin.Instance.Logger.Info("StrmDirectRedirect: 影音直链目标方法已就绪");
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _processRequestMethod, prefix: nameof(ProcessRequestPrefix));
        }

        /// <summary>
        /// 保存选项后即时生效：按新选项应用/撤销补丁。
        /// </summary>
        public void ApplyOptionsAndConfigure(Options.DirectLinkOptions directLinkOptions)
        {
            ApplyOptions(directLinkOptions);

            if (PatchTracker.FallbackPatchApproach == PatchApproach.None) return;

            if (_isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        private static void ApplyOptions(Options.DirectLinkOptions options)
        {
            options ??= new Options.DirectLinkOptions();

            _isEnabled = options.EnableStrmDirectRedirect;
            _follow302 = options.StrmDirectRedirectFollow302;
            _videoClientBlacklist = ParseClientBlacklist(options.StrmVideoDirectRedirectClientBlacklist);
            _audioClientBlacklist = ParseClientBlacklist(options.StrmAudioDirectRedirectClientBlacklist);
        }

        [HarmonyPrefix]
        private static bool ProcessRequestPrefix(object __instance, object __0, ref Task<object> __result)
        {
            if (!_isEnabled || __instance == null || __0 == null) return true;

            var isVideo = (_videoStreamRequestType != null && _videoStreamRequestType.IsInstanceOfType(__0)) ||
                          (_liveStreamRequestType != null && _liveStreamRequestType.IsInstanceOfType(__0));
            var isAudio = _progressiveAudioRequestType != null && _progressiveAudioRequestType.IsInstanceOfType(__0);

            if (!isVideo && !isAudio) return true;

            var itemId = GetPropertyValue<string>(__0, "Id");

            try
            {
                bool isLiveTv;

                if (isVideo)
                {
                    if (!ResolveStrmOrTvPath(__0, out isLiveTv)) return true;
                    NormalizeOriginalRequest(__0);
                    DisableHlsSubtitleOnly(__0);
                }
                else
                {
                    isLiveTv = false;
                    if (!ResolveStrmPath(__0)) return true;
                    NormalizeOriginalRequest(__0);
                    if (!GetPropertyValue<bool>(__0, "Static")) return true;
                }

                var state = InvokeGetState(__instance, __0);
                if (!CanRedirect(state, isVideo, isLiveTv))
                {
                    DisposeState(state);
                    return true;
                }

                var resultFactory = GetPropertyValue<IHttpResultFactory>(__instance, "ResultFactory");
                var requestContext = GetPropertyValue<IRequest>(__instance, "Request");
                var mediaSource = GetPropertyValue<MediaSourceInfo>(state, "MediaSource");

                if (resultFactory == null || mediaSource == null || string.IsNullOrWhiteSpace(mediaSource.Path))
                {
                    DisposeState(state);
                    return true;
                }

                if (IsClientBlocked(requestContext, isVideo))
                {
                    DisposeState(state);
                    return true;
                }

                var finalUrl = ResolveRedirectUrl(mediaSource.Path, requestContext?.UserAgent);

                __result = Task.FromResult(resultFactory.GetRedirectResult(finalUrl));

                Plugin.Instance.Logger.Info(
                    $"StrmDirectRedirect: itemId={itemId}, type={(isVideo ? (isLiveTv ? "LIVE" : "VIDEO") : "AUDIO")}, target={UrlSanitizer.ToLogSafe(finalUrl)}");

                DisposeState(state);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"StrmDirectRedirect 预判失败，回退 Emby 中转: itemId={itemId}, error={e.Message}");
                return true;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch
                {
                    // 忽略无法反射的程序集
                }
            }

            return null;
        }

        private static bool HasParameterSignature(MethodInfo method, Type[] parameterTypes)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length) return false;

            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i]) return false;
            }

            return true;
        }

        private static bool ResolveStrmOrTvPath(object request, out bool isLiveTv)
        {
            isLiveTv = false;

            var itemId = GetPropertyValue<string>(request, "Id");
            if (string.IsNullOrWhiteSpace(itemId)) return false;

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            if (item == null) return false;

            var isLiveRequest = _liveStreamRequestType != null && _liveStreamRequestType.IsInstanceOfType(request);
            if (item.GetType().Name == "LiveTvChannel" || isLiveRequest)
            {
                isLiveTv = true;
                return true;
            }

            return LibraryApi.IsFileShortcut(item.Path ?? item.FileName);
        }

        private static bool ResolveStrmPath(object request)
        {
            var itemId = GetPropertyValue<string>(request, "Id");
            if (string.IsNullOrWhiteSpace(itemId)) return false;

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            var path = item?.Path ?? item?.FileName;

            return LibraryApi.IsFileShortcut(path);
        }

        private static void NormalizeOriginalRequest(object request)
        {
            var streamFileName = GetPropertyValue<string>(request, "StreamFileName");
            if (string.IsNullOrEmpty(streamFileName)) return;

            if (string.Equals(Path.GetFileNameWithoutExtension(streamFileName), "original",
                StringComparison.OrdinalIgnoreCase))
            {
                SetPropertyValue(request, "Static", true);
            }

            if (string.IsNullOrEmpty(GetPropertyValue<string>(request, "Container")))
            {
                var extension = Path.GetExtension(streamFileName);
                if (!string.IsNullOrEmpty(extension))
                {
                    var container = extension.TrimStart('.').ToLowerInvariant();
                    SetPropertyValue(request, "Container", string.IsNullOrEmpty(container) ? null : container);
                }
            }
        }

        private static void DisableHlsSubtitleOnly(object request)
        {
            try
            {
                SetPropertyValue(request, "SubtitleMethod", null);
                SetPropertyValue(request, "ManifestSubtitles", null);
            }
            catch
            {
                // 忽略：部分版本可能不存在这些属性
            }
        }

        private static object InvokeGetState(object service, object request)
        {
            var requestContext = GetPropertyValue<IRequest>(service, "Request");
            if (requestContext == null) throw new InvalidOperationException("当前请求上下文为空");

            if (!(_getStateMethod?.Invoke(service, new object[] { request, true, requestContext.CancellationToken }) is Task task))
            {
                throw new InvalidOperationException("GetState 返回为空");
            }

            task.GetAwaiter().GetResult();

            var state = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
            if (state == null) throw new InvalidOperationException("GetState 未返回有效 StreamState");

            return state;
        }

        private static bool CanRedirect(object state, bool isVideo, bool isLiveTv)
        {
            if (state == null) return false;
            if (!(GetPropertyValue<MediaSourceInfo>(state, "MediaSource") is { } mediaSource)) return false;

            if (isVideo)
            {
                if (!GetPropertyValue<bool>(state, "IsVideoRequest")) return false;
                if (!mediaSource.IsRemote || mediaSource.Protocol != MediaProtocol.Http ||
                    !mediaSource.SupportsDirectPlay || mediaSource.RequiresOpening || mediaSource.RequiresClosing)
                {
                    return false;
                }

                // LiveTV 允许为无限流
                if (isLiveTv) return true;

                return GetPropertyValue<object>(state, "LiveStream") == null && !mediaSource.IsInfiniteStream;
            }

            if (GetPropertyValue<bool>(state, "IsVideoRequest")) return false;
            if (GetPropertyValue<object>(state, "LiveStream") != null) return false;

            if (!mediaSource.IsRemote || mediaSource.Protocol != MediaProtocol.Http ||
                !mediaSource.SupportsDirectPlay || mediaSource.IsInfiniteStream ||
                mediaSource.RequiresOpening || mediaSource.RequiresClosing)
            {
                return false;
            }

            if (mediaSource.RequiredHttpHeaders != null && mediaSource.RequiredHttpHeaders.Count > 0) return false;

            return Uri.TryCreate(mediaSource.Path, UriKind.Absolute, out var uri) &&
                   (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static void DisposeState(object state)
        {
            try
            {
                _disposeStateMethod?.Invoke(state, new object[] { true, true });
            }
            catch
            {
                // 忽略：释放失败不影响回退
            }
        }

        private static string ResolveRedirectUrl(string url, string userAgent)
        {
            var normalized = NormalizeRedirectUrl(url);

            if (!_follow302) return normalized;

            var httpClient = Plugin.HttpClient;
            if (httpClient == null || string.IsNullOrWhiteSpace(normalized)) return normalized;

            string redirected = null;

            foreach (var method in new[] { "GET", "HEAD" })
            {
                try
                {
                    using var response = httpClient.SendAsync(new HttpRequestOptions
                    {
                        Url = normalized,
                        UserAgent = userAgent ?? string.Empty,
                        TimeoutMs = 3000,
                        BufferContent = false,
                        LogErrors = false,
                        LogRequest = false,
                        LogResponse = false,
                        EnableHttpCompression = false,
                        EnableKeepAlive = false,
                        EnableDefaultUserAgent = false,
                        ThrowOnErrorResponse = false
                    }, method).GetAwaiter().GetResult();

                    if (response != null && !string.IsNullOrWhiteSpace(response.ResponseUrl))
                    {
                        redirected = NormalizeRedirectUrl(response.ResponseUrl);
                        break;
                    }
                }
                catch
                {
                    // 忽略：单个请求失败则尝试下一个方法
                }
            }

            return string.IsNullOrWhiteSpace(redirected) ? normalized : redirected;
        }

        private static string NormalizeRedirectUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            var queryIndex = url.IndexOf('?');
            if (queryIndex < 0)
            {
                return Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute.AbsoluteUri : url;
            }

            var basePart = url.Substring(0, queryIndex);
            var query = queryIndex + 1 < url.Length ? url.Substring(queryIndex + 1) : string.Empty;

            if (!Uri.TryCreate(basePart, UriKind.Absolute, out var baseUri)) return url;

            var normalizedQuery = string.Join("&", query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair =>
                {
                    var equalsIndex = pair.IndexOf('=');
                    if (equalsIndex < 0) return Uri.EscapeDataString(Uri.UnescapeDataString(pair));

                    var key = pair.Substring(0, equalsIndex);
                    var value = equalsIndex + 1 < pair.Length ? pair.Substring(equalsIndex + 1) : string.Empty;

                    return Uri.EscapeDataString(Uri.UnescapeDataString(key)) + "=" +
                           Uri.EscapeDataString(Uri.UnescapeDataString(value));
                }));

            return new UriBuilder(baseUri) { Query = normalizedQuery }.Uri.AbsoluteUri;
        }

        private static bool IsClientBlocked(IRequest requestContext, bool isVideo)
        {
            var blacklist = isVideo ? _videoClientBlacklist : _audioClientBlacklist;
            if (requestContext == null || blacklist.Length == 0) return false;

            var client = ResolveClient(requestContext);
            if (string.IsNullOrWhiteSpace(client)) return false;

            if (blacklist.Any(pattern => client.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Plugin.Instance.Logger.Info($"StrmDirectRedirect: 客户端命中黑名单，回退 Emby 中转。client={client}");
                return true;
            }

            return false;
        }

        private static string ResolveClient(IRequest requestContext)
        {
            try
            {
                if (_sessionContext == null)
                {
                    _sessionContext = Plugin.Instance.ApplicationHost.Resolve<ISessionContext>();
                }

                return _sessionContext?.GetSession(requestContext)?.Client?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string[] ParseClientBlacklist(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

            return text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string SafeUnescape(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;

            try
            {
                return Uri.UnescapeDataString(url);
            }
            catch
            {
                return url;
            }
        }

        private static T GetPropertyValue<T>(object instance, string propertyName)
        {
            if (instance == null) return default;

            var property = instance.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) return default;

            var value = property.GetValue(instance);

            return value is T typed ? typed : default;
        }

        private static void SetPropertyValue(object instance, string propertyName, object value)
        {
            var property = instance?.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property != null && property.CanWrite)
            {
                property.SetValue(instance, value);
            }
        }
    }
}