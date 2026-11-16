using System.Collections.Generic;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// ISO 采样探测的码率重算：ffprobe 探测「头 8MB + 尾 8MB」样本时，
    /// 其 bit_rate ≈ 样本字节 × 8 / 全片时间跨度，3 小时 4K 原盘会算出约 12 kbps。
    /// 这里用 UDF 选片记录的正片真实字节数重算，并钉住各流的缩放与兜底边界。
    /// </summary>
    public class IsoBitrateTests
    {
        private static IsoMounter.TitleHint Hint(long sourceBytes, double durationSeconds)
        {
            return new IsoMounter.TitleHint
            {
                IsProbeSample = true,
                SourceBytes = sourceBytes,
                DurationSeconds = durationSeconds
            };
        }

        private static MediaInfo Probed(int? containerBitrate, params int[] streamBitrates)
        {
            var streams = new List<MediaStream>();
            foreach (var rate in streamBitrates)
                streams.Add(new MediaStream { Type = MediaStreamType.Video, BitRate = rate });
            return new MediaInfo { Bitrate = containerBitrate, MediaStreams = streams };
        }

        [Fact]
        public void RecomputesContainerBitrate_FromTitleBytes()
        {
            // 4K 原盘：正片 60GB，2.5 小时 → 约 53.3 Mbps
            var mediaInfo = Probed(12_000); // ffprobe 从 16MB 样本算出的脏值
            var hint = Hint(60L * 1024 * 1024 * 1024, 9000);

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            var expected = (int)System.Math.Round(60L * 1024 * 1024 * 1024 * 8.0 / 9000);
            Assert.Equal(expected, mediaInfo.Bitrate);
            Assert.True(mediaInfo.Bitrate.Value > 50_000_000);
        }

        [Fact]
        public void ScalesStreamBitrates_WhenProbedLikeContainer()
        {
            // 各流码率之和(9k)与容器脏值(12k)同数量级 → 认定被样本污染，按比例缩放
            var mediaInfo = Probed(12_000, 6_000, 3_000);
            var hint = Hint(4_800_000, 10); // 真实总码率 3.84 Mbps = 缩放系数 320

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            Assert.Equal(3_840_000, mediaInfo.Bitrate);
            // 缩放后各流之和 = 真实总码率：6000+3000=9000，系数 3840000/9000
            Assert.Equal(2_560_000, mediaInfo.MediaStreams[0].BitRate);
            Assert.Equal(1_280_000, mediaInfo.MediaStreams[1].BitRate);
        }

        [Fact]
        public void ScalesVideoStreams_EvenWhenAudioBitrateLooksReal()
        {
            // 真机场景：样本把视频算成 17 kbps，但 TrueHD 音频带真实 4 Mbps 标记。
            // 旧实现把音频一起求和（ratio >> 5）导致整段跳过，视频永远停在 17 kbps。
            var mediaInfo = Probed(17_000,
                17_000, 17_000);
            mediaInfo.MediaStreams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Codec = "truehd",
                BitRate = 4_000_000
            });
            var hint = Hint(39_800_000_000, 10_355); // 39.8GB / 2:52:35 → 约 30.8 Mbps

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            Assert.True(mediaInfo.Bitrate > 30_000_000);
            // 两路视频按比例分掉真实总码率，音频保持自己的真实值
            Assert.True(mediaInfo.MediaStreams[0].BitRate > 15_000_000);
            Assert.True(mediaInfo.MediaStreams[1].BitRate > 15_000_000);
            Assert.Equal(4_000_000, mediaInfo.MediaStreams[2].BitRate);
        }

        [Fact]
        public void KeepsStreamBitrates_WhenTheyLookReal()
        {
            // 流码率之和(80M)远大于容器脏值(12k) → 是码流内 VUI 真实值，不应缩放
            var mediaInfo = Probed(12_000, 50_000_000, 30_000_000);
            var hint = Hint(4_800_000, 10);

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            Assert.Equal(3_840_000, mediaInfo.Bitrate);
            Assert.Equal(50_000_000, mediaInfo.MediaStreams[0].BitRate);
            Assert.Equal(30_000_000, mediaInfo.MediaStreams[1].BitRate);
        }

        [Fact]
        public void Skips_WhenNotProbeSample()
        {
            // 挂载路径走真实 m2ts，ffprobe 的码率本来就对，不动
            var mediaInfo = Probed(46_000_000, 40_000_000);
            var hint = Hint(60L * 1024 * 1024 * 1024, 9000);
            hint.IsProbeSample = false;

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            Assert.Equal(46_000_000, mediaInfo.Bitrate);
            Assert.Equal(40_000_000, mediaInfo.MediaStreams[0].BitRate);
        }

        [Theory]
        [InlineData(0, 9000)]
        [InlineData(1000, 0)]
        [InlineData(1000, -5)]
        public void Skips_WhenInputsMissing(long sourceBytes, double durationSeconds)
        {
            var mediaInfo = Probed(12_000, 6_000);
            IsoExtraction.ApplyBitrate(mediaInfo, Hint(sourceBytes, durationSeconds));

            Assert.Equal(12_000, mediaInfo.Bitrate);
            Assert.Equal(6_000, mediaInfo.MediaStreams[0].BitRate);
        }

        [Fact]
        public void Skips_WhenContainerBitrateMissing()
        {
            // ffprobe 没给容器码率时，无法判断各流是否被污染，流码率保持原样
            var mediaInfo = Probed(null, 6_000, 3_000);
            var hint = Hint(4_800_000, 10);

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            Assert.Equal(3_840_000, mediaInfo.Bitrate);
            Assert.Equal(6_000, mediaInfo.MediaStreams[0].BitRate);
        }

        [Fact]
        public void Skips_WhenNoMediaStreams()
        {
            var mediaInfo = new MediaInfo { Bitrate = 12_000, MediaStreams = null };
            IsoExtraction.ApplyBitrate(mediaInfo, Hint(4_800_000, 10));

            Assert.Equal(3_840_000, mediaInfo.Bitrate);
        }
    }
}
