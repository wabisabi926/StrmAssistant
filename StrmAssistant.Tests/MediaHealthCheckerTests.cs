using System.Collections.Generic;
using System.Linq;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 片源体检 · 声明级检查器的判据测试。
    /// 重点是「只报有明确反证的问题」：探测字段为空时必须放过，避免把未知当错误。
    /// </summary>
    public class MediaHealthCheckerTests
    {
        private static MediaHealthStream Video(int? height, string colorTransfer = "bt709", int? bitDepth = 8,
            string extendedVideoType = "None", int? width = null) =>
            new MediaHealthStream
            {
                Type = "Video",
                Codec = "hevc",
                // 默认按 16:9 推导宽度，避免每个用例都手写
                Width = width ?? (height.HasValue ? height.Value * 16 / 9 : (int?)null),
                Height = height,
                BitDepth = bitDepth,
                ColorTransfer = colorTransfer,
                ExtendedVideoType = extendedVideoType
            };

        private static MediaHealthStream Audio(string codec = "eac3") =>
            new MediaHealthStream { Type = "Audio", Codec = codec, Language = "chi" };

        private static MediaHealthStream Subtitle(bool external = false, string language = "chi") =>
            new MediaHealthStream
            {
                Type = "Subtitle", Codec = "subrip", Language = language, IsExternal = external
            };

        private static MediaHealthInput Input(string fileName, params MediaHealthStream[] streams) =>
            new MediaHealthInput
            {
                ItemName = "Test Item",
                FileName = fileName,
                RunTimeTicks = 1_200_000_000,
                Streams = streams.ToList()
            };

        private static List<string> Codes(MediaHealthInput input) =>
            MediaHealthChecker.Check(input).Select(i => i.Code).ToList();

        [Fact]
        public void Fake4K_Reported_For1080pClaiming4K()
        {
            var input = Input("Movie.2020.2160p.WEB-DL.mkv", Video(1080), Audio(), Subtitle());

            Assert.Contains("Fake4K", Codes(input));
        }

        [Fact]
        public void Fake4K_NotReported_ForReal4K()
        {
            var input = Input("Movie.2020.2160p.WEB-DL.mkv", Video(2160), Audio(), Subtitle());

            Assert.DoesNotContain("Fake4K", Codes(input));
        }

        [Fact]
        public void Fake4K_NotReported_ForWidescreen4K()
        {
            // 3840x1608 是 2.39:1 宽银幕的正常 4K 编码，只看高度会整季误报
            var input = Input("Movie.2020.2160p.WEB-DL.mkv", Video(1608, width: 3840), Audio(), Subtitle());

            Assert.DoesNotContain("Fake4K", Codes(input));
        }

        [Fact]
        public void Fake4K_NotReported_WhenDimensionsUnknown()
        {
            // 探测失败（宽高都为空）不能当成假 4K
            var input = Input("Movie.2020.2160p.WEB-DL.mkv", Video(null), Audio(), Subtitle());

            Assert.DoesNotContain("Fake4K", Codes(input));
        }

        [Fact]
        public void FakeHdr_Reported_WhenClaimedButBt709()
        {
            var input = Input("Movie.2020.HDR.mkv", Video(2160, "bt709", 8), Audio(), Subtitle());

            Assert.Contains("FakeHdr", Codes(input));
        }

        [Fact]
        public void FakeHdr_NotReported_WhenEmbyRecognizesHdr()
        {
            var input = Input("Movie.2020.HDR.mkv", Video(2160, "smpte2084", 10, "Hdr10"), Audio(), Subtitle());

            Assert.DoesNotContain("FakeHdr", Codes(input));
        }

        [Fact]
        public void FakeAtmos_Reported_WhenOnlyAac()
        {
            var input = Input("Movie.2020.Atmos.mkv", Video(2160, "smpte2084", 10, "Hdr10"), Audio("aac"),
                Subtitle());

            Assert.Contains("FakeAtmos", Codes(input));
        }

        [Fact]
        public void FakeAtmos_NotReported_ForTrueHd()
        {
            var input = Input("Movie.2020.Atmos.mkv", Video(2160, "smpte2084", 10, "Hdr10"), Audio("truehd"),
                Subtitle());

            Assert.DoesNotContain("FakeAtmos", Codes(input));
        }

        [Fact]
        public void NoAudio_Reported_AsCritical()
        {
            var input = Input("Movie.2020.mkv", Video(1080), Subtitle());

            var issue = MediaHealthChecker.Check(input).FirstOrDefault(i => i.Code == "NoAudio");
            Assert.NotNull(issue);
            Assert.Equal(MediaHealthSeverity.Critical, issue.Severity);
        }

        [Fact]
        public void NoSubtitle_Reported_AsInfo()
        {
            var input = Input("Movie.2020.mkv", Video(1080), Audio());

            var issue = MediaHealthChecker.Check(input).FirstOrDefault(i => i.Code == "NoSubtitle");
            Assert.NotNull(issue);
            Assert.Equal(MediaHealthSeverity.Info, issue.Severity);
        }

        [Fact]
        public void ZeroDuration_Reported_WhenTicksMissing()
        {
            var input = Input("Movie.2020.mkv", Video(1080), Audio(), Subtitle());
            input.RunTimeTicks = null;

            Assert.Contains("ZeroDuration", Codes(input));
        }

        [Fact]
        public void UndSubtitle_Reported_ForExternalUnd()
        {
            var input = Input("Movie.2020.mkv", Video(1080), Audio(), Subtitle(true, "und"), Subtitle());

            Assert.Contains("UndSubtitle", Codes(input));
        }

        [Fact]
        public void UndSubtitle_NotReported_ForEmbeddedUnd()
        {
            // 内嵌轨道的语言由 ffprobe 给出，不属于本功能处理范围
            var input = Input("Movie.2020.mkv", Video(1080), Audio(),
                new MediaHealthStream { Type = "Subtitle", Language = "und", IsExternal = false });

            Assert.DoesNotContain("UndSubtitle", Codes(input));
        }

        [Fact]
        public void HealthyItem_ProducesNoIssues()
        {
            var input = Input("Movie.2020.2160p.HDR.Atmos.mkv",
                Video(2160, "smpte2084", 10, "Hdr10"), Audio("truehd"), Subtitle());

            Assert.Empty(MediaHealthChecker.Check(input));
        }

        [Fact]
        public void ChineseMovieTitle_DoesNotTriggerLanguageClaims()
        {
            // 片名里的语言词不应被当成画质/音效宣称
            var input = Input("倩女幽魂 (1987).FRA.UHD.BluRay.Remux.2160p.mkv",
                Video(2160, "smpte2084", 10, "Hdr10"), Audio("dtshd"), Subtitle());

            Assert.Empty(MediaHealthChecker.Check(input));
        }
    }
}
