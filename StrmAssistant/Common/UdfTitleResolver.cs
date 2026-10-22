using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 碟片正片解析（跨平台，进程内读 UDF，不依赖任何 OS 挂载能力）。
    ///
    /// 蓝光：遍历 <c>BDMV\PLAYLIST\*.mpls</c>，按 PlayItem 时长求和取「总时长最长的 playlist」，
    /// 仅单 PlayItem 时可直接选取对应 m2ts。多片段/无缝分支返回明确的不支持标志，
    /// 调用方保留原 ISO 交给宿主处理，不能以最长片段冒充完整标题。
    /// DVD：<c>VIDEO_TS\VTS_01_1.VOB</c>，缺失则取最大 .VOB。
    ///
    /// 两种来源共用同一套选片逻辑：<see cref="IDiscFs"/> 分别由进程内 UDF 与已挂载卷实现。
    /// 只读、静默失败：任何异常都返回 null，调用方保持原路径不变。
    /// </summary>
    internal static class UdfTitleResolver
    {
        private const string BlurayPlaylistDir = @"BDMV\PLAYLIST";
        private const string BlurayStreamDir = @"BDMV\STREAM";
        private const string DvdVideoTsDir = @"VIDEO_TS";
        private const string DvdMainTitle = @"VIDEO_TS\VTS_01_1.VOB";
        private const double TicksPerSecond = 45000.0;

        internal sealed class TitleInfo
        {
            /// <summary>蓝光碟片（true）还是 DVD（false）。</summary>
            public bool IsBluray;

            /// <summary>碟内相对路径（统一以 <c>\</c> 分隔），如 <c>BDMV\STREAM\00001.m2ts</c>。</summary>
            public string RelativePath;

            // Ordered playlist streams used only to sample metadata, never as a substitute playback title.
            public List<string> ProbePaths = new List<string>();

            /// <summary>选中的 playlist 名（如 00001.mpls）或 VOB 名，仅用于日志。</summary>
            public string SourceName;

            /// <summary>非空时禁止选择单文件及注入章节，由宿主继续处理原始 ISO。</summary>
            public string UnsupportedReason;

            /// <summary>蓝光为 playlist 总时长（秒）；无法计算时为 0。</summary>
            public double DurationSeconds;

            /// <summary>MPLS STN_table 解析出的主音轨（语言/编码/声道）；可能为空。</summary>
            public List<MplsStreamInfo> AudioStreams = new List<MplsStreamInfo>();

            /// <summary>MPLS STN_table 解析出的字幕（语言）；可能为空。</summary>
            public List<MplsStreamInfo> SubtitleStreams = new List<MplsStreamInfo>();

            /// <summary>MPLS PlayListMark 解析出的章节（秒，升序，首个通常为 0）；可能为空。</summary>
            public List<double> Chapters = new List<double>();
        }

        /// <summary>
        /// STN_table 中一条基本流（primary audio / PG / IG / secondary 等）的语言与编码信息。
        /// 仅承载 MPLS 能提供的字段；ffprobe 侧的宽度/高度/时长等由探测器补充。
        /// </summary>
        internal sealed class MplsStreamInfo
        {
            /// <summary>基本流 PID（包标识）。</summary>
            public int Pid;

            /// <summary>BD-ROM 层面的流类型，如 Audio/Subtitle。</summary>
            public BlurayStreamKind Kind;

            /// <summary>编码类型（coding_type），如 AC3/DTS/TrueHD/PG 等。</summary>
            public BlurayCodingType Coding;

            /// <summary>编码类型原始值（codint_type 4bit）。</summary>
            public int CodingRaw;

            /// <summary>语言码（ISO 639-2，3 字符，小写）；未知为 "und" 或空。</summary>
            public string Language;

            /// <summary>音频：声道数（1~8），未知为 0。</summary>
            public int Channels;

            /// <summary>音频：采样率（Hz），未知为 0。</summary>
            public int SampleRate;

            public override string ToString() =>
                $"{Kind}/{Coding} pid={Pid} lang={Language} ch={Channels}";
        }

        internal enum BlurayStreamKind
        {
            Unknown = 0,
            Audio = 1,
            Subtitle = 2,
            Interactive = 3,
            Text = 4
        }

        internal enum BlurayCodingType
        {
            Unknown = 0,
            Ac3,
            Dts,
            TrueHd,
            Eac3,
            Pcm,     // LPCM
            DtsHd,
            DtsHdMa,
            DtsHdHra,
            DtsHdLbr,
            DtsExpress,
            Dsd,
            Pg,      // PGS 字幕
            Ig,      // 交互图形
            Text,    // ASCII text subtitle
            Other
        }

        /// <summary>打开本地 ISO 文件解析正片；失败返回 null。</summary>
        public static TitleInfo Resolve(string isoPath)
        {
            try
            {
                using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

                return Resolve(stream, isoPath);
            }
            catch (Exception e)
            {
                Log($"resolve failed for {isoPath}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 用任意可 Seek 的流解析正片（本地文件 / <see cref="HttpRangeStream"/> 远程 ISO）。
        ///
        /// 关键约束：<c>UdfReader</c> 构造时会校验分区长度 ≤ 流长度，流的 <c>Length</c> 必须是真实总长，
        /// 否则直接抛 <c>Substream extends beyond end of parent stream</c>。
        /// 流由调用方拥有，本方法不负责释放。
        /// </summary>
        public static TitleInfo Resolve(Stream stream, string label)
        {
            if (stream == null || !stream.CanSeek) return null;

            try
            {
                using var udf = OpenDisc(stream);
                return Resolve(new UdfDiscFs(udf));
            }
            catch (Exception e)
            {
                Log($"resolve failed for {label}: {e.Message}");
                return null;
            }
        }

        private static DiscFileSystem OpenDisc(Stream stream)
        {
            try { return new UdfReader(stream); }
            catch
            {
                // Some DVD images contain ISO9660/Joliet instead of UDF.
                stream.Position = 0;
                return new CDReader(stream, true);
            }
        }

        internal static TitleInfo WriteProbeSample(Stream image, string destination, long maxBytes)
        {
            using var disc = OpenDisc(image);
            var title = Resolve(new UdfDiscFs(disc), true);
            if (title == null) throw new InvalidDataException("No supported Blu-ray/DVD title found in ISO");
            var paths = title.ProbePaths.Count > 0 ? title.ProbePaths :
                string.IsNullOrEmpty(title.RelativePath) ? new List<string>() : new List<string> { title.RelativePath };
            if (paths.Count == 0 || paths.Any(path => !disc.FileExists(path)))
                throw new InvalidDataException("The selected playlist contains missing media streams");
            if (paths.Count > 256) throw new InvalidDataException("Playlist exceeds metadata sample limit (256 clips)");

            // Reserve a tail segment as well, so MPEG streams without a playlist duration can
            // expose their final timestamps. Bound aggregate I/O regardless of playlist length.
            var alignment = title.IsBluray ? 192 : 2048;
            var perPart = Math.Min(8L * 1024 * 1024, maxBytes / (paths.Count + 1));
            perPart = perPart / alignment * alignment;
            if (perPart < 64 * 1024) throw new InvalidDataException("Playlist too large for the metadata budget");
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            for (var i = 0; i < paths.Count; i++)
            {
                using var input = disc.OpenFile(paths[i], FileMode.Open, FileAccess.Read);
                if (input.Length == 0) throw new InvalidDataException("Empty media stream in ISO");
                var head = Math.Min(input.Length, perPart);
                CopyProbeBytes(input, output, head);
                if (i == paths.Count - 1 && input.Length > head)
                {
                    var tailStart = Math.Max(head, input.Length - perPart);
                    tailStart = (tailStart + alignment - 1) / alignment * alignment;
                    input.Position = Math.Min(tailStart, input.Length);
                    CopyProbeBytes(input, output, input.Length - input.Position);
                }
            }
            output.Flush();
            return title;
        }

        private static void CopyProbeBytes(Stream input, Stream output, long count)
        {
            var buffer = new byte[64 * 1024];
            while (count > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException("ISO range read ended before the selected stream");
                output.Write(buffer, 0, read);
                count -= read;
            }
        }

        /// <summary>在已挂载的卷根目录（真实文件系统）上解析正片；失败返回 null。</summary>
        public static TitleInfo ResolveFromMount(string driveRoot)
        {
            try
            {
                return Resolve(new LocalDiscFs(driveRoot));
            }
            catch (Exception e)
            {
                Log($"resolve failed for mounted {driveRoot}: {e.Message}");
                return null;
            }
        }

        private static TitleInfo Resolve(IDiscFs fs, bool metadataProbe = false)
        {
            if (fs.DirectoryExists(BlurayPlaylistDir) && fs.DirectoryExists(BlurayStreamDir))
            {
                var bluray = ResolveBluray(fs, metadataProbe);
                if (bluray != null) return bluray;
            }

            if (fs.DirectoryExists(DvdVideoTsDir))
            {
                var dvd = ResolveDvd(fs);
                if (dvd != null) return dvd;
            }

            return null;
        }

        private static TitleInfo ResolveBluray(IDiscFs fs, bool metadataProbe = false)
        {
            PlaylistEntry best = null;
            var hadParsedPlaylist = false;

            foreach (var path in fs.GetFiles(BlurayPlaylistDir))
            {
                if (!path.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)) continue;

                PlaylistEntry entry = null;
                try
                {
                    entry = ParsePlaylist(LeafOf(path), fs.ReadAllBytes(path));
                }
                catch (Exception e)
                {
                    Log($"playlist parse failed for {path}: {e.Message}");
                }

                if (entry == null || entry.Clips.Count == 0) continue;
                hadParsedPlaylist = true;
                var reason = GetPlaylistRejectionReason(entry);
                var uniquePaths = entry.Clips.Select(c => BlurayStreamDir + "\\" + c.Name + ".m2ts")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (reason == null && metadataProbe && uniquePaths.Length > 256)
                    reason = "more than 256 distinct stream files exceed the metadata sampling budget";
                if (reason == null && uniquePaths.Any(path => !fs.FileExists(path)))
                    reason = "playlist references missing stream files";
                if (reason != null)
                {
                    Plugin.Instance?.Logger?.Info($"UdfTitleResolver - skipped playlist {entry.Name}: {reason}");
                    continue;
                }

                // A stable tie break avoids depending on the physical UDF directory order.
                if (best == null || entry.TotalSeconds > best.TotalSeconds + 1.0 / TicksPerSecond ||
                    (Math.Abs(entry.TotalSeconds - best.TotalSeconds) <= 1.0 / TicksPerSecond &&
                     string.Compare(entry.Name, best.Name, StringComparison.OrdinalIgnoreCase) < 0)) best = entry;
            }

            if (best != null)
            {
                Plugin.Instance?.Logger?.Info($"UdfTitleResolver - selected playlist {best.Name}: {best.Clips.Count} play items, {best.TotalSeconds:F1}s");
                if (best.Clips.Count != 1 && !metadataProbe)
                {
                    return new TitleInfo
                    {
                        IsBluray = true,
                        SourceName = best.Name,
                        DurationSeconds = best.TotalSeconds,
                        UnsupportedReason = "Multi-clip Blu-ray playlists require complete title/branch handling; " +
                            "plugin extraction is unavailable for this title. Keeping the original ISO for the host."
                    };
                }
                var clip = best.Clips[0];
                var relative = BlurayStreamDir + "\\" + clip.Name + ".m2ts";

                // 选中的 playlist 携带的 STN_table / 章节，供上层补全音轨、字幕与章节
                var streams = best.Streams;
                var chapters = best.Chapters;

                if (fs.FileExists(relative))
                {
                    return new TitleInfo
                    {
                        IsBluray = true,
                        RelativePath = best.Clips.Count == 1 ? relative : null,
                        ProbePaths = metadataProbe
                            ? best.Clips.Select(c => BlurayStreamDir + "\\" + c.Name + ".m2ts")
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                            : new List<string>(),
                        SourceName = best.Name,
                        DurationSeconds = best.TotalSeconds,
                        AudioStreams = streams.Where(s => s.Kind == BlurayStreamKind.Audio).ToList(),
                        SubtitleStreams = streams.Where(s => s.Kind == BlurayStreamKind.Subtitle).ToList(),
                        Chapters = chapters
                    };
                }

                Log($"selected stream {relative} not found; falling back to largest m2ts");
            }

            if (hadParsedPlaylist)
                return new TitleInfo { IsBluray = true, UnsupportedReason =
                    "No reliable playable playlist remains after validation; refusing an arbitrary stream fallback." };
            return LargestFile(fs, BlurayStreamDir, ".m2ts", true);
        }

        internal static string GetPlaylistRejectionReason(PlaylistEntry entry)
        {
            if (entry == null || entry.Clips.Count == 0 || entry.TotalSeconds <= 0 ||
                double.IsNaN(entry.TotalSeconds) || double.IsInfinity(entry.TotalSeconds))
                return "empty or invalid duration";
            if (entry.Clips.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Duration <= 0 ||
                c.InTime < 0 || double.IsNaN(c.Duration) || double.IsInfinity(c.Duration) ||
                double.IsNaN(c.InTime) || double.IsInfinity(c.InTime)))
                return "invalid play item";

            // Reusing a file at DIFFERENT in/out times is legitimate seamless branching.
            // Only count exact repetitions of the same segment at the 45 kHz MPLS clock.
            var repeats = entry.Clips.GroupBy(c => new
            {
                Name = c.Name.ToUpperInvariant(),
                In = (long)Math.Round(c.InTime * TicksPerSecond),
                Length = (long)Math.Round(c.Duration * TicksPerSecond)
            }).ToList();
            var repeatedCount = repeats.Sum(g => g.Count() - 1);
            var repeatedSeconds = repeats.Sum(g => (g.Count() - 1) * g.First().Duration);
            if (entry.Clips.Count >= 16 && repeatedCount >= 8 &&
                repeatedCount >= entry.Clips.Count * 0.75 && repeatedSeconds >= entry.TotalSeconds * 0.5)
                return $"abnormally repeated segments ({entry.Clips.Count} play items, {repeats.Count} distinct segments)";
            return null;
        }

        private static TitleInfo ResolveDvd(IDiscFs fs)
        {
            if (fs.FileExists(DvdMainTitle))
            {
                return new TitleInfo
                {
                    IsBluray = false,
                    RelativePath = DvdMainTitle,
                    SourceName = LeafOf(DvdMainTitle)
                };
            }

            return LargestFile(fs, DvdVideoTsDir, ".vob", false);
        }

        /// <summary>兜底：目录内体积最大的指定扩展名文件。</summary>
        private static TitleInfo LargestFile(IDiscFs fs, string dir, string extension, bool isBluray)
        {
            var largest = fs.GetFiles(dir)
                .Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .Select(p => new { Path = p, Size = fs.GetFileLength(p) })
                .Where(x => x.Size > 0)
                .OrderByDescending(x => x.Size)
                .FirstOrDefault();

            if (largest == null) return null;

            return new TitleInfo
            {
                IsBluray = isBluray,
                RelativePath = largest.Path,
                SourceName = LeafOf(largest.Path)
            };
        }

        internal sealed class PlaylistEntry
        {
            public string Name;
            public double TotalSeconds;
            public List<ClipEntry> Clips = new List<ClipEntry>();

            /// <summary>STN_table 中解析出的基本流（音轨 + 字幕）。</summary>
            public List<MplsStreamInfo> Streams = new List<MplsStreamInfo>();

            /// <summary>PlayListMark 解析出的章节时间点（秒，升序）。</summary>
            public List<double> Chapters = new List<double>();
        }

        internal sealed class ClipEntry
        {
            public string Name;
            public double Duration;

            /// <summary>
            /// 该 PlayItem 的 IN_time（45kHz → 秒，**绝对值**，含 m2ts 自身的 PTS 基址）。
            /// 章节时间戳同为绝对值，需减去本值才是标题内偏移。
            /// </summary>
            public double InTime;

            /// <summary>该 PlayItem 在 MPLS 文件内的绝对起始偏移（指向 itemLength 字段）。</summary>
            public int ItemStart;
        }

        /// <summary>
        /// 轻量 MPLS 解析（MPLS0100/0200/0300）。结构以 libbluray 的 mpls_parse.c 为准。
        ///
        /// 头部（magic 8 字节之后均为大端）：
        /// <c>PlayList_start_address</c>(4) <c>PlayListMark_start_address</c>(4) <c>ExtensionData_start_address</c>(4)
        ///
        /// PlayList = 长度4 + 保留2 + PlayItem 数2 + SubPath 数2，随后是内联的 PlayItem 列表。
        ///
        /// PlayItem = 长度2 + Clip 名5 + codec4
        ///          + 标志2（保留 11bit + 多角度 1bit + connection_condition 4bit）+ stc_id 1
        ///          + IN_time 4 + OUT_time 4
        ///          + <b>UO_mask_table 8</b> + 随机访问标志 1 + still_mode 1 + still_time 2
        ///          + [多角度：angle_count 1 + 标志 1 + 每个额外 angle 10 字节]
        ///          + <b>STN_table（紧跟其后、内联；没有起始地址/长度字段）</b>
        ///
        /// STN_table = 长度2 + 保留2 + 8 个流计数（各 1 字节）+ 保留4 + 各流「entry 段 + attributes 段」逐条交错。
        ///
        /// ⚠️ 历史缺陷：旧实现把 UO_mask_table 的前 4 字节当成 STN 的「起始地址 + 长度」。
        /// 常见全零 UO mask 会让 stnLength == 0 从而整段跳过，音轨/字幕补全永远拿不到数据。
        ///
        /// PlayListMark = 长度4 + mark 数2 + 每条 14 字节（无长度后保留字节，见 ParsePlayListMark）。
        /// 所有 MPLS 时间戳为 45kHz。
        /// </summary>
        internal static PlaylistEntry ParsePlaylist(string name, byte[] data)
        {
            if (data == null || data.Length < 40) return null;

            var magic = Encoding.ASCII.GetString(data, 0, 8);
            if (magic != "MPLS0100" && magic != "MPLS0200" && magic != "MPLS0300") return null;

            var pos = 8;
            var playlistStart = ReadUInt32(data, ref pos);
            var playlistMarkStart = ReadUInt32(data, ref pos);
            if (playlistStart + 10u > (uint)data.Length) return null;

            var entry = new PlaylistEntry { Name = name };

            // ---- PlayList（PlayItem 列表 + 各 item 内联的 STN_table）----
            pos = (int)playlistStart;
            ReadUInt32(data, ref pos);                  // PlayList 长度
            ReadUInt16(data, ref pos);                  // 保留
            var itemCount = ReadUInt16(data, ref pos);  // PlayItem 数
            ReadUInt16(data, ref pos);                  // SubPath 数

            for (var i = 0; i < itemCount; i++)
            {
                if (pos + 22 > data.Length) return null;

                var itemStart = pos;
                var itemLength = ReadUInt16(data, ref pos);   // PlayItem 长度（不含本字段，含其后的 STN_table）
                var clipName = ReadAscii(data, 5, ref pos);
                ReadAscii(data, 4, ref pos);                  // codec identifier（M2TS / FMTS）

                // 保留 11 bit + is_multi_angle 1 bit + connection_condition 4 bit
                var flags = ReadUInt16(data, ref pos);
                var isMultiAngle = (flags & 0x0010) != 0;
                pos++;                                        // stc_id（不使用，但必须消费）

                var inTime = ReadUInt32(data, ref pos) / TicksPerSecond;
                var outTime = ReadUInt32(data, ref pos) / TicksPerSecond;
                var duration = Math.Max(0, outTime - inTime);

                if (pos + 12 > data.Length) return null;

                pos += 8;                                     // UO_mask_table（不是 STN 地址！）
                pos += 1;                                     // random_access_flag(1) + 保留(7)
                pos += 1;                                     // still_mode
                pos += 2;                                     // still_time（still_mode != 0x01 时为保留，占位不变）

                if (isMultiAngle)
                {
                    if (pos + 2 > data.Length) return null;
                    var angleCount = data[pos++];
                    pos += 1;                                 // 保留6 + is_different_audio1 + is_seamless_angle1
                    if (angleCount > 1) pos += (angleCount - 1) * 10;   // 额外 angle：clip5 + codec4 + stc1
                }

                entry.Clips.Add(new ClipEntry
                {
                    Name = clipName,
                    Duration = duration,
                    InTime = inTime,
                    ItemStart = itemStart
                });
                entry.TotalSeconds += duration;

                // STN_table 内联在 PlayItem 尾部，边界由 PlayItem 长度限定
                var itemEnd = itemStart + 2 + itemLength;
                try
                {
                    ParseStnTable(data, pos, itemEnd, entry.Streams);
                }
                catch (Exception e)
                {
                    Log($"STN_table parse failed in {name}: {e.Message}");
                }

                var next = itemEnd;
                if (next <= itemStart || next > data.Length) return null;
                pos = next;
            }

            // ---- PlayListMark（章节）----
            if (playlistMarkStart > 0 && playlistMarkStart + 6u < (uint)data.Length)
            {
                try
                {
                    ParsePlayListMark(data, (int)playlistMarkStart, entry);
                }
                catch (Exception e)
                {
                    Log($"PlayListMark parse failed in {name}: {e.Message}");
                }
            }

            return entry;
        }

        // ================= STN_table =================

        /// <summary>
        /// 解析 STN_table（BD-ROM MPLS，内联在 PlayItem 尾部）。
        ///
        /// 布局：长度2 + 保留2 + 8 个流计数（video/audio/pg/ig/次级音频/次级视频/PIP-PG/DV，各 1 字节）
        ///       + 保留4 + 各流「stream_entry 段 + stream_attributes 段」逐条交错。
        /// </summary>
        private static void ParseStnTable(byte[] data, int stnStart, int limit, List<MplsStreamInfo> output)
        {
            var hardEnd = Math.Min(limit, data.Length);
            if (stnStart <= 0 || stnStart + 20 > hardEnd) return;

            var pos = stnStart;
            var stnLength = ReadUInt16(data, ref pos);
            ReadUInt16(data, ref pos);                        // 保留

            var numVideo = data[pos++];
            var numAudio = data[pos++];
            var numPg = data[pos++];
            var numIg = data[pos++];
            var numSecAudio = data[pos++];
            var numSecVideo = data[pos++];
            var numPipPg = data[pos++];
            var numDv = data[pos++];
            pos += 4;                                         // 保留

            // STN 自身长度不含开头 2 字节；PlayItem 长度可能更宽，取较紧的那个
            var end = Math.Min(stnStart + 2 + stnLength, hardEnd);

            for (var i = 0; i < numVideo; i++) ReadStream(data, ref pos, end, false, output);
            for (var i = 0; i < numAudio; i++) ReadStream(data, ref pos, end, true, output);
            for (var i = 0; i < numPg + numPipPg; i++) ReadStream(data, ref pos, end, true, output);
            for (var i = 0; i < numIg; i++) ReadStream(data, ref pos, end, true, output);
            for (var i = 0; i < numDv; i++) ReadStream(data, ref pos, end, false, output);

            // 次级音频/视频：每条流后还带引用表，必须跳过才能保持后续偏移正确
            for (var i = 0; i < numSecAudio; i++)
            {
                ReadStream(data, ref pos, end, true, output);
                SkipRefTable(data, ref pos, end);
            }

            for (var i = 0; i < numSecVideo; i++)
            {
                ReadStream(data, ref pos, end, true, output);
                SkipRefTable(data, ref pos, end);              // 次级音频引用
                SkipRefTable(data, ref pos, end);              // PIP-PG 引用
            }
        }

        /// <summary>跳过一段引用表：数量1 + 保留1 + 引用项（奇数长度补齐）。</summary>
        private static void SkipRefTable(byte[] data, ref int pos, int end)
        {
            if (pos + 2 > end)
            {
                pos = end;
                return;
            }

            var count = data[pos++];
            pos += 1;                                          // 保留
            if (count > 0) pos += count + (count % 2);
            if (pos > end) pos = end;
        }

        /// <summary>
        /// 读一条流：entry 段（stream_type + PID）→ attributes 段（coding_type + 属性），
        /// 两段各自带 1 字节长度并按长度跳齐。emit=false 时只推进偏移（视频流不需要输出）。
        /// </summary>
        private static void ReadStream(byte[] data, ref int pos, int end, bool emit, List<MplsStreamInfo> output)
        {
            // ---- entry 段 ----
            if (pos + 2 > end)
            {
                pos = end;
                return;
            }

            var entryLen = data[pos];
            var entryStart = pos + 1;
            var entryEnd = Math.Min(entryStart + entryLen, end);

            var p = entryStart;
            var streamType = p < entryEnd ? data[p++] : 0;
            var pid = 0;

            switch (streamType)
            {
                case 1:                                        // 主路径
                    if (p + 2 <= entryEnd) pid = ReadUInt16(data, ref p);
                    break;
                case 2:                                        // 子路径（含 sub-clip）
                    if (p + 4 <= entryEnd)
                    {
                        p += 2;
                        pid = ReadUInt16(data, ref p);
                    }
                    break;
                case 3:
                case 4:                                        // 子路径
                    if (p + 3 <= entryEnd)
                    {
                        p += 1;
                        pid = ReadUInt16(data, ref p);
                    }
                    break;
            }

            pos = entryEnd;

            // ---- attributes 段 ----
            if (pos + 2 > end)
            {
                pos = end;
                return;
            }

            var attrLen = data[pos];
            var attrStart = pos + 1;
            var attrEnd = Math.Min(attrStart + attrLen, end);

            if (emit && attrStart < attrEnd)
            {
                // 同一 PID 在多个 PlayItem 里重复出现时只保留第一次
                if (!output.Any(s => s.Pid == pid))
                {
                    var info = new MplsStreamInfo { Pid = pid };
                    FillStreamAttributes(data, attrStart, attrEnd, info);
                    output.Add(info);
                }
            }

            pos = attrEnd;
        }

        /// <summary>
        /// 解析单条 stream_attributes。coding_type 是**完整 8 位**（0x90=PG、0x81=AC3…），
        /// 不能取高 4 位——那样 0x90 会变成 0x09 被误判成 DTS-HD MA 音频。
        /// 各 coding_type 的后续字段长度不同，这里只取需要的字段；偏移由调用方按段长度跳齐。
        /// </summary>
        private static void FillStreamAttributes(byte[] data, int start, int end, MplsStreamInfo info)
        {
            var pos = start;
            var codingType = data[pos++];
            info.CodingRaw = codingType;

            switch (codingType)
            {
                // ---- 音频：format(高4) + rate(低4) + 语言3 ----
                case 0x03:
                case 0x04:
                case 0x80:
                case 0x81:
                case 0x82:
                case 0x83:
                case 0x84:
                case 0x85:
                case 0x86:
                case 0xa1:
                case 0xa2:
                {
                    var b = pos < end ? data[pos++] : 0;
                    info.Kind = BlurayStreamKind.Audio;
                    info.Coding = MapAudioCoding(codingType);
                    info.Channels = MapChannels((b >> 4) & 0x0F);
                    info.SampleRate = MapSampleRate(b & 0x0F);
                    info.Language = NormalizeLanguage(pos + 3 <= end ? ReadAscii(data, 3, ref pos) : string.Empty);
                    break;
                }

                // ---- PG / IG：语言3 ----
                case 0x90:
                case 0x91:
                {
                    info.Kind = BlurayStreamKind.Subtitle;
                    info.Coding = codingType == 0x90 ? BlurayCodingType.Pg : BlurayCodingType.Ig;
                    info.Language = NormalizeLanguage(pos + 3 <= end ? ReadAscii(data, 3, ref pos) : string.Empty);
                    break;
                }

                // ---- Text：char_code(8) + 语言3 ----
                case 0x92:
                {
                    if (pos < end) pos++;
                    info.Kind = BlurayStreamKind.Text;
                    info.Coding = BlurayCodingType.Text;
                    info.Language = NormalizeLanguage(pos + 3 <= end ? ReadAscii(data, 3, ref pos) : string.Empty);
                    break;
                }

                // ---- 视频：format(4) + rate(4)，不需要输出 ----
                case 0x01:
                case 0x02:
                case 0x1b:
                case 0x24:
                case 0xea:
                    info.Kind = BlurayStreamKind.Unknown;
                    info.Coding = BlurayCodingType.Other;
                    break;

                default:
                    info.Kind = BlurayStreamKind.Unknown;
                    info.Coding = BlurayCodingType.Unknown;
                    break;
            }
        }

        /// <summary>音频 coding_type（完整 8 位）→ 编码类型。</summary>
        private static BlurayCodingType MapAudioCoding(int codingType) => codingType switch
        {
            0x80 => BlurayCodingType.Pcm,
            0x81 => BlurayCodingType.Ac3,
            0x82 => BlurayCodingType.Dts,
            0x83 => BlurayCodingType.TrueHd,
            0x84 => BlurayCodingType.Eac3,
            0x85 => BlurayCodingType.DtsHdHra,
            0x86 => BlurayCodingType.DtsHdMa,
            _ => BlurayCodingType.Other
        };

        // ================= PlayListMark（章节） =================

        /// <summary>
        /// 解析 PlayListMark（BD-ROM MPLS）：
        /// <c>长度4 + mark 数2 + 每条 mark 14 字节</c>；
        /// 每条 mark = <c>保留1 + mark_type1 + PlayItem_ref2 + timestamp4 + duration4 + entry_ES_PID2</c>。
        /// mark_type = 1 为章节标记（entry mark）。其余（如 0=PlayList mark / link point）不计入章节。
        ///
        /// ⚠️ 易错点：PlayListMark **没有** PlayList() 那样的「长度后的保留字段」——紧跟长度的 2 字节
        /// 就是 mark 数（保留字节在**每条 mark 内部**）。实测样本 <c>00000.mpls</c>：
        /// <c>190:00 00 00 10 | 194:00 01 | 196:00 01 0000 0b43e940 ...</c> →
        /// 长度=16(=2+14×1)、mark 数=1、唯一 mark 为 entry mark@4200s。长度字段可作一致性校验
        /// （应满足 <c>length == 2 + 14×count</c>），不符则放弃解析，避免读到越界垃圾。
        ///
        /// 时间戳为 45kHz 的**绝对 PTS**（含 m2ts 自身 PTS 基址，实测样本基址 4200s），
        /// 故须按 <c>PlayItem_ref</c> 减去对应 PlayItem 的 IN_time，再加上其前面各 PlayItem 的累计时长，
        /// 才是 Emby 需要的「标题内偏移秒」。
        /// </summary>
        internal static void ParsePlayListMark(byte[] data, int markStart, PlaylistEntry entry)
        {
            if (markStart <= 0 || markStart + 6 > data.Length) return;

            var pos = markStart;
            var sectionLength = ReadUInt32(data, ref pos);   // PlayListMark 长度（= 2 + 14×mark 数）
            if (pos + 2 > data.Length) return;
            var markCount = ReadUInt16(data, ref pos);       // mark 数（紧跟长度，无保留字节）

            // 一致性校验：长度字段必须与「2 + 14×count」吻合，否则视为结构不符，宁可不解析
            if (sectionLength != (uint)(2 + 14 * markCount)) return;

            var marks = new List<double>(markCount);

            for (var i = 0; i < markCount; i++)
            {
                if (pos + 14 > data.Length) break;

                pos += 1;                            // 保留（每条 mark 内部）
                var markType = data[pos++];          // mark_type
                var playItemRef = ReadUInt16(data, ref pos);
                var timestamp = ReadUInt32(data, ref pos) / TicksPerSecond;  // 绝对 PTS
                ReadUInt32(data, ref pos);           // duration
                ReadUInt16(data, ref pos);           // entry_ES_PID

                // mark_type: 1 = entry mark（章节）
                if (markType != 1) continue;

                var relative = ToTitleRelativeSeconds(entry, playItemRef, timestamp);
                if (relative >= 0) marks.Add(relative);
            }

            marks.Sort();

            // 去重：相邻 <1ms 视为同一章节（多 PlayItem 时首尾相接可能产生重复点）
            var deduped = new List<double>(marks.Count);
            foreach (var mark in marks)
            {
                if (deduped.Count == 0 || mark - deduped[deduped.Count - 1] > 0.001) deduped.Add(mark);
            }

            entry.Chapters = deduped;
        }

        /// <summary>
        /// 把 PlayListMark 的绝对 PTS 换算为「标题内偏移秒」：
        /// 前面各 PlayItem 的累计时长 + (mark 绝对时间 − 本 PlayItem 的 IN_time)。
        /// </summary>
        private static double ToTitleRelativeSeconds(PlaylistEntry entry, int playItemRef, double absoluteSeconds)
        {
            if (entry.Clips.Count == 0) return -1;

            // PlayItem_ref 越界时回退到第一个 PlayItem（单 PlayItem 盘最常见，ref 恒为 0）
            var index = playItemRef >= 0 && playItemRef < entry.Clips.Count ? playItemRef : 0;

            var cumulative = 0.0;
            for (var i = 0; i < index; i++) cumulative += entry.Clips[i].Duration;

            var relative = cumulative + (absoluteSeconds - entry.Clips[index].InTime);
            return relative < 0 ? 0 : relative;
        }

        // ================= 语言与属性映射 =================

        /// <summary>把 3 字符语言码规范化为小写；全 0 或非法返回 "und"。</summary>
        private static string NormalizeLanguage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "und";

            var cleaned = raw.Trim('\0', ' ');
            if (cleaned.Length == 0) return "und";

            // 全 0 字节 / 非 ASCII 可打印 → 未知
            if (cleaned.All(c => c == '\0' || c < 0x21 || c > 0x7E)) return "und";

            var lower = cleaned.ToLowerInvariant();
            return lower == "und" ? "und" : lower;
        }

        /// <summary>音频声道数：BD 音频 attribute 的 channel 编码（0b0001=1ch ... 0b0111=8ch）。</summary>
        private static int MapChannels(int channelCode) => channelCode switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            5 => 5,
            6 => 6,
            7 => 8,
            8 => 2,   // 立体声（按规范保留）
            _ => 0
        };

        /// <summary>音频采样率：rate code → Hz。</summary>
        private static int MapSampleRate(int rateCode) => rateCode switch
        {
            1 => 48000,
            4 => 96000,
            5 => 192000,
            _ => 0
        };

        private static ushort ReadUInt16(byte[] data, ref int pos)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
            pos += 2;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int pos)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            pos += 4;
            return value;
        }

        private static string ReadAscii(byte[] data, int count, ref int pos)
        {
            var value = Encoding.ASCII.GetString(data, pos, count);
            pos += count;
            return value;
        }

        /// <summary>从已归一化为 <c>\</c> 分隔的相对路径里取文件名。</summary>
        private static string LeafOf(string path)
        {
            var index = path.LastIndexOf('\\');
            return index >= 0 ? path.Substring(index + 1) : path;
        }

        private static void Log(string message)
        {
            try
            {
                Plugin.Instance?.Logger?.Debug("UdfTitleResolver - " + message);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>碟内文件系统的最小抽象：进程内 UDF 与已挂载卷各实现一份，路径统一为 <c>\</c> 分隔的相对路径。</summary>
        private interface IDiscFs
        {
            bool DirectoryExists(string relativeDir);
            bool FileExists(string relativePath);
            string[] GetFiles(string relativeDir);
            long GetFileLength(string relativePath);
            byte[] ReadAllBytes(string relativePath);
        }

        private sealed class UdfDiscFs : IDiscFs
        {
            private readonly DiscFileSystem _udf;

            public UdfDiscFs(DiscFileSystem udf)
            {
                _udf = udf;
            }

            public bool DirectoryExists(string relativeDir) => _udf.DirectoryExists(relativeDir);

            public bool FileExists(string relativePath) => _udf.FileExists(relativePath);

            public string[] GetFiles(string relativeDir)
            {
                try
                {
                    // 注意：DiscUtils 的 searchPattern 按 Windows 语义实现，"*" **不匹配带扩展名的文件**
                    // （实测 BDMV\PLAYLIST 下 "*" → 0 个，"*.*" / "*.mpls" → 1 个）。
                    // 用 GetFileSystemEntries 全量列举再过滤，避免再踩这个坑。
                    return _udf.GetFileSystemEntries(relativeDir)
                        .Where(entry => _udf.FileExists(entry))
                        .Select(Normalize)
                        .ToArray();
                }
                catch (Exception e)
                {
                    Log($"list {relativeDir} failed: {e.Message}");
                    return Array.Empty<string>();
                }
            }

            public long GetFileLength(string relativePath)
            {
                try
                {
                    return _udf.GetFileLength(relativePath);
                }
                catch (Exception e)
                {
                    Log($"length of {relativePath} failed: {e.Message}");
                    return 0;
                }
            }

            public byte[] ReadAllBytes(string relativePath)
            {
                using var source = _udf.OpenFile(relativePath, FileMode.Open, FileAccess.Read);
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                return buffer.ToArray();
            }

            private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');
        }

        private sealed class LocalDiscFs : IDiscFs
        {
            private readonly string _root;

            public LocalDiscFs(string driveRoot)
            {
                _root = driveRoot.EndsWith("\\") || driveRoot.EndsWith("/") ? driveRoot : driveRoot + "\\";
            }

            public bool DirectoryExists(string relativeDir) => Directory.Exists(ToLocal(relativeDir));

            public bool FileExists(string relativePath) => File.Exists(ToLocal(relativePath));

            public string[] GetFiles(string relativeDir)
            {
                var dir = ToLocal(relativeDir);

                try
                {
                    return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Select(Normalize).ToArray() : Array.Empty<string>();
                }
                catch
                {
                    return Array.Empty<string>();
                }
            }

            public long GetFileLength(string relativePath)
            {
                try
                {
                    var info = new FileInfo(ToLocal(relativePath));
                    return info.Exists ? info.Length : 0;
                }
                catch
                {
                    return 0;
                }
            }

            public byte[] ReadAllBytes(string relativePath) => File.ReadAllBytes(ToLocal(relativePath));

            private string ToLocal(string relativePath) => Path.Combine(_root, relativePath);

            private string Normalize(string fullPath)
            {
                var relative = fullPath.StartsWith(_root, StringComparison.Ordinal)
                    ? fullPath.Substring(_root.Length)
                    : fullPath;

                return relative.Replace('/', '\\').TrimStart('\\');
            }
        }
    }
}
