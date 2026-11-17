using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// TS 按 PID 字节统计：用来还原每路视频的真实码率比例。
    ///
    /// 背景：ffprobe 对 mpegts 的视频流一律给 <c>bit_rate=N/A</c>，Emby 只能把容器码率
    /// 平均分给各路视频。实测两路视频的样本里字节占比是 73% : 27%，界面却显示 50% : 50%。
    /// 这里用合成的 TS 钉住统计口径（含 188 / 192 两种包长）。
    /// </summary>
    public class TsByteAccountingTests : IDisposable
    {
        private readonly string _dir;

        public TsByteAccountingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "strm-tsacc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }

        /// <summary>构造一个最小可解析的 TS：PAT + PMT + 各 PID 的填充包。</summary>
        private static byte[] BuildTs(int stride, int[] videoPids, int[] payloadPackets)
        {
            const int pmtPid = 0x1000;
            const int patPid = 0x0000;

            var packets = new List<byte[]>();
            packets.Add(Section(stride, patPid, BuildPat(pmtPid)));
            packets.Add(Section(stride, pmtPid, BuildPmt(videoPids)));

            // 每个视频 PID 写 payloadPackets[i] 个包，用固定填充值便于校验
            for (var i = 0; i < videoPids.Length; i++)
                for (var n = 0; n < payloadPackets[i]; n++)
                    packets.Add(Payload(stride, videoPids[i], (byte)(0x10 + i)));

            // 再补一个音频 PID，验证「视频只拿属于视频的那部分」
            for (var n = 0; n < 2; n++)
                packets.Add(Payload(stride, 0x1100, 0x80));

            return packets.SelectMany(p => p).ToArray();
        }

        private static byte[] BuildPat(int pmtPid)
        {
            var body = new List<byte> { 0x00, 0xB0, 0x00, 0x00, 0x01, 0xC1, 0x00, 0x00 };
            body.AddRange(new byte[] { 0x00, 0x01 });                 // program_number = 1
            body.Add((byte)(0xE0 | ((pmtPid >> 8) & 0x1F)));          // reserved(3) + PMT_PID(13)
            body.Add((byte)(pmtPid & 0xFF));
            body.AddRange(new byte[] { 0, 0, 0, 0 });   // CRC 占位
            body[1] = (byte)(0xB0 | (((body.Count - 3) >> 8) & 0x0F));
            body[2] = (byte)((body.Count - 3) & 0xFF);
            return body.ToArray();
        }

        private static byte[] BuildPmt(int[] videoPids)
        {
            var body = new List<byte> { 0x02, 0xB0, 0x00, 0x00, 0x01, 0xC1, 0x00, 0x00 };
            body.AddRange(new byte[] { 0xE1, 0x00 });   // PCR_PID
            body.AddRange(new byte[] { 0xF0, 0x00 });   // program_info_length = 0

            foreach (var pid in videoPids)
            {
                body.Add(0x24);                          // HEVC
                body.Add((byte)(0xE0 | ((pid >> 8) & 0x1F)));
                body.Add((byte)(pid & 0xFF));
                body.Add(0xF0);                          // es_info_length = 0
                body.Add(0x00);
            }

            body.Add(0x83);                              // TrueHD
            body.Add(0xF1);                              // 0xE0 | (0x1100 >> 8)
            body.Add(0x00);
            body.Add(0xF0);
            body.Add(0x00);

            body.AddRange(new byte[] { 0, 0, 0, 0 });    // CRC 占位
            body[1] = (byte)(0xB0 | (((body.Count - 3) >> 8) & 0x0F));
            body[2] = (byte)((body.Count - 3) & 0xFF);
            return body.ToArray();
        }

        /// <summary>把 PSI section 塞进一个带 PUSI 的包。</summary>
        private static byte[] Section(int stride, int pid, byte[] section)
        {
            var packet = new byte[stride];
            var ts = stride == 192 ? 4 : 0;
            packet[ts] = 0x47;
            packet[ts + 1] = (byte)(0x40 | ((pid >> 8) & 0x1F));
            packet[ts + 2] = (byte)(pid & 0xFF);
            packet[ts + 3] = 0x10;                       // payload only
            packet[ts + 4] = 0x00;                       // pointer_field
            Array.Copy(section, 0, packet, ts + 5, Math.Min(section.Length, 183));
            for (var i = ts + 5 + section.Length; i < ts + 188; i++) packet[i] = 0xFF;
            return packet;
        }

        private static byte[] Payload(int stride, int pid, byte fill)
        {
            var packet = new byte[stride];
            var ts = stride == 192 ? 4 : 0;
            packet[ts] = 0x47;
            packet[ts + 1] = (byte)((pid >> 8) & 0x1F);   // 无 PUSI
            packet[ts + 2] = (byte)(pid & 0xFF);
            packet[ts + 3] = 0x10;
            for (var i = ts + 4; i < ts + 188; i++) packet[i] = fill;
            return packet;
        }

        [Theory]
        [InlineData(188)]
        [InlineData(192)]
        public void CountsPerPid_InPmtOrder(int stride)
        {
            var path = Path.Combine(_dir, "sample.ts");
            // 两路视频 + 1 路音频：视频 6 包 vs 2 包（含 PMT/PAT 的公共包也计入分母）
            File.WriteAllBytes(path, BuildTs(stride, new[] { 0x1011, 0x1012 }, new[] { 6, 2 }));

            var usage = TsByteAccounting.TryCount(path);

            Assert.NotNull(usage);
            Assert.Equal(3, usage.Count);
            Assert.Equal(0x1011, usage[0].Pid);
            Assert.Equal(0x1012, usage[1].Pid);
            Assert.Equal(0x1100, usage[2].Pid);
            Assert.All(usage, u => Assert.True(u.Bytes > 0));
            Assert.True(usage[0].Bytes > usage[1].Bytes);
        }

        [Fact]
        public void VideoShare_IsNotEqualSplit()
        {
            // 6 : 2 包 → 视频内部 75% : 25%；若按 Emby 的等分会是 50% : 50%
            var path = Path.Combine(_dir, "unequal.ts");
            File.WriteAllBytes(path, BuildTs(192, new[] { 0x1011, 0x1012 }, new[] { 6, 2 }));

            var usage = TsByteAccounting.TryCount(path);
            var video = usage.Where(u => TsByteAccounting.IsVideoStreamType(u.StreamType)).ToList();
            var total = usage.Sum(u => u.Bytes);

            Assert.Equal(2, video.Count);
            // 6 包 : 2 包 = 3 : 1，等分会得到 1 : 1
            Assert.True(video[0].Bytes > video[1].Bytes * 2);
            // 分母含 PSI + 音频，视频不该吃掉全部字节（否则音频开销会被算进视频码率）
            var videoShare = (video[0].Bytes + video[1].Bytes) / (double)total;
            Assert.True(videoShare < 0.95, $"视频占比应小于 1，实际 {videoShare:F2}");
        }

        [Fact]
        public void ReturnsNull_WhenNotTs()
        {
            var path = Path.Combine(_dir, "junk.bin");
            File.WriteAllBytes(path, new byte[4096]);

            Assert.Null(TsByteAccounting.TryCount(path));
        }

        [Fact]
        public void ReturnsNull_WhenFileTooSmall()
        {
            var path = Path.Combine(_dir, "tiny.ts");
            File.WriteAllBytes(path, new byte[64]);

            Assert.Null(TsByteAccounting.TryCount(path));
        }

        [Fact]
        public void ReturnsNull_WhenMissing()
        {
            Assert.Null(TsByteAccounting.TryCount(Path.Combine(_dir, "nope.ts")));
            Assert.Null(TsByteAccounting.TryCount(null));
        }

        [Fact]
        public void RespectsMaxScanBytes()
        {
            var path = Path.Combine(_dir, "long.ts");
            File.WriteAllBytes(path, BuildTs(192, new[] { 0x1011, 0x1012 }, new[] { 6, 2 }));

            // 只允许读一个包长度：连 PAT 都读不全，应返回 null 而不是抛异常
            Assert.Null(TsByteAccounting.TryCount(path, 192));
        }

        /// <summary>
        /// 真实两路视频样本（4K + 1080p HEVC + TrueHD，2 秒，192 字节 m2ts 包长）。
        ///
        /// 这就是「两路码率不等的盘」的最小复现：两个视频 PID 的字节数实测是
        /// 214080 : 78912 ≈ 2.71 : 1，即 73% : 27%。而 Emby 因为 ffprobe 给 N/A
        /// 会把容器码率等分，界面显示 50% : 50% —— 本用例钉住真实比例，防止退回等分。
        /// </summary>
        [Fact]
        public void RealTwoVideoStreamSample_HasUnequalShares()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures/two-video-streams.m2ts");
            if (!File.Exists(path)) return;   // fixture 缺失时不做断言（CI 正常会复制过去）

            var usage = TsByteAccounting.TryCount(path);

            Assert.NotNull(usage);
            var video = usage.Where(u => TsByteAccounting.IsVideoStreamType(u.StreamType)).ToList();
            Assert.Equal(2, video.Count);

            // 主画面占绝对多数：等分会得到 1.0 的比值，这里必须是 2 倍以上
            var ratio = video[0].Bytes / (double)video[1].Bytes;
            Assert.True(ratio > 2.0, $"两路视频字节比应明显大于 2，实际 {ratio:F2}");
            Assert.True(ratio < 4.0, $"两路视频字节比应在 4 以内，实际 {ratio:F2}");

            // 音频 PID 也在统计里，说明分母包含全部基本流（视频不该吃掉音频那份）
            Assert.Contains(usage, u => !TsByteAccounting.IsVideoStreamType(u.StreamType));
        }
    }
}
