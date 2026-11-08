using Emby.Media.Model.ProbeModel;
using Emby.Media.Model.ProbeModel.SideData;
using HarmonyLib;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// HDR 静态元数据补采（MaxCLL / MaxFALL / 母版色域与亮度范围）。
    ///
    /// ffprobe 的 <c>side_data_list</c> 里本来就有这些值，但 Emby 归一化成 <see cref="MediaStream"/>
    /// 时只挂到内部的 <c>StreamBase.SideDataJson</c>，而 <c>MediaStream</c> 没有对应字段，
    /// 于是在写入 <c>*-mediainfo.json</c> 之前就丢失了。这里在
    /// <c>ProbeResultNormalizer.GetMediaInfo</c> 的 postfix 上把它格式化成一行文本，
    /// 写入 <see cref="MediaStream.Title"/>（视频流的 Title 不参与选轨，且可持久化）。
    ///
    /// 载体选择：
    /// - 不写 <c>Comment</c>：已被 <see cref="HdrVividTagging"/> 用作 "HDR Vivid" 标记，
    ///   且 <c>VideoRange</c> 的显示覆写依赖它等于固定值，混写会破坏判定。
    /// - 不写 <c>MediaSourceInfo.Name</c>：那是多版本标签的载体（ServerSideVersionLabel）。
    ///
    /// 仅在读到 side_data 时写入；读不到一律不动，避免覆盖人工填写的内容。
    /// </summary>
    public class HdrStaticMetadata : PatchBase<HdrStaticMetadata>
    {
        private const string VideoCodecType = "video";

        private static MethodInfo _getMediaInfo;

        public HdrStaticMetadata()
        {
            Initialize();

            var hdr = Plugin.Instance.MediaInfoExtractStore.GetOptions().HdrTagging;
            if (hdr.EnableHdrTagging && hdr.CaptureHdrStaticMetadata)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.MediaEncoding");
                var normalizer = assembly.GetType("Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer");

                _getMediaInfo = normalizer?
                    .GetMethod("GetMediaInfo", BindingFlags.Instance | BindingFlags.Public);

                if (_getMediaInfo == null)
                {
                    Plugin.Instance.Logger.Warn(
                        "HdrStaticMetadata - ProbeResultNormalizer.GetMediaInfo not found; feature disabled");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    PatchTracker.IsSupported = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("HdrStaticMetadata init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_getMediaInfo != null)
            {
                PatchUnpatch(PatchTracker, apply, _getMediaInfo, postfix: nameof(GetMediaInfoPostfix));
            }
        }

        [HarmonyPostfix]
        private static void GetMediaInfoPostfix(ProbeResult data, bool isAudio, string path, MediaProtocol protocol,
            MediaInfo __result)
        {
            try
            {
                if (isAudio || data?.streams == null || __result?.MediaStreams == null) return;

                foreach (var probe in data.streams)
                {
                    if (!string.Equals(probe?.codec_type, VideoCodecType, StringComparison.OrdinalIgnoreCase)) continue;

                    var input = ExtractInput(probe.side_data_list);
                    var text = HdrStaticMetadataFormatter.Format(input);
                    if (string.IsNullOrEmpty(text)) continue;

                    var stream = HdrVividTagging.FindVideoStream(__result.MediaStreams, probe.index);
                    if (stream == null) continue;

                    // 值没变就不写，减少无意义的日志与数据库更新
                    if (string.Equals(stream.Title, text, StringComparison.Ordinal)) continue;

                    stream.Title = text;
                    // 插件实例可能尚未初始化（例如启动早期或单元测试中），
                    // postfix 绝不能因为记日志而把异常丢回 ffprobe 链路。
                    Plugin.Instance?.Logger?.Info(
                        $"HdrStaticMetadata - tagged (probe index={probe.index}): {text}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("HdrStaticMetadata GetMediaInfoPostfix failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 从 ffprobe 的 side_data 列表提取内容亮度与母版显示信息。抽成 internal 便于单测。
        /// </summary>
        internal static HdrStaticMetadataInput ExtractInput(IEnumerable<ProbeSideData> sideData)
        {
            var input = new HdrStaticMetadataInput();
            if (sideData == null) return input;

            foreach (var item in sideData)
            {
                switch (item)
                {
                    case ProbeSideDataContentLightLevel contentLight:
                        input.MaxCll = contentLight.max_content;
                        input.MaxFall = contentLight.max_average;
                        break;

                    case ProbeSideDataMateringDisplay mastering:
                        input.RedX = mastering.red_x;
                        input.RedY = mastering.red_y;
                        input.GreenX = mastering.green_x;
                        input.GreenY = mastering.green_y;
                        input.BlueX = mastering.blue_x;
                        input.BlueY = mastering.blue_y;
                        input.MinLuminance = mastering.min_luminance;
                        input.MaxLuminance = mastering.max_luminance;
                        break;
                }
            }

            return input;
        }
    }
}
