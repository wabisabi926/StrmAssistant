using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 删除版本时的「附属文件归属」判定（回归 F1）。
    ///
    /// 背景：旧实现用 <c>GetFileNameWithoutExtension(...).StartsWith(basename)</c> 判断归属，
    /// 没有名称边界，导致删除 <c>Movie.mkv</c> 时会把同目录 <c>Movie2.nfo</c> / <c>Movie2.en.srt</c> /
    /// <c>Movie2-poster.jpg</c> / <c>Movie2-mediainfo.json</c> 一起删掉——那是另一部影片的附属文件。
    ///
    /// 这里把边界规则钉死：基名之后必须是分隔符，否则视为另一部影片。
    /// </summary>
    public class SidecarMatchingTests
    {
        [Theory]
        // 完全同名
        [InlineData("Movie", "Movie", true)]
        [InlineData("S01E01", "S01E01", true)]
        [InlineData("流浪地球", "流浪地球", true)]
        // 合法分隔符边界
        [InlineData("Movie", "Movie.en", true)]
        [InlineData("Movie", "Movie-poster", true)]
        [InlineData("Movie", "Movie (2020)", true)]
        [InlineData("Movie", "Movie_1080p", true)]
        [InlineData("Movie", "Movie[2160p]", true)]
        [InlineData("Movie", "Movie+extras", true)]
        [InlineData("Movie", "Movie-mediainfo", true)]
        // 非分隔符 → 属于另一部影片，必须拒绝
        [InlineData("Movie", "Movie2", false)]
        [InlineData("Movie", "MovieA", false)]
        [InlineData("Movie", "MovieII", false)]
        [InlineData("Movie", "Movie流浪地球", false)]
        [InlineData("Movie", "Movie2-mediainfo", false)]
        [InlineData("Movie", "Movie2.en", false)]
        [InlineData("Movie", "Movie2-poster", false)]
        // 多版本：只匹配同一个版本的附属文件
        [InlineData("Movie - 1080p", "Movie - 1080p", true)]
        [InlineData("Movie - 1080p", "Movie - 2160p", false)]
        // 相邻集数
        [InlineData("S01E01", "S01E011", false)]
        // 中文续集名
        [InlineData("流浪地球", "流浪地球2", false)]
        [InlineData("流浪地球", "流浪地球.nfo", true)]
        // 空值
        [InlineData("Movie", "", false)]
        [InlineData("", "Movie", false)]
        public void IsSidecarOf_RespectsNameBoundary(string basename, string candidate, bool expected)
        {
            Assert.Equal(expected, LibraryApi.IsSidecarOf(candidate, basename));
        }
    }
}
