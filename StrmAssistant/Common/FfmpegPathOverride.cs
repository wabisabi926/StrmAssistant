using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 自定义 ffmpeg/ffprobe 路径覆盖：把 Emby 全局 FfmpegConfiguration 的
    /// ProbePath / EncoderPath 底层字段替换为用户指定二进制（静态编译版、
    /// rffmpeg 包装器等），用于解决 DV P5 截图发绿、ISO/bluray 协议支持等问题。
    /// 自检日志：运行 -version 并输出关键能力行。
    /// </summary>
    public static class FfmpegPathOverride
    {
        private static IFfmpegManager _ffmpegManager;
        private static ILogger _logger;
        private static bool _initialized;

        private static readonly object ApplySync = new object();
        private static string _pendingProbePath;
        private static string _pendingEncoderPath;
        private static int _applyVersion;
        private static int _ensureAttemptVersion = -1;

        /// <summary>
        /// 自定义 ffprobe 是否具备 bluray 输入协议能力（<c>--enable-libbluray</c>）。
        /// 由 <see cref="SelfCheck"/> 在应用 CustomProbePath 时探测并缓存，用于日志与诊断。
        ///
        /// 注意：ISO 提取已改为「进程内 UDF 选片 + 正片采样」，不再依赖
        /// <c>bluray:"{0}"</c>（该协议读 ISO 还需要 libudfread）。此能力位保留用于排查外部二进制差异。
        ///
        /// 语义：<c>null</c>=尚未探测（未知）；<c>true</c>=支持；<c>false</c>=不支持。
        /// </summary>
        public static bool? ProbeSupportsBluray { get; private set; }

        /// <summary>自定义 ffmpeg 是否具备 bluray 输入协议能力（预留，用于截图/章节图路径）。</summary>
        public static bool? EncoderSupportsBluray { get; private set; }

        /// <summary>
        /// 当前生效的 ffprobe 路径（含用户自定义覆盖）。未初始化或配置为空时返回 null。
        /// 供需要自己发起一次探测的功能（如 HDR Vivid 首帧补探）使用。
        /// </summary>
        public static string CurrentProbePath
        {
            get
            {
                try
                {
                    return _ffmpegManager?.FfmpegConfiguration?.ProbePath;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static void Initialize(IFfmpegManager ffmpegManager, ILogger logger)
        {
            _ffmpegManager = ffmpegManager;
            _logger = logger;
            _initialized = true;
        }

        /// <summary>
        /// 按当前配置应用路径覆盖。空值或文件不存在时跳过对应项，不回滚已生效的覆盖。
        ///
        /// Emby 启动时 <see cref="IFfmpegManager.FfmpegConfiguration"/> 可能尚未创建（实测为 null），
        /// 此时不能直接放弃，否则自定义 ffprobe 永远不会生效，直到用户手动重新保存设置。
        /// 这里把待应用路径记下来，后台每 2 秒重试，最多 3 分钟；更晚保存的设置会通过版本号复用同一个重试。
        /// </summary>
        public static void Apply(string customProbePath, string customEncoderPath)
        {
            if (_logger != null)
            {
                _logger.Info(
                    "FfmpegPathOverride - Apply invoked: probe='{0}', encoder='{1}', initialized={2}, mgr={3}, cfg={4}",
                    customProbePath ?? "(null)", customEncoderPath ?? "(null)", _initialized,
                    _ffmpegManager == null ? "null" : _ffmpegManager.GetType().Name,
                    _ffmpegManager?.FfmpegConfiguration == null ? "null" : _ffmpegManager.FfmpegConfiguration.GetType().Name);
            }

            if (!_initialized) return;

            int version;
            lock (ApplySync)
            {
                _pendingProbePath = customProbePath;
                _pendingEncoderPath = customEncoderPath;
                version = ++_applyVersion;
            }

            // 两项都为空时没有可应用的内容，不需要后台重试（默认配置就是这种情况）。
            if (string.IsNullOrWhiteSpace(customProbePath) && string.IsNullOrWhiteSpace(customEncoderPath))
            {
                if (_ffmpegManager?.FfmpegConfiguration != null) ApplyCore(null, null);
                return;
            }

            if (_ffmpegManager?.FfmpegConfiguration == null)
            {
                _logger?.Info(
                    "FfmpegPathOverride - FfmpegConfiguration not ready; deferring apply (retry every 2s, up to 3 min)");
                DeferApply(version);
                return;
            }

            ApplyCore(customProbePath, customEncoderPath);
        }

        /// <summary>
        /// 探测前兜底：如果 FfmpegConfiguration 已就绪但自定义路径仍未写入（例如启动重试还没到），
        /// 在这里立即应用一次。相同配置只尝试一次，避免每次探测重复刷日志。
        /// </summary>
        public static void EnsureApplied()
        {
            if (!_initialized) return;

            var configuration = _ffmpegManager?.FfmpegConfiguration;
            if (configuration == null) return;

            string probe, encoder;
            lock (ApplySync)
            {
                if (_ensureAttemptVersion == _applyVersion) return;
                _ensureAttemptVersion = _applyVersion;
                probe = _pendingProbePath;
                encoder = _pendingEncoderPath;
            }

            if (PathsMatch(configuration.ProbePath, probe) && PathsMatch(configuration.EncoderPath, encoder)) return;

            _logger?.Info("FfmpegPathOverride - ensuring deferred paths before probe");
            ApplyCore(probe, encoder);
        }

        private static bool PathsMatch(string configured, string pending)
        {
            if (string.IsNullOrWhiteSpace(pending)) return true;

            try
            {
                return string.Equals(configured, Path.GetFullPath(pending), StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void ApplyCore(string customProbePath, string customEncoderPath)
        {
            var configuration = _ffmpegManager?.FfmpegConfiguration;
            if (configuration == null) return;

            // 每次重新应用先复位能力位，避免旧值残留导致误判
            if (string.IsNullOrWhiteSpace(customProbePath)) ProbeSupportsBluray = null;
            if (string.IsNullOrWhiteSpace(customEncoderPath)) EncoderSupportsBluray = null;

            TryOverride(configuration, "ProbePath", customProbePath);
            TryOverride(configuration, "EncoderPath", customEncoderPath);
        }

        private static void DeferApply(int version)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 0; attempt < 90; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                        // 期间有更新的 Apply（用户改设置）→ 让新调用负责
                        if (version != Volatile.Read(ref _applyVersion)) return;

                        if (_ffmpegManager?.FfmpegConfiguration == null) continue;

                        string probe, encoder;
                        lock (ApplySync)
                        {
                            probe = _pendingProbePath;
                            encoder = _pendingEncoderPath;
                        }

                        _logger?.Info(
                            "FfmpegPathOverride - FfmpegConfiguration ready; applying deferred paths");
                        ApplyCore(probe, encoder);
                        return;
                    }

                    if (version == Volatile.Read(ref _applyVersion))
                    {
                        _logger?.Warn(
                            "FfmpegPathOverride - FfmpegConfiguration not ready after 3 minutes; custom paths were not applied");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn("FfmpegPathOverride - deferred apply failed: " + ex.Message);
                }
            });
        }

        private static void TryOverride(IFfmpegConfiguration configuration, string propertyName, string customPath)
        {
            if (string.IsNullOrWhiteSpace(customPath)) return;

            var fullPath = Path.GetFullPath(customPath);
            if (!File.Exists(fullPath))
            {
                _logger.Warn("FfmpegPathOverride - {0} not found: {1}", propertyName, fullPath);
                return;
            }

            if (SetBackingField(configuration, propertyName, fullPath))
            {
                _logger.Info("FfmpegPathOverride - {0} set to {1}", propertyName, fullPath);
                SelfCheck(fullPath, propertyName);
            }
            else
            {
                _logger.Warn("FfmpegPathOverride - failed to set {0} backing field on {1}",
                    propertyName, configuration.GetType().FullName);
            }
        }

        private static bool SetBackingField(IFfmpegConfiguration configuration, string propertyName, string value)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                // 首选自动属性后备字段：<ProbePath>k__BackingField
                var field = configuration.GetType().GetField("<" + propertyName + ">k__BackingField", flags) ??
                            configuration.GetType()
                                .GetFields(flags)
                                .FirstOrDefault(f => f.FieldType == typeof(string) &&
                                                     f.Name.IndexOf(propertyName, StringComparison.OrdinalIgnoreCase) >= 0);

                if (field == null)
                {
                    var names = string.Join(", ", configuration.GetType()
                        .GetFields(flags).Select(f => f.Name).Take(20));
                    _logger.Warn("FfmpegPathOverride - no backing field for {0}; fields=[{1}]",
                        propertyName, names);
                    return false;
                }

                // .NET Core 下反射写入 initonly（readonly）后备字段是有效的；
                // 若目标运行时未来禁止此操作，SetValue 会抛异常并落入 catch 返回 false。
                field.SetValue(configuration, value);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug("FfmpegPathOverride - SetBackingField {0} failed: {1}", propertyName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 从 <c>ffprobe -version</c> / <c>ffmpeg -version</c> 的输出判断是否编译了 bluray 输入协议。
        /// ffmpeg 的 configuration 行会包含 <c>--enable-libbluray</c>；只认带 <c>--enable</c> 的行，
        /// 避免把运行日志里偶然出现的库名当成能力声明。
        /// </summary>
        internal static bool VersionOutputIndicatesBlurayInput(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.IndexOf("--enable", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("libbluray", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 自检：执行 -version 并把协议/能力相关输出写入日志，便于诊断 bluray/smb/chromaprint 支持。
        /// 同时把 bluray 能力缓存到 <see cref="ProbeSupportsBluray"/> / <see cref="EncoderSupportsBluray"/>。
        /// </summary>
        private static void SelfCheck(string binaryPath, string label)
        {
            var isProbe = string.Equals(label, "ProbePath", StringComparison.OrdinalIgnoreCase);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = binaryPath,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    process.WaitForExit(10000);

                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(l => l.StartsWith(binaryPath) || l.Contains("version"));
                    _logger.Info("FfmpegPathOverride - {0} self-check exit {1}: {2}", label, process.ExitCode,
                        firstLine?.Trim());

                    var features = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.Contains("--enable"))
                        .ToList();

                    // 关键：探测 bluray 输入协议能力（--enable-libbluray）并缓存
                    var supportsBluray = VersionOutputIndicatesBlurayInput(output);
                    if (isProbe) ProbeSupportsBluray = supportsBluray;
                    else EncoderSupportsBluray = supportsBluray;

                    _logger.Info("FfmpegPathOverride - {0} {1} bluray input protocol",
                        label, supportsBluray ? "supports" : "does NOT support");

                    if (features.Count > 0)
                    {
                        var keywords = new[] { "bluray", "libsmbclient", "chromaprint", "libplacebo", "vulkan" };
                        var hits = keywords.Where(k => features.Any(l => l.Contains(k))).ToList();
                        if (hits.Count > 0)
                        {
                            _logger.Info("FfmpegPathOverride - {0} supports: {1}", label, string.Join(", ", hits));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("FfmpegPathOverride - {0} self-check failed: {1}", label, ex.Message);
            }
        }
    }
}
