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

            Assert.Contains("Emby Server_v4.10.2.0 +", text);
            Assert.DoesNotContain("Support 4.", text);
        }
    }
}
