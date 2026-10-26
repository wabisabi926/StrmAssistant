using System;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 中文搜索「上次见到的版本」标记解析。
    ///
    /// 这组用例钉住的是**自动关闭功能的判定依据**：
    /// 只有索引方案号真的变了才关；同一方案内的版本升级必须保持原状。
    /// 判错的两个方向都很难受 —— 要么升级后功能静默消失，要么该关时没关、带着坏索引跑。
    /// </summary>
    public class ChineseSearchMarkerTests
    {
        [Fact]
        public void ParsesLegacyFormat1_PlainVersion()
        {
            var marker = ChineseSearchMarker.Parse("3.0.0.55");

            Assert.Equal(1, marker.Format);
            Assert.False(marker.HasIndexScheme);
            Assert.Equal(new Version(3, 0, 0, 55), marker.Version);
        }

        [Fact]
        public void ParsesLegacyFormat1_DateStyleVersion()
        {
            var marker = ChineseSearchMarker.Parse("26.3.25");

            Assert.Equal(1, marker.Format);
            Assert.False(marker.HasIndexScheme);
            Assert.Equal(new Version(26, 3, 25), marker.Version);
        }

        [Fact]
        public void ParsesFormat2_FormatAndVersion()
        {
            var marker = ChineseSearchMarker.Parse("2|3.0.0.65");

            Assert.Equal(2, marker.Format);
            Assert.False(marker.HasIndexScheme);
            Assert.Equal(new Version(3, 0, 0, 65), marker.Version);
        }

        [Fact]
        public void ParsesFormat3_WithIndexScheme()
        {
            var marker = ChineseSearchMarker.Parse("3|2|3.0.0.65");

            Assert.Equal(3, marker.Format);
            Assert.True(marker.HasIndexScheme);
            Assert.Equal(2, marker.IndexScheme);
            Assert.Equal(new Version(3, 0, 0, 65), marker.Version);
        }

        [Fact]
        public void ParsesFormat3_WithOlderScheme()
        {
            // 方案号小于当前值 = 需要关闭并重建的那条路径
            var marker = ChineseSearchMarker.Parse("3|1|3.0.0.65");

            Assert.True(marker.HasIndexScheme);
            Assert.Equal(1, marker.IndexScheme);
            Assert.Equal(new Version(3, 0, 0, 65), marker.Version);
        }

        [Fact]
        public void Format3_WithoutSchemeField_LeavesSchemeUnknown()
        {
            var marker = ChineseSearchMarker.Parse("3|3.0.0.65");

            Assert.Equal(3, marker.Format);
            Assert.False(marker.HasIndexScheme);
            Assert.Equal(new Version(3, 0, 0, 65), marker.Version);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyText_YieldsEmptyMarker(string text)
        {
            var marker = ChineseSearchMarker.Parse(text);

            Assert.Null(marker.Version);
            Assert.False(marker.HasIndexScheme);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData("2|not-a-version")]
        [InlineData("3|2|not-a-version")]
        public void UnparsableVersion_DoesNotThrow(string text)
        {
            var marker = ChineseSearchMarker.Parse(text);

            Assert.Null(marker.Version);
        }

        [Fact]
        public void RoundTripsThroughFormat()
        {
            var version = new Version(3, 0, 0, 65);
            var text = ChineseSearchMarker.Format(2, version);

            var marker = ChineseSearchMarker.Parse(text);

            Assert.Equal(ChineseSearchMarker.CurrentFormat, marker.Format);
            Assert.Equal(2, marker.IndexScheme);
            Assert.Equal(version, marker.Version);
        }

        [Fact]
        public void Format_UsesCurrentFormatNumber()
        {
            var text = ChineseSearchMarker.Format(7, new Version(1, 2, 3, 4));

            Assert.Equal($"{ChineseSearchMarker.CurrentFormat}|7|1.2.3.4", text);
        }
    }
}
