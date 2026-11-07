using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static StrmAssistant.Options.MultipleVersionsOptions;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 版本标签构造。
    ///
    /// 这套规则同时供网页端（<c>MultiVersionService</c>）与客户端（<c>MediaSourceInfo.Name</c>）使用，
    /// 一旦两边不一致，同一部片在浏览器和 Kodi 里就会显示成两个不同的版本，因此这里把格式锁死。
    /// </summary>
    public class VersionLabelBuilderTests
    {
        private static MediaStream Video(int height, ExtendedVideoSubTypes dv = ExtendedVideoSubTypes.None,
            string codec = "hevc", string comment = null) =>
            new MediaStream
            {
                Type = MediaStreamType.Video,
                Height = height,
                Width = height * 16 / 9,
                Codec = codec,
                ExtendedVideoSubType = dv,
                Comment = comment
            };

        private static MediaStream Audio(string title, string codec) =>
            new MediaStream { Type = MediaStreamType.Audio, Title = title, Codec = codec };

        private static MediaSourceInfo Source(params MediaStream[] streams) =>
            new MediaSourceInfo
            {
                Path = "/media/Test.Movie.2026.2160p.DV.Atmos.mkv",
                MediaStreams = streams.ToList()
            };

        [Fact]
        public void BuildsQualityHdrAndAudioLabel()
        {
            var source = Source(
                Video(2160, ExtendedVideoSubTypes.DoviProfile76),
                Audio("Dolby Atmos 7.1", "truehd"));

            Assert.Equal("4K \u00b7 DV P7 \u00b7 Atmos",
                VersionLabelBuilder.BuildSourceName(source, MultiVersionDisplayOption.MediaInfo, true, true));
        }

        [Fact]
        public void SystemDefaultKeepsEmbyOriginalName()
        {
            var source = Source(Video(1080), Audio("AAC 2.0", "aac"));

            // 返回 null 表示「不要覆盖」，调用方必须保留 MediaSourceInfo.Name 原值
            Assert.Null(VersionLabelBuilder.BuildSourceName(
                source, MultiVersionDisplayOption.SystemDefault, true, true));
        }

        [Fact]
        public void OmitsHdrAndAudioWhenNotRequested()
        {
            var source = Source(Video(1080), Audio("Dolby Atmos 7.1", "eac3"));

            Assert.Equal("1080p",
                VersionLabelBuilder.BuildSourceName(source, MultiVersionDisplayOption.MediaInfo, false, false));
        }

        [Fact]
        public void FileNameModeUsesNameWithoutExtension()
        {
            var source = Source(Video(1080), Audio("AAC 2.0", "aac"));

            Assert.Equal("Test.Movie.2026.2160p.DV.Atmos",
                VersionLabelBuilder.BuildSourceName(
                    source, MultiVersionDisplayOption.FileNameWithoutExtension, false, false));
        }

        [Fact]
        public void ReturnsNullWhenNoStreams()
        {
            Assert.Null(VersionLabelBuilder.BuildSourceName(
                new MediaSourceInfo { MediaStreams = new List<MediaStream>() },
                MultiVersionDisplayOption.MediaInfo, true, true));
        }

        [Fact]
        public void HdrVividCommentWinsOverVideoRange()
        {
            var vivid = Video(2160, ExtendedVideoSubTypes.None, comment: "HDR Vivid");

            Assert.Equal("HDR Vivid", VersionLabelBuilder.GetHdrLabel(new[] { vivid }));
        }

        [Fact]
        public void AacSourceIsNeverTaggedAtmos()
        {
            // 文件名带 Atmos 但音轨是 AAC：宁可不出标签，也不能误标
            var source = new MediaSourceInfo
            {
                Path = "/media/Fake.Atmos.Movie.2026.AAC.mkv",
                MediaStreams = new List<MediaStream> { Video(1080), Audio("Mandarin AAC 2.0", "aac") }
            };

            Assert.Equal("1080p",
                VersionLabelBuilder.BuildSourceName(source, MultiVersionDisplayOption.MediaInfo, true, true));
        }

        [Fact]
        public void DuplicateLabelsAreDisambiguatedBySize()
        {
            var first = Source(Video(1080), Audio("AAC 2.0", "aac"));
            first.Size = 1L * 1024 * 1024 * 1024;
            var second = Source(Video(1080), Audio("AAC 2.0", "aac"));
            second.Size = 2L * 1024 * 1024 * 1024;

            var names = VersionLabelBuilder.BuildSourceNames(
                new[] { first, second }, MultiVersionDisplayOption.MediaInfo, true, true);

            Assert.Equal(new[] { "1080p \u00b7 1GB", "1080p \u00b7 2GB" }, names);
        }

        [Fact]
        public void DuplicateLabelsFallBackToBitrate()
        {
            var first = Source(Video(1080));
            first.Bitrate = 10_000_000;
            var second = Source(Video(1080));
            second.Bitrate = 20_000_000;

            var names = VersionLabelBuilder.BuildSourceNames(
                new[] { first, second }, MultiVersionDisplayOption.MediaInfo, true, true);

            Assert.Equal(new[] { "1080p \u00b7 10Mbps", "1080p \u00b7 20Mbps" }, names);
        }

        [Fact]
        public void UniqueLabelsRemainUnchanged()
        {
            var names = VersionLabelBuilder.BuildSourceNames(
                new[] { Source(Video(2160)), Source(Video(1080)) },
                MultiVersionDisplayOption.MediaInfo, true, true);

            Assert.Equal(new[] { "4K", "1080p" }, names);
        }

        [Fact]
        public void SystemDefaultReturnsNoOverrides()
        {
            var names = VersionLabelBuilder.BuildSourceNames(
                new[] { Source(Video(1080)), Source(Video(1080)) },
                MultiVersionDisplayOption.SystemDefault, true, true);

            Assert.All(names, Assert.Null);
        }

        [Fact]
        public void DvProfileOutranksHdrForSameResolution()
        {
            var dv = VersionLabelBuilder.QualityScore(2160, "DV P7");
            var dv8 = VersionLabelBuilder.QualityScore(2160, "DV P8");
            var hdr = VersionLabelBuilder.QualityScore(2160, "HDR");
            var plain = VersionLabelBuilder.QualityScore(2160, null);

            Assert.True(dv > dv8);
            Assert.True(dv8 > hdr);
            Assert.True(hdr > plain);
        }
    }
}
