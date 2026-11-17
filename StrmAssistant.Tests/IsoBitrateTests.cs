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

        /// <summary>
        /// 有可统计的探测输入时，每路视频码率必须按 PID 字节占比分配，而不是 Emby 的等分。
        ///
        /// 这就是真机上的现场：容器 1,572,864 bps、两路视频各 786,432 bps（正好一半），
        /// 而样本里两个视频 PID 的字节是 214080 : 78912（≈73% : 27%）。
        /// </summary>
        [Fact]
        public void SplitsPerStreamBitrate_ByPidBytes()
        {
            var sample = System.IO.Path.Combine(System.AppContext.BaseDirectory,
                "fixtures/two-video-streams.m2ts");
            if (!System.IO.File.Exists(sample)) return;   // fixture 缺失时不断言

            var mediaInfo = Probed(1_572_864, 786_432, 786_432);
            var hint = Hint(4_800_000, 10);               // 真实总码率 3.84 Mbps
            hint.SamplePath = sample;
            hint.InputReplaced = true;

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            Assert.Equal(3_840_000, mediaInfo.Bitrate);
            var main = mediaInfo.MediaStreams[0].BitRate.Value;
            var pip = mediaInfo.MediaStreams[1].BitRate.Value;
            Assert.True(main > pip * 2,
                $"主画面码率应明显高于画中画，实际 {main} vs {pip}（等分时两者相等）");
            // 视频只拿属于视频的那部分：两者之和小于容器码率（音频也占字节）
            Assert.True(main + pip < 3_840_000);
        }

        /// <summary>
        /// PMT 声明的视频 PID 数与探测到的视频流数不一致时，宁可不动：
        /// 说明样本没覆盖全部视频流，硬套会把码率分给错的流。
        /// </summary>
        [Fact]
        public void FallsBack_WhenPidCountMismatchesProbe()
        {
            var sample = System.IO.Path.Combine(System.AppContext.BaseDirectory,
                "fixtures/two-video-streams.m2ts");
            if (!System.IO.File.Exists(sample)) return;

            // 探测到 3 路视频，但样本 PMT 只有 2 个视频 PID
            var mediaInfo = Probed(3_000, 1_000, 1_000, 1_000);
            var hint = Hint(4_800_000, 10);
            hint.SamplePath = sample;
            hint.InputReplaced = true;

            IsoExtraction.ApplyBitrate(mediaInfo, hint);

            // 退回按比例整体缩放：3 路等分真实总码率
            Assert.Equal(1_280_000, mediaInfo.MediaStreams[0].BitRate);
            Assert.Equal(1_280_000, mediaInfo.MediaStreams[2].BitRate);
        }
    }
}
