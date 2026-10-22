using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 自定义 ffmpeg/ffprobe 路径覆盖：把 Emby 全局 FfmpegConfiguration 的
    /// ProbePath / EncoderPath 底层字段替换为用户指定二进制（静态编译版、
    /// rffmpeg 包装器等），用于解决 DV P5 截图发绿、ISO/bluray 协议支持等问题。
    /// 参考自检日志范式：运行 -version 并输出关键能力行。
    /// </summary>
    public static class FfmpegPathOverride
    {
        private static IFfmpegManager _ffmpegManager;
        private static ILogger _logger;
        private static bool _initialized;

        /// <summary>
        /// 自定义 ffprobe 是否具备 bluray 输入协议能力（<c>--enable-libbluray</c>）。
        /// 由 <see cref="SelfCheck"/> 在应用 CustomProbePath 时探测并缓存；
        /// <c>IsoExtraction</c> 据此决定是否把探测输入改写为 <c>bluray:"{0}"</c>。
        ///
        /// 语义：<c>null</c>=尚未探测（未知）；<c>true</c>=支持；<c>false</c>=不支持。
        /// 未配置自定义 ffprobe 或探测失败时保持 <c>null</c>，调用方应保守回退（不改写输入）。
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

            if (!_initialized || _ffmpegManager?.FfmpegConfiguration == null) return;

            // 每次重新应用先复位能力位，避免旧值残留导致误判
            if (string.IsNullOrWhiteSpace(customProbePath)) ProbeSupportsBluray = null;
            if (string.IsNullOrWhiteSpace(customEncoderPath)) EncoderSupportsBluray = null;

            TryOverride(_ffmpegManager.FfmpegConfiguration, "ProbePath", customProbePath);
            TryOverride(_ffmpegManager.FfmpegConfiguration, "EncoderPath", customEncoderPath);
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
                    var supportsBluray = features.Any(l => l.IndexOf("libbluray", StringComparison.OrdinalIgnoreCase) >= 0);
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
