using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// MPLS PlayItem / STN_table 二进制解析测试。
    ///
    /// 历史缺陷（2026-10 审查 F3）：旧实现把 <c>UO_mask_table</c> 的前 4 字节当成
    /// 「STN_table 起始地址 + 长度」，常见全零 UO mask 会让长度读成 0，整段 STN 被跳过；
    /// 且流计数按 2 字节读、entry/attributes 当成分离数组、coding_type 截成高 4 位，
    /// 导致「从播放列表补音轨/字幕语言」永远拿不到数据（静默失效）。
    ///
    /// 下面的样本严格按 libbluray 的 mpls_parse.c 构造，钉死整条链路：
    /// PlayItem → UO_mask → STN_table → stream entry + attributes。
    /// </summary>
    public class UdfTitleResolverStnTests
    {
        private const int HeaderSize = 40;

        // ---------- 构造工具 ----------

        private static void Be32(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static void Be16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        /// <summary>stream entry 段：长度1 + stream_type + PID2（主路径）。</summary>
        private static byte[] Entry(ushort pid)
        {
            var body = new List<byte> { 1 };        // stream_type = 1（主路径）
            Be16(body, pid);
            var buffer = new List<byte> { (byte)body.Count };
            buffer.AddRange(body);
            return buffer.ToArray();
        }

        /// <summary>音频 attributes：长度1 + coding_type(8bit) + format/rate + 语言3。</summary>
        private static byte[] AudioAttr(byte codingType, int channels, int rateCode, string lang)
        {
            var body = new List<byte>
            {
                codingType,
                (byte)(((channels & 0x0F) << 4) | (rateCode & 0x0F))
            };
            body.AddRange(Encoding.ASCII.GetBytes(lang));
            var buffer = new List<byte> { (byte)body.Count };
            buffer.AddRange(body);
            return buffer.ToArray();
        }

        /// <summary>PG / IG attributes：长度1 + coding_type(8bit) + 语言3。</summary>
        private static byte[] PgAttr(byte codingType, string lang)
        {
            var body = new List<byte> { codingType };
            body.AddRange(Encoding.ASCII.GetBytes(lang));
            var buffer = new List<byte> { (byte)body.Count };
            buffer.AddRange(body);
            return buffer.ToArray();
        }

        /// <summary>视频 attributes：长度1 + coding_type(8bit) + format/rate。</summary>
        private static byte[] VideoAttr(byte codingType)
        {
            var body = new List<byte> { codingType, 0x61 };
            var buffer = new List<byte> { (byte)body.Count };
            buffer.AddRange(body);
            return buffer.ToArray();
        }

        /// <summary>
        /// STN_table：长度2 + 保留2 + 8 个流计数 + 保留4 + 各流「entry + attributes」逐条交错。
        /// </summary>
        private static byte[] BuildStn(int video, int audio, int pg, int ig, byte[][] streams)
        {
            var body = new List<byte>();
            Be16(body, 0);                 // 保留
            body.Add((byte)video);
            body.Add((byte)audio);
            body.Add((byte)pg);
            body.Add((byte)ig);
            body.Add(0);                   // 次级音频
            body.Add(0);                   // 次级视频
            body.Add(0);                   // PIP-PG
            body.Add(0);                   // Dolby Vision
            for (var i = 0; i < 4; i++) body.Add(0);   // 保留

            foreach (var stream in streams) body.AddRange(stream);

            var buffer = new List<byte>();
            Be16(buffer, (ushort)body.Count);
            buffer.AddRange(body);
            return buffer.ToArray();
        }

        /// <summary>标准流组合：H.264 视频 + AC3 英语 5.1 + PGS 中文字幕。</summary>
        private static byte[] StandardStn()
        {
            return BuildStn(1, 1, 1, 0, new[]
            {
                Entry(0x1011).Concat(VideoAttr(0x1B)).ToArray(),
                Entry(0x1100).Concat(AudioAttr(0x81, 6, 1, "eng")).ToArray(),
                Entry(0x1200).Concat(PgAttr(0x90, "zho")).ToArray()
            });
        }

        /// <summary>
        /// PlayItem：长度2 + clip5 + codec4 + 标志2 + stc1 + IN/OUT 4+4
        ///          + UO_mask 8 + 随机访问1 + still_mode1 + still_time2 + [多角度] + STN_table。
        /// </summary>
        private static byte[] BuildPlayItem(byte[] stn, uint inTicks, uint outTicks,
            bool uoMaskNonZero = false, bool multiAngle = false, byte angleCount = 1)
        {
            var body = new List<byte>();
            body.AddRange(Encoding.ASCII.GetBytes("00000"));   // clip id
            body.AddRange(Encoding.ASCII.GetBytes("M2TS"));    // codec identifier

            var flags = (ushort)0x0001;                        // connection_condition = 1
            if (multiAngle) flags |= 0x0010;                   // is_multi_angle
            Be16(body, flags);

            body.Add(0);                                       // stc_id
            Be32(body, inTicks);
            Be32(body, outTicks);

            for (var i = 0; i < 8; i++) body.Add(uoMaskNonZero ? (byte)0xFF : (byte)0);

            body.Add(0);                                       // random_access_flag + 保留
            body.Add(0);                                       // still_mode
            Be16(body, 0);                                     // still_time

            if (multiAngle)
            {
                body.Add(angleCount);
                body.Add(0);                                   // 保留6 + 2 个标志位
                for (var a = 1; a < angleCount; a++)
                {
                    body.AddRange(Encoding.ASCII.GetBytes("0000" + a));
                    body.AddRange(Encoding.ASCII.GetBytes("M2TS"));
                    body.Add(0);
                }
            }

            body.AddRange(stn);

            var buffer = new List<byte>();
            Be16(buffer, (ushort)body.Count);
            buffer.AddRange(body);
            return buffer.ToArray();
        }

        /// <summary>完整 MPLS 文件：头部 + PlayList（内联 PlayItem 列表）。</summary>
        private static byte[] BuildMpls(params byte[][] playItems)
        {
            var playlistBody = new List<byte>();
            Be16(playlistBody, 0);                              // 保留
            Be16(playlistBody, (ushort)playItems.Length);       // PlayItem 数
            Be16(playlistBody, 0);                              // SubPath 数
            foreach (var item in playItems) playlistBody.AddRange(item);

            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("MPLS0200"));
            Be32(file, HeaderSize);                             // PlayList_start_address
            Be32(file, 0);                                      // PlayListMark_start_address
            Be32(file, 0);                                      // ExtensionData_start_address
            while (file.Count < HeaderSize) file.Add(0);

            Be32(file, (uint)playlistBody.Count);               // PlayList 长度
            file.AddRange(playlistBody);
            return file.ToArray();
        }

        // ---------- 用例 ----------

        [Fact]
        public void ParsesAudioAndSubtitle_FromInlineStnTable()
        {
            var mpls = BuildMpls(BuildPlayItem(StandardStn(), 0, 60u * 45000u));

            var entry = UdfTitleResolver.ParsePlaylist("00000.mpls", mpls);

            Assert.NotNull(entry);
            Assert.Equal(60d, entry.TotalSeconds, 2);

            var audio = entry.Streams.Single(s => s.Kind == UdfTitleResolver.BlurayStreamKind.Audio);
            Assert.Equal(0x1100, audio.Pid);
            Assert.Equal("eng", audio.Language);
            Assert.Equal(6, audio.Channels);
            Assert.Equal(48000, audio.SampleRate);
            Assert.Equal(UdfTitleResolver.BlurayCodingType.Ac3, audio.Coding);

            var subtitle = entry.Streams.Single(s => s.Kind == UdfTitleResolver.BlurayStreamKind.Subtitle);
            Assert.Equal(0x1200, subtitle.Pid);
            Assert.Equal("zho", subtitle.Language);
            Assert.Equal(UdfTitleResolver.BlurayCodingType.Pg, subtitle.Coding);
        }

        [Fact]
        public void ParsesStreams_EvenWhenUoMaskIsNonZero()
        {
            // 旧实现会把 UO mask 前 4 字节当成 STN 地址/长度：全零时长度=0 直接跳过，
            // 非零时又跳到错误偏移。两种取值都必须拿到同样的结果。
            var mpls = BuildMpls(BuildPlayItem(StandardStn(), 0, 60u * 45000u, uoMaskNonZero: true));

            var entry = UdfTitleResolver.ParsePlaylist("00000.mpls", mpls);

            Assert.NotNull(entry);
            Assert.Equal(2, entry.Streams.Count(s =>
                s.Kind == UdfTitleResolver.BlurayStreamKind.Audio ||
                s.Kind == UdfTitleResolver.BlurayStreamKind.Subtitle));
        }

        [Fact]
        public void ParsesStreams_ForMultiAnglePlayItem()
        {
            // 多角度时 PlayItem 在 still_time 之后还有 angle 记录，漏解析会让 STN 从错误位置开始
            var mpls = BuildMpls(BuildPlayItem(StandardStn(), 0, 60u * 45000u,
                multiAngle: true, angleCount: 2));

            var entry = UdfTitleResolver.ParsePlaylist("00000.mpls", mpls);

            Assert.NotNull(entry);
            Assert.Equal(60d, entry.TotalSeconds, 2);
            Assert.Contains(entry.Streams, s => s.Language == "eng");
            Assert.Contains(entry.Streams, s => s.Language == "zho");
        }

        [Fact]
        public void DeduplicatesStreams_AcrossMultiplePlayItems()
        {
            // 两个 PlayItem（正片分两段），PID 相同 → 只保留一份，不重复追加
            var mpls = BuildMpls(
                BuildPlayItem(StandardStn(), 0, 30u * 45000u),
                BuildPlayItem(StandardStn(), 0, 30u * 45000u));

            var entry = UdfTitleResolver.ParsePlaylist("00000.mpls", mpls);

            Assert.NotNull(entry);
            Assert.Equal(60d, entry.TotalSeconds, 2);
            Assert.Equal(2, entry.Streams.Count);
        }

        [Fact]
        public void ReadsLanguage_FromTextSubtitleWithCharCode()
        {
            // 0x92（Text）在语言前多一个 char_code 字节，漏读会让语言整体错位。
            // Text 与 PG 同属 num_pg 计数（PG/textST），所以 pg 记 1。
            var stn = BuildStn(0, 0, 1, 0, new[]
            {
                Entry(0x1200).Concat(PgAttr(0x92, "chi")).ToArray()
            });

            var mpls = BuildMpls(BuildPlayItem(stn, 0, 10u * 45000u));

            var entry = UdfTitleResolver.ParsePlaylist("00000.mpls", mpls);

            Assert.NotNull(entry);
            var text = Assert.Single(entry.Streams);
            Assert.Equal(UdfTitleResolver.BlurayStreamKind.Text, text.Kind);
            Assert.Equal(UdfTitleResolver.BlurayCodingType.Text, text.Coding);
        }

        [Fact]
        public void ReturnsNull_ForNonMplsMagic()
        {
            var data = BuildMpls(BuildPlayItem(StandardStn(), 0, 60u * 45000u));
            data[0] = (byte)'X';

            Assert.Null(UdfTitleResolver.ParsePlaylist("00000.mpls", data));
        }
    }
}
