using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    public enum AudioTag
    {
        None,
        Atmos,
        DtsX
    }

    /// <summary>
    /// 识别「杜比全景声 / DTS:X」标识。
    ///
    /// 前提（已核实）：Emby 的 <c>MediaStream</c> 没有音频扩展类型字段（只有视频侧
    /// <c>ExtendedVideoType</c>），Emby 自身程序集里也不存在 Atmos/DTS:X 概念；
    /// ffprobe 同样无法区分 EAC3 是否 JOC。所以「识别」只能基于文本：
    /// 轨道标题（容器里 mkv 的 track name，最可靠）→ 文件名（兜底）。
    ///
    /// 防误标：只有无损/对象音频编码（truehd / mlp / eac3 / dts / dtshd）才允许命中，
    /// 避免「片名里带 Atmos 但实际是 AAC」这类误判。
    /// </summary>
    public static class AudioTagDetector
    {
        private static readonly Regex AtmosPattern = new Regex(
            @"(?<![A-Za-z])atmos(?![A-Za-z])|全景声",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DtsXPattern = new Regex(
            @"dts[\s:_-]?x(?![A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>允许被标记的编码（Emby 的 MediaStream.Codec 取值）。</summary>
        private static readonly HashSet<string> EligibleCodecs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "truehd", "mlp", "eac3", "dts", "dtshd"
            };

        public static AudioTag Detect(string text, string codec)
        {
            if (string.IsNullOrEmpty(text)) return AudioTag.None;
            if (string.IsNullOrEmpty(codec) || !EligibleCodecs.Contains(codec)) return AudioTag.None;

            if (AtmosPattern.IsMatch(text)) return AudioTag.Atmos;
            if (DtsXPattern.IsMatch(text)) return AudioTag.DtsX;

            return AudioTag.None;
        }

        /// <summary>先看轨道标题，再看文件名/路径。</summary>
        public static AudioTag Detect(string title, string codec, string fileNameOrPath)
        {
            var byTitle = Detect(title, codec);
            return byTitle != AudioTag.None ? byTitle : Detect(fileNameOrPath, codec);
        }

        public static string ToLabel(AudioTag tag) => tag switch
        {
            AudioTag.Atmos => "Atmos",
            AudioTag.DtsX => "DTS:X",
            _ => null
        };
    }
}
