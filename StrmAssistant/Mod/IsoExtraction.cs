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
    ///    - 本地 .iso → Windows 挂载取正片；失败退回进程内 UDF 选片（不改输入，交由 ffprobe 直读）。
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

                        // ★ 章节补写（本地）：m2ts 无章节原子，ffprobe 即便带 -show_chapters 也取不到；
                        //   这里把 MPLS PlayListMark 解析出的章节带出，由 postfix 注入探测结果，
                        //   使 Emby 能把它落库到 Chapters3（BaseMediaInfoProber → metadataResult.Chapters）。
                        __state = BuildLocalHint(isoPath);
                    }

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
                ApplyChapters(mediaInfo, hint);
                ApplyStreamDetails(mediaInfo, hint);
            }
            catch (Exception e)
            {
                // 补写失败 = 用户少看到章节/语言，但探测本身已成功；用 Warn 让它可被定位
                Plugin.Instance.Logger.Warn("IsoExtraction - hint injection failed: " + e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }

            return mediaInfo;
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
    }
}
