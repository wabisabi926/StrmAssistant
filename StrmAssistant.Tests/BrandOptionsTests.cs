using StrmAssistant.Options;
using Xunit;

namespace StrmAssistant.Tests
{
    public class BrandOptionsTests
    {
        [Fact]
        public void BrandLine_UsesEmbyServerVersion()
        {
            var text = new BrandOptions().PluginVersion.Text;

            Assert.Contains("Support Emby Server_v4.10.2.0", text);
            Assert.DoesNotContain("Support 4.", text);
            // 品牌行结尾不再带 " +"（空格加号）
            Assert.DoesNotContain("+", text);
        }

        [Fact]
        public void DisplayVersion_KeepsFourSegments()
        {
            // 4.0.1.0 曾因旧版「砍结尾 .0」逻辑被显示成 4.0.1，
            // 与 tag/CHANGELOG/Release 名称不一致（见 issue 截图）。
            var version = AboutOptions.GetVersion();

            Assert.Equal(4, version.Split('.').Length);
            Assert.DoesNotContain(" ", version);
        }

        [Fact]
        public void BrandLine_VersionMatchesAssemblyFourSegments()
        {
            var text = new BrandOptions().PluginVersion.Text;
            var version = AboutOptions.GetVersion();

            Assert.Contains("版本：v" + version, text);
        }
    }
}
