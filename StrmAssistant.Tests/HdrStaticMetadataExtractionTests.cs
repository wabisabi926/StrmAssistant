using Emby.Media.Model.ProbeModel;
using Emby.Media.Model.ProbeModel.SideData;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// HDR 静态元数据的提取链路：从 Emby 真实的 ProbeSideData 子类读到字段，
    /// 再经格式化产出写入 Title 的文本。
    ///
    /// 这一环覆盖的是「类型识别是否正确」——ffprobe 的 side_data 在 Emby 里被反序列化成
    /// ProbeSideDataContentLightLevel / ProbeSideDataMateringDisplay 两个子类，
    /// 一旦 Emby 改类名或字段名，这里会先失败，而不是等到真机补采静默失效。
    /// </summary>
    public class HdrStaticMetadataExtractionTests
    {
        [Fact]
        public void ExtractInput_ReadsContentLightAndMasteringDisplay()
        {
            var sideData = new List<ProbeSideData>
            {
                new ProbeSideDataContentLightLevel { max_content = 1000, max_average = 400 },
                new ProbeSideDataMateringDisplay
                {
                    red_x = 0.708, red_y = 0.292,
                    green_x = 0.170, green_y = 0.797,
                    blue_x = 0.131, blue_y = 0.046,
                    min_luminance = 0.005, max_luminance = 1000
                }
            };

            var input = HdrStaticMetadata.ExtractInput(sideData);

            Assert.Equal(1000, input.MaxCll);
            Assert.Equal(400, input.MaxFall);
            Assert.Equal("CLL 1000 / FALL 400 · BT.2020 · 0.005–1000nit",
                HdrStaticMetadataFormatter.Format(input));
        }

        [Fact]
        public void ExtractInput_IgnoresUnrelatedSideData()
        {
            var sideData = new List<ProbeSideData> { new ProbeSideDataDisplayMatrix() };

            var input = HdrStaticMetadata.ExtractInput(sideData);

            Assert.Null(input.MaxCll);
            Assert.Null(HdrStaticMetadataFormatter.Format(input));
        }

        [Fact]
        public void ExtractInput_HandlesNull()
        {
            var input = HdrStaticMetadata.ExtractInput(null);

            Assert.NotNull(input);
            Assert.Null(HdrStaticMetadataFormatter.Format(input));
        }

        [Fact]
        public void ExtractInput_HandlesContentLightOnly()
        {
            var sideData = new List<ProbeSideData>
            {
                new ProbeSideDataContentLightLevel { max_content = 600, max_average = 200 }
            };

            var input = HdrStaticMetadata.ExtractInput(sideData);

            Assert.Equal("CLL 600 / FALL 200", HdrStaticMetadataFormatter.Format(input));
        }

        /// <summary>
        /// 完整 postfix 链路：构造 Emby 真实的 ProbeResult / MediaInfo，直接调用补丁方法，
        /// 验证 side_data 最终落到视频流 Title 上（而不只是格式化函数返回了字符串）。
        /// </summary>
        [Fact]
        public void Postfix_WritesVideoStreamTitle_WhenStaticSideDataPresent()
        {
            var result = InvokePostfix(new List<ProbeSideData>
            {
                new ProbeSideDataContentLightLevel { max_content = 1000, max_average = 400 },
                new ProbeSideDataMateringDisplay
                {
                    red_x = 0.708, red_y = 0.292,
                    green_x = 0.170, green_y = 0.797,
                    blue_x = 0.131, blue_y = 0.046,
                    min_luminance = 0.005, max_luminance = 1000
                }
            });

            Assert.Equal("CLL 1000 / FALL 400 · BT.2020 · 0.005–1000nit",
                result.MediaStreams[0].Title);
            Assert.Null(result.MediaStreams[1].Title);
        }

        /// <summary>
        /// 真机实测中多数 WEB-DL / DV 源只有 DOVI configuration record，没有 SMPTE 2086。
        /// 这种数据下补丁必须保持静默，不能覆盖已有标题。
        /// </summary>
        [Fact]
        public void Postfix_LeavesTitleUntouched_WhenOnlyDoviSideData()
        {
            var result = InvokePostfix(new List<ProbeSideData> { new ProbeSideDataDovi() },
                videoTitle: "Mandarin [AAC.2.0]");

            Assert.Equal("Mandarin [AAC.2.0]", result.MediaStreams[0].Title);
        }

        [Fact]
        public void Postfix_LeavesTitleUntouched_WhenNoSideData()
        {
            var result = InvokePostfix(null);

            Assert.Null(result.MediaStreams[0].Title);
        }

        private static MediaInfo InvokePostfix(List<ProbeSideData> sideData, string videoTitle = null)
        {
            var probe = new ProbeStream
            {
                index = 0,
                codec_type = "video",
                side_data_list = sideData == null ? null : new ProbeSideDataList(sideData)
            };
            var data = new ProbeResult { streams = new[] { probe } };
            var result = new MediaInfo
            {
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Index = 0, Title = videoTitle },
                    new MediaStream { Type = MediaStreamType.Audio, Index = 1 }
                }
            };

            var postfix = typeof(HdrStaticMetadata).GetMethod("GetMediaInfoPostfix",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(postfix);
            postfix.Invoke(null, new object[] { data, false, "probe.mkv", MediaProtocol.File, result });

            return result;
        }
    }
}
