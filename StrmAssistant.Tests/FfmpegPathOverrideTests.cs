using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class FfmpegPathOverrideTests
    {
        [Theory]
        [InlineData("configuration: --enable-gpl --enable-libbluray --enable-libx264", true)]
        [InlineData("--enable-libbluray", true)]
        [InlineData("configuration: --enable-gpl --enable-libx264", false)]
        [InlineData("libbluray", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void VersionOutputIndicatesBlurayInput(string output, bool expected)
        {
            Assert.Equal(expected, FfmpegPathOverride.VersionOutputIndicatesBlurayInput(output));
        }
    }
}
