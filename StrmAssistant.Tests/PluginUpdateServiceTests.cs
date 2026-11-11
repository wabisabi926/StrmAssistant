using System;
using System.Collections.Generic;
using System.Net;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class PluginUpdateServiceTests
    {
        [Theory]
        [InlineData("v4.0.0.1", 4, 0, 0, 1)]
        [InlineData("4.0.0.1", 4, 0, 0, 1)]
        [InlineData("  v5.1.0 ", 5, 1, 0, 0)]
        public void ParseVersion_AcceptsTagAndPlainVersion(string text, int major, int minor, int build,
            int revision)
        {
            Assert.Equal(new Version(major, minor, build, revision), PluginUpdateService.ParseVersion(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("latest")]
        [InlineData("v4.0")]
        [InlineData("v4.0.0.1-beta")]
        public void ParseVersion_RejectsInvalid(string text)
        {
            Assert.Null(PluginUpdateService.ParseVersion(text));
        }

        [Fact]
        public void IsNewer_ComparesVersions()
        {
            Assert.True(PluginUpdateService.IsNewer(new Version(4, 0, 0, 1), new Version(4, 0, 0, 2)));
            Assert.False(PluginUpdateService.IsNewer(new Version(4, 0, 0, 2), new Version(4, 0, 0, 1)));
            Assert.False(PluginUpdateService.IsNewer(new Version(4, 0, 0, 1), new Version(4, 0, 0, 1)));
        }

        [Fact]
        public void IsSameMajor_ComparesMajorOnly()
        {
            Assert.True(PluginUpdateService.IsSameMajor(new Version(4, 0, 0, 1), new Version(4, 2, 0, 0)));
            Assert.False(PluginUpdateService.IsSameMajor(new Version(4, 0, 0, 1), new Version(5, 0, 0, 0)));
        }

        [Fact]
        public void SelectAsset_MatchesVersionedAsset()
        {
            var assets = new List<PluginReleaseAsset>
            {
                new PluginReleaseAsset
                {
                    Name = "StrmAssistantPlus_v4.0.0.2.dll",
                    DownloadUrl = "https://example.com/v4.0.0.2.dll"
                },
                new PluginReleaseAsset
                {
                    Name = "StrmAssistantPlus_v4.0.0.1.dll",
                    DownloadUrl = "https://example.com/v4.0.0.1.dll"
                }
            };

            var asset = PluginUpdateService.SelectAsset(assets, new Version(4, 0, 0, 1));

            Assert.NotNull(asset);
            Assert.Equal("StrmAssistantPlus_v4.0.0.1.dll", asset.Name);
        }

        [Fact]
        public void SelectAsset_RejectsMismatchedOrInvalidAssets()
        {
            var assets = new List<PluginReleaseAsset>
            {
                new PluginReleaseAsset { Name = "StrmAssistant.dll", DownloadUrl = "https://example.com/a.dll" },
                new PluginReleaseAsset
                {
                    Name = "StrmAssistantPlus_v4.0.0.9.dll", DownloadUrl = "https://example.com/b.dll"
                },
                new PluginReleaseAsset
                {
                    Name = "StrmAssistantPlus_v4.0.0.1.dll", DownloadUrl = "not-a-url"
                }
            };

            Assert.Null(PluginUpdateService.SelectAsset(assets, new Version(4, 0, 0, 1)));
        }

        [Theory]
        [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
        public void GetDigestHex_NormalizesSha256(string digest, string expected)
        {
            Assert.Equal(expected, PluginUpdateService.GetDigestHex(digest));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("sha1:abc")]
        [InlineData("sha256:not-hex")]
        public void GetDigestHex_RejectsInvalid(string digest)
        {
            Assert.Null(PluginUpdateService.GetDigestHex(digest));
        }

        [Theory]
        [InlineData("https://github.com/wabisabi926/StrmAssistant/releases/tag/v4.0.0.3", "v4.0.0.3")]
        [InlineData("https://github.com/wabisabi926/StrmAssistant/releases/tag/4.0.0.3/", "4.0.0.3")]
        [InlineData("https://ghproxy.example/https://github.com/wabisabi926/StrmAssistant/releases/tag/v4.1.0?x=1",
            "v4.1.0")]
        public void TryParseTagFromReleaseUrl_ParsesVersionTag(string url, string expected)
        {
            Assert.True(PluginUpdateService.TryParseTagFromReleaseUrl(url, out var tag));
            Assert.Equal(expected, tag);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("https://github.com/wabisabi926/StrmAssistant/releases/latest")]
        [InlineData("https://github.com/wabisabi926/StrmAssistant/releases/tag/latest")]
        [InlineData("not-a-url")]
        public void TryParseTagFromReleaseUrl_RejectsNonVersionUrl(string url)
        {
            Assert.False(PluginUpdateService.TryParseTagFromReleaseUrl(url, out var tag));
            Assert.Null(tag);
        }

        [Fact]
        public void BuildReleaseAssetUrl_UsesFixedAssetName()
        {
            Assert.Equal(
                "https://github.com/wabisabi926/StrmAssistant/releases/download/v4.0.0.3/StrmAssistantPlus_v4.0.0.3.dll",
                PluginUpdateService.BuildReleaseAssetUrl("v4.0.0.3", new Version(4, 0, 0, 3)));
            Assert.Null(PluginUpdateService.BuildReleaseAssetUrl(null, new Version(4, 0, 0, 3)));
            Assert.Null(PluginUpdateService.BuildReleaseAssetUrl("v4.0.0.3", null));
        }

        [Fact]
        public void DescribeGitHubHttpError_MapsStatusCodes()
        {
            var checkForbidden = PluginUpdateService.DescribeGitHubHttpError(HttpStatusCode.Forbidden, false);
            var downloadForbidden = PluginUpdateService.DescribeGitHubHttpError(HttpStatusCode.Forbidden, true);

            Assert.Contains("403", checkForbidden);
            Assert.Contains("403", downloadForbidden);
            Assert.NotEqual(checkForbidden, downloadForbidden);
            Assert.Contains("401",
                PluginUpdateService.DescribeGitHubHttpError(HttpStatusCode.Unauthorized, false));
            Assert.Contains("404", PluginUpdateService.DescribeGitHubHttpError(HttpStatusCode.NotFound, false));
            Assert.Contains("500",
                PluginUpdateService.DescribeGitHubHttpError(HttpStatusCode.InternalServerError, true));
            Assert.False(string.IsNullOrWhiteSpace(
                PluginUpdateService.DescribeGitHubHttpError(null, false)));
        }
    }
}
