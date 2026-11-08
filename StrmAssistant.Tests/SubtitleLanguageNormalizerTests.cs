using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 外挂音轨 / 字幕语言标签规范化测试。
    ///
    /// 这是 P0 的核心判据：只认文件名末尾的明确标记，判不出来一律不改；
    /// 简繁只写进 Title（Emby 的 CultureDto.Equals 只比两字母码，选轨层面无法区分简繁）。
    /// </summary>
    public class SubtitleLanguageNormalizerTests
    {
        [Theory]
        [InlineData("Movie.2020.1080p.BluRay.chs.ass", "chi", "简体中文")]
        [InlineData("Movie.cht.srt", "chi", "繁体中文")]
        [InlineData("Movie.zh-CN.ass", "chi", "简体中文")]
        [InlineData("Movie.zh-Hant.ass", "chi", "繁体中文")]
        [InlineData("Movie.chs&eng.ass", "chi", "中英双语")]
        [InlineData("Movie.eng.srt", "eng", "English")]
        [InlineData("Movie.jp.ass", "jpn", "日本語")]
        [InlineData("Movie.kor.srt", "kor", "한국어")]
        [InlineData("Movie.简.srt", "chi", "简体中文")]
        [InlineData("Movie.繁體.ass", "chi", "繁体中文")]
        [InlineData("Movie.CHS.SRT", "chi", "简体中文")]
        public void Normalize_DetectsLanguageFromFileName(string fileName, string expectedLanguage,
            string expectedTitle)
        {
            var result = SubtitleLanguageNormalizer.Normalize(fileName, null, null);

            Assert.Equal(expectedLanguage, result.Language);
            Assert.Equal(expectedTitle, result.Title);
            Assert.True(result.HasChange);
        }

        [Fact]
        public void Normalize_DetectsBareLanguageSegment()
        {
            // 外挂音轨只有语言段，没有完整文件名
            var result = SubtitleLanguageNormalizer.Normalize("chs", null, null);

            Assert.Equal("chi", result.Language);
            Assert.Equal("简体中文", result.Title);
        }

        [Fact]
        public void Normalize_HandlesFullPath()
        {
            var result = SubtitleLanguageNormalizer.Normalize(@"D:\media\Movie (2020)\Movie.cht.ass", null, null);

            Assert.Equal("chi", result.Language);
            Assert.Equal("繁体中文", result.Title);
        }

        [Theory]
        [InlineData("Movie.forced.chs.srt")]
        [InlineData("Movie.chs.forced.srt")]
        public void Normalize_DetectsForcedMarkerOnEitherSide(string fileName)
        {
            var result = SubtitleLanguageNormalizer.Normalize(fileName, null, null);

            Assert.Equal("chi", result.Language);
            Assert.True(result.IsForced);
        }

        [Fact]
        public void Normalize_DetectsHearingImpairedMarker()
        {
            var result = SubtitleLanguageNormalizer.Normalize("Movie.sdh.eng.srt", null, null);

            Assert.Equal("eng", result.Language);
            Assert.True(result.IsHearingImpaired);
        }

        [Theory]
        [InlineData("Movie.2020.1080p.srt")]
        [InlineData("Movie.S01E01.2160p.WEB-DL.srt")]
        [InlineData("")]
        [InlineData(null)]
        public void Normalize_LeavesUnmarkedFileUntouched(string fileName)
        {
            var result = SubtitleLanguageNormalizer.Normalize(fileName, "und", null);

            Assert.Null(result.DetectedLabel);
            Assert.Equal("und", result.Language);
            Assert.False(result.HasChange);
        }

        [Fact]
        public void Normalize_NoChangeWhenAlreadyNormalized()
        {
            var result = SubtitleLanguageNormalizer.Normalize("Movie.chs.ass", "chi", "简体中文");

            Assert.False(result.HasChange);
        }

        [Fact]
        public void Normalize_NoChangeWhenForcedAlreadySet()
        {
            // 重复运行不应反复写库
            var result = SubtitleLanguageNormalizer.Normalize("Movie.forced.chs.srt", "chi", "简体中文", true);

            Assert.False(result.HasChange);
        }

        [Fact]
        public void Normalize_KeepsLocalizedTitle()
        {
            // Title 已含中文说明被人工改写或本地化，只修 Language，不动标题
            var result = SubtitleLanguageNormalizer.Normalize("Movie.cht.ass", null, "中文字幕（繁）");

            Assert.Equal("chi", result.Language);
            Assert.Equal("中文字幕（繁）", result.Title);
            Assert.True(result.HasChange);
        }

        [Fact]
        public void Normalize_UndIsReplaced()
        {
            var result = SubtitleLanguageNormalizer.Normalize("Movie.chs.ass", "und", null);

            Assert.Equal("chi", result.Language);
            Assert.True(result.HasChange);
        }

        [Fact]
        public void Normalize_DoesNotScanBeyondLastThreeSegments()
        {
            // 语言词在片名中段，不应被当成轨道标记
            var result = SubtitleLanguageNormalizer.Normalize("Chinese.Zodiac.2012.1080p.BluRay.x264-GROUP.mkv", null, null);

            Assert.Null(result.DetectedLabel);
            Assert.False(result.HasChange);
        }
    }
}
