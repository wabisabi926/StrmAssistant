using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// Atmos / DTS:X 标识识别。
    ///
    /// 边界：Emby 没有音频扩展类型字段，只能靠文本；因此「防误标」比「尽量识别」更重要 ——
    /// 普通 AAC 片源即使文件名带 Atmos 也不得出标识。
    /// </summary>
    public class AudioTagDetectorTests
    {
        [Theory]
        [InlineData("Dolby Atmos 7.1", "truehd", AudioTag.Atmos)]
        [InlineData("国语 全景声", "truehd", AudioTag.Atmos)]
        [InlineData("Atmos", "eac3", AudioTag.Atmos)]
        [InlineData("DTS:X 7.1", "dts", AudioTag.DtsX)]
        [InlineData("dts-x", "dtshd", AudioTag.DtsX)]
        public void DetectsTagFromTitle(string title, string codec, AudioTag expected)
        {
            Assert.Equal(expected, AudioTagDetector.Detect(title, codec));
        }

        [Theory]
        [InlineData("Atmosphere 5.1", "truehd")]
        [InlineData("DTS HD MA 7.1", "dts")]
        [InlineData("DTS-HD.MA", "dts")]
        public void IgnoresLookAlikeText(string text, string codec)
        {
            Assert.Equal(AudioTag.None, AudioTagDetector.Detect(text, codec));
        }

        [Theory]
        [InlineData("aac")]
        [InlineData("ac3")]
        [InlineData("flac")]
        [InlineData(null)]
        public void DoesNotTagLossyOrUnknownCodec(string codec)
        {
            Assert.Equal(AudioTag.None, AudioTagDetector.Detect("Dolby Atmos 7.1", codec));
        }

        [Fact]
        public void FallsBackToPathWhenTitleHasNoTag()
        {
            const string path = @"D:\Media\Movie.2024.2160p.BluRay.TrueHD.7.1.Atmos-GRP.mkv";

            Assert.Equal(AudioTag.Atmos,
                AudioTagDetector.Detect("国语 7.1", "truehd", path));
        }

        [Fact]
        public void TitleWinsOverPath()
        {
            const string path = @"D:\Media\Movie.2024.DTS-X.7.1-GRP.mkv";

            Assert.Equal(AudioTag.None,
                AudioTagDetector.Detect("国语 7.1", "aac", path));
        }

        [Theory]
        [InlineData(AudioTag.Atmos, "Atmos")]
        [InlineData(AudioTag.DtsX, "DTS:X")]
        [InlineData(AudioTag.None, null)]
        public void MapsLabel(AudioTag tag, string expected)
        {
            Assert.Equal(expected, AudioTagDetector.ToLabel(tag));
        }
    }
}
