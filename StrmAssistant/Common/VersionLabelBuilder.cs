using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.MultipleVersionsOptions;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 版本标签构造。网页端（<c>MultiVersionService</c>）与客户端（<c>MediaSourceInfo.Name</c>）
    /// 必须用同一套规则，否则同一个版本在浏览器和 Kodi 里显示不同，用户会以为是两个版本。
    /// 这里只放纯函数，便于单测。
    /// </summary>
    public static class VersionLabelBuilder
    {
        /// <summary>标签分隔符，必须与 <c>strmassistant.js</c> 的 <c>' \u00b7 '</c> 保持一致。</summary>
        public const string Separator = " \u00b7 ";

        /// <summary>
        /// 画质评分：分辨率为主，HDR/DV 加成（用于排序比较）。
        /// 档位顺序须与 <c>strmassistant.js</c> 的 <c>hdrs</c> 列表保持一致（前 = 最优）。
        /// DV 细分档：原盘双层 P7 &gt; 单层 P8 &gt; P9 &gt; 流媒体 P5 &gt; 未识别 DV。
        /// </summary>
        public static int QualityScore(int? height, string hdr)
        {
            var score = height ?? 0;
            if (string.IsNullOrEmpty(hdr)) return score;

            if (hdr == "DV P7") score += 520;
            else if (hdr == "DV P8") score += 515;
            else if (hdr == "DV P9") score += 510;
            else if (hdr == "DV P5") score += 505;
            else if (hdr == "DV") score += 500;
            else if (hdr == "HDR10+") score += 400;
            else if (hdr == "HDR Vivid") score += 350;
            else if (hdr == "HDR") score += 300;

            return score;
        }

        public static string GetQualityLabel(int? height)
        {
            if (!height.HasValue || height.Value <= 0) return null;

            if (height >= 3800) return "8K";
            if (height >= 2100) return "4K";
            if (height >= 1400) return "1440p";
            if (height >= 1000) return "1080p";
            if (height >= 700) return "720p";

            return "SD";
        }

        /// <summary>
        /// 「画质+体积」标签：画质档位 + 视频编码 + 码率 + 文件体积，
        /// 形如 <c>4K HEVC 20.4Mbps 45.2GB</c>。任一缺项自动省略，全缺则返回 null。
        /// 注意：档位词必须留在**首位**，前端 <c>strmassistant.js</c> 的排序按首个空格分词取档位。
        /// </summary>
        public static string BuildMediaInfoLabel(MediaStream video, long? bitrate, long? size)
        {
            var parts = new List<string>();

            var quality = GetQualityLabel(video?.Height);
            if (quality != null) parts.Add(quality);

            var codec = FormatCodec(video?.Codec);
            if (codec != null) parts.Add(codec);

            var bitRate = bitrate ?? video?.BitRate;
            if (bitRate.HasValue && bitRate.Value > 0) parts.Add(FormatBitrate(bitRate.Value));

            if (size.HasValue && size.Value > 0) parts.Add(FormatSize(size.Value));

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }

        /// <summary>把 Emby 的编码短名转成常见写法（hevc→HEVC、h264→H.264…）。</summary>
        public static string FormatCodec(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec)) return null;

            switch (codec.Trim().ToLowerInvariant())
            {
                case "hevc":
                case "h265":
                    return "HEVC";
                case "h264":
                case "avc":
                    return "H.264";
                case "av1":
                    return "AV1";
                case "mpeg2video":
                    return "MPEG-2";
                case "vc1":
                    return "VC-1";
                case "vp9":
                    return "VP9";
                default:
                    return codec.Trim().ToUpperInvariant();
            }
        }

        /// <summary>码率（bit/s → Mbps），整数不留小数点（20.4 → <c>20.4Mbps</c>、20.0 → <c>20Mbps</c>）。</summary>
        public static string FormatBitrate(long bitsPerSecond)
        {
            var mbps = bitsPerSecond / 1000000.0;
            return mbps >= 100 ? $"{mbps:0}Mbps" : $"{mbps:0.#}Mbps";
        }

        /// <summary>文件体积（字节 → 人类可读），1024 进制。</summary>
        public static string FormatSize(long bytes)
        {
            const double kb = 1024.0;
            var gb = bytes / (kb * kb * kb);

            if (gb >= 1024) return $"{gb / 1024:0.##}TB";
            if (gb >= 1) return $"{gb:0.#}GB";

            return $"{bytes / (kb * kb):0.#}MB";
        }

        public static string GetFileNameLabel(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                path = uri.AbsolutePath;
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// HDR 标签。优先按 DV Profile 细分档：
        /// 双层 DV Profile 7 原盘的**增强层**会被 <c>DolbyVisionProfile</c> 标注为
        /// <c>ExtendedVideoSubType = DoviProfile76</c>，而基础层仍是 HDR10，
        /// 因此这里需要扫描**全部视频流**取最优档位，只看主视频流会漏掉 DV 信息。
        /// 档位顺序须与 <c>strmassistant.js</c> 的 <c>hdrs</c> 列表保持一致（前 = 最优）。
        /// </summary>
        public static string GetHdrLabel(IEnumerable<MediaStream> streams)
        {
            var videos = streams?
                .Where(s => s != null && s.Type == MediaStreamType.Video)
                .ToList();

            if (videos == null || videos.Count == 0) return null;

            // HDR Vivid 由插件探测后固化在 MediaStream.Comment 上；Comment 优先于类型分支
            if (videos.Any(v => string.Equals(v.Comment, "HDR Vivid", StringComparison.Ordinal)))
            {
                return "HDR Vivid";
            }

            foreach (var video in videos)
            {
                var dvLabel = GetDvProfileLabel(video.ExtendedVideoSubType);
                if (dvLabel != null) return dvLabel;
            }

            var videoRange = videos[0].VideoRange;
            if (string.IsNullOrEmpty(videoRange)) return null;

            if (videoRange.IndexOf("DoVi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                videoRange.IndexOf("Dovi", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DV";
            }

            if (videoRange.IndexOf("HDR10Plus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                videoRange.IndexOf("HDR10+", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDR10+";
            }

            if (videoRange.IndexOf("Vivid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDR Vivid";
            }

            if (videoRange.IndexOf("HDR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDR";
            }

            return null;
        }

        /// <summary>
        /// 音轨标识：Atmos / DTS:X。
        /// Emby 的 MediaStream 没有音频扩展类型字段，只能按「轨道标题 → 文件名」做文本识别，
        /// 且限定无损/对象音频编码（见 <see cref="AudioTagDetector"/>），避免误标。
        /// </summary>
        public static string GetAudioLabel(IEnumerable<MediaStream> streams, string path)
        {
            var audio = streams?
                .FirstOrDefault(s => s != null && s.Type == MediaStreamType.Audio);

            if (audio == null) return null;

            return AudioTagDetector.ToLabel(
                AudioTagDetector.Detect(audio.Title, audio.Codec, path));
        }

        /// <summary>
        /// Emby 的 <c>ExtendedVideoSubTypes</c> 命名规则为「profile × 10 + level」，
        /// 例如 <c>DoviProfile76</c> = Profile 7。此处按 Profile 归并出对外标签。
        /// 其余 Dolby Vision 取值（如 Profile 4/6）返回 null，回落为笼统的 "DV"。
        /// </summary>
        public static string GetDvProfileLabel(ExtendedVideoSubTypes subType)
        {
            switch (subType)
            {
                case ExtendedVideoSubTypes.DoviProfile76:
                    return "DV P7";
                case ExtendedVideoSubTypes.DoviProfile81:
                case ExtendedVideoSubTypes.DoviProfile82:
                case ExtendedVideoSubTypes.DoviProfile83:
                case ExtendedVideoSubTypes.DoviProfile84:
                case ExtendedVideoSubTypes.DoviProfile85:
                    return "DV P8";
                case ExtendedVideoSubTypes.DoviProfile92:
                    return "DV P9";
                case ExtendedVideoSubTypes.DoviProfile50:
                    return "DV P5";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 构造客户端可见的版本名：<c>档位 [· HDR] [· 音轨]</c>，例如 <c>4K · DV P7 · Atmos</c>。
        /// <para>
        /// <see cref="MultiVersionDisplayOption.SystemDefault"/> 表示「沿用 Emby 原样」，
        /// 此时返回 null，调用方不得覆盖原值。
        /// </para>
        /// </summary>
        public static string BuildSourceName(MediaSourceInfo source,
            MultiVersionDisplayOption displayOption, bool includeHdr, bool includeAudio)
        {
            if (source == null) return null;
            if (displayOption == MultiVersionDisplayOption.SystemDefault) return null;

            var video = source.MediaStreams?
                .FirstOrDefault(s => s != null && s.Type == MediaStreamType.Video);

            var label = displayOption == MultiVersionDisplayOption.FileNameWithoutExtension
                ? (string.IsNullOrEmpty(source.Path) ? null : GetFileNameLabel(source.Path))
                : displayOption == MultiVersionDisplayOption.MediaInfoExtended
                    ? BuildMediaInfoLabel(video, source.Bitrate, source.Size)
                    : GetQualityLabel(video?.Height); // MediaInfo（默认）

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(label)) parts.Add(label);

            if (includeHdr)
            {
                var hdr = GetHdrLabel(source.MediaStreams);
                if (!string.IsNullOrEmpty(hdr)) parts.Add(hdr);
            }

            if (includeAudio)
            {
                var audio = GetAudioLabel(source.MediaStreams, source.Path);
                if (!string.IsNullOrEmpty(audio)) parts.Add(audio);
            }

            return parts.Count > 0 ? string.Join(Separator, parts) : null;
        }

        /// <summary>
        /// 为多个版本生成服务端版本名，并处理重名。
        /// 默认 MediaInfo 只含画质/HDR/音轨，同规格的两个版本会得到相同字符串；
        /// 重名时依次追加体积、码率、原名称，仍冲突则追加序号，保证客户端下拉可区分。
        /// <see cref="MultiVersionDisplayOption.SystemDefault"/> 返回 null 项，调用方不得覆盖原值。
        /// </summary>
        public static List<string> BuildSourceNames(IReadOnlyList<MediaSourceInfo> sources,
            MultiVersionDisplayOption displayOption, bool includeHdr, bool includeAudio)
        {
            var labels = new List<string>();
            if (sources == null || sources.Count == 0) return labels;

            for (var i = 0; i < sources.Count; i++)
            {
                labels.Add(BuildSourceName(sources[i], displayOption, includeHdr, includeAudio));
            }

            var counts = labels
                .Where(label => !string.IsNullOrEmpty(label))
                .GroupBy(label => label, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                if (string.IsNullOrEmpty(label)) continue;

                if (counts[label] > 1)
                {
                    var suffix = GetDisambiguator(sources[i]);
                    label = string.IsNullOrEmpty(suffix)
                        ? $"{label} #{i + 1}"
                        : $"{label}{Separator}{suffix}";
                }

                if (!used.Add(label))
                {
                    var suffixIndex = 2;
                    var candidate = $"{label} #{suffixIndex}";
                    while (!used.Add(candidate))
                    {
                        candidate = $"{label} #{++suffixIndex}";
                    }
                    label = candidate;
                }

                labels[i] = label;
            }

            return labels;
        }

        private static string GetDisambiguator(MediaSourceInfo source)
        {
            if (source == null) return null;
            if (source.Size.HasValue && source.Size.Value > 0) return FormatSize(source.Size.Value);
            if (source.Bitrate.HasValue && source.Bitrate.Value > 0) return FormatBitrate(source.Bitrate.Value);
            if (!string.IsNullOrWhiteSpace(source.Name)) return source.Name.Trim();
            return string.IsNullOrEmpty(source.Path) ? null : GetFileNameLabel(source.Path);
        }
    }
}
