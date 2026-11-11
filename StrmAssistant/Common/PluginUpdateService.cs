using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    internal enum PluginUpdateState
    {
        NeverChecked,
        UpToDate,
        UpdateAvailable,
        Updated,
        MajorAvailable,
        Failed
    }

    internal sealed class PluginUpdateStatus
    {
        public DateTimeOffset? LastCheckUtc { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public PluginUpdateState State { get; set; }
        public string Message { get; set; }
    }

    internal sealed class PluginReleaseAsset
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public string Digest { get; set; }
        public long? Size { get; set; }
    }

    /// <summary>
    /// 插件在线更新：固定更新源、版本/资产匹配、状态落盘。
    /// 只做纯逻辑与状态读写；网络下载与文件替换在 PluginUpdateTask 中。
    /// </summary>
    internal static class PluginUpdateService
    {
        public const string RepoOwner = "wabisabi926";
        public const string RepoName = "StrmAssistant";
        public const string LatestReleaseUrl =
            "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        private const string StatusFileName = "StrmAssistant_plugin_update_status.json";
        private static readonly Regex AssetPattern = new Regex(
            @"^StrmAssistantPlus_v(?<version>\d+(?:\.\d+){1,3})\.dll$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        internal static string StatusPath =>
            Plugin.Instance == null
                ? StatusFileName
                : Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StatusFileName);

        internal static PluginUpdateStatus Load()
        {
            try
            {
                var path = StatusPath;
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<PluginUpdateStatus>(File.ReadAllText(path), JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        internal static bool Save(PluginUpdateStatus status)
        {
            if (status == null) return false;

            try
            {
                var path = StatusPath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(status, JsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var text = value.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            if (text.Split('.').Length < 3) return null;
            if (!Version.TryParse(text, out var version)) return null;

            return version.Revision >= 0
                ? version
                : new Version(version.Major, version.Minor, Math.Max(version.Build, 0), 0);
        }

        internal static bool IsNewer(Version current, Version latest) =>
            current != null && latest != null && latest > current;

        internal static bool IsSameMajor(Version current, Version latest) =>
            current != null && latest != null && current.Major == latest.Major;

        internal static PluginReleaseAsset SelectAsset(IEnumerable<PluginReleaseAsset> assets, Version version)
        {
            if (assets == null || version == null) return null;

            return assets.FirstOrDefault(asset =>
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.DownloadUrl)) return false;

                var match = AssetPattern.Match(asset.Name ?? string.Empty);
                if (!match.Success) return false;

                var assetVersion = ParseVersion(match.Groups["version"].Value);
                return assetVersion != null && assetVersion == version &&
                       Uri.IsWellFormedUriString(asset.DownloadUrl, UriKind.Absolute);
            });
        }

        internal static string Summarize(PluginUpdateStatus status)
        {
            if (status == null || status.State == PluginUpdateState.NeverChecked)
            {
                return Resources.PluginUpdate_Status_NeverChecked;
            }

            var checkedAt = status.LastCheckUtc.HasValue
                ? status.LastCheckUtc.Value.ToLocalTime().ToString("MM-dd HH:mm")
                : "-";

            var summary = string.Format(Resources.PluginUpdate_Status_Summary,
                status.CurrentVersion ?? "-", status.LatestVersion ?? "-", checkedAt);

            string stateText;
            switch (status.State)
            {
                case PluginUpdateState.UpToDate:
                    stateText = Resources.PluginUpdate_Status_UpToDate;
                    break;
                case PluginUpdateState.UpdateAvailable:
                    stateText = string.Format(Resources.PluginUpdate_Status_UpdateAvailable,
                        status.LatestVersion ?? "-");
                    break;
                case PluginUpdateState.Updated:
                    stateText = string.Format(Resources.PluginUpdate_Status_Updated,
                        status.LatestVersion ?? "-");
                    break;
                case PluginUpdateState.MajorAvailable:
                    stateText = string.Format(Resources.PluginUpdate_Status_MajorAvailable,
                        status.LatestVersion ?? "-");
                    break;
                case PluginUpdateState.Failed:
                    stateText = Resources.PluginUpdate_Status_Failed;
                    break;
                default:
                    stateText = status.Message ?? string.Empty;
                    break;
            }

            return string.IsNullOrEmpty(stateText) ? summary : summary + " · " + stateText;
        }

        internal static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        internal static string GetDigestHex(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest)) return null;

            var text = digest.Trim();
            var separator = text.IndexOf(':');
            if (separator >= 0)
            {
                var algorithm = text.Substring(0, separator);
                if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase)) return null;
                text = text.Substring(separator + 1);
            }

            return Regex.IsMatch(text, "^[0-9a-fA-F]{64}$") ? text.ToLowerInvariant() : null;
        }
    }
}
