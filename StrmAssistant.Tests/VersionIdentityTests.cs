using System.IO;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 「删除整季某版本」的候选归属判定（回归 SA-01）。
    ///
    /// 旧实现只看名称相似度：长文件名里 1080p 与 2160p 的相似度可达 0.94，
    /// 超过 0.92 阈值就被判成同一版本，删除时会连带删掉另一版本的真实文件。
    /// 现在要求「同目录 + 分辨率一致 + 编码/片源（若有）一致」，判不明一律排除。
    /// </summary>
    public class VersionIdentityTests
    {
        private static bool Same(string targetPath, string targetName, string candidatePath, string candidateName,
            out string reason) =>
            VersionIdentity.IsSameVersion(targetPath, targetName, candidatePath, candidateName, out reason);

        // 测试路径必须用平台分隔符拼：Linux 上 '\' 不是分隔符，
        // Path.GetDirectoryName(@"D:\TV\A\f.mkv") 会返回空串，导致「不同目录」用例误判。
        private static string Dir(params string[] parts) => Path.Combine(parts);

        [Theory]
        [InlineData("A.Show.S01E01.1080p.WEB-DL.H.264-GRP", "1080")]
        [InlineData("A.Show.S01E01.2160p.WEB-DL.H.265-GRP", "2160")]
        [InlineData("A.Show.S01E01.4K.HDR", "2160")]
        [InlineData("A.Show.S01E01.720p", "720")]
        [InlineData("A.Show.S01E01", null)]
        public void ParsesResolution(string name, string expected)
        {
            Assert.Equal(expected, VersionIdentity.ParseResolutionToken(name));
        }

        [Theory]
        [InlineData("Show.1080p.H.264-GRP", "h264")]
        [InlineData("Show.1080p.x264-GRP", "h264")]
        [InlineData("Show.1080p.HEVC-GRP", "h265")]
        [InlineData("Show.1080p.H.265-GRP", "h265")]
        [InlineData("Show.1080p.AV1-GRP", "av1")]
        [InlineData("Show.1080p-GRP", null)]
        public void ParsesCodec(string name, string expected)
        {
            Assert.Equal(expected, VersionIdentity.ParseCodecToken(name));
        }

        [Theory]
        [InlineData("Show.1080p.WEB-DL", "webdl")]
        [InlineData("Show.1080p.WEBRip", "webrip")]
        [InlineData("Show.1080p.BluRay", "bluray")]
        [InlineData("Show.1080p.Blu-Ray", "bluray")]
        [InlineData("Show.1080p.REMUX", "remux")]
        [InlineData("Show.1080p", null)]
        public void ParsesSource(string name, string expected)
        {
            Assert.Equal(expected, VersionIdentity.ParseSourceToken(name));
        }

        [Fact]
        public void RejectsDifferentResolution_InSameFolder()
        {
            // 审查报告给出的真实场景：长文件名、仅分辨率不同，旧实现相似度 0.944 > 0.92
            var dir = Dir("D:", "TV", "Show", "Season 1");
            const string targetName = "A.Very.Long.Television.Series.Name.S01E01.1080p.WEB-DL.DDP5.1.H.264-GROUP";
            const string candidateName = "A.Very.Long.Television.Series.Name.S01E02.2160p.WEB-DL.DDP5.1.H.264-GROUP";

            Assert.False(Same(Path.Combine(dir, targetName + ".mkv"), targetName,
                Path.Combine(dir, candidateName + ".mkv"), candidateName, out var reason));
            Assert.Contains("resolution", reason);
        }

        [Fact]
        public void AcceptsSameResolution_AcrossEpisodes()
        {
            var dir = Dir("D:", "TV", "Show", "Season 1");
            const string a = "Show.S01E01.1080p.WEB-DL.H.264-GRP";
            const string b = "Show.S01E02.1080p.WEB-DL.H.264-GRP";

            Assert.True(Same(Path.Combine(dir, a + ".mkv"), a, Path.Combine(dir, b + ".mkv"), b, out _));
        }

        [Fact]
        public void RejectsDifferentCodec()
        {
            var dir = Dir("D:", "TV", "Show");
            const string a = "Show.S01E01.1080p.WEB-DL.H.264-GRP";
            const string b = "Show.S01E02.1080p.WEB-DL.HEVC-GRP";

            Assert.False(Same(Path.Combine(dir, a + ".mkv"), a, Path.Combine(dir, b + ".mkv"), b, out var reason));
            Assert.Contains("codec", reason);
        }

        [Fact]
        public void RejectsDifferentFolder()
        {
            var dirA = Dir("D:", "TV", "Show", "A");
            var dirB = Dir("D:", "TV", "Show", "B");
            const string a = "Show.S01E01.1080p.WEB-DL";
            const string b = "Show.S01E02.1080p.WEB-DL";

            Assert.False(Same(Path.Combine(dirA, a + ".mkv"), a,
                Path.Combine(dirB, b + ".mkv"), b, out var reason));
            Assert.Contains("folder", reason);
        }

        [Fact]
        public void RejectsWhenResolutionTokenMissing()
        {
            // 分辨率标记缺失时无法可靠区分版本 → 保守排除，避免误删
            var dir = Dir("D:", "TV", "Show");
            const string a = "Show.S01E01.WEB-DL";
            const string b = "Show.S01E02.WEB-DL";

            Assert.False(Same(Path.Combine(dir, a + ".mkv"), a, Path.Combine(dir, b + ".mkv"), b, out var reason));
            Assert.Contains("missing", reason);
        }
    }
}
