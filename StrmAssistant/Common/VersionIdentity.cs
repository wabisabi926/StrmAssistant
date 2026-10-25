using System;
using System.IO;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 版本身份判定：判断两个媒体文件是否属于「同一版本」。
    ///
    /// 纯字符串逻辑，不依赖 Emby 实体，可脱离 Emby 单测。
    /// 用于「删除整季某版本」时确定删除集合——名称相似度不足以证明版本身份
    /// （长文件名里 1080p 与 2160p 的相似度可达 0.94），必须比对目录与版本标记。
    ///
    /// 判定规则：同目录 + 分辨率一致（必须存在）+ 编码/片源（双方都有时才比较）。
    /// 任何一项无法判定即返回 false —— 删除不可逆，宁可少删。
    /// </summary>
    public static class VersionIdentity
    {
        private static readonly Regex ResolutionTokenRegex = new Regex(
            @"(?<![0-9])(2160p|1080p|720p|576p|480p|4k|uhd)(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodecTokenRegex = new Regex(
            @"(?<![a-z0-9])(h\.?264|x264|avc|h\.?265|x265|hevc|av1|vp9)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SourceTokenRegex = new Regex(
            @"(?<![a-z0-9])(web-?dl|webrip|blu-?ray|bdrip|remux|hdtv|dvdrip)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsSameVersion(string targetPath, string targetName,
            string candidatePath, string candidateName, out string reason)
        {
            reason = null;

            var targetDir = Path.GetDirectoryName(targetPath);
            var candidateDir = Path.GetDirectoryName(candidatePath);

            if (!string.Equals(targetDir, candidateDir, StringComparison.OrdinalIgnoreCase))
            {
                reason = "different folder";
                return false;
            }

            targetName = targetName ?? string.Empty;
            candidateName = candidateName ?? string.Empty;

            var targetResolution = ParseResolutionToken(targetName);
            var candidateResolution = ParseResolutionToken(candidateName);

            if (targetResolution == null || candidateResolution == null)
            {
                reason = "resolution token missing";
                return false;
            }

            if (!string.Equals(targetResolution, candidateResolution, StringComparison.Ordinal))
            {
                reason = $"resolution {targetResolution} vs {candidateResolution}";
                return false;
            }

            var targetCodec = ParseCodecToken(targetName);
            var candidateCodec = ParseCodecToken(candidateName);

            if (targetCodec != null && candidateCodec != null &&
                !string.Equals(targetCodec, candidateCodec, StringComparison.Ordinal))
            {
                reason = $"codec {targetCodec} vs {candidateCodec}";
                return false;
            }

            var targetSource = ParseSourceToken(targetName);
            var candidateSource = ParseSourceToken(candidateName);

            if (targetSource != null && candidateSource != null &&
                !string.Equals(targetSource, candidateSource, StringComparison.Ordinal))
            {
                reason = $"source {targetSource} vs {candidateSource}";
                return false;
            }

            return true;
        }

        public static string ParseResolutionToken(string fileName)
        {
            var match = ResolutionTokenRegex.Match(fileName ?? string.Empty);
            if (!match.Success) return null;

            switch (match.Value.ToLowerInvariant())
            {
                case "2160p":
                case "4k":
                case "uhd":
                    return "2160";
                case "1080p":
                    return "1080";
                case "720p":
                    return "720";
                case "576p":
                    return "576";
                default:
                    return "480";
            }
        }

        public static string ParseCodecToken(string fileName)
        {
            var match = CodecTokenRegex.Match(fileName ?? string.Empty);
            if (!match.Success) return null;

            var token = match.Value.ToLowerInvariant().Replace(".", string.Empty);

            switch (token)
            {
                case "h264":
                case "x264":
                case "avc":
                    return "h264";
                case "h265":
                case "x265":
                case "hevc":
                    return "h265";
                default:
                    return token; // av1 / vp9
            }
        }

        public static string ParseSourceToken(string fileName)
        {
            var match = SourceTokenRegex.Match(fileName ?? string.Empty);
            if (!match.Success) return null;

            var token = match.Value.ToLowerInvariant().Replace("-", string.Empty);

            switch (token)
            {
                case "webdl":
                    return "webdl";
                case "bluray":
                    return "bluray";
                default:
                    return token; // webrip / bdrip / remux / hdtv / dvdrip
            }
        }
    }
}
