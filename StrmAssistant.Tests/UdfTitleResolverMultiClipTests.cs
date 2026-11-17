using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 多段无缝分支原盘的选片回归。
    ///
    /// 真机事故：300 段 playlist 在元数据采样阶段被「超过 256 段」整段拒绝，
    /// 于是回退到裸 ISO 探测 —— 只剩第一路视频、码率也是脏值（用户看到 17 kbps，
    /// 1080p Dolby Vision 增强层消失）。采样现在只取首尾各 128 段，代价与片段数无关，
    /// 选片阶段不应再按片段数拒绝。
    /// </summary>
    public class UdfTitleResolverMultiClipTests
    {
        private const int ClipCount = 300;
        private const int SecondsPerClip = 2;

        // ---------- MPLS 构造 ----------

        private static void Be16(List<byte> buffer, int value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static void Be32(List<byte> buffer, uint value)
        {
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        private static byte[] BuildStnTable()
        {
            var entries = new List<byte>();

            foreach (var pid in new[] { 0x1011, 0x1012 })
            {
                entries.Add(3);            // entry 段长度
                entries.Add(1);            // stream_type = 主路径
                Be16(entries, pid);
                entries.Add(3);            // attributes 段长度
                entries.Add(0x24);         // HEVC
                entries.Add(0x0F);         // format
                entries.Add(0x0F);         // rate
            }

            entries.Add(3);
            entries.Add(1);
            Be16(entries, 0x1100);
            entries.Add(5);
            entries.Add(0x83);             // TrueHD
            entries.Add(0x61);             // 6 声道 / 48kHz
            entries.AddRange(Encoding.ASCII.GetBytes("eng"));

            var body = new List<byte> { 0, 0 };                 // 保留
            body.AddRange(new byte[] { 2, 1, 0, 0, 0, 0, 0, 0 }); // 8 类流计数
            body.AddRange(new byte[4]);                        // 保留
            body.AddRange(entries);

            var stn = new List<byte>();
            Be16(stn, body.Count);
            stn.AddRange(body);
            return stn.ToArray();
        }

        private static byte[] BuildMpls(int clipCount)
        {
            var stn = BuildStnTable();
            var items = new List<byte>();

            for (var i = 0; i < clipCount; i++)
            {
                var body = new List<byte>();
                body.AddRange(Encoding.ASCII.GetBytes((i + 1).ToString("D5")));
                body.AddRange(Encoding.ASCII.GetBytes("M2TS"));
                Be16(body, 0);                                  // flags
                body.Add(0);                                    // stc_id
                Be32(body, 0);                                  // IN_time
                Be32(body, (uint)(SecondsPerClip * 45000));     // OUT_time
                body.AddRange(new byte[8]);                     // UO_mask_table
                body.Add(0);                                    // random_access_flag
                body.Add(0);                                    // still_mode
                Be16(body, 0);                                  // still_time
                body.AddRange(stn);

                Be16(items, body.Count);
                items.AddRange(body);
            }

            var playlist = new List<byte>();
            Be16(playlist, 0);                                  // 保留
            Be16(playlist, clipCount);                          // PlayItem 数
            Be16(playlist, 0);                                  // SubPath 数
            playlist.AddRange(items);

            var section = new List<byte>();
            Be32(section, (uint)playlist.Count);
            section.AddRange(playlist);

            var header = new List<byte>(Encoding.ASCII.GetBytes("MPLS0200"));
            Be32(header, 40);
            Be32(header, (uint)(40 + section.Count));
            Be32(header, 0);
            header.AddRange(new byte[40 - header.Count]);

            var mark = new List<byte>();
            Be32(mark, 2);                                      // 长度（仅 mark 数）
            Be16(mark, 0);                                      // mark 数 = 0

            var file = new List<byte>(header);
            file.AddRange(section);
            file.AddRange(mark);
            return file.ToArray();
        }

        // ---------- 内存盘 ----------

        private sealed class FakeDiscFs : UdfTitleResolver.IDiscFs
        {
            private readonly Dictionary<string, byte[]> _files;

            public FakeDiscFs(Dictionary<string, byte[]> files)
            {
                _files = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase);
            }

            public bool DirectoryExists(string relativeDir) =>
                _files.Keys.Any(k => k.StartsWith(relativeDir + "\\", StringComparison.OrdinalIgnoreCase));

            public bool FileExists(string relativePath) => _files.ContainsKey(relativePath);

            public string[] GetFiles(string relativeDir)
            {
                var prefix = relativeDir + "\\";
                return _files.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                k.IndexOf('\\', prefix.Length) < 0)
                    .ToArray();
            }

            public long GetFileLength(string relativePath) =>
                _files.TryGetValue(relativePath, out var data) ? data.Length : 0;

            public byte[] ReadAllBytes(string relativePath) =>
                _files.TryGetValue(relativePath, out var data) ? data : Array.Empty<byte>();
        }

        private static FakeDiscFs BuildDisc(int clipCount)
        {
            var files = new Dictionary<string, byte[]>
            {
                [@"BDMV\PLAYLIST\00000.mpls"] = BuildMpls(clipCount)
            };

            for (var i = 0; i < clipCount; i++)
                files[@"BDMV\STREAM\" + (i + 1).ToString("D5") + ".m2ts"] = new byte[1024];

            return new FakeDiscFs(files);
        }

        // ---------- 用例 ----------

        [Fact]
        public void MetadataProbe_KeepsLongMultiClipPlaylist()
        {
            var info = UdfTitleResolver.ResolveFromFileSystem(BuildDisc(ClipCount), true);

            Assert.NotNull(info);
            Assert.Null(info.UnsupportedReason);
            Assert.Equal(ClipCount, info.ProbePaths.Count);
            Assert.Equal(ClipCount * SecondsPerClip, info.DurationSeconds, 3);
            Assert.Equal(2, info.VideoStreams.Count);
            Assert.Single(info.AudioStreams);
        }

        [Fact]
        public void PlaybackProbe_StillRejectsMultiClipTitle()
        {
            var info = UdfTitleResolver.ResolveFromFileSystem(BuildDisc(ClipCount), false);

            Assert.NotNull(info);
            Assert.False(string.IsNullOrEmpty(info.UnsupportedReason));
        }

        [Fact]
        public void SingleClipPlaylist_ResolvesRelativePath()
        {
            var info = UdfTitleResolver.ResolveFromFileSystem(BuildDisc(1), false);

            Assert.NotNull(info);
            Assert.Null(info.UnsupportedReason);
            Assert.Equal(@"BDMV\STREAM\00001.m2ts", info.RelativePath);
            Assert.Equal(SecondsPerClip, info.DurationSeconds, 3);
        }

        // ---------- 采样覆盖 ----------

        [Fact]
        public void SampleSelection_KeepsEveryClipWhenUnderLimit()
        {
            var paths = Enumerable.Range(1, 100).Select(i => "clip" + i).ToList();

            var sampled = UdfTitleResolver.SelectSamplePaths(paths, 256);

            Assert.Equal(paths, sampled);
        }

        [Fact]
        public void SampleSelection_IsUniformAcrossLongPlaylist()
        {
            var paths = Enumerable.Range(1, 300).Select(i => "clip" + i).ToList();

            var sampled = UdfTitleResolver.SelectSamplePaths(paths, 256);

            Assert.Equal(256, sampled.Count);
            Assert.Equal(paths[0], sampled[0]);
            Assert.Equal(paths[^1], sampled[^1]);
            Assert.Equal(sampled.Count, sampled.Distinct().Count());
            // 保持原顺序，且不是「前 128 + 后 128」那种两段式
            var indices = sampled.Select(p => paths.IndexOf(p)).ToList();
            Assert.True(indices.Zip(indices.Skip(1), (a, b) => b > a).All(x => x));
            Assert.Contains(indices, i => i > 100 && i < 200);
        }

        [Fact]
        public void SampleSelection_HandlesThousandClips()
        {
            var paths = Enumerable.Range(1, 1000).Select(i => "clip" + i).ToList();

            var sampled = UdfTitleResolver.SelectSamplePaths(paths, 256);

            Assert.Equal(256, sampled.Count);
            Assert.Equal(paths[0], sampled[0]);
            Assert.Equal(paths[^1], sampled[^1]);
            Assert.Equal(256, sampled.Distinct().Count());
        }
    }
}
