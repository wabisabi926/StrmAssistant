using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// HDR 静态元数据格式化测试。
    /// 重点是量纲：ffprobe 的部分版本把亮度按 1/10000 nit 输出，min 与 max 必须同步换算，
    /// 否则会出现「0.005–10000000nit」这种自相矛盾的字符串。
    /// </summary>
    public class HdrStaticMetadataFormatterTests
    {
        private static HdrStaticMetadataInput Bt2020() => new HdrStaticMetadataInput
        {
            RedX = 0.708, RedY = 0.292,
            GreenX = 0.170, GreenY = 0.797,
            BlueX = 0.131, BlueY = 0.046
        };

        [Fact]
        public void Format_ProducesAllSections()
        {
            var input = Bt2020();
            input.MaxCll = 1000;
            input.MaxFall = 400;
            input.MinLuminance = 0.005;
            input.MaxLuminance = 1000;

            Assert.Equal("CLL 1000 / FALL 400 · BT.2020 · 0.005–1000nit",
                HdrStaticMetadataFormatter.Format(input));
        }

        [Fact]
        public void Format_KeepsMissingFallAsDash()
        {
            var input = new HdrStaticMetadataInput { MaxCll = 1000 };

            Assert.Equal("CLL 1000 / FALL -", HdrStaticMetadataFormatter.Format(input));
        }

        [Fact]
        public void Format_GamutOnly()
        {
            Assert.Equal("BT.2020", HdrStaticMetadataFormatter.Format(Bt2020()));
        }

        [Fact]
        public void Format_DetectsDciP3()
        {
            var input = new HdrStaticMetadataInput
            {
                RedX = 0.680, RedY = 0.320,
                GreenX = 0.265, GreenY = 0.690,
                BlueX = 0.150, BlueY = 0.060
            };

            Assert.Equal("DCI-P3", HdrStaticMetadataFormatter.DetectGamut(input));
        }

        [Fact]
        public void Format_DetectsBt709()
        {
            var input = new HdrStaticMetadataInput
            {
                RedX = 0.640, RedY = 0.330,
                GreenX = 0.300, GreenY = 0.600,
                BlueX = 0.150, BlueY = 0.060
            };

            Assert.Equal("BT.709", HdrStaticMetadataFormatter.DetectGamut(input));
        }

        [Fact]
        public void Format_NormalizesTenThousandthNitScale()
        {
            // ffprobe 以 1/10000 nit 输出时，min 与 max 要一起换算
            var input = new HdrStaticMetadataInput
            {
                MinLuminance = 50,
                MaxLuminance = 10000000
            };

            Assert.Equal("0.005–1000nit", HdrStaticMetadataFormatter.Format(input));
        }

        [Fact]
        public void Format_ReturnsNull_WhenNothingUsable()
        {
            Assert.Null(HdrStaticMetadataFormatter.Format(new HdrStaticMetadataInput()));
            Assert.Null(HdrStaticMetadataFormatter.Format(null));
        }

        [Fact]
        public void DetectGamut_ReturnsNull_ForUnknownPrimaries()
        {
            var input = new HdrStaticMetadataInput
            {
                RedX = 0.5, RedY = 0.5,
                GreenX = 0.5, GreenY = 0.5,
                BlueX = 0.5, BlueY = 0.5
            };

            Assert.Null(HdrStaticMetadataFormatter.DetectGamut(input));
        }

        [Fact]
        public void DetectGamut_ReturnsNull_WhenCoordinatesIncomplete()
        {
            Assert.Null(HdrStaticMetadataFormatter.DetectGamut(new HdrStaticMetadataInput { RedX = 0.708 }));
        }
    }
}
