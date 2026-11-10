using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Common.CommonUtility;

namespace StrmAssistant.Options.Store
{
    /// <summary>
    /// 网络与直链设置存储：代理、影音直链、TMDB 替换三块的 URL 规整、可达性探测与补丁即时生效。
    /// 原先这些逻辑分散在 <see cref="PluginOptionsStore"/> 与 <see cref="MetadataEnhanceOptionsStore"/>。
    /// </summary>
    public class NetworkProxyOptionsStore : SimpleFileStore<NetworkProxyOptions>
    {
        private readonly ILogger _logger;

        public NetworkProxyOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            FileSaving += OnFileSaving;
        }

        public NetworkProxyOptions NetworkProxyOptions => GetOptions();

        private static string NormalizeUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) ? url.Trim().TrimEnd('/') : url?.Trim();
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (!(e.Options is NetworkProxyOptions options)) return;

            var network = options.Network;
            var tmdb = options.TmdbReplacement;

            network.ProxyServerUrl = NormalizeUrl(network.ProxyServerUrl);
            network.LocalDiscoveryUrl = NormalizeUrl(network.LocalDiscoveryUrl);
            tmdb.AltMovieDbApiUrl = NormalizeUrl(tmdb.AltMovieDbApiUrl);
            tmdb.AltMovieDbImageUrl = NormalizeUrl(tmdb.AltMovieDbImageUrl);

            // 代理可达性探测（只影响状态展示，不阻断保存）
            if (network.EnableProxyServer && !string.IsNullOrWhiteSpace(network.ProxyServerUrl))
            {
                if (TryParseProxyUrl(network.ProxyServerUrl, out var schema, out var host, out var port,
                        out var username, out var password) &&
                    CheckProxyReachability(schema, host, port, username, password) is (true, var httpPing))
                {
                    network.ProxyServerStatus.Status = ItemStatus.Succeeded;
                    network.ProxyServerStatus.Caption = Resources.ProxyServer_Available;
                    network.ProxyServerStatus.StatusText = $"{httpPing} ms";
                }
                else
                {
                    network.ProxyServerStatus.Status = ItemStatus.Unavailable;
                    network.ProxyServerStatus.Caption = Resources.ProxyServer_Unavailable;
                    network.ProxyServerStatus.StatusText = "N/A";
                }

                network.ShowProxyServerStatus = true;
            }
            else
            {
                network.ProxyServerStatus.StatusText = string.Empty;
                network.ShowProxyServerStatus = false;
            }

            var changes = PropertyChangeDetector.DetectObjectPropertyChanges(NetworkProxyOptions, options);
            var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

            // 代理服务器
            if (changedProperties.Contains(nameof(NetworkOptions.EnableProxyServer)))
            {
                if (network.EnableProxyServer)
                {
                    PatchManager.EnableProxyServer.Patch();
                }
                else
                {
                    PatchManager.EnableProxyServer.Unpatch();
                }
            }

            if (changedProperties.Contains(nameof(NetworkOptions.ProxyServerUrl)) ||
                changedProperties.Contains(nameof(NetworkOptions.EnableProxyServer)))
            {
                if (network.EnableProxyServer && network.ProxyServerStatus.Status == ItemStatus.Succeeded)
                {
                    Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                }
                else if (!network.EnableProxyServer)
                {
                    Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                }
            }

            // 本地发现地址
            if (changedProperties.Contains(nameof(NetworkOptions.LocalDiscoveryUrl)))
            {
                if (!string.IsNullOrWhiteSpace(network.LocalDiscoveryUrl))
                {
                    PatchManager.CustomLocalDiscovery.Patch();
                }
                else
                {
                    PatchManager.CustomLocalDiscovery.Unpatch();
                }
            }

            // 影音直链：直接比较新旧值，避免依赖嵌套层级的变更检测
            try
            {
                var oldDirectLink = NetworkProxyOptions.DirectLink ?? new DirectLinkOptions();
                var newDirectLink = options.DirectLink ?? new DirectLinkOptions();

                if (oldDirectLink.EnableStrmDirectRedirect != newDirectLink.EnableStrmDirectRedirect ||
                    oldDirectLink.StrmDirectRedirectFollow302 != newDirectLink.StrmDirectRedirectFollow302 ||
                    !string.Equals(oldDirectLink.StrmVideoDirectRedirectClientBlacklist,
                        newDirectLink.StrmVideoDirectRedirectClientBlacklist) ||
                    !string.Equals(oldDirectLink.StrmAudioDirectRedirectClientBlacklist,
                        newDirectLink.StrmAudioDirectRedirectClientBlacklist))
                {
                    PatchManager.StrmDirectRedirect?.ApplyOptionsAndConfigure(newDirectLink);
                }

                if (oldDirectLink.EnableLiveTvDirectRedirect != newDirectLink.EnableLiveTvDirectRedirect)
                {
                    PatchManager.LiveTvDirectRedirect?.ApplyOptionsAndConfigure(newDirectLink);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"影音直链选项即时生效失败: {ex.Message}");
            }

            // TMDB 替换
            if (changedProperties.Contains(nameof(AltMovieDbOptions.AltMovieDbConfig)))
            {
                if (tmdb.AltMovieDbConfig)
                {
                    PatchManager.AltMovieDbConfig.PatchApiUrl();
                    if (!string.IsNullOrEmpty(tmdb.AltMovieDbImageUrl))
                    {
                        PatchManager.AltMovieDbConfig.PatchImageUrl();
                    }
                }
                else
                {
                    PatchManager.AltMovieDbConfig.UnpatchApiUrl();
                    if (!string.IsNullOrEmpty(tmdb.AltMovieDbImageUrl))
                    {
                        PatchManager.AltMovieDbConfig.UnpatchImageUrl();
                    }
                }
            }

            if (changedProperties.Contains(nameof(AltMovieDbOptions.AltMovieDbImageUrl)))
            {
                if (!string.IsNullOrEmpty(tmdb.AltMovieDbImageUrl))
                {
                    PatchManager.AltMovieDbConfig.PatchImageUrl();
                }
                else
                {
                    PatchManager.AltMovieDbConfig.UnpatchImageUrl();
                }
            }
        }
    }
}
