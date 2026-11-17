using System;
using System.Collections.Generic;
using System.IO;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 按 PID 统计 MPEG-TS / 蓝光 m2ts 样本的字节数，用于还原**每路视频的真实码率比例**。
    ///
    /// 为什么需要它：ffprobe 对 mpegts 的视频流给不出 <c>bit_rate</c>（实测全部 N/A），
    /// Emby 于是把容器码率**平均**分给各路视频。带画中画（第二路小画面）的原盘里，
    /// 主画面和 PiP 会报成同一个码率 —— 实测样本里两路视频字节占比是 73% : 27%，
    /// 界面却显示成 50% : 50%。样本本身就是真实码流，按 PID 数字节即可还原真实比例。
    ///
    /// 只做只读统计，不依赖 ffprobe / ffmpeg，纯逻辑便于单测。
    /// </summary>
    internal static class TsByteAccounting
    {
        /// <summary>
        /// 最多扫描的字节数。样本本身只有 16MB 左右，挂载路径上则是完整 m2ts 的前缀；
        /// 前缀足以反映码流构成，不必读完几十 GB。
        /// </summary>
        internal const int MaxScanBytes = 32 * 1024 * 1024;

        internal sealed class PidUsage
        {
            public int Pid;
            public int StreamType;
            public long Bytes;
        }

        // 蓝光 / 通用 MPEG-TS 的视频 stream_type：
        // 0x01 MPEG-1、0x02 MPEG-2、0x10 MPEG-4 Part 2、0x1B H.264、0x20 MVC、0x24 HEVC、0xEA VC-1
        private static readonly HashSet<int> VideoStreamTypes =
            new HashSet<int> { 0x01, 0x02, 0x10, 0x1B, 0x20, 0x24, 0xEA };

        internal static bool IsVideoStreamType(int streamType) => VideoStreamTypes.Contains(streamType);

        /// <summary>
        /// 统计 PMT 声明的各 PID 的字节数，**按 PMT 声明顺序**返回。
        ///
        /// 读不到 PAT/PMT、文件过小或不是 TS 时返回 <c>null</c>，调用方保持原有行为。
        /// </summary>
        internal static List<PidUsage> TryCount(string path, int maxBytes = MaxScanBytes)
        {
            if (string.IsNullOrEmpty(path) || maxBytes <= 0) return null;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 20, FileOptions.SequentialScan);

                var probe = new byte[192 * 2];
                var probeRead = ReadFull(fs, probe, probe.Length);
                if (probeRead < 188) return null;

                var (stride, baseOffset) = DetectStride(probe, probeRead);
                if (stride == 0) return null;

                var counts = new Dictionary<int, long>();
                var declared = new List<KeyValuePair<int, int>>();   // stream_type -> pid
                var pmtPid = -1;

                var buffer = new byte[1 << 20];
                var chunkSize = buffer.Length / stride * stride;
                long scanned = 0;

                fs.Position = 0;
                while (scanned < maxBytes)
                {
                    var want = (int)Math.Min(chunkSize, maxBytes - scanned);
                    want -= want % stride;
                    if (want <= 0) break;

                    var got = ReadFull(fs, buffer, want);
                    if (got < stride) break;
                    got -= got % stride;

                    for (var off = 0; off < got; off += stride)
                        ProcessPacket(buffer, off + baseOffset, stride, counts, declared, ref pmtPid);

                    scanned += got;
                    if (got < want) break;
                }

                if (declared.Count == 0) return null;

                var result = new List<PidUsage>(declared.Count);
                foreach (var entry in declared)
                {
                    counts.TryGetValue(entry.Value, out var bytes);
                    result.Add(new PidUsage { Pid = entry.Value, StreamType = entry.Key, Bytes = bytes });
                }
                return result;
            }
            catch
            {
                // 统计失败只是拿不到更精确的比例，绝不能影响探测本身
                return null;
            }
        }

        /// <summary>
        /// 判断包长：蓝光 m2ts 是 192 字节（4 字节 TP_extra_header + 188 字节 TS），普通 .ts 是 188。
        /// </summary>
        private static (int stride, int baseOffset) DetectStride(byte[] buffer, int length)
        {
            var tsAtZero = buffer[0] == 0x47;
            var tsAtFour = length >= 192 && buffer[4] == 0x47;

            if (tsAtZero && (!tsAtFour || (length >= 376 && buffer[188] != 0x47))) return (188, 0);
            if (tsAtFour) return (192, 4);
            if (tsAtZero) return (188, 0);
            return (0, 0);
        }

        private static void ProcessPacket(byte[] buffer, int start, int stride,
            Dictionary<int, long> counts, List<KeyValuePair<int, int>> declared, ref int pmtPid)
        {
            if (start + 188 > buffer.Length || buffer[start] != 0x47) return;

            var pid = ((buffer[start + 1] & 0x1F) << 8) | buffer[start + 2];
            counts.TryGetValue(pid, out var seen);
            // 整包字节（含包头/自适应域）按 PID 记账：开销比例对所有 PID 一致，不影响占比
            counts[pid] = seen + stride;

            var payloadStart = (buffer[start + 1] & 0x40) != 0;
            if (!payloadStart) return;

            var afc = (buffer[start + 3] >> 4) & 0x03;
            if (afc != 1 && afc != 3) return;

            var offset = start + 4;
            if (afc == 3) offset += 1 + buffer[start + 4];
            if (offset + 4 > start + 188) return;

            var sectionStart = offset + 1 + buffer[offset];   // pointer_field
            if (sectionStart + 3 > start + 188) return;

            var tableId = buffer[sectionStart];
            if (tableId == 0x00 && pmtPid < 0)
                ParsePat(buffer, sectionStart, ref pmtPid);
            else if (tableId == 0x02 && pid == pmtPid && declared.Count == 0)
                ParsePmt(buffer, sectionStart, declared);
        }

        private static void ParsePat(byte[] buffer, int sectionStart, ref int pmtPid)
        {
            var sectionLength = ((buffer[sectionStart + 1] & 0x0F) << 8) | buffer[sectionStart + 2];
            var end = sectionStart + 3 + sectionLength - 4;   // 去掉 CRC
            for (var p = sectionStart + 8; p + 4 <= end; p += 4)
            {
                var program = (buffer[p] << 8) | buffer[p + 1];
                if (program == 0) continue;                    // network PID，跳过
                pmtPid = ((buffer[p + 2] & 0x1F) << 8) | buffer[p + 3];
                return;
            }
        }

        private static void ParsePmt(byte[] buffer, int sectionStart, List<KeyValuePair<int, int>> declared)
        {
            var sectionLength = ((buffer[sectionStart + 1] & 0x0F) << 8) | buffer[sectionStart + 2];
            var end = sectionStart + 3 + sectionLength - 4;

            var pos = sectionStart + 8 + 2;                    // 跳过表头 + PCR_PID
            var programInfoLength = ((buffer[pos] & 0x0F) << 8) | buffer[pos + 1];
            pos += 2 + programInfoLength;

            while (pos + 5 <= end)
            {
                var streamType = buffer[pos];
                var pid = ((buffer[pos + 1] & 0x1F) << 8) | buffer[pos + 2];
                var esInfoLength = ((buffer[pos + 3] & 0x0F) << 8) | buffer[pos + 4];
                declared.Add(new KeyValuePair<int, int>(streamType, pid));
                pos += 5 + esInfoLength;
            }
        }

        private static int ReadFull(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }
    }
}
