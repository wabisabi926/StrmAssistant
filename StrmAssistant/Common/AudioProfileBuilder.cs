using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 构造 Emby 详情页「配置」字段（<c>MediaStream.Profile</c>）的显示文案。
    ///
    /// 背景：Emby 的 <c>MediaStream</c> 没有音频扩展类型字段（只有视频侧的
    /// <c>ExtendedVideoType</c>），因此 Atmos / DTS:X 无法由 ffprobe 直接给出。
    /// 但「配置」这一行前端读的就是 <c>Profile</c>，写入
    /// <c>Dolby TrueHD + Dolby Atmos</c> 这类文案后，Web / 客户端都能看到。
    ///
    /// 文案格式（不写死单一值，按编码 + 标识动态拼）：
    /// - truehd / mlp  + Atmos  → <c>Dolby TrueHD + Dolby Atmos</c>
    /// - eac3          + Atmos  → <c>Dolby EAC3 + Dolby Atmos</c>
    /// - dts / dtshd   + DTS:X  → <c>DTS / DTS-HD MA + DTS:X</c>
    ///
    /// 防误标：交给 <see cref="AudioTagDetector"/> —— 只认无损/对象音频编码，
    /// 片名里带 Atmos 的 AAC/AC3 不会命中。识别不到标识时返回 null，绝不覆盖已有 Profile。
    /// </summary>
    internal static class AudioProfileBuilder
    {
        /// <summary>
        /// 根据编码 + 轨道标题 + 文件名/路径生成 Profile 文案；不该标记时返回 null。
        /// </summary>
        internal static string Build(string codec, string title, string fileNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(codec)) return null;

            // 远程 strm 的 ISO URL 里文件名是 %20 / %40 编码的（如 Atmos%20TrueHD），
            // 不解码会让「Atmos TrueHD」变成「Atmos%20TrueHD」而漏判。
            var searchText = fileNameOrPath;
            if (!string.IsNullOrEmpty(searchText) && searchText.IndexOf('%') >= 0)
            {
                try
                {
                    searchText = Uri.UnescapeDataString(searchText);
                }
                catch (UriFormatException)
                {
                    // 编码非法时按原样再试一次，不影响探测
                }
            }

            var tag = AudioTagDetector.Detect(title, codec, searchText);
            if (tag == AudioTag.None) return null;

            var codecLabel = GetCodecLabel(codec);
            if (codecLabel == null) return null;

            return tag == AudioTag.Atmos
                ? codecLabel + " + Dolby Atmos"
                : codecLabel + " + DTS:X";
        }

        /// <summary>
        /// 与 <see cref="AudioTagDetector"/> 允许的编码集合保持一致：
        /// 只有这些编码承载得了对象音频，才配得上「+ Atmos / DTS:X」文案。
        /// </summary>
        private static string GetCodecLabel(string codec)
        {
            switch (codec.Trim().ToLowerInvariant())
            {
                case "truehd":
                case "mlp":
                    return "Dolby TrueHD";
                case "eac3":
                    return "Dolby EAC3";
                case "dtshd":
                    return "DTS-HD MA";
                case "dts":
                    return "DTS";
                default:
                    return null;
            }
        }
    }
}
