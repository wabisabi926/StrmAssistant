using Emby.Media.Model.ProbeModel;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// HDR Vivid 标注。
    ///
    /// Emby 的 <see cref="MediaStream.ExtendedVideoType"/> 没有 Vivid 取值，ffprobe 也会把
    /// CUVA 动态元数据归为未知 side_data，所以插件需要自己完成三件事：
    /// 1. 探测：读取 ffprobe 的 side_data 判断是否含 CUVA；流级没有时再补探首帧。
    /// 2. 落库：把命中的视频流 <see cref="MediaStream.Comment"/> 写为 "HDR Vivid"，
    ///    这是 MediaStreams2 表里可持久化、且不会被 Emby 重算覆盖的载体。
    /// 3. 显示：Emby 的 <see cref="MediaStream.VideoRange"/> 是计算属性，
    ///    只依据 ExtendedVideoType，不会返回 Vivid；因此在 getter 上覆写返回值。
    ///
    /// 安全性：
    /// - 只在命中 CUVA 时改写 <c>MediaStream.Comment</c>（Emby 定义为可写），未命中一律不动。
    /// - 显示覆写同样只在 Comment 命中时生效，其余条目走原逻辑。
    /// - 不改 <c>MediaSourceInfo.Path</c>，播放路径与条目身份不受影响。
    /// - 开关见 <see cref="Options.HdrTaggingOptions"/>（3.0.0.62 起与 ISO 提取解耦，默认 OFF）。
    /// </summary>
    public class HdrVividTagging : PatchBase<HdrVividTagging>
    {
        internal const string VividLabel = "HDR Vivid";

        private const string VideoCodecType = "video";
        private const string CuvaMarker = "CUVA";

        /// <summary>首帧补探的超时（毫秒）。正常 1~2 秒，留足余量给慢网盘。</summary>
        private const int FrameProbeTimeoutMs = 10_000;

        /// <summary>
        /// 首帧补探结果缓存（key = 目标地址 + 视频流序号）。
        /// 每条目在进程生命周期内只探一次，避免重复探测拖慢整库扫描。
        /// </summary>
        private static readonly ConcurrentDictionary<string, bool> FrameProbeCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static MethodInfo _getMediaInfo;
        private static MethodInfo _generateVideoRange;
        private static MethodInfo _videoRangeGetter;

        private enum CuvaEvidence
        {
            None,
            StreamSideData,
            FrameProbe
        }

        public HdrVividTagging()
        {
            Initialize();

            // 与 ISO 提取解耦：改由「HDR 标注」分组控制（3.0.0.62 起）
            var hdr = Plugin.Instance.MediaInfoExtractStore.GetOptions().HdrTagging;

            if (hdr.EnableHdrTagging && hdr.EnableHdrVividTagging)
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

                _generateVideoRange = typeof(MediaStream).GetMethod("GenerateVideoRange",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);

                _videoRangeGetter = typeof(MediaStream)
                    .GetProperty("VideoRange", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod();

                if (_getMediaInfo == null || _videoRangeGetter == null)
                {
                    Plugin.Instance.Logger.Warn(
                        $"HdrVividTagging - target method missing (GetMediaInfo={_getMediaInfo != null}, VideoRangeGetter={_videoRangeGetter != null}); HDR Vivid tagging disabled");
                }
                else if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"HdrVividTagging - resolved [{_getMediaInfo}], [{_videoRangeGetter}], [{_generateVideoRange}]");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("HdrVividTagging init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_getMediaInfo != null)
            {
                PatchUnpatch(PatchTracker, apply, _getMediaInfo, postfix: nameof(GetMediaInfoPostfix));
            }

            if (_videoRangeGetter != null)
            {
                PatchUnpatch(PatchTracker, apply, _videoRangeGetter, postfix: nameof(VideoRangePostfix));
            }

            if (_generateVideoRange != null)
            {
                PatchUnpatch(PatchTracker, apply, _generateVideoRange, postfix: nameof(GenerateVideoRangePostfix));
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
                    if (!IsVideoProbe(probe)) continue;

                    var stream = FindVideoStream(__result.MediaStreams, probe.index);
                    if (stream == null || IsAlreadyTagged(stream)) continue;

                    var evidence = FindCuvaEvidence(data, probe, stream);
                    if (evidence == CuvaEvidence.None) continue;

                    stream.Comment = VividLabel;
                    Plugin.Instance.Logger.Info(
                        $"HdrVividTagging - HDR Vivid tagged (probe index={probe.index}, evidence={evidence})");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("HdrVividTagging GetMediaInfoPostfix failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 判定 CUVA 证据：优先读流级 side_data；流级为空时，仅对 Emby 已识别为 HDR 的流
        /// 补探首帧，避免给 SDR 条目平白多跑一次 ffprobe。
        /// </summary>
        private static CuvaEvidence FindCuvaEvidence(ProbeResult data, ProbeStream probe, MediaStream stream)
        {
            if (ContainsCuvaSideData(probe.side_data_list)) return CuvaEvidence.StreamSideData;

            if (stream.ExtendedVideoType == ExtendedVideoTypes.None) return CuvaEvidence.None;

            var target = data?.format?.filename;
            if (string.IsNullOrWhiteSpace(target)) return CuvaEvidence.None;

            return HasVividByFrameProbe(target, probe.index)
                ? CuvaEvidence.FrameProbe
                : CuvaEvidence.None;
        }

        /// <summary>
        /// 读首帧的 side_data 判断是否 CUVA。带超时与按目标缓存：
        /// 单次约 1.6 秒，每个条目在进程生命周期内只探一次。
        /// </summary>
        private static bool HasVividByFrameProbe(string target, int? probeIndex)
        {
            var key = target + "|" + (probeIndex?.ToString() ?? "v:0");
            if (FrameProbeCache.TryGetValue(key, out var cached)) return cached;

            var found = false;

            try
            {
                var probePath = ResolveProbePath();
                if (string.IsNullOrEmpty(probePath) || !File.Exists(probePath))
                {
                    Plugin.Instance.Logger.Debug("HdrVividTagging - ffprobe not found, frame probe skipped");
                    FrameProbeCache[key] = false;
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = probePath,
                    Arguments = ProbeCommand.BuildFrameProbeArgs(target, probeIndex),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();   // 必须排空，否则可能写满管道阻塞

                if (!process.WaitForExit(FrameProbeTimeoutMs))
                {
                    try { process.Kill(true); } catch { /* ignored */ }
                    Plugin.Instance.Logger.Debug($"HdrVividTagging - frame probe timed out: {target}");
                    return false;
                }

                _ = stderr.Result;
                found = OutputMentionsCuva(stdout.Result);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("HdrVividTagging frame probe failed: " + e.Message);
            }

            FrameProbeCache[key] = found;
            return found;
        }

        /// <summary>
        /// ffprobe 输出里是否出现 CUVA 标记。抽成纯函数便于单测。
        /// </summary>
        internal static bool OutputMentionsCuva(string probeOutput)
        {
            return !string.IsNullOrEmpty(probeOutput) &&
                   probeOutput.IndexOf(CuvaMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// CUVA 005.1 即 HDR Vivid 的动态元数据；ffprobe 将其归为未知 side_data，
        /// side_data_type 形如 "HDR Dynamic Metadata CUVA 005.1 2021 (Vivid)"。
        /// 用 "CUVA" 子串匹配以兼容不同 ffprobe 版本的后缀差异。
        /// </summary>
        internal static bool ContainsCuvaSideData(IEnumerable<ProbeSideData> sideData)
        {
            if (sideData == null) return false;

            foreach (var item in sideData)
            {
                var type = item?.side_data_type;
                if (type != null && type.IndexOf(CuvaMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>优先用用户自定义 ffprobe（可能带新特性），否则用 Emby 当前的。</summary>
        private static string ResolveProbePath()
        {
            try
            {
                var custom = Plugin.Instance.MediaInfoExtractStore.GetOptions().IsoTool.CustomProbePath;
                if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
            }
            catch
            {
                // 选项尚未就绪时忽略
            }

            return FfmpegPathOverride.CurrentProbePath;
        }

        /// <summary>
        /// 显示出口：Emby 的 <c>VideoRange</c> 属性 getter 返回该条目的画质标签，API 与 Web UI 都读它。
        /// 命中 Comment 标记时直接改写返回值，其余条目完全走 Emby 原逻辑。
        /// </summary>
        [HarmonyPostfix]
        private static void VideoRangePostfix(MediaStream __instance, ref string __result)
        {
            try
            {
                if (__instance?.Type != MediaStreamType.Video) return;
                if (!IsAlreadyTagged(__instance)) return;

                __result = VividLabel;
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("HdrVividTagging VideoRangePostfix failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 兼容直接调用 <c>GenerateVideoRange(isForDisplay:true)</c> 的路径。
        /// 注意 Emby 的 <c>VideoRange</c> getter 传的是 <b>false</b>，所以属性路径由
        /// <see cref="VideoRangePostfix"/> 负责；这里只覆盖显式要求显示值的调用方。
        /// </summary>
        [HarmonyPostfix]
        private static void GenerateVideoRangePostfix(MediaStream __instance, bool isForDisplay, ref string __result)
        {
            try
            {
                if (!isForDisplay || __instance?.Type != MediaStreamType.Video) return;
                if (!IsAlreadyTagged(__instance)) return;

                __result = VividLabel;
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("HdrVividTagging GenerateVideoRangePostfix failed: " + ex.Message);
            }
        }

        private static bool IsVideoProbe(ProbeStream probe)
        {
            return string.Equals(probe?.codec_type, VideoCodecType, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAlreadyTagged(MediaStream stream)
        {
            return string.Equals(stream?.Comment, VividLabel, StringComparison.Ordinal);
        }

        /// <summary>
        /// 按 ffprobe 流序号定位对应的视频 <see cref="MediaStream"/>；序号缺失时回退为首路视频流。
        /// </summary>
        internal static MediaStream FindVideoStream(IEnumerable<MediaStream> streams, int? probeIndex)
        {
            if (streams == null) return null;

            MediaStream firstVideo = null;

            foreach (var stream in streams)
            {
                if (stream == null || stream.Type != MediaStreamType.Video) continue;

                if (probeIndex.HasValue && stream.Index == probeIndex.Value) return stream;
                if (firstVideo == null) firstVideo = stream;
            }

            return firstVideo;
        }
    }
}
