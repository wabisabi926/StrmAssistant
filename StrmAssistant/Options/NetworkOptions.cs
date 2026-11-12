using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static StrmAssistant.Common.CommonUtility;

namespace StrmAssistant
{
    public class NetworkOptions : EditableOptionsBase
    {
        [DisplayNameL("NetworkOptions_EditorTitle_Network", typeof(Resources))]
        public override string EditorTitle => Resources.NetworkOptions_EditorTitle_Network;

        [DisplayNameL("NetworkOptions_EnableProxyServer_Enable_Proxy_Server", typeof(Resources))]
        [DescriptionL("NetworkOptions_EnableProxyServer_Enable_Proxy_Server__Default_is_OFF_", typeof(Resources))]
        [EnabledCondition(nameof(IsModSupported), SimpleCondition.IsTrue)]
        public bool EnableProxyServer { get; set; } = false;

        [DisplayNameL("ModOptions_ProxyServer_Proxy_Server", typeof(Resources))]
        [DescriptionL("ModOptions_ProxyServer_Enable_http_proxy_server__Blank_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public string ProxyServerUrl { get; set; } = string.Empty;

        public enum ProxyRoutingMode
        {
            [DescriptionL("ProxyMode_Global_Global", typeof(Resources))]
            Global,
            [DescriptionL("ProxyMode_ScrapeOnly_ScrapeOnly", typeof(Resources))]
            ScrapeOnly
        }

        [DisplayNameL("NetworkOptions_ProxyMode_Proxy_Mode", typeof(Resources))]
        [DescriptionL("NetworkOptions_ProxyMode_Global_proxies_all_outbound_requests__ScrapeOnly_proxies_only_the_scraping_whitelist_hosts__Default_is_Global_", typeof(Resources))]
        [VisibleCondition(nameof(EnableProxyServer), SimpleCondition.IsTrue)]
        public ProxyRoutingMode ProxyMode { get; set; } = ProxyRoutingMode.Global;

        [VisibleCondition(nameof(ShowProxyServerStatus), SimpleCondition.IsTrue)]
        public StatusItem ProxyServerStatus { get; set; } = new StatusItem();

        [Browsable(false)]
        public bool ShowProxyServerStatus { get; set; } = false;

        [DisplayNameL("NetworkOptions_ScrapeProxyWhitelist_Scrape_Proxy_Whitelist", typeof(Resources))]
        [DescriptionL("NetworkOptions_ScrapeProxyWhitelist_Comma_separated_host_suffixes_that_go_through_the_proxy_in_ScrapeOnly_mode__Default_covers_TMDB_and_TVDB_", typeof(Resources))]
        [VisibleCondition(nameof(ShowScrapeProxyWhitelist), SimpleCondition.IsTrue)]
        public string ScrapeProxyWhitelist { get; set; } =
            "api.themoviedb.org,image.tmdb.org,www.themoviedb.org,api.thetvdb.com,www.thetvdb.com,thetvdb.com";

        [Browsable(false)]
        public bool ShowScrapeProxyWhitelist => EnableProxyServer && ProxyMode == ProxyRoutingMode.ScrapeOnly;

        [Browsable(false)]
        public bool IgnoreCertificateValidation { get; set; } = false;

        [DisplayNameL("NetworkOptions_LocalDiscoveryUrl_Customize_Local_Discovery_Url", typeof(Resources))]
        [DescriptionL("NetworkOptions_LocalDiscoveryUrl_Discover_local_reverse_proxy__Default_is_OFF_", typeof(Resources))]
        public string LocalDiscoveryUrl { get; set; } = string.Empty;

        [Browsable(false)]
        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        protected override void Validate(ValidationContext context)
        {
            if (!string.IsNullOrWhiteSpace(ProxyServerUrl) &&
                !IsValidProxyUrl(ProxyServerUrl))
            {
                context.AddValidationError(Resources.InvalidProxyServer);
            }

            if (!string.IsNullOrWhiteSpace(LocalDiscoveryUrl) &&
                !IsValidDiscoveryUrl(LocalDiscoveryUrl))
            {
                context.AddValidationError(Resources.InvalidDiscoveryUrl);
            }
        }

        private static bool IsValidDiscoveryUrl(string value)
        {
            return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        public void Initialize()
        {
            ProxyServerStatus.StatusText = string.Empty;
            ShowProxyServerStatus = false;
        }
    }
}
