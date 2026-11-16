using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Mod;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 蓝光 / MPEG-TS 容器注册标签被 ffprobe 当成编解码器标签的清理。
    /// TrueHD 在 PMT 里注册为 AC-3、HEVC 注册为 HDMV，客户端会显示成
    /// 「TRUEHD (AC-3)」这种自相矛盾的文案；只清明确不一致的，不动正常四字符码。
    /// </summary>
    public class IsoCodecTagTests
    {
        private static MediaInfo Probed(params (string Codec, string Tag)[] streams)
        {
            var list = new List<MediaStream>();
            foreach (var s in streams)
                list.Add(new MediaStream { Type = MediaStreamType.Audio, Codec = s.Codec, CodecTag = s.Tag });
            return new MediaInfo { MediaStreams = list };
        }

        [Fact]
        public void ClearsAc3Tag_OnTrueHd()
        {
            var mediaInfo = Probed(("truehd", "AC-3"));

            var cleared = IsoExtraction.ClearMisleadingCodecTags(mediaInfo);

            Assert.Equal(1, cleared);
            Assert.Null(mediaInfo.MediaStreams[0].CodecTag);
            Assert.Equal("truehd", mediaInfo.MediaStreams[0].Codec);
        }

        [Fact]
        public void ClearsAc3Tag_OnEac3()
        {
            var mediaInfo = Probed(("eac3", "ac-3"));

            IsoExtraction.ClearMisleadingCodecTags(mediaInfo);

            Assert.Null(mediaInfo.MediaStreams[0].CodecTag);
        }

        [Fact]
        public void ClearsHdmvTag_OnVideo()
        {
            var mediaInfo = Probed(("hevc", "HDMV"), ("h264", "hdmv"));

            var cleared = IsoExtraction.ClearMisleadingCodecTags(mediaInfo);

            Assert.Equal(2, cleared);
            Assert.All(mediaInfo.MediaStreams, s => Assert.Null(s.CodecTag));
        }

        [Fact]
        public void KeepsAc3Tag_OnRealAc3()
        {
            var mediaInfo = Probed(("ac3", "AC-3"), ("ac3", "ac-3"), ("AC3", " Ac-3 "));

            var cleared = IsoExtraction.ClearMisleadingCodecTags(mediaInfo);

            Assert.Equal(0, cleared);
            Assert.Equal("AC-3", mediaInfo.MediaStreams[0].CodecTag);
            Assert.Equal("ac-3", mediaInfo.MediaStreams[1].CodecTag);
            Assert.Equal(" Ac-3 ", mediaInfo.MediaStreams[2].CodecTag);
        }

        [Fact]
        public void KeepsNormalFourCcTags()
        {
            var mediaInfo = Probed(
                ("hevc", "hev1"), ("hevc", "hvc1"), ("hevc", "dvhe"),
                ("h264", "avc1"), ("aac", "mp4a"), ("eac3", "ec-3"),
                ("flac", "fLaC"), ("truehd", null));

            var cleared = IsoExtraction.ClearMisleadingCodecTags(mediaInfo);

            Assert.Equal(0, cleared);
            Assert.Equal("hev1", mediaInfo.MediaStreams[0].CodecTag);
            Assert.Equal("ec-3", mediaInfo.MediaStreams[5].CodecTag);
            Assert.Null(mediaInfo.MediaStreams[7].CodecTag);
        }

        [Fact]
        public void SkipsMissingCodecOrTag()
        {
            var mediaInfo = Probed((null, "AC-3"), ("truehd", ""), ("truehd", "   "));

            var cleared = IsoExtraction.ClearMisleadingCodecTags(mediaInfo);

            Assert.Equal(0, cleared);
            Assert.Equal("AC-3", mediaInfo.MediaStreams[0].CodecTag);
        }

        [Fact]
        public void HandlesNullMediaInfo_AndEmptyStreams()
        {
            Assert.Equal(0, IsoExtraction.ClearMisleadingCodecTags(null));
            Assert.Equal(0, IsoExtraction.ClearMisleadingCodecTags(new MediaInfo()));
            Assert.Equal(0, IsoExtraction.ClearMisleadingCodecTags(
                new MediaInfo { MediaStreams = new List<MediaStream>() }));
        }
    }
}
