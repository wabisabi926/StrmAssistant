using StrmAssistant.Common;
using System.Linq;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 单段长 m2ts 的统计采样窗口分布：必须沿全片等距覆盖，不能退化成「只取头尾」——
    /// 后者正是死侍盘字节占比失真（视频 56.9% vs BDInfo 85.5%）的根因。
    /// </summary>
    public class UniformStatsSamplingTests
    {
        [Fact]
        public void CoversWholeStreamWithAlignedWindowStarts()
        {
            const long streamLength = 60L * 1024 * 1024 * 1024; // 60GB 单段原盘
            const long perWindow = 256 * 1024;
            const int alignment = 192;

            var starts = UdfTitleResolver.GetStatsWindowStarts(streamLength, perWindow, alignment);

            Assert.Equal(UdfTitleResolver.StatsWindowCount, starts.Length);
            Assert.Equal(0, starts[0]);                                  // 首窗口从 0 开始
            Assert.Equal(starts[1] - starts[0], starts[2] - starts[1]);  // 等距
            Assert.True(starts.All(s => s % alignment == 0));            // TS 包对齐
            // 末窗口接近尾部（留出一个窗口的空间）
            Assert.True(starts[^1] >= streamLength - perWindow - alignment);
            Assert.True(starts[^1] <= streamLength - perWindow);
        }

        [Fact]
        public void ShortStreamClampsToZeroSpread()
        {
            // 流短到一个窗口就覆盖完：所有起点都归零，不产生越界 seek
            var starts = UdfTitleResolver.GetStatsWindowStarts(
                streamLength: 200 * 1024, perWindow: 256 * 1024, alignment: 192);

            Assert.All(starts, s => Assert.Equal(0, s));
        }

        [Fact]
        public void WindowsStayMonotonic()
        {
            var starts = UdfTitleResolver.GetStatsWindowStarts(10L * 1024 * 1024 * 1024, 128 * 1024, 192);

            for (var i = 1; i < starts.Length; i++)
                Assert.True(starts[i] >= starts[i - 1], $"window {i} went backwards");
        }
    }
}
