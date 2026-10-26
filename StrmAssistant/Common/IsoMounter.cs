using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using StrmAssistant.Core;

namespace StrmAssistant.Common
{
    /// <summary>
    /// ISO 正片定位器（跨平台，免 OS 强依赖）。
    ///
    /// - **Windows**：调用 PowerShell 的 <c>Mount-DiskImage</c> 把 .iso 挂成虚拟光驱，
    ///   再由 <see cref="UdfTitleResolver.ResolveFromMount"/> 在挂载卷上按 playlist 选出正片
    ///   （失败回退「最大 .m2ts / VTS_01_1.VOB」），交给 ffprobe 探测。
    /// - **Linux / Docker**：无法挂载，改由 <see cref="UdfTitleResolver.Resolve"/> 进程内读 UDF
    ///   选出正片（仅用于发现与日志校验），.iso 本身仍交由 <c>CustomProbePath</c> 指定的
    ///   带 ISO 支持的自定义 ffprobe 直读。
    ///
    /// 生命周期：按 ISO 路径缓存挂载结果（含最后使用时间），后台清扫器在空闲超过
    /// <see cref="IdleTimeout"/> 后自动卸载；插件卸载时全部卸载。
    ///
    /// **远程 ISO（strm/http）**：无法挂载，且 Windows 版 libbluray 无法直读 UDF（实测
    /// <c>bluray:</c> 协议在文件路径/挂载盘/HTTP 上均失败）。改为：
    /// <see cref="GetRemoteProbePath"/> 用 HTTP Range 定位碟内标题，读取各片段的影音样本。
    /// ffprobe 处理影音样本，完整标题时长和章节由播放列表补充，不截取ISO头冒充影音数据。
    /// 临时文件按 URL 缓存并有空闲清理。
    /// </summary>
    public static class IsoMounter
    {
        private const string MountCmdlet = "Mount-DiskImage";
        private const string DismountCmdlet = "Dismount-DiskImage";

        /// <summary>远程 ISO 下载窗口大小：足够覆盖 UDF 头部 + 首个 m2ts 的 SPS/VPS（默认 8MB）。</summary>
        private const long RemoteWindowBytes = 8L * 1024 * 1024;

        /// <summary>远程 ISO 下载超时（毫秒）。慢速代理下 8MB 可能需要 1~2 分钟。</summary>
        private const int RemoteDownloadTimeoutMs = 180_000;

        /// <summary>读取空闲看门狗：连续该时长未收到新数据即中止（毫秒）。</summary>
        private const int ReadIdleTimeoutMs = 20_000;

        /// <summary>可接受的最小字节数：达到即视为窗口可用（典型 ISO 头部窗口远大于此）。</summary>
        private const long MinAcceptableBytes = 1L * 1024 * 1024;

        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RemoteCacheTtl = TimeSpan.FromMinutes(30);
        private static readonly object _lock = new object();

        private sealed class MountEntry
        {
            public string DriveRoot;
            public DateTime LastUsedUtc;
            public bool Failed;
        }

        private sealed class RemoteEntry
        {
            public string TempFile;
            public DateTime LastUsedUtc;
        }

        private static readonly ConcurrentDictionary<string, RemoteEntry> RemoteIsos =
            new ConcurrentDictionary<string, RemoteEntry>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, MountEntry> Mounts =
            new ConcurrentDictionary<string, MountEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 一次 playlist 解析能提供给探测结果的补充信息。
        ///
        /// 由 <see cref="ResolveMountedTitle"/> / <see cref="ResolveInProcessTitle"/> / 远程解析写入，
        /// 供 <c>IsoExtraction</c> 在 ffprobe 结果上补写：章节、音轨/字幕语言、总时长。
        ///
        /// 为什么不只存章节：m2ts 码流里不带语言标签（语言写在 MPLS 的 STN_table 里），
        /// 只靠 ffprobe 会让所有音轨显示"未知语言"；总时长在远程 ISO 上 ffprobe 也给不出。
        /// </summary>
        internal sealed class TitleHint
        {
            public List<double> Chapters = new List<double>();
            public List<UdfTitleResolver.MplsStreamInfo> AudioStreams = new List<UdfTitleResolver.MplsStreamInfo>();
            public List<UdfTitleResolver.MplsStreamInfo> SubtitleStreams = new List<UdfTitleResolver.MplsStreamInfo>();
            public double DurationSeconds;
            public bool IsProbeSample;
            public int? OriginalProtocol;
            public long SourceSize;

            public bool IsEmpty => !IsProbeSample && Chapters.Count == 0 && AudioStreams.Count == 0 &&
                                   SubtitleStreams.Count == 0 && DurationSeconds <= 0;
        }

        private static readonly ConcurrentDictionary<string, TitleHint> TitleHints =
            new ConcurrentDictionary<string, TitleHint>(StringComparer.OrdinalIgnoreCase);

        private static Timer _sweeper;
        private static bool? _supported;
        private static DateTime _supportedProbedUtc = DateTime.MinValue;

        /// <summary>
        /// 「不支持挂载」结果的缓存时长。
        /// <para>
        /// <b>为什么 false 不能永久缓存</b>：首次探测要起一个 <c>powershell.exe</c> 子进程（上限 30s）。
        /// 冷启动 / 杀软扫描 / 机器繁忙时可能超时，此时会误判成「不支持」。旧实现把结果缓存在
        /// <c>_supported</c> 里永不重算、且不打任何日志 —— 判错一次，整个进程生命周期都不挂载，
        /// 表现为「章节静默丢失」而日志毫无线索。改为 false 只缓存 5 分钟，到点重试。
        /// </para>
        /// <para>true 结果仍然永久缓存（探测成功没有理由再变）。</para>
        /// </summary>
        private static readonly TimeSpan NegativeSupportCacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>当前平台是否支持「OS 原生挂载」（Windows + 存在 Mount-DiskImage）。</summary>
        public static bool IsSupported
        {
            get
            {
                // 成功结果永久有效
                if (_supported == true) return true;

                // 失败结果只在冷却期内有效，到点重新探测（避免一次超时误判锁死整个进程）
                if (_supported == false &&
                    DateTime.UtcNow - _supportedProbedUtc < NegativeSupportCacheDuration)
                {
                    return false;
                }

                var stopwatch = Stopwatch.StartNew();
                string reason;

                try
                {
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        _supported = false;
                        reason = "not running on Windows";
                    }
                    else
                    {
                        var probe = RunPowerShell(
                            $"if (Get-Command {MountCmdlet} -ErrorAction SilentlyContinue) {{ 'yes' }}");
                        var output = probe?.StdOut?.Trim();

                        _supported = output == "yes";

                        reason = probe == null
                            ? "powershell process could not be started"
                            : probe.TimedOut
                                ? "powershell probe timed out (30s limit)"
                                : probe.ExitCode != 0
                                    ? $"powershell exit={probe.ExitCode}, err='{probe.StdErr?.Trim()}'"
                                    : $"Get-Command {MountCmdlet} -> '{output}'";
                    }
                }
                catch (Exception e)
                {
                    _supported = false;
                    reason = "probe threw: " + e.Message;
                }

                stopwatch.Stop();
                _supportedProbedUtc = DateTime.UtcNow;

                Plugin.Instance?.Logger?.Info(
                    $"IsoMounter - native mount support = {_supported.Value} ({reason}), " +
                    $"probe took {stopwatch.ElapsedMilliseconds} ms" +
                    (_supported.Value
                        ? string.Empty
                        : $"; will re-probe in {NegativeSupportCacheDuration.TotalMinutes:F0} min"));

                return _supported.Value;
            }
        }

        /// <summary>是否具备进程内 UDF 读盘能力（本插件内建 DiscUtils，跨平台恒可用）。</summary>
        public static bool UdfReadSupported => true;

        /// <summary>
        /// 宽松判断：路径是否「看起来是 ISO」——用于解锁 Emby 对 .iso 的探测闸门。
        ///
        /// 与 <see cref="TryNormalizeIsoPath"/> / <see cref="TryNormalizeRemoteIso"/> 不同，
        /// 这里**不做**存在性/协议校验，只按后缀判断；因为闸门处的 path 可能是：
        /// - 本地裸路径 <c>D:\a.iso</c>
        /// - ffmpeg 包裹形式 <c>file:"D:\a.iso"</c>
        /// - 远程 URL <c>http://x/api/strm?...&amp;file_name=a.iso</c>（含查询串，Path.GetExtension 仍为 .iso）
        ///
        /// 注意：URL 场景下 <see cref="Path.GetExtension(string)"/> 会因查询串返回空，故需先剥离 query。
        /// </summary>
        public static bool LooksLikeIsoPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var candidate = raw.Trim().Trim('"', '\'').Trim();
            if (candidate.Length == 0) return false;

            // 去掉 file: 前缀
            if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring(5).Trim().Trim('"', '\'').Trim();

            // ① 整体以 .iso 结尾（含 strm 代理 URL 把文件名放在 query 末尾的情形）
            if (candidate.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return true;

            // ② 剥离 query/fragment 后按路径扩展名判断；并额外对 query 做 URL 解码再判一次
            var q = candidate.IndexOf('?');
            if (q >= 0)
            {
                var pathPart = candidate.Substring(0, q);
                if (pathPart.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return true;

                try
                {
                    var decoded = Uri.UnescapeDataString(candidate);
                    if (decoded.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return true;

                    var dq = decoded.IndexOf('?');
                    if (dq >= 0 && decoded.Substring(0, dq).EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // 解码失败忽略
                }
            }

            return false;
        }

        /// <summary>
        /// 判断是否为本地 ISO 文件路径。
        /// 兼容 ffmpeg 输入串的两种包裹：<c>file:"D:\a.iso"</c>（本地文件）与 <c>"D:\a.iso"</c>。
        /// </summary>
        public static bool TryNormalizeIsoPath(string raw, out string isoPath)
        {
            isoPath = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var candidate = raw.Trim();

            // 去掉 Emby 为本地文件生成的 file: 协议前缀
            if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                candidate = candidate.Substring(5).Trim();

            candidate = candidate.Trim('"', '\'').Trim();
            if (candidate.Length == 0) return false;

            // 排除 concat/多输入与远程协议
            if (candidate.Contains("://") || candidate.Contains('|') ||
                candidate.Contains("concat:", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!candidate.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                if (!File.Exists(candidate)) return false;
            }
            catch
            {
                return false;
            }

            isoPath = candidate;
            return true;
        }

        /// <summary>
        /// 判断是否为「远程/流式」ISO 输入（http/https/strm 代理的 ISO）。
        ///
        /// 与本地的区别：
        /// - **不做** <see cref="File.Exists"/> 校验（URL 无法落地）；
        /// - 允许 `://`（http/https），但**拒绝** concat/多输入/管道串。
        /// - 兼容 ffprobe 已包裹的形式，如 <c>"http://x/a.iso"</c> 或 <c>file:"..."</c> 之外的裸 URL。
        ///
        /// 命中后由 <see cref="GetRemoteProbePath"/> 拉取头部窗口到临时文件再探测。
        /// </summary>
        public static bool TryNormalizeRemoteIso(string raw, out string isoUrl)
        {
            isoUrl = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var candidate = raw.Trim().Trim('"', '\'').Trim();
            if (candidate.Length == 0) return false;

            // 排除 concat/多输入/管道（远程 ISO 不应出现在这些复合串里）
            if (candidate.Contains('|') ||
                candidate.Contains("concat:", StringComparison.OrdinalIgnoreCase))
                return false;

            // 必须是 http/https 绝对 URL（strm 代理场景；其余协议如 smb 暂无内置支持，交由自定义 ffprobe）
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

            // Inspect decoded filename hints without decoding/rebuilding the signed URL itself.
            var isIso = Uri.UnescapeDataString(uri.AbsolutePath).EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
            if (!isIso)
            {
                foreach (var part in uri.Query.TrimStart('?').Split('&'))
                {
                    var pair = part.Split(new[] { '=' }, 2);
                    if (pair.Length != 2) continue;
                    var key = Uri.UnescapeDataString(pair[0]);
                    if (!new[] { "file", "filename", "file_name", "name", "path" }.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                    var name = Uri.UnescapeDataString(pair[1]);
                    if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) { isIso = true; break; }
                }
            }
            if (!isIso)
            {
                // 115 STRM links commonly expose only ?pickcode=...; the ISO filename appears
                // after redirect. Do not bypass the ISO sampler merely because the proxy URL
                // itself has no extension. This requests headers and at most a one-byte range.
                var is115Link = uri.Query.TrimStart('?').Split('&').Any(part =>
                    string.Equals(Uri.UnescapeDataString(part.Split('=')[0]), "pickcode", StringComparison.OrdinalIgnoreCase));
                if (!is115Link || !RedirectTargetsIso(candidate)) return false;
            }

            isoUrl = candidate;
            return true;
        }

        private static bool RedirectTargetsIso(string url)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.None
                });
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                request.Headers.UserAgent.ParseAdd("Emby/4.10 StrmAssistant");
                using var response = client.Send(request,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode) return false;
                var path = response.RequestMessage?.RequestUri?.AbsolutePath;
                var disposition = response.Content.Headers.ContentDisposition;
                var name = disposition?.FileNameStar ?? disposition?.FileName;
                return (!string.IsNullOrEmpty(path) && Uri.UnescapeDataString(path)
                            .EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) ||
                       (!string.IsNullOrEmpty(name) && Uri.UnescapeDataString(name.Trim('"'))
                            .EndsWith(".iso", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn("IsoExtraction - 115 redirect detection failed: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 远程 ISO → 本地临时文件（可交给 ffprobe 探测）。
        ///
        /// 用带真实总长度的 Range 流解析光盘目录，采样选中标题的影音数据而非 ISO 文件头。
        /// 临时文件仅用于媒体信息探测，playlist 提供完整标题时长与章节。
        ///
        /// 失败返回 null（调用方保持原路径放行）。
        /// </summary>
        public static string GetRemoteProbePath(string isoUrl)
        {
            if (string.IsNullOrEmpty(isoUrl)) return null;
            string tempFile = null;
            try
            {
                EnsureSweeper();
                if (RemoteIsos.TryGetValue(isoUrl, out var cached) && File.Exists(cached.TempFile))
                {
                    cached.LastUsedUtc = DateTime.UtcNow;
                    if (RemoteHints.TryGetValue(isoUrl, out var cachedHint)) cachedHint.LastUsedUtc = DateTime.UtcNow;
                    return cached.TempFile;
                }
                if (!HttpRangeStream.TryOpen(isoUrl, out var image, out var error,
                        TimeSpan.FromMilliseconds(RemoteDownloadTimeoutMs)))
                {
                    Plugin.Instance?.Logger?.Warn("IsoExtraction - cannot read remote ISO ranges: " + error);
                    return null;
                }
                using (image)
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "strmassistant_iso");
                    Directory.CreateDirectory(tempDir);
                    tempFile = Path.Combine(tempDir, "title_" + Guid.NewGuid().ToString("N") + ".ts");
                    var title = UdfTitleResolver.WriteProbeSample(image, tempFile, 64L * 1024 * 1024);
                    var hint = new TitleHint
                    {
                        Chapters = title.Chapters ?? new List<double>(),
                        AudioStreams = title.AudioStreams ?? new List<UdfTitleResolver.MplsStreamInfo>(),
                        SubtitleStreams = title.SubtitleStreams ?? new List<UdfTitleResolver.MplsStreamInfo>(),
                        DurationSeconds = title.DurationSeconds,
                        IsProbeSample = true,
                        SourceSize = image.Length
                    };
                    // Multi-clip metadata samples cover each play item. This does not enable
                    // returning one clip as a complete mounted playback title.
                    RemoteHints[isoUrl] = new RemoteHintEntry { Hint = hint, LastUsedUtc = DateTime.UtcNow };
                    RemoteIsos[isoUrl] = new RemoteEntry { TempFile = tempFile, LastUsedUtc = DateTime.UtcNow };
                    Plugin.Instance?.Logger?.Info($"IsoExtraction - remote title sample saved: {new FileInfo(tempFile).Length} bytes, playlist={title.SourceName}, duration={title.DurationSeconds:F1}s");
                    return tempFile;
                }
            }
            catch (Exception e)
            {
                TryDelete(tempFile);
                Plugin.Instance?.Logger?.Warn("IsoExtraction - remote title sample failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// HTTP Range 下载字节区间到本地文件。
        ///
        /// 与常规实现的差异（应对 strm 代理限流/卡死）：
        /// - **读取空闲看门狗**：连续 <see cref="ReadIdleTimeoutMs"/> 未收到新数据即中止，
        ///   返回已下载的部分内容（避免 HttpClient.Timeout 在某些代理上完全不触发而永久挂起）。
        /// - **接受部分内容**：只要已拿到 ≥ <see cref="MinAcceptableBytes"/>，即视为成功；
        ///   ISO 头部窗口（UDF 结构 + 首个 mpegts 同步头）在数 MB 内即可被 ffprobe 识别。
        /// - 目标文件以 <see cref="FileShare.Read"/> 打开，保持期间外部只读也能访问。
        ///
        /// 成功（含部分）返回 true。
        /// </summary>
        private static bool DownloadRange(string url, long from, long to, string destFile)
            => DownloadRangeWithTimeout(url, from, to, destFile, RemoteDownloadTimeoutMs, ReadIdleTimeoutMs);

        private static bool DownloadRangeWithTimeout(string url, long from, long to, string destFile,
            int overallTimeoutMs, int idleTimeoutMs)
        {
            var total = 0L;
            try
            {
                // One budget covers DNS/connect/response headers and the entire response body.
                using var overall = new CancellationTokenSource(overallTimeoutMs);
                using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.None
                }) { Timeout = Timeout.InfiniteTimeSpan };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Emby/4.10 StrmAssistant");
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                using var response = client.Send(request,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead, overall.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Plugin.Instance?.Logger?.Warn($"IsoExtraction - range request returned {(int)response.StatusCode}");
                    return false;
                }
                using var src = response.Content.ReadAsStream(overall.Token);
                using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                var buffer = new byte[1 << 20];
                var want = checked(to - from + 1);
                while (total < want)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    idle.CancelAfter(idleTimeoutMs);
                    var count = (int)Math.Min(buffer.Length, want - total);
                    var read = src.ReadAsync(buffer.AsMemory(0, count), idle.Token).AsTask().GetAwaiter().GetResult();
                    if (read <= 0) break;
                    dst.Write(buffer, 0, read);
                    total += read;
                }
                dst.Flush();
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn("IsoExtraction - range download error or timeout: " + e.Message);
            }
            if (total >= MinAcceptableBytes) return true;
            Plugin.Instance?.Logger?.Warn(
                $"IsoExtraction - range download produced only {total} bytes (< {MinAcceptableBytes}); discarding");
            return false;
        }

        private static string SafeShorten(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            var q = url.IndexOf('?');
            return q > 0 ? url.Substring(0, q) + "?...(len=" + url.Length + ")" : url;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        /// <summary>
        /// 返回可用于 ffprobe 的「正片」路径；无可用真实路径时返回 null（调用方保持原路径）。
        ///
        /// Windows 挂载成功 → 返回挂载卷上的正片路径；
        /// 非 Windows 或挂载失败 → 返回 null，仅做一次进程内 UDF 选片记录（Linux/Docker 由自定义 ffprobe 直读 .iso）。
        /// </summary>
        public static string GetProbePath(string isoPath)
        {
            if (string.IsNullOrEmpty(isoPath)) return null;

            if (IsSupported)
            {
                try
                {
                    var entry = Mounts.GetOrAdd(isoPath, k => Mount(k));
                    entry.LastUsedUtc = DateTime.UtcNow;

                    // 缓存盘符失效（被外部卸载 / 盘符被其它卷复用）→ 重置并重新挂载（方案 5）
                    if (!entry.Failed && !string.IsNullOrEmpty(entry.DriveRoot) &&
                        !IsVolumeStillMounted(isoPath, entry.DriveRoot))
                    {
                        Plugin.Instance?.Logger?.Info(
                            $"IsoExtraction - cached mount {entry.DriveRoot} for {isoPath} is stale, remounting");
                        entry = Mount(isoPath);
                        entry.LastUsedUtc = DateTime.UtcNow;
                        Mounts[isoPath] = entry;      // 覆盖失效条目
                    }

                    if (!entry.Failed && !string.IsNullOrEmpty(entry.DriveRoot))
                    {
                        var title = ResolveMountedTitle(isoPath, entry.DriveRoot);
                        if (!string.IsNullOrEmpty(title)) return title;

                        Plugin.Instance?.Logger?.Info(
                            $"IsoExtraction - no main title found under {entry.DriveRoot} for {isoPath}");
                    }
                    else
                    {
                        // 挂载没成功（Failed 或拿不到盘符）：下面会退回进程内 UDF，
                        // 但**不会**把章节注入探测结果。必须明说，否则表现为「章节静默丢失」。
                        Plugin.Instance?.Logger?.Info(
                            $"IsoExtraction - mount unavailable for {isoPath} " +
                            $"(failed={entry.Failed}, drive='{entry.DriveRoot}'); " +
                            "falling back to in-process UDF, playlist chapters will NOT be injected this round");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Instance?.Logger?.Warn("IsoExtraction - mount failed: " + e.Message);
                }
            }
            else
            {
                // IsSupported 为 false（非 Windows，或 PowerShell 探测失败/超时）。
                // 明确记录：否则用户只看到「没有章节」而日志毫无线索。
                Plugin.Instance?.Logger?.Info(
                    "IsoExtraction - native mount not supported on this host; " +
                    $"falling back to in-process UDF for {isoPath} (playlist chapters will NOT be injected)");
            }

            ResolveInProcessTitle(isoPath);
            return null;
        }

        /// <summary>
        /// 已挂载卷上定位正片：优先按 playlist 选片（多 m2ts / 无缝分支才不会选错），再回退旧逻辑。
        /// </summary>
        private static string ResolveMountedTitle(string isoPath, string driveRoot)
        {
            var info = UdfTitleResolver.ResolveFromMount(driveRoot);
            if (!string.IsNullOrEmpty(info?.UnsupportedReason))
            {
                TitleHints.TryRemove(isoPath, out _);
                Plugin.Instance?.Logger?.Warn("IsoExtraction - " + info.UnsupportedReason);
                return null;
            }

            RememberTitle(isoPath, info);

            if (!string.IsNullOrEmpty(info?.RelativePath))
            {
                var path = Path.Combine(driveRoot, info.RelativePath);

                if (File.Exists(path))
                {
                    Plugin.Instance?.Logger?.Info(
                        $"IsoExtraction - selected {info.RelativePath} " +
                        $"(playlist {info.SourceName}, {info.DurationSeconds:F1}s) from {isoPath}");
                    return path;
                }

                Plugin.Instance?.Logger?.Warn(
                    $"IsoExtraction - selected title {info.RelativePath} not found on {driveRoot} for {isoPath}");
            }

            var fallback = ResolveMainTitle(driveRoot);
            if (!string.IsNullOrEmpty(fallback))
            {
                Plugin.Instance?.Logger?.Info(
                    $"IsoExtraction - fallback to largest title {fallback} for {isoPath}");
            }

            return fallback;
        }

        /// <summary>
        /// 非挂载路径：进程内读 UDF 选出正片，仅记录结果用于发现与日志校验（不改探测输入）。
        /// </summary>
        private static void ResolveInProcessTitle(string isoPath)
        {
            if (!UdfReadSupported) return;

            var info = UdfTitleResolver.Resolve(isoPath);
            var logger = Plugin.Instance?.Logger;
            if (!string.IsNullOrEmpty(info?.UnsupportedReason))
            {
                TitleHints.TryRemove(isoPath, out _);
                logger?.Warn("IsoExtraction - " + info.UnsupportedReason);
                return;
            }
            if (logger == null) return;

            RememberTitle(isoPath, info);

            if (info == null)
            {
                logger.Debug($"IsoExtraction - UDF title resolution found nothing in {isoPath}");
                return;
            }

            logger.Info(
                $"IsoExtraction - UDF resolved {(info.IsBluray ? "bluray" : "dvd")} title {info.RelativePath} " +
                $"(playlist {info.SourceName}, {info.DurationSeconds:F1}s) in {isoPath}; probing the iso directly");
        }

        /// <summary>
        /// 记录一次解析得到的章节（仅当有内容时覆盖；解析失败/为空时不覆盖既有缓存）。
        /// </summary>
        private static void RememberTitle(string isoPath, UdfTitleResolver.TitleInfo info)
        {
            if (!string.IsNullOrEmpty(info?.UnsupportedReason))
            {
                if (!string.IsNullOrEmpty(isoPath)) TitleHints.TryRemove(isoPath, out _);
                return;
            }
            if (info == null) return;

            var hint = new TitleHint
            {
                Chapters = info.Chapters != null ? new List<double>(info.Chapters) : new List<double>(),
                AudioStreams = info.AudioStreams ?? new List<UdfTitleResolver.MplsStreamInfo>(),
                SubtitleStreams = info.SubtitleStreams ?? new List<UdfTitleResolver.MplsStreamInfo>(),
                DurationSeconds = info.DurationSeconds
            };

            // 什么都没解析出来时不要覆盖既有缓存（避免一次失败把上一轮的好结果清掉）
            if (hint.IsEmpty) return;

            try
            {
                TitleHints[isoPath] = hint;

                if (hint.Chapters.Count > 0)
                {
                    Plugin.Instance?.Logger?.Info(
                        $"IsoExtraction - playlist chapters resolved for {isoPath}: {hint.Chapters.Count} " +
                        $"(first={hint.Chapters[0]:F3}s, last={hint.Chapters[hint.Chapters.Count - 1]:F3}s)");
                }

                if (hint.AudioStreams.Count > 0 || hint.SubtitleStreams.Count > 0)
                {
                    Plugin.Instance?.Logger?.Info(
                        $"IsoExtraction - playlist STN resolved for {isoPath}: " +
                        $"{hint.AudioStreams.Count} audio, {hint.SubtitleStreams.Count} subtitle, " +
                        $"{hint.DurationSeconds:F1}s");
                }
            }
            catch
            {
                // 记录失败不影响探测
            }
        }

        /// <summary>
        /// 取该 ISO 最近一次解析出的章节（标题内偏移秒）。
        /// Emby 对 m2ts 走 ffprobe <c>-show_chapters</c> 永远拿不到章节（m2ts 无章节原子），
        /// 需由调用方把这里的 MPLS 章节补写进探测结果。
        /// </summary>
        public static bool TryGetTitleChapters(string isoPath, out List<double> chapters)
        {
            chapters = null;
            if (!TryGetTitleHint(isoPath, out var hint)) return false;
            if (hint.Chapters.Count == 0) return false;

            chapters = hint.Chapters;
            return true;
        }

        /// <summary>
        /// 取该 ISO 最近一次解析出的完整补充信息（章节 + 音轨/字幕语言 + 总时长）。
        /// 无缓存或全部为空时返回 false。
        /// </summary>
        internal static bool TryGetTitleHint(string isoPath, out TitleHint hint)
        {
            hint = null;
            if (string.IsNullOrEmpty(isoPath)) return false;
            if (!TitleHints.TryGetValue(isoPath, out var cached) || cached == null || cached.IsEmpty) return false;

            hint = cached;
            return true;
        }

        private sealed class RemoteHintEntry
        {
            public string UnsupportedReason;
            public TitleHint Hint;
            public DateTime LastUsedUtc;
        }

        private static readonly ConcurrentDictionary<string, RemoteHintEntry> RemoteHints =
            new ConcurrentDictionary<string, RemoteHintEntry>(StringComparer.Ordinal);

        /// <summary>
        /// 远程 ISO（strm → http）的章节解析：用 <see cref="HttpRangeStream"/> 提供真实长度的可 Seek 流，
        /// 交给 <see cref="UdfTitleResolver"/> 走进程内 UDF → MPLS PlayListMark。
        ///
        /// 与本地 ISO 的差别：本地走「挂载 + 真实文件系统」，远程只能走进程内 UDF（不落盘、按需 Range）。
        /// 结果按 URL 缓存 <see cref="RemoteCacheTtl"/>，避免每次刷新都重新解析。
        /// **任何失败都返回 false 并静默回退**（拿不到章节不能影响探测本身）。
        /// </summary>
        public static bool TryGetRemoteChapters(string isoUrl, out List<double> chapters)
        {
            chapters = null;
            if (!TryGetRemoteHint(isoUrl, out var hint) || hint.Chapters.Count == 0) return false;

            chapters = hint.Chapters;
            return true;
        }

        /// <summary>
        /// 远程 ISO 的完整补充信息（章节 + 音轨/字幕语言 + 总时长）。语义同 <see cref="TryGetTitleHint"/>。
        /// </summary>
        internal static bool TryGetRemoteHint(string isoUrl, out TitleHint hint)
        {
            hint = null;
            if (string.IsNullOrEmpty(isoUrl)) return false;

            if (RemoteHints.TryGetValue(isoUrl, out var cached) &&
                DateTime.UtcNow - cached.LastUsedUtc < RemoteCacheTtl)
            {
                cached.LastUsedUtc = DateTime.UtcNow;
                hint = cached.Hint;
                return hint != null && !hint.IsEmpty;
            }

            try
            {
                using (PerfProbe.Measure("IsoMounter.RemoteChapters", 3000))
                {
                    if (!HttpRangeStream.TryOpen(isoUrl, out var stream, out var error))
                    {
                        Plugin.Instance?.Logger?.Info(
                            $"IsoExtraction - remote iso does not support Range, chapters skipped: {error}");

                        RemoteHints[isoUrl] = new RemoteHintEntry { Hint = null, LastUsedUtc = DateTime.UtcNow };
                        return false;
                    }

                    using (stream)
                    {
                        var info = UdfTitleResolver.Resolve(stream, isoUrl);
                        if (!string.IsNullOrEmpty(info?.UnsupportedReason))
                            Plugin.Instance?.Logger?.Warn("IsoExtraction - " + info.UnsupportedReason);

                        var usable = string.IsNullOrEmpty(info?.UnsupportedReason);
                        var resolved = usable
                            ? new TitleHint
                            {
                                Chapters = info.Chapters ?? new List<double>(),
                                AudioStreams = info.AudioStreams ?? new List<UdfTitleResolver.MplsStreamInfo>(),
                                SubtitleStreams = info.SubtitleStreams ?? new List<UdfTitleResolver.MplsStreamInfo>(),
                                DurationSeconds = info.DurationSeconds
                            }
                            : null;

                        if (resolved != null && resolved.Chapters.Count > 0)
                        {
                            Plugin.Instance?.Logger?.Info(
                                $"IsoExtraction - remote playlist chapters resolved for {SafeShorten(isoUrl)}: " +
                                $"{resolved.Chapters.Count} (first={resolved.Chapters[0]:F3}s, last={resolved.Chapters[resolved.Chapters.Count - 1]:F3}s)");
                        }
                        else
                        {
                            Plugin.Instance?.Logger?.Info(
                                $"IsoExtraction - remote iso has no resolvable playlist chapters: {SafeShorten(isoUrl)}");
                        }

                        if (resolved != null && (resolved.AudioStreams.Count > 0 || resolved.SubtitleStreams.Count > 0))
                        {
                            Plugin.Instance?.Logger?.Info(
                                $"IsoExtraction - remote playlist STN resolved for {SafeShorten(isoUrl)}: " +
                                $"{resolved.AudioStreams.Count} audio, {resolved.SubtitleStreams.Count} subtitle, " +
                                $"{resolved.DurationSeconds:F1}s");
                        }

                        RemoteHints[isoUrl] = new RemoteHintEntry
                        {
                            Hint = resolved,
                            UnsupportedReason = info?.UnsupportedReason,
                            LastUsedUtc = DateTime.UtcNow
                        };

                        hint = resolved;
                        return resolved != null && !resolved.IsEmpty;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn(
                    $"IsoExtraction - remote chapter resolution failed: {e.Message}");

                RemoteHints[isoUrl] = new RemoteHintEntry { Hint = null, LastUsedUtc = DateTime.UtcNow };
                return false;
            }
        }

        /// <summary>
        /// 该路径是否为「本地 ISO 挂载出的正片」——即 <c>&lt;盘符&gt;:\BDMV\STREAM\*.m2ts</c>。
        ///
        /// 用途：抽帧时为这类输入追加 <c>-skip_frame nokey</c>（规避 mpegts input seek 花屏）。
        /// **不包含**远程 ISO 头部窗口临时文件：窗口通常只有数百 ms 的视频，<c>-ss</c> 落点之后
        /// 未必还有关键帧，追加该参数反而可能抽不出帧（远程窗口的 <c>-ss</c> 很小、原本不花屏）。
        /// </summary>
        public static bool IsMountedIsoTitle(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            return path.IndexOf(@"\BDMV\STREAM\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static MountEntry Mount(string isoPath)
        {
            var entry = new MountEntry { LastUsedUtc = DateTime.UtcNow };

            try
            {
                EnsureSweeper();

                var script =
                    $"$ErrorActionPreference='Stop'; " +
                    $"$img = Mount-DiskImage -ImagePath '{EscapePs(isoPath)}' -PassThru; " +
                    "($img | Get-Volume).DriveLetter";

                var res = RunPowerShell(script);
                var output = res?.StdOut?.Trim();

                if (res == null || res.TimedOut || res.ExitCode != 0 ||
                    string.IsNullOrEmpty(output) || output.Length > 3)
                {
                    entry.Failed = true;
                    Plugin.Instance?.Logger?.Warn(
                        $"IsoExtraction - Mount-DiskImage failed for {isoPath}: " +
                        $"exit={res?.ExitCode}, out='{output}', err='{res?.StdErr?.Trim()}'" +
                        (res?.TimedOut == true ? ", timed out" : ""));

                    // 回滚：镜像可能已挂载但取盘符失败，立即尝试释放，避免孤儿挂载
                    Dismount(isoPath);
                    return entry;
                }

                entry.DriveRoot = output.TrimEnd(':') + ":\\";
                ManifestAdd(isoPath);
                Plugin.Instance?.Logger?.Info($"IsoExtraction - mounted {isoPath} -> {entry.DriveRoot}");
            }
            catch (Exception e)
            {
                entry.Failed = true;
                Plugin.Instance?.Logger?.Warn($"IsoExtraction - Mount-DiskImage failed for {isoPath}: {e.Message}");
            }

            return entry;
        }

        /// <summary>
        /// 在已挂载的卷里定位正片：
        /// 1) Bluray: BDMV\STREAM 下最大的 .m2ts
        /// 2) DVD:    VIDEO_TS\VTS_01_1.VOB（缺失则取最大的 .VOB）
        /// 3) 兜底:   卷内（深度 ≤ 3）最大的常见视频文件
        /// </summary>
        private static string ResolveMainTitle(string driveRoot)
        {
            try
            {
                var bdmvStream = Path.Combine(driveRoot, "BDMV", "STREAM");
                var m2ts = PickLargest(bdmvStream, "*.m2ts", "*.M2TS");
                if (!string.IsNullOrEmpty(m2ts)) return m2ts;

                var videoTs = Path.Combine(driveRoot, "VIDEO_TS");
                if (Directory.Exists(videoTs))
                {
                    var main = Path.Combine(videoTs, "VTS_01_1.VOB");
                    if (File.Exists(main)) return main;

                    var vob = PickLargest(videoTs, "*.VOB", "*.vob");
                    if (!string.IsNullOrEmpty(vob)) return vob;
                }

                // 兜底：递归（限深）找最大视频文件
                string[] extensions = { ".m2ts", ".mts", ".mkv", ".mp4", ".ts", ".vob", ".avi", ".mov", ".mpg", ".mpeg", ".wmv", ".flv" };
                var best = Directory.EnumerateFiles(driveRoot, "*", SearchOption.AllDirectories)
                    .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();

                return best?.FullName;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("IsoExtraction - ResolveMainTitle failed: " + e.Message);
                return null;
            }
        }

        private static string PickLargest(string dir, params string[] patterns)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;

                return patterns
                    .SelectMany(p => Directory.EnumerateFiles(dir, p))
                    .Select(f => new FileInfo(f))
                    .Where(f => f.Length > 0)
                    .OrderByDescending(f => f.Length)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureSweeper()
        {
            lock (_lock)
            {
                _sweeper ??= new Timer(_ => SweepIdle(), null, IdleTimeout, TimeSpan.FromMinutes(1));
            }
        }

        private static void SweepIdle()
        {
            try
            {
                foreach (var kv in Mounts.ToArray())
                {
                    if (kv.Value.Failed)
                    {
                        // 挂载失败 ≠ OS 层未挂上：先尝试卸载，成功才移除；失败则保留并延后重试
                        if (Dismount(kv.Key)) Mounts.TryRemove(kv.Key, out _);
                        else kv.Value.LastUsedUtc = DateTime.UtcNow;
                        continue;
                    }

                    if (DateTime.UtcNow - kv.Value.LastUsedUtc < IdleTimeout) continue;
                    if (Dismount(kv.Key)) Mounts.TryRemove(kv.Key, out _);
                }

                // 远程 ISO 临时文件：超过 TTL 清理
                foreach (var kv in RemoteIsos.ToArray())
                {
                    if (DateTime.UtcNow - kv.Value.LastUsedUtc < RemoteCacheTtl) continue;

                    TryDelete(kv.Value.TempFile);
                    RemoteIsos.TryRemove(kv.Key, out _);
                }

                // 远程 ISO 解析结果缓存：超过 TTL 清理
                foreach (var kv in RemoteHints.ToArray())
                {
                    if (DateTime.UtcNow - kv.Value.LastUsedUtc < RemoteCacheTtl) continue;
                    RemoteHints.TryRemove(kv.Key, out _);
                }
            }
            catch
            {
                // 忽略
            }
        }

        public static void DismountAll()
        {
            try
            {
                _sweeper?.Dispose();
                _sweeper = null;
            }
            catch
            {
                // 忽略
            }

            foreach (var kv in Mounts.ToArray())
            {
                if (Dismount(kv.Key)) Mounts.TryRemove(kv.Key, out _);
            }

            // 清理全部远程 ISO 临时文件
            foreach (var kv in RemoteIsos.ToArray())
            {
                TryDelete(kv.Value.TempFile);
                RemoteIsos.TryRemove(kv.Key, out _);
            }
        }

        private static bool Dismount(string isoPath)
        {
            // 先探测真实挂载状态：仅在确实 Attached 时才卸载，并据实返回结果（杜绝"假成功"）
            var script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                $"$img = Get-DiskImage -ImagePath '{EscapePs(isoPath)}'; " +
                "if ($null -eq $img -or -not $img.Attached) { 'not-attached' } " +
                "else { " +
                    $"Dismount-DiskImage -ImagePath '{EscapePs(isoPath)}' | Out-Null; " +
                    $"$img = Get-DiskImage -ImagePath '{EscapePs(isoPath)}'; " +
                    "if ($null -ne $img -and $img.Attached) { 'still-attached' } else { 'dismounted' } }";

            var res = RunPowerShell(script);
            var token = res?.StdOut?.Trim();

            switch (token)
            {
                case "dismounted":
                    Plugin.Instance?.Logger?.Info($"IsoExtraction - dismounted {isoPath}");
                    ManifestRemove(isoPath);                 // 方案 4A：同步挂载清单
                    return true;

                case "not-attached":
                    // 本就没有挂载：视为"已释放"，调用方安全移除跟踪
                    Plugin.Instance?.Logger?.Info($"IsoExtraction - {isoPath} not attached, nothing to dismount");
                    ManifestRemove(isoPath);                 // 方案 4A
                    return true;

                default:
                    Plugin.Instance?.Logger?.Warn(
                        $"IsoExtraction - dismount failed for {isoPath}: " +
                        $"exit={res?.ExitCode}, out='{token}', err='{res?.StdErr?.Trim()}'" +
                        (res?.TimedOut == true ? ", timed out" : ""));
                    return false;
            }
        }

        // ===== 方案 4A：挂载清单持久化 + 启动期对账 =====

        private const string ManifestFileName = "StrmAssistant_iso_mounts.txt";

        private static string ManifestFilePath()
        {
            var dir = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, ManifestFileName);
        }

        private static void ManifestAdd(string isoPath)
        {
            var file = ManifestFilePath();
            if (file == null) return;

            try
            {
                lock (_lock)
                {
                    var lines = File.Exists(file)
                        ? File.ReadAllLines(file).ToList()
                        : new List<string>();

                    if (!lines.Any(l => string.Equals(l.Trim(), isoPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        lines.Add(isoPath);
                        File.WriteAllLines(file, lines);
                    }
                }
            }
            catch
            {
                // 忽略：清单写入失败不影响挂载主流程
            }
        }

        private static void ManifestRemove(string isoPath)
        {
            var file = ManifestFilePath();
            if (file == null) return;

            try
            {
                lock (_lock)
                {
                    if (!File.Exists(file)) return;

                    var lines = File.ReadAllLines(file)
                        .Where(l => !string.Equals(l.Trim(), isoPath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (lines.Count == 0) File.Delete(file);
                    else File.WriteAllLines(file, lines);
                }
            }
            catch
            {
                // 忽略
            }
        }

        private static bool IsAttached(string isoPath)
        {
            var script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                $"$img = Get-DiskImage -ImagePath '{EscapePs(isoPath)}'; " +
                "if ($null -ne $img -and $img.Attached) { 'attached' } else { 'detached' }";

            return string.Equals(RunPowerShell(script)?.StdOut?.Trim(), "attached", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 校验缓存盘符是否仍是该 ISO 的挂载点（方案 5）：
        /// 用 <c>Get-DiskImage</c> 反查该 ISO 当前实际挂载盘符，与缓存比对；不一致或已卸载即视为失效。
        /// </summary>
        private static bool IsVolumeStillMounted(string isoPath, string driveRoot)
        {
            var script =
                "$ErrorActionPreference='SilentlyContinue'; " +
                $"$img = Get-DiskImage -ImagePath '{EscapePs(isoPath)}'; " +
                $"if ($null -ne $img -and $img.Attached) {{ ((Get-DiskImage -ImagePath '{EscapePs(isoPath)}' | Get-Volume).DriveLetter) }}";

            var outv = RunPowerShell(script)?.StdOut?.Trim();
            return !string.IsNullOrEmpty(outv) &&
                   string.Equals(outv.TrimEnd(':') + ":\\", driveRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 启动期对账：释放上一次进程遗留的孤儿挂载（跨 Emby 重启泄漏的唯一兜底）。
        /// 依赖挂载清单文件；无清单或清单为空时直接返回。
        /// </summary>
        public static void ReconcileOnStartup()
        {
            var file = ManifestFilePath();
            if (file == null || !File.Exists(file)) return;

            string[] paths;
            try
            {
                paths = File.ReadAllLines(file)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToArray();
            }
            catch
            {
                return;
            }

            var logger = Plugin.Instance?.Logger;

            foreach (var p in paths)
            {
                try
                {
                    if (IsAttached(p))
                    {
                        logger?.Info($"IsoExtraction - reconciling orphan mount from previous run: {p}");
                        Dismount(p);
                    }
                }
                catch
                {
                    // 忽略单条失败
                }
            }

            try { File.Delete(file); } catch { /* 忽略 */ }
        }

        private static string EscapePs(string value) => value.Replace("'", "''");

        /// <summary>PowerShell 调用的结构化结果（方案 2）：暴露退出码 / stderr / 超时，供上层据实判定。</summary>
        private sealed class PsResult
        {
            public string StdOut;
            public string StdErr;
            public int ExitCode = -1;   // -1：进程未能启动
            public bool TimedOut;
        }

        private static string WaitString(Task<string> task)
        {
            try { return task.Wait(2000) ? task.Result : string.Empty; }
            catch { return string.Empty; }
        }

        private static PsResult RunPowerShell(string script)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                            script.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var result = new PsResult();
            try
            {
                using var process = Process.Start(psi);
                if (process == null) return result;

                // 并发读取两条管道，避免其中一条写满导致另一条阻塞（原先顺序读取存在死锁风险）
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(true); } catch { /* 忽略 */ }
                    result.TimedOut = true;
                }

                result.StdOut = WaitString(stdout);
                result.StdErr = WaitString(stderr);
                if (!result.TimedOut) result.ExitCode = process.ExitCode;
            }
            catch (Exception e)
            {
                result.StdErr = e.Message;
            }

            return result;
        }
    }
}
