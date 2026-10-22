using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using System;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 双层杜比 Vision Profile 7 标注（Wiki「提取 ISO 媒体信息」第 3 条）。
    ///
    /// 挂点与 参考实现的 MonoMod 补丁同源：<c>ProbeResultNormalizer.GetMediaInfo</c> 是 Emby 把
    /// ffprobe 原始输出归一化为 <see cref="MediaInfo"/>（继承 <c>MediaSourceInfo</c>）的位置，
    /// 此处对该结果做 Postfix，写回媒体流标注。
    ///
    /// 命中条件见 <see cref="DvProfileDetector"/>（面积前二 + 均 hevc + BL 为 Hdr10 + 宽高各 2 倍）。
    /// 命中后把**增强层（面积较小者）**写为 Dolby Vision Profile 7 —— 与 参考实现的写入目标流一致，
    /// 也与 Wiki「显示增强层」的表述一致。
    ///
    /// 安全性：
    /// - 只写 <c>MediaStream.ExtendedVideoType / ExtendedVideoSubType</c>（Emby 定义为可写）；
    ///   <c>VideoRange</c>、<c>ExtendedVideoSubTypeDescription</c> 只读，不动。
    /// - **不改** <c>MediaSourceInfo.Path</c>，播放路径与条目身份不受影响。
    /// - 未命中（单层 / 非 hevc / 非 HDR10 基础层 / 非 2 倍几何）一律不改，行为与原来完全一致。
    /// - 开关见 <see cref="Options.HdrTaggingOptions"/>（3.0.0.62 起与 ISO 提取解耦，默认 OFF）。
    /// </summary>
    public class DolbyVisionProfile : PatchBase<DolbyVisionProfile>
    {
        private static MethodInfo _getMediaInfo;

        public DolbyVisionProfile()
        {
            Initialize();

            // 与 ISO 提取解耦：改由「HDR 标注」分组控制（3.0.0.62 起）
            var hdr = Plugin.Instance.MediaInfoExtractStore.GetOptions().HdrTagging;

            if (hdr.EnableHdrTagging && hdr.EnableDolbyVisionProfileTagging)
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
                        "DolbyVisionProfile - ProbeResultNormalizer.GetMediaInfo not found; DV P7 tagging disabled");
                }
                else if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"DolbyVisionProfile - resolved {_getMediaInfo}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("DolbyVisionProfile init failed: " + ex.Message);
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
        private static void GetMediaInfoPostfix(bool isAudio, MediaInfo __result)
        {
            try
            {
                if (isAudio || __result?.MediaStreams == null) return;

                var logger = Plugin.Instance.Logger;

                if (!DvProfileDetector.TryDetectDualLayerProfile7(__result.MediaStreams, out var enhancementLayer,
                        out var reason))
                {
                    // 未命中的原因仅 Debug 输出，避免每次探测都刷 Info 日志
                    if (Plugin.Instance.DebugMode)
                    {
                        logger.Debug($"DolbyVisionProfile - no dual-layer DV P7: {reason}");
                    }

                    return;
                }

                enhancementLayer.ExtendedVideoType = ExtendedVideoTypes.DolbyVision;
                enhancementLayer.ExtendedVideoSubType = ExtendedVideoSubTypes.DoviProfile76;

                logger.Info($"DolbyVisionProfile - dual-layer DV P7 tagged on enhancement layer ({reason})");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("DolbyVisionProfile postfix failed: " + ex.Message);
            }
        }
    }
}
