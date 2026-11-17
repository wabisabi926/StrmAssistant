using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;
using ProbeMediaInfo = MediaBrowser.Model.MediaInfo.MediaInfo;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// ISO 提取（跨平台，免第三方强依赖）。
    ///
    /// 背景（Emby 4.10 源码实测）：
    /// - <c>Emby.Providers.MediaInfo.FFProbeProvider.IsSupported(path, protocol)</c> 对 <c>.iso</c>
    ///   后缀**硬编码返回 false**，导致 ISO 条目永远不会进入 ffprobe 探测。
    /// - <c>BaseMediaInfoProber.Probe</c> 中：内置 mounter（BlurayMounter/DvdMounter）的
    ///   <c>CanMount</c> 只认**目录**（BDMV/VIDEO_TS 文件夹）+ container=bluray/dvd，
    ///   对 <c>.iso</c> **文件**一律返回 false，因此 path 不会被替换为挂载路径，
    ///   随后 <c>IsSupported(".iso")</c> 返回 false，探测被整体跳过。
    ///
    /// 本模块的两步干预：
    /// 1. **解锁闸门**：Prefix <c>FFProbeProvider.IsSupported</c>，当选项开启且路径为
    ///    <c>.iso</c>（本地文件 / strm 解析出的 http(s) ISO URL）时返回 true。
    /// 2. **替换探测输入**：Prefix <c>MediaProbeManager.GetMediaInfoInternal</c>，把
    ///    <c>inputPath</c>（形如 <c>file:"D:\a.iso"</c> 或 <c>"http://x/a.iso"</c>）替换为
    ///    ffprobe 能稳定读取的本地路径：
    ///    - 本地 .iso → Windows 挂载取正片；无挂载时进程内 UDF 选片，并把正片样本写成临时文件交给 ffprobe。
    ///    - 远程 http(s) ISO → HTTP Range 拉取头部窗口到本地临时文件再探测。
    ///
    /// 3. **补写章节**（Postfix 同方法，**仅本地 .iso**）：m2ts 没有章节原子，ffprobe 即使带
    ///    <c>-show_chapters</c> 也拿不到章节，导致 ISO 条目章节恒为空（进而章节图/BIF 永不生成）。
    ///    这里把 <see cref="UdfTitleResolver"/> 从 MPLS <c>PlayListMark</c> 解析出的章节注入探测结果，
    ///    由 Emby 的 <c>BaseMediaInfoProber</c> 搬进 <c>metadataResult.Chapters</c> 并落库。
    ///    远程（strm→HTTP）ISO 因头部窗口被截断、DiscUtils 无法构造 UDF 读取器，故不补写。
    ///
    /// 关键安全性：
    /// - 只替换 `inputPath`（仅用于 ffprobe 参数），**不改** `MediaSourceInfo.Path`，
    ///   因此播放路径与条目身份完全不受影响。
    /// - 无法取得可用本地路径时**保守放行原路径**，不影响常规探测。
    /// - 选项 <c>EnableIsoExtraction</c> 默认 OFF。
    /// </summary>
    public class IsoExtraction : PatchBase<IsoExtraction>
    {
        private static MethodInfo _getMediaInfoInternal;
        private static MethodInfo _isSupported;

        public IsoExtraction()
        {
            Initialize();

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().IsoTool.EnableIsoExtraction)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.MediaEncoding");
                var probeManager = assembly.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");

                _getMediaInfoInternal = probeManager?
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "GetMediaInfoInternal" && m.GetParameters().Length == 7);

                if (_getMediaInfoInternal == null)
                {
                    Plugin.Instance.Logger.Warn(
                        "IsoExtraction - MediaProbeManager.GetMediaInfoInternal not found; ISO extraction disabled");
                }

                // FFProbeProvider.IsSupported(string path, MediaProtocol protocol) —— 解锁 .iso 闸门
                var providers = Assembly.Load("Emby.Providers");
                var ffProbeProvider = providers.GetType("Emby.Providers.MediaInfo.FFProbeProvider");
                _isSupported = ffProbeProvider?
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "IsSupported" && m.GetParameters().Length == 2);

                if (_isSupported == null)
                {
                    Plugin.Instance.Logger.Warn(
                        "IsoExtraction - FFProbeProvider.IsSupported not found; ISO extraction may be skipped by Emby");
                }
                else if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"IsoExtraction - resolved {_getMediaInfoInternal} / {_isSupported}, " +
                        $"mount supported={IsoMounter.IsSupported}, udf supported={IsoMounter.UdfReadSupported}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("IsoExtraction init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_getMediaInfoInternal != null)
            {
                PatchUnpatch(PatchTracker, apply, _getMediaInfoInternal,
                    prefix: nameof(GetMediaInfoInternalPrefix), postfix: nameof(GetMediaInfoInternalPostfix));
            }

            if (_isSupported != null)
            {
                PatchUnpatch(PatchTracker, apply, _isSupported, prefix: nameof(IsSupportedPrefix));
            }
        }

        /// <summary>
        /// 解锁 Emby 对 .iso 的探测闸门：仅当选项开启且目标为 .iso 时跳过原实现、直接返回 true。
        /// </summary>
        [HarmonyPrefix]
        private static bool IsSupportedPrefix(string path, MediaProtocol protocol, ref bool __result)
        {
            try
            {
                if (Plugin.Instance.MediaInfoExtractStore.GetOptions().IsoTool.EnableIsoExtraction &&
                    !string.IsNullOrWhiteSpace(path) &&
                    IsoMounter.LooksLikeIsoPath(path))
                {
                    __result = true;
                    return false;
                }
            }
            catch
            {
                // 出错时回退原实现
            }

            return true;
        }

        [HarmonyPrefix]
        private static void GetMediaInfoInternalPrefix(ref string inputPath, ref MediaProtocol protocol, out IsoMounter.TitleHint __state)
        {
            __state = null;

            try
            {
                if (string.IsNullOrWhiteSpace(inputPath)) return;

                // Emby 启动早期 FfmpegConfiguration 可能为 null，覆盖会延后；探测前再兜底一次，
                // 保证首个 ISO 探测已经用上自定义 ffprobe。
                FfmpegPathOverride.EnsureApplied();

                var logger = Plugin.Instance.Logger;

                // ① 远程/流式 ISO（strm → http/https）：拉取头部窗口到本地临时文件
                //
                // Window probing and Range metadata resolution are separate. Unsupported multi-clip
                // titles keep their original ISO path and must not receive a single clip's metadata.
                if (IsoMounter.TryNormalizeRemoteIso(inputPath, out var isoUrl))
                {
                    var originalProtocol = protocol;
                    var tempPath = IsoMounter.GetRemoteProbePath(isoUrl);
                    if (!string.IsNullOrEmpty(tempPath))
                    {
                        inputPath = '"' + tempPath + '"';
                        protocol = MediaProtocol.File;
                        logger.Info($"IsoExtraction - remote iso probing via title sample -> {tempPath}");
                    }
                    else
                    {
                        logger.Info($"IsoExtraction - remote ISO title sample unavailable; leaving input unchanged: {isoUrl}");
                    }

                    // 章节：远程 ISO 走 Range 流 + 进程内 UDF（不落盘、按需取 UDF 元数据区）。
                    // 失败（不支持 Range / 无 mpls / 超时）静默回退，不影响探测本身。
                    __state = BuildRemoteHint(isoUrl);
                    if (__state != null) __state.OriginalProtocol = (int)originalProtocol;

                    return;
                }

                // ② 本地 .iso 文件：挂载取正片，失败退回进程内 UDF（不改探测输入）
                if (IsoMounter.TryNormalizeIsoPath(inputPath, out var isoPath))
                {
                    var probePath = IsoMounter.GetProbePath(isoPath);
                    if (!string.IsNullOrEmpty(probePath))
                    {
                        inputPath = '"' + probePath + '"';
                        protocol = MediaProtocol.File;
                        logger.Info($"IsoExtraction - probing mounted title for {isoPath} -> {probePath}");
                    }

                    // ★ 章节/语言补写（本地）：m2ts 没有章节原子，ffprobe 即便带 -show_chapters 也取不到。
                    //   Windows 挂载成功时用挂载卷解析；Linux 无挂载时 GetProbePath 返回 null，
                    //   但进程内 UDF 选片已经缓存了 playlist 信息，这里同样把 hint 交给 postfix。
                    //   ApplyChapters 会校验探测时长与 playlist 时长，避免把别的 m2ts 的结果套进来。
                    __state = BuildLocalHint(isoPath);

                    return;
                }

                if (Plugin.Instance.DebugMode &&
                    inputPath.IndexOf(".iso", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Plugin.Instance.Logger.Debug($"IsoExtraction - iso input not handled: {inputPath}");
                }
            }
            catch
            {
                // 保持原路径，不影响常规探测
                __state = null;
            }
        }

        /// <summary>
        /// 本地 ISO 的补充信息（章节 / 音轨字幕语言 / 总时长）。无缓存返回 null。
        /// </summary>
        private static IsoMounter.TitleHint BuildLocalHint(string isoPath)
        {
            return IsoMounter.TryGetTitleHint(isoPath, out var hint) ? hint : null;
        }

        /// <summary>
        /// 远程 ISO（strm → http）的补充信息：走 Range 流 + 进程内 UDF，失败静默返回 null。
        /// </summary>
        private static IsoMounter.TitleHint BuildRemoteHint(string isoUrl)
        {
            return IsoMounter.TryGetRemoteHint(isoUrl, out var hint) ? hint : null;
        }

        private static List<ChapterInfo> ToChapterInfos(List<double> chapterSeconds)
        {
            if (chapterSeconds == null || chapterSeconds.Count == 0) return null;

            return chapterSeconds
                .Where(seconds => seconds >= 0)
                .Select(seconds => new ChapterInfo
                {
                    StartPositionTicks = (long)(seconds * TimeSpan.TicksPerSecond)
                })
                .ToList();
        }

        /// <summary>
        /// 把 playlist 解析出的补充信息写进 ffprobe 探测结果。
        ///
        /// <c>GetMediaInfoInternal</c> 返回 <c>Task&lt;MediaInfo&gt;</c>，Harmony 的 <c>__result</c>
        /// 是 Task 本身（不会自动解包），因此这里**换包**成一个先 await 原任务、再写入的新任务返回，
        /// 调用方 await 到即是补写后的结果。
        /// </summary>
        [HarmonyPostfix]
        private static void GetMediaInfoInternalPostfix(ref Task<ProbeMediaInfo> __result,
            IsoMounter.TitleHint __state)
        {
            if (__state == null || __state.IsEmpty || __result == null) return;

            __result = ApplyHintsAsync(__result, __state);
        }

        private static async Task<ProbeMediaInfo> ApplyHintsAsync(Task<ProbeMediaInfo> probeTask,
            IsoMounter.TitleHint hint)
        {
            var mediaInfo = await probeTask.ConfigureAwait(false);
            if (mediaInfo == null) return null;
            // The temporary sample is a file; the actual library source remains HTTP.
            if (hint.IsProbeSample && hint.OriginalProtocol.HasValue)
                mediaInfo.Protocol = (MediaProtocol)hint.OriginalProtocol.Value;

            try
            {
                // 顺序有意义：先补时长，章节校验才有依据（远程 ISO 的 ffprobe 给不出时长）
                ApplyRuntime(mediaInfo, hint);
                ApplyBitrate(mediaInfo, hint);
                ApplyChapters(mediaInfo, hint);
                // 只有探测输入确实被换成了样本/挂载正片时，才清容器注册标签。
                // 裸 ISO 回退探测的结果不能按样本语义改写（否则会留下“清了一半”的假象）。
                if (hint.InputReplaced) ClearMisleadingCodecTags(mediaInfo);
                ApplyStreamDetails(mediaInfo, hint);
                LogProbeSummary(mediaInfo, hint);
            }
            catch (Exception e)
            {
                // 补写失败 = 用户少看到章节/语言，但探测本身已成功；用 Warn 让它可被定位
                Plugin.Instance.Logger.Warn("IsoExtraction - hint injection failed: " + e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }

            return mediaInfo;
        }

        /// <summary>
        /// 输出一条可检索的探测摘要，并在「STN 声明的流数 &gt; 实际探测到的流数」时告警。
        ///
        /// 采样只覆盖 playlist 的一部分片段；如果某路视频/音轨只出现在未采样的片段里，
        /// ffprobe 就会少报。静默接受会让客户端少一路流，必须留下明确日志。
        /// 字幕不参与告警：STN 的 PG/PIP 计数与 ffprobe 的可见字幕流口径不完全一致。
        /// </summary>
        private static void LogProbeSummary(ProbeMediaInfo mediaInfo, IsoMounter.TitleHint hint)
        {
            var streams = mediaInfo?.MediaStreams;
            var probedVideo = streams?.Count(s => s.Type == MediaStreamType.Video) ?? 0;
            var probedAudio = streams?.Count(s => s.Type == MediaStreamType.Audio) ?? 0;
            var probedSubtitle = streams?.Count(s => s.Type == MediaStreamType.Subtitle) ?? 0;
            var declaredVideo = hint.VideoStreams?.Count ?? 0;
            var declaredAudio = hint.AudioStreams?.Count ?? 0;
            var declaredSubtitle = hint.SubtitleStreams?.Count ?? 0;

            var coverage = hint.TotalClips > 0
                ? $"{hint.SampledClips}/{hint.TotalClips} clips"
                : "not sampled";
            Plugin.Instance?.Logger?.Info(
                $"IsoExtraction - probe summary: playlist={hint.SourceName ?? "?"}, {coverage}, " +
                $"probe={probedVideo}V/{probedAudio}A/{probedSubtitle}S, " +
                $"playlist declares={declaredVideo}V/{declaredAudio}A/{declaredSubtitle}S");

            if (hint.TotalClips <= 0) return;
            if (declaredVideo <= probedVideo && declaredAudio <= probedAudio) return;

            Plugin.Instance?.Logger?.Warn(
                $"IsoExtraction - probe stream mismatch: playlist declares " +
                $"{declaredVideo}V/{declaredAudio}A but probe returned {probedVideo}V/{probedAudio}A; " +
                $"sample covers {hint.SampledClips}/{hint.TotalClips} clips, some streams may be outside the sample");
        }

        /// <summary>
        /// 用正片真实字节数与 playlist 时长重算码率。
        ///
        /// ffprobe 探测的是「头 8MB + 尾 8MB」的截断样本，但样本保留了整片的原始时间戳。
        /// 对 MPEG-TS，ffprobe 的 bit_rate ≈ 文件字节 × 8 / 时间跨度，
        /// 于是把 16MB 样本套在 2.5 小时的跨度上，算出约 12 kbps 的荒谬值（实测 3 小时 4K 原盘）。
        /// ApplyRuntime 已把 Size / RunTimeTicks 覆盖成真实值，但 Bitrate 仍是这个脏数据，
        /// 会被 MediaInfoApi 原样写入 TotalBitrate，导致详情页/客户端显示错误码率。
        ///
        /// 这里用 UDF 选片时记录的正片真实字节数（非 ISO 总大小、非样本大小）重算：
        /// 容器码率 = 正片字节 × 8 / playlist 时长。
        /// 各媒体流按同一比例缩放（ffprobe 对各流的低估比例与容器一致），保持流间相对关系。
        /// 缺任一输入则整段跳过，保留 ffprobe 原值，绝不写入 0 或负数码率。
        ///
        /// 每路视频的**分配**另有一层：ffprobe 对 mpegts 的视频流给不出 bit_rate（全部 N/A），
        /// Emby 只能把容器码率平均分给各路视频 —— 带画中画的原盘会把 PiP 报成和主画面同码率。
        /// 有可统计的输入路径时（<see cref="IsoMounter.TitleHint.SamplePath"/>），
        /// 按 TS 的 PID 字节占比还原真实比例，见 <see cref="ApplyBitrateByPidBytes"/>。
        /// </summary>
        internal static void ApplyBitrate(ProbeMediaInfo mediaInfo, IsoMounter.TitleHint hint)
        {
            if (mediaInfo == null || hint == null) return;

            var totalBitrate = mediaInfo.Bitrate.GetValueOrDefault();
            // ffprobe 的容器原值（样本截断时是被污染的低估值）。判断「各流码率是不是真实值」
            // 必须拿它做基准 —— 换成重算后的真实值会把「流被低估」误判成「流远小于容器」。
            var probedBitrate = totalBitrate;
            var correctedBitrate = 0;

            // 第一步：样本截断时用正片真实字节重算容器码率（挂载路径的 ffprobe 值本来就对）
            if (hint.IsProbeSample && hint.SourceBytes > 0 &&
                hint.DurationSeconds > 0 && !double.IsNaN(hint.DurationSeconds) &&
                !double.IsInfinity(hint.DurationSeconds))
            {
                var rawBitrate = hint.SourceBytes * 8.0 / hint.DurationSeconds;
                // MediaInfo.Bitrate / MediaStream.BitRate 都是 int?（bit/s），封顶到 int.MaxValue 防止溢出
                if (rawBitrate > 0)
                {
                    correctedBitrate = (int)Math.Min(Math.Round(rawBitrate), int.MaxValue);
                    if (correctedBitrate > 0)
                    {
                        mediaInfo.Bitrate = correctedBitrate;
                        totalBitrate = correctedBitrate;
                    }
                }
            }

            if (totalBitrate <= 0) return;

            // 只对**视频流**做缩放判断。
            //
            // 音频流的 bit_rate 往往来自码流内真实标记（TrueHD 约 4 Mbps），而容器与视频流
            // 才是「样本字节 / 全片时长」的低估值。若把音频一起求和，video 17 kbps + audio 4 Mbps
            // 会让 sum/container 远大于 5，缩放被误判为“真实值”而整段跳过 —— 视频就永远停在
            // 17 kbps（真机 4K 原盘实测）。所以这里只统计视频流，且只缩放视频流。
            if (mediaInfo.MediaStreams == null || probedBitrate <= 0) return;
            var videoStreams = mediaInfo.MediaStreams
                .Where(s => s.Type == MediaStreamType.Video && s.BitRate.HasValue && s.BitRate.Value > 0)
                .ToList();
            if (videoStreams.Count == 0) return;

            var probedVideoSum = videoStreams.Sum(s => (long)s.BitRate.Value);
            if (probedVideoSum <= 0) return;

            // 视频流之和与容器**原值**同数量级 → 同样是样本污染（或 Emby 的等分），需要重新分配；
            // 远大于容器原值 → 是码流内 VUI 真实值，保持不动。
            var ratio = probedVideoSum / (double)probedBitrate;
            if (ratio < 0.2 || ratio > 5.0)
            {
                Plugin.Instance?.Logger?.Info(
                    $"IsoExtraction - video bitrates look real ({probedVideoSum} bps vs container " +
                    $"{probedBitrate} bps); scaling skipped");
                return;
            }

            // 第二步：优先按 PID 字节占比还原真实比例（多路视频才有意义）
            if (ApplyBitrateByPidBytes(hint, videoStreams, totalBitrate)) return;

            // 第三步：没有可统计的输入时退回「按 ffprobe 的比例整体缩放」。
            // 注意：ffprobe 对 mpegts 的每路视频都给 N/A，Emby 填的是等分值，
            // 所以这条路径对多路视频只能给出等分结果 —— 有样本时不会被走到。
            if (correctedBitrate <= 0) return;

            var scale = correctedBitrate / (double)probedVideoSum;
            foreach (var stream in videoStreams)
                stream.BitRate = (int)Math.Min(Math.Round(stream.BitRate.Value * scale), int.MaxValue);

            Plugin.Instance?.Logger?.Info(
                $"IsoExtraction - corrected bitrate from title bytes: {correctedBitrate / 1000000.0:F1} Mbps " +
                $"({hint.SourceBytes} bytes / {hint.DurationSeconds:F1}s, {videoStreams.Count} video stream(s))");
        }

        /// <summary>
        /// 按探测输入的 TS 字节占比，把容器码率分配给各路视频。
        ///
        /// 只在「PMT 声明的视频 PID 数 == 探测到的视频流数」时生效：
        /// 数量对不上说明样本没覆盖全部视频流，硬套会把码率分给错的流，宁可不做。
        /// 分配口径用**全部 PID 的字节**作分母，即视频只拿到属于视频的那部分，
        /// 音频开销不再被算进视频码率。
        /// </summary>
        private static bool ApplyBitrateByPidBytes(IsoMounter.TitleHint hint,
            List<MediaStream> videoStreams, int totalBitrate)
        {
            if (string.IsNullOrEmpty(hint.SamplePath)) return false;
            // 单路视频时 Emby 本来就把整个容器码率给它，改了只会引入偏差，不碰
            if (videoStreams.Count < 2) return false;
            // 探测的若不是我们替换过的输入（裸 ISO 直扫），结果不可信，一律不动
            if (!hint.InputReplaced) return false;

            var usage = TsByteAccounting.TryCount(hint.SamplePath);
            if (usage == null || usage.Count == 0) return false;

            var videoUsage = usage.Where(u => TsByteAccounting.IsVideoStreamType(u.StreamType)).ToList();
            if (videoUsage.Count != videoStreams.Count)
            {
                Plugin.Instance?.Logger?.Info(
                    $"IsoExtraction - TS byte accounting found {videoUsage.Count} video PID(s) " +
                    $"but probe returned {videoStreams.Count} video stream(s); per-stream split skipped");
                return false;
            }

            var totalBytes = usage.Sum(u => u.Bytes);
            var videoBytes = videoUsage.Sum(u => u.Bytes);
            if (totalBytes <= 0 || videoBytes <= 0) return false;

            for (var i = 0; i < videoStreams.Count; i++)
            {
                var share = videoUsage[i].Bytes / (double)totalBytes;
                videoStreams[i].BitRate =
                    (int)Math.Min(Math.Round(totalBitrate * share), int.MaxValue);
            }

            Plugin.Instance?.Logger?.Info(
                $"IsoExtraction - per-stream bitrate from TS byte accounting: " +
                $"{string.Join(" / ", videoStreams.Select(s => ((s.BitRate ?? 0) / 1000000.0).ToString("F2")))} Mbps " +
                $"(video {videoBytes}/{totalBytes} bytes, PIDs " +
                $"{string.Join(",", videoUsage.Select(u => "0x" + u.Pid.ToString("X4")))}; " +
                $"container {totalBitrate / 1000000.0:F1} Mbps)");
            return true;
        }

        /// <summary>
        /// 清掉 MPEG-TS / 蓝光容器层的注册标识被 ffprobe 当成「编解码器标签」写出的误导值。
        ///
        /// 蓝光 TS 的 PMT 里，TrueHD 用 stream_type 0x81（名义上的 AC-3）承载，
        /// HEVC/H.264 则注册为 HDMV。ffprobe 会同时给出真实的 codec（truehd / hevc）
        /// 和容器标签（"AC-3" / "HDMV"），客户端于是显示成「TRUEHD (AC-3)」这类自相矛盾的文案，
        /// 让人误以为音轨是 AC-3 或存在多条编码。
        ///
        /// 只清**明确不一致**的容器标签：
        /// - <c>AC-3</c> 只在 codec 确实是 ac3 时保留，其余（truehd / eac3 …）清空；
        /// - <c>HDMV</c> 是容器名而非编解码器，一律清空；
        /// 正常的四字符码（hev1 / hvc1 / avc1 / mp4a / ec-3 / dvhe …）一律不动。
        /// </summary>
        internal static int ClearMisleadingCodecTags(ProbeMediaInfo mediaInfo)
        {
            var streams = mediaInfo?.MediaStreams;
            if (streams == null || streams.Count == 0) return 0;

            var cleared = 0;
            foreach (var stream in streams)
            {
                var tag = stream.CodecTag;
                var codec = stream.Codec;
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(codec)) continue;

                bool misleading;
                switch (tag.Trim().ToLowerInvariant())
                {
                    case "ac-3":
                        // AC-3 在 MP4 里还有 "ac-3"/"ec-3" 两种写法，只有 ac3 本体该保留
                        misleading = !codec.Trim().Equals("ac3", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "hdmv":
                        // 蓝光容器注册名，不是编解码器
                        misleading = true;
                        break;
                    default:
                        misleading = false;
                        break;
                }

                if (!misleading) continue;
                stream.CodecTag = null;
                cleared++;
            }

            if (cleared > 0)
                Plugin.Instance?.Logger?.Info(
                    $"IsoExtraction - cleared {cleared} misleading codec tag(s) from probe result");
            return cleared;
        }

        /// <summary>探测结果没有时长时，用 playlist 各 PlayItem 时长之和补上。</summary>
        private static void ApplyRuntime(ProbeMediaInfo mediaInfo, IsoMounter.TitleHint hint)
        {
            if (hint.IsProbeSample && hint.SourceSize > 0) mediaInfo.Size = hint.SourceSize;
            if (!hint.IsProbeSample && mediaInfo.RunTimeTicks.HasValue && mediaInfo.RunTimeTicks.Value > 0) return;
            if (hint.DurationSeconds <= 0 || double.IsNaN(hint.DurationSeconds) || double.IsInfinity(hint.DurationSeconds)) return;

            mediaInfo.RunTimeTicks = (long)(hint.DurationSeconds * TimeSpan.TicksPerSecond);
            Plugin.Instance.Logger.Info(
                $"IsoExtraction - filled runtime from playlist: {hint.DurationSeconds:F1}s");
        }

        private static void ApplyChapters(ProbeMediaInfo mediaInfo, IsoMounter.TitleHint hint)
        {
            var chapters = ToChapterInfos(hint.Chapters);
            if (chapters == null || chapters.Count == 0) return;

            if (!mediaInfo.RunTimeTicks.HasValue || mediaInfo.RunTimeTicks.Value <= 0 ||
                chapters.Any(c => c.StartPositionTicks < 0 ||
                    c.StartPositionTicks > mediaInfo.RunTimeTicks.Value))
            {
                Plugin.Instance.Logger.Warn(
                    "IsoExtraction - playlist chapters do not fit the probed title; injection skipped");
                return;
            }

            // Linux 无挂载时探测的是整个 ISO，ffprobe 可能扫到别的 m2ts；
            // 时长对不上就不注入章节，宁可没有也不能把别的片段的章节标错。
            var probedSeconds = mediaInfo.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
            if (!RuntimesMatch(probedSeconds, hint.DurationSeconds))
            {
                Plugin.Instance.Logger.Info(
                    $"IsoExtraction - probed runtime {probedSeconds:F1}s does not match playlist " +
                    $"{hint.DurationSeconds:F1}s; playlist chapters skipped");
                return;
            }

            mediaInfo.Chapters = chapters.ToArray();

            Plugin.Instance.Logger.Info(
                $"IsoExtraction - injected {chapters.Count} playlist chapter(s) into probe result");
        }

        /// <summary>
        /// 用 playlist 的 STN_table 补音轨/字幕的**语言**，以及音轨的**声道数 / 采样率**。
        ///
        /// 背景：ffprobe 探测的是挂载出来的 <c>.m2ts</c>，而语言标签写在 MPLS 的 STN_table 里，
        /// 码流里不一定带 —— 不补的话 Emby 里音轨全显示"未知语言"。
        /// 声道/采样率同理：探测结果里缺（0）时才补，已有值不覆盖。
        /// </summary>
        private static void ApplyStreamDetails(ProbeMediaInfo mediaInfo, IsoMounter.TitleHint hint)
        {
            var streams = mediaInfo.MediaStreams;
            if (streams == null || streams.Count == 0) return;
            if (hint.AudioStreams.Count == 0 && hint.SubtitleStreams.Count == 0) return;

            // 与章节同理：探测时长对不上说明 ffprobe 读的不是 playlist 选中的标题，
            // 此时按位置补语言可能标错，宁可不补。
            if (!mediaInfo.RunTimeTicks.HasValue || mediaInfo.RunTimeTicks.Value <= 0) return;
            var probedSeconds = mediaInfo.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
            if (!RuntimesMatch(probedSeconds, hint.DurationSeconds))
            {
                Plugin.Instance.Logger.Info(
                    $"IsoExtraction - probed runtime {probedSeconds:F1}s does not match playlist " +
                    $"{hint.DurationSeconds:F1}s; playlist stream details skipped");
                return;
            }

            var audioFilled =
                FillMissingStreamDetails(streams.Where(s => s.Type == MediaStreamType.Audio).ToList(),
                    hint.AudioStreams, fillAudioDetails: true);
            var subtitleFilled =
                FillMissingStreamDetails(streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList(),
                    hint.SubtitleStreams, fillAudioDetails: false);

            if (audioFilled > 0 || subtitleFilled > 0)
            {
                Plugin.Instance.Logger.Info(
                    $"IsoExtraction - filled {audioFilled} audio / {subtitleFilled} subtitle field(s) from playlist STN");
            }
        }

        /// <summary>
        /// 按顺序把 STN 里的字段补到探测结果上。
        ///
        /// 只补**缺失**的（语言 null/空/<c>und</c>；声道/采样率为 0），已有值不覆盖；
        /// 两边数量不等则整体放弃 —— ffprobe 输出里没有 PID，无法精确匹配，
        /// **宁可标不出也不能标错**。
        /// </summary>
        internal static int FillMissingStreamDetails(List<MediaStream> probed,
            List<UdfTitleResolver.MplsStreamInfo> fromPlaylist, bool fillAudioDetails)
        {
            if (probed == null || fromPlaylist == null || probed.Count == 0 ||
                fromPlaylist.Count != probed.Count) return 0;

            var filled = 0;

            for (var i = 0; i < probed.Count; i++)
            {
                var target = probed[i];
                var source = fromPlaylist[i];

                if (IsUnknownLanguage(target.Language) && !IsUnknownLanguage(source.Language))
                {
                    target.Language = source.Language;
                    filled++;
                }

                if (!fillAudioDetails) continue;

                if (target.Channels.GetValueOrDefault() <= 0 && source.Channels > 0)
                {
                    target.Channels = source.Channels;
                    filled++;
                }

                if (target.SampleRate.GetValueOrDefault() <= 0 && source.SampleRate > 0)
                {
                    target.SampleRate = source.SampleRate;
                    filled++;
                }
            }

            return filled;
        }

        private static bool IsUnknownLanguage(string language) =>
            string.IsNullOrWhiteSpace(language) ||
            string.Equals(language, "und", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// playlist 时长与探测时长是否可视为同一条标题。容忍 5 秒，覆盖 m2ts 首尾时间戳差异；
        /// 任一未知时返回 false，由调用方决定是否跳过。
        /// </summary>
        internal static bool RuntimesMatch(double probedSeconds, double playlistSeconds)
        {
            if (probedSeconds <= 0 || playlistSeconds <= 0) return false;
            if (double.IsNaN(probedSeconds) || double.IsInfinity(probedSeconds) ||
                double.IsNaN(playlistSeconds) || double.IsInfinity(playlistSeconds)) return false;

            return Math.Abs(probedSeconds - playlistSeconds) <= 5;
        }
    }
}
