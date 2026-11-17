using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 音频「配置」文案（MediaStream.Profile）：按 codec + Atmos/DTS:X 标识动态拼装，
    /// 不写死单一值；识别不到标识时保持原值，避免误标。
    /// </summary>
    public class AudioProfileBuilderTests
    {
        [Theory]
        [InlineData("Movie.2024.2160p.BluRay.TrueHD.Atmos.mkv", "truehd",
            "Dolby TrueHD + Dolby Atmos")]
        [InlineData("Movie.2024.2160p.TrueHD.7.1.Atmos.mkv", "mlp",
            "Dolby TrueHD + Dolby Atmos")]
        [InlineData("Movie.2024.EAC3.Atmos.mkv", "eac3",
            "Dolby EAC3 + Dolby Atmos")]
        [InlineData("Movie.2024.DTSX.mkv", "dts",
            "DTS + DTS:X")]
        [InlineData("Movie.2024.DTS-HD.MA.DTSX.mkv", "dtshd",
            "DTS-HD MA + DTS:X")]
        public void BuildsProfileFromFileName(string path, string codec, string expected)
        {
            Assert.Equal(expected, AudioProfileBuilder.Build(codec, null, path));
        }

        [Fact]
        public void TrackTitleWinsOverFileName()
        {
            // 轨道标题明确带标识时用标题；标题无标识而文件名有时，仍回退到文件名。
            const string path = "Movie.TrueHD.Atmos.mkv";

            Assert.Equal("Dolby TrueHD + Dolby Atmos",
                AudioProfileBuilder.Build("truehd", "Dolby Atmos 7.1", "Movie.TrueHD.mkv"));
            Assert.Equal("Dolby TrueHD + Dolby Atmos",
                AudioProfileBuilder.Build("truehd", "普通话 7.1", path));
        }

        [Theory]
        [InlineData("Movie.AAC.Atmos.mkv", "aac")]
        [InlineData("Movie.AC3.Atmos.mkv", "ac3")]
        [InlineData("Movie.TrueHD.mkv", "truehd")]
        [InlineData("Movie.DolbyAtmosNoSpace.mkv", "truehd")]
        [InlineData("Movie.TrueHD.Atmos.mkv", null)]
        public void ReturnsNullWhenNotTagged(string path, string codec)
        {
            Assert.Null(AudioProfileBuilder.Build(codec, null, path));
        }

        [Fact]
        public void DecodesUrlEncodedRemoteIsoPath()
        {
            // strm 指向的 115 网盘 URL：path 参数里是 %20 / %40 编码的文件名
            const string url = "http://192.168.50.250:12333/115/url/video.iso?pickcode=abc" +
                               "&path=%E4%B8%B4%E6%97%B6%2FMovie.2024.TrueHD.Atmos%207.1.iso";

            Assert.Equal("Dolby TrueHD + Dolby Atmos",
                AudioProfileBuilder.Build("truehd", null, url));
        }

        [Fact]
        public void DoesNotMislabelAtmosphereWord()
        {
            Assert.Null(AudioProfileBuilder.Build("truehd", "Atmosphere 7.1", null));
        }
    }
}
