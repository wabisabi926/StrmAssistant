using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    public enum MediaHealthSeverity
    {
        Critical,
        Warning,
        Info
    }

    public sealed class MediaHealthIssue
    {
        public MediaHealthSeverity Severity { get; set; }
        public string Code { get; set; }
        public string ItemName { get; set; }
        public string FileName { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// 检查器的输入模型。刻意不依赖 Emby 类型，便于对判据做纯函数单测。
    /// </summary>
    public sealed class MediaHealthStream
    {
        public string Type { get; set; }
        public string Codec { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? BitDepth { get; set; }
        public string ColorTransfer { get; set; }
        public string ExtendedVideoType { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        public bool IsExternal { get; set; }
        public bool IsForced { get; set; }
    }

    public sealed class MediaHealthInput
    {
        public string ItemName { get; set; }
        public string FileName { get; set; }
        public long? RunTimeTicks { get; set; }
        public List<MediaHealthStream> Streams { get; set; } = new List<MediaHealthStream>();
    }

    public sealed class MediaHealthReport
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public int ItemsChecked { get; set; }
        public int ItemsWithIssues { get; set; }
        public int Critical { get; set; }
        public int Warning { get; set; }
        public int Info { get; set; }
        public List<MediaHealthIssue> Issues { get; set; } = new List<MediaHealthIssue>();
    }

    /// <summary>
    /// 片源体检 · 声明级检查。
    ///
    /// 只做「文件名宣称」与「已提取媒体信息」的交叉比对，不发起任何探测、不读媒体文件本体：
    /// 因此能发现「假 4K / 假 HDR / 假 Atmos / 缺音轨 / 零时长 / 字幕语言未识别」，
    /// 但发现不了「容器正常、中段花屏」这类需要解码采样的问题——那属于另一条链路。
    ///
    /// 误报控制：所有规则都要求「有明确反证」才报（例如 Height 有值且确实小于 2000），
    /// 探测失败的条目（字段为空）一律不报，避免把未知当错误。
    /// </summary>
    public static class MediaHealthChecker
    {
        private static readonly Regex Claim4K = new Regex(
            @"(?<![a-z0-9])(2160p|4k|uhd)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClaimHdr = new Regex(
            @"(?<![a-z0-9])(hdr10\+?|hdr|dolby[.\s_-]?vision|dovi|dv)(?![a-z0-9])|杜比视界",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ClaimAtmos = new Regex(
            @"(?<![a-z0-9])(atmos|dts[\s:_-]?x|truehd)(?![a-z0-9])|全景声",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>允许被认定「能承载 Atmos / DTS:X」的编码，与 AudioTagDetector 保持一致。</summary>
        private static readonly HashSet<string> ObjectAudioCodecs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "truehd", "mlp", "eac3", "dts", "dtshd"
            };

        public static List<MediaHealthIssue> Check(MediaHealthInput input)
        {
            var issues = new List<MediaHealthIssue>();
            if (input == null) return issues;

            var fileName = input.FileName ?? string.Empty;
            var video = input.Streams.FirstOrDefault(s => IsType(s, "Video"));
            var audio = input.Streams.Where(s => IsType(s, "Audio")).ToList();
            var subtitles = input.Streams.Where(s => IsType(s, "Subtitle")).ToList();

            void Add(MediaHealthSeverity severity, string code, string detail) =>
                issues.Add(new MediaHealthIssue
                {
                    Severity = severity,
                    Code = code,
                    ItemName = input.ItemName,
                    FileName = fileName,
                    Detail = detail
                });

            // 1. 假 4K：文件名宣称 2160p/4K/UHD，但有效画面的长边明显不足。
            //    必须用长边而不是高度：3840x1608 是 2.39:1 宽银幕的正常 4K 编码（无黑边），
            //    只看高度会把整季宽银幕剧集全部误报。
            if (Claim4K.IsMatch(fileName) && video != null)
            {
                var longEdge = Math.Max(video.Width ?? 0, video.Height ?? 0);
                if (longEdge > 0 && longEdge < 3000)
                {
                    Add(MediaHealthSeverity.Warning, "Fake4K", BuildDetail("Fake4K", longEdge));
                }
            }

            // 2. 假 HDR：文件名宣称 HDR/DV，但 Emby 判定为 SDR 且有明确反证
            if (ClaimHdr.IsMatch(fileName) && video != null)
            {
                var embySaysSdr = string.Equals(video.ExtendedVideoType ?? "None", "None",
                    StringComparison.OrdinalIgnoreCase);
                var bt709 = string.Equals(video.ColorTransfer, "bt709", StringComparison.OrdinalIgnoreCase);
                var eightBit = video.BitDepth.HasValue && video.BitDepth.Value <= 8;

                if (embySaysSdr && (bt709 || eightBit))
                {
                    Add(MediaHealthSeverity.Warning, "FakeHdr", BuildDetail("FakeHdr", DescribeVideoState(video)));
                }
            }

            // 3. 假 Atmos：文件名宣称 Atmos/DTS:X/TrueHD，但没有任何可承载的编码
            if (ClaimAtmos.IsMatch(fileName) && audio.Count > 0 &&
                !audio.Any(a => ObjectAudioCodecs.Contains(a.Codec ?? string.Empty)))
            {
                var codecs = string.Join("/", audio
                    .Select(a => a.Codec)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

                Add(MediaHealthSeverity.Warning, "FakeAtmos",
                    BuildDetail("FakeAtmos", string.IsNullOrEmpty(codecs) ? "?" : codecs));
            }

            // 4. 无音轨
            if (audio.Count == 0)
            {
                Add(MediaHealthSeverity.Critical, "NoAudio", BuildDetail("NoAudio"));
            }

            // 5. 无字幕（很多人不需要，降为提示）
            if (subtitles.Count == 0)
            {
                Add(MediaHealthSeverity.Info, "NoSubtitle", BuildDetail("NoSubtitle"));
            }

            // 6. 零时长
            if (!input.RunTimeTicks.HasValue || input.RunTimeTicks.Value <= 0)
            {
                Add(MediaHealthSeverity.Critical, "ZeroDuration", BuildDetail("ZeroDuration"));
            }

            // 7. 外挂字幕语言未识别——与「规范化音轨字幕语言」形成闭环
            var undCount = subtitles.Count(s => s.IsExternal &&
                                               (string.IsNullOrWhiteSpace(s.Language) ||
                                                string.Equals(s.Language, "und", StringComparison.OrdinalIgnoreCase)));
            if (undCount > 0)
            {
                Add(MediaHealthSeverity.Warning, "UndSubtitle", BuildDetail("UndSubtitle", undCount));
            }

            return issues;
        }

        public static string BuildDetail(string code, params object[] args)
        {
            string template;
            switch (code)
            {
                case "Fake4K":
                    template = Resources.Health_Issue_Fake4K;
                    break;
                case "FakeHdr":
                    template = Resources.Health_Issue_FakeHdr;
                    break;
                case "FakeAtmos":
                    template = Resources.Health_Issue_FakeAtmos;
                    break;
                case "NoAudio":
                    template = Resources.Health_Issue_NoAudio;
                    break;
                case "NoSubtitle":
                    template = Resources.Health_Issue_NoSubtitle;
                    break;
                case "ZeroDuration":
                    template = Resources.Health_Issue_ZeroDuration;
                    break;
                case "UndSubtitle":
                    template = Resources.Health_Issue_UndSubtitle;
                    break;
                default:
                    template = code;
                    break;
            }

            return args != null && args.Length > 0 ? string.Format(template, args) : template;
        }

        private static bool IsType(MediaHealthStream stream, string type) =>
            stream != null && string.Equals(stream.Type, type, StringComparison.OrdinalIgnoreCase);

        private static string DescribeVideoState(MediaHealthStream video)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(video.ColorTransfer)) parts.Add(video.ColorTransfer);
            if (video.BitDepth.HasValue) parts.Add(video.BitDepth.Value + "bit");

            return parts.Count > 0 ? string.Join(" / ", parts) : "SDR";
        }
    }
}
