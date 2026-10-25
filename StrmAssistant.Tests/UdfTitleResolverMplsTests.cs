using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// MPLS PlayListMark 解析测试（章节来源）。
    ///
    /// 历史 bug：把「长度 4 字节之后」当成保留字节跳过，实际**没有**该保留字节，
    /// 保留字节在每条 mark 内部（reserved + mark_type + ref + ts + dur + pid = 14 字节）。
    /// 跳错一位 → mark 数/时间戳全部错位 → 章节为空。下面的用例把这个结构钉死。
    /// </summary>
    public class UdfTitleResolverMplsTests
    {
        private const int MarkStart = 32;   // 前置空白，模拟真实文件中非零的 mark 起始偏移

        // ---------- 构造工具 ----------

        private static void WriteBe32(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static void WriteBe16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static byte[] BuildMarkSection(params (byte Type, ushort PlayItemRef, double Seconds, uint Duration)[] marks)
        {
            var body = new List<byte>();

            foreach (var mark in marks)
            {
                body.Add(0);                                          // reserved（每条 mark 内部）
                body.Add(mark.Type);                                  // mark_type
                WriteBe16(body, mark.PlayItemRef);
                WriteBe32(body, (uint)Math.Round(mark.Seconds * 45000));  // 45kHz 绝对 PTS
                WriteBe32(body, mark.Duration);
                WriteBe16(body, 0x1011);                              // entry_ES_PID
            }

            var buffer = new List<byte>(new byte[MarkStart]);
            WriteBe32(buffer, (uint)(2 + body.Count));                 // 长度 = 2 + 14×count（无额外保留字节）
            WriteBe16(buffer, (ushort)marks.Length);
            buffer.AddRange(body);

            return buffer.ToArray();
        }

        private static UdfTitleResolver.PlaylistEntry NewEntry(params (double Duration, double InTime)[] clips)
        {
            var entry = new UdfTitleResolver.PlaylistEntry { Name = "00000.mpls" };

            foreach (var clip in clips)
            {
                entry.Clips.Add(new UdfTitleResolver.ClipEntry
                {
                    Name = "00000",
                    Duration = clip.Duration,
                    InTime = clip.InTime
                });
            }

            return entry;
        }

        // ---------- 用例 ----------

        [Fact]
        public void ParsesEntryMarks_WithoutReservedByteAfterLength()
        {
            // 1 个 PlayItem：IN_time = 10s；两条 mark 落在 10s / 40s（绝对 PTS）
            var data = BuildMarkSection((1, 0, 10, 0), (1, 0, 40, 0));
            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Equal(new[] { 0d, 30d }, entry.Chapters.ToArray());
        }

        [Fact]
        public void RejectsSection_WhenLengthFieldDoesNotMatchCount()
        {
            var data = BuildMarkSection((1, 0, 10, 0), (1, 0, 40, 0));
            data[MarkStart + 3] = (byte)(data[MarkStart + 3] + 2);   // 长度字段 +2，结构不符

            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Empty(entry.Chapters);
        }

        [Fact]
        public void IgnoresMarks_ThatAreNotEntryMarks()
        {
            // mark_type = 2 不是章节入口标记
            var data = BuildMarkSection((2, 0, 10, 0), (1, 0, 40, 0));
            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Equal(new[] { 30d }, entry.Chapters.ToArray());
        }

        [Fact]
        public void OffsetsMark_ByAccumulatedDurationOfPreviousPlayItems()
        {
            // 两个 PlayItem：60s（IN 5s）+ 40s（IN 100s）
            // mark 落在第二个 PlayItem 的 110s → 60 + (110 - 100) = 70s
            var data = BuildMarkSection((1, 1, 110, 0));
            var entry = NewEntry((60, 5), (40, 100));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Equal(new[] { 70d }, entry.Chapters.ToArray());
        }

        [Fact]
        public void FallsBackToFirstPlayItem_WhenRefOutOfRange()
        {
            var data = BuildMarkSection((1, 9, 40, 0));   // ref=9，但只有 1 个 PlayItem
            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Equal(new[] { 30d }, entry.Chapters.ToArray());
        }

        [Fact]
        public void DedupesAdjacentMarks_WithinOneMillisecond()
        {
            var data = BuildMarkSection((1, 0, 10, 0), (1, 0, 10.0004, 0), (1, 0, 30, 0));
            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Equal(2, entry.Chapters.Count);
            Assert.Equal(0d, entry.Chapters[0], 3);
            Assert.Equal(20d, entry.Chapters[1], 3);
        }

        [Fact]
        public void ClampsNegativeRelativeTime_ToZero()
        {
            // mark 时间早于 PlayItem 的 IN_time → 钳到 0，不产生负章节
            var data = BuildMarkSection((1, 0, 5, 0));
            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(data, MarkStart, entry);

            Assert.Equal(new[] { 0d }, entry.Chapters.ToArray());
        }

        [Fact]
        public void IgnoresTruncatedSection()
        {
            var data = BuildMarkSection((1, 0, 10, 0));
            var truncated = data.Take(data.Length - 4).ToArray();
            var entry = NewEntry((100, 10));

            UdfTitleResolver.ParsePlayListMark(truncated, MarkStart, entry);

            Assert.Empty(entry.Chapters);
        }

        /// <summary>
        /// 端到端：真实本地 ISO（存在才跑，样本缺失时跳过）。
        /// 覆盖 DiscUtils 读 UDF → 找 mpls → 解析 PlayListMark 的整条链路。
        ///
        /// 这条用例同时是 <c>UdfDiscFs.GetFiles</c> 的回归网：
        /// DiscUtils 的 searchPattern 按 Windows 语义实现，"*" **不匹配带扩展名的文件**
        /// （实测 BDMV\PLAYLIST 下 "*" → 0 个、"*.*" → 1 个）。
        /// 曾经这里用 "*"，导致进程内 UDF 解析永远返回 null（挂载路径掩盖了该缺陷）。
        /// </summary>
        [IsoSampleFact]
        public void Resolve_RealIso_ReturnsTitleWithDurationAndChapters()
        {
            var isoPath = IsoSample.ResolvedPath;

            var title = UdfTitleResolver.Resolve(isoPath);

            Assert.NotNull(title);
            Assert.True(title.IsBluray, "应识别为 Blu-ray 结构");
            Assert.True(title.DurationSeconds > 0, "正片时长应大于 0");
            Assert.Contains("STREAM", title.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(title.Chapters);
        }
    }
}
