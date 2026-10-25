using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 直链日志脱敏（回归 SA-05）。
    ///
    /// 网盘的访问签名、pickcode、临时凭据几乎都在 query 里。旧实现把完整地址
    /// （还做过 URL 解码，等于让签名更易读）写进 Info 日志，日志一旦外发就等于交出可用链接。
    /// </summary>
    public class StrmDirectRedirectLogTests
    {
        [Fact]
        public void Removes_Query_UserInfo_And_Fragment()
        {
            const string url =
                "http://user:pass@192.168.50.250:12333/115/url/video.mp4?pickcode=abc123&userid=341871580#frag";

            var safe = UrlSanitizer.ToLogSafe(url);

            Assert.Equal("http://192.168.50.250:12333/115/url/video.mp4", safe);
            Assert.DoesNotContain("pickcode", safe);
            Assert.DoesNotContain("abc123", safe);
            Assert.DoesNotContain("user:pass", safe);
            Assert.DoesNotContain("341871580", safe);
        }

        [Fact]
        public void Keeps_DefaultPort_Out_Of_Result()
        {
            Assert.Equal("https://example.com/video.mp4",
                UrlSanitizer.ToLogSafe("https://example.com/video.mp4?sig=xyz"));
        }

        [Fact]
        public void Handles_Empty_And_Unparsable()
        {
            Assert.Equal("(empty)", UrlSanitizer.ToLogSafe(null));
            Assert.Equal("(empty)", UrlSanitizer.ToLogSafe("  "));
            Assert.Equal("(unparsable)", UrlSanitizer.ToLogSafe("not a url"));
        }
    }
}
