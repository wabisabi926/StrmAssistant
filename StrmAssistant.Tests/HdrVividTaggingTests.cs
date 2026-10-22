using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using StrmAssistant.Mod;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// HDR Vivid 显示覆写（回归）。
    ///
    /// 关键事实：Emby 4.10 的 <c>MediaStream.VideoRange</c> getter IL 是
    /// <c>ldarg.0; ldc.i4.0; call GenerateVideoRange</c> —— 传的是 <b>isForDisplay = false</b>。
    /// 所以「只挂 GenerateVideoRange 且判断 isForDisplay」的写法永远不会生效（参考实现也是这么写的）。
    /// 本用例用真实 Harmony 补丁验证：改挂 <c>get_VideoRange</c> 之后，Comment 命中即可覆写返回值。
    /// </summary>
    public class HdrVividTaggingTests
    {
        private const string HarmonyId = "strmassistant.tests.hdrvivid";

        [Fact]
        public void VideoRange_IsOverridden_OnlyForTaggedVideoStreams()
        {
            var target = typeof(MediaStream).GetProperty("VideoRange")?.GetGetMethod();
            Assert.NotNull(target);

            var postfix = typeof(HdrVividTagging).GetMethod("VideoRangePostfix",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(postfix);

            var harmony = new Harmony(HarmonyId);
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            try
            {
                // 1) 打了标记的视频流 → 覆写
                var tagged = new MediaStream
                {
                    Type = MediaStreamType.Video,
                    ExtendedVideoType = ExtendedVideoTypes.Hdr10,
                    Comment = HdrVividTagging.VividLabel
                };
                Assert.Equal(HdrVividTagging.VividLabel, tagged.VideoRange);

                // 2) 未打标记的视频流 → 保持 Emby 原值（不能被误改）
                var plain = new MediaStream
                {
                    Type = MediaStreamType.Video,
                    ExtendedVideoType = ExtendedVideoTypes.Hdr10
                };
                Assert.Equal("HDR 10", plain.VideoRange);

                // 3) 非视频流即使 Comment 命中也不改
                var audio = new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Comment = HdrVividTagging.VividLabel
                };
                Assert.NotEqual(HdrVividTagging.VividLabel, audio.VideoRange);

                // 4) 补丁卸载后恢复原值
                harmony.UnpatchAll(HarmonyId);
                Assert.Equal("HDR 10", tagged.VideoRange);
            }
            finally
            {
                harmony.UnpatchAll(HarmonyId);
            }
        }

        /// <summary>
        /// 首帧补探的输出解析：CUVA 只出现在**帧级** side_data（stream 级为空），
        /// 所以补探结果用字符串匹配判断，抽成纯函数以便固定。
        /// </summary>
        [Theory]
        [InlineData("{\"side_data_list\":[{\"side_data_type\":\"HDR Dynamic Metadata CUVA 005.1 2021 (Vivid)\"}]}", true)]
        [InlineData("{\"side_data_type\": \"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)\"}", false)]
        [InlineData("{\"side_data_type\": \"Mastering display metadata\"}", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OutputMentionsCuva_DetectsOnlyCuva(string output, bool expected)
        {
            Assert.Equal(expected, HdrVividTagging.OutputMentionsCuva(output));
        }

        /// <summary>
        /// 回归锁：Emby 的 VideoRange 传的是 isForDisplay=false。
        /// 若哪天 Emby 改成 true，这个用例会失败，提示可以简化实现。
        /// </summary>
        [Fact]
        public void Emby_VideoRange_UsesNonDisplayVariant()
        {
            var ms = new MediaStream
            {
                Type = MediaStreamType.Video,
                ExtendedVideoType = ExtendedVideoTypes.DolbyVision
            };

            var generate = typeof(MediaStream).GetMethod("GenerateVideoRange");
            Assert.NotNull(generate);

            var forDisplay = (string)generate.Invoke(ms, new object[] { true });
            var compact = (string)generate.Invoke(ms, new object[] { false });

            Assert.NotEqual(forDisplay, compact);
            Assert.Equal(compact, ms.VideoRange);   // getter 传 false
        }
    }
}
