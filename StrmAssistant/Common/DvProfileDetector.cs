using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 双层 Dolby Vision Profile 7（BL + EL）识别。
    ///
    /// 不依赖 ffprobe 的 DOVI side data，而是使用双层盘片稳定的几何关系：
    /// 基础层（BL）是 HDR10 的 HEVC 主画面，增强层（EL）是同一画面的半宽半高 HEVC
    /// （UHD Blu-ray 常见 3840x2160 + 1920x1080）。
    ///
    /// 配对顺序：
    /// 1. 过滤出宽高有效的视频流，按像素面积从大到小排列；
    /// 2. 取第一条「HEVC + HDR10」的流作为 BL；
    /// 3. 在其余流中找宽、高都约为 BL 一半的 HEVC 流作为 EL。
    ///
    /// 这样即使存在画中画、花絮等额外视频轨，也不会因为「只取面积前二」而配错。
    /// 判定是启发式的：它识别的是「双层几何」，不能替代 DOVI 元数据解析。
    /// </summary>
    public static class DvProfileDetector
    {
        private const string HevcCodec = "hevc";

        /// <summary>允许的尺寸取整误差（像素）。双层盘片的 EL 通常是精确的半宽半高。</summary>
        private const int DimensionTolerance = 2;

        /// <summary>
        /// 尝试识别双层 DV Profile 7。<paramref name="reason"/> 始终带出结论说明（用于日志）。
        /// </summary>
        public static bool TryDetectDualLayerProfile7(IEnumerable<MediaStream> streams,
            out MediaStream enhancementLayer, out string reason)
        {
            enhancementLayer = null;

            var videos = (streams ?? Enumerable.Empty<MediaStream>())
                .Where(IsUsableVideo)
                .OrderByDescending(PixelCount)
                .ToList();

            if (videos.Count < 2)
            {
                reason = $"need at least 2 video streams, found {videos.Count}";
                return false;
            }

            var baseLayer = videos.FirstOrDefault(IsHdr10Hevc);
            if (baseLayer == null)
            {
                reason = "no HDR10 HEVC base layer";
                return false;
            }

            var candidate = videos.FirstOrDefault(v =>
                !ReferenceEquals(v, baseLayer) && IsHevc(v) && IsHalfSize(baseLayer, v));

            if (candidate == null)
            {
                reason = $"no half-size HEVC enhancement layer for {Describe(baseLayer)}";
                return false;
            }

            enhancementLayer = candidate;
            reason = $"base {Describe(baseLayer)}, enhancement {Describe(candidate)}";
            return true;
        }

        private static bool IsUsableVideo(MediaStream stream)
        {
            return stream != null &&
                   stream.Type == MediaStreamType.Video &&
                   stream.Width.HasValue && stream.Height.HasValue &&
                   stream.Width.Value > 0 && stream.Height.Value > 0;
        }

        private static bool IsHevc(MediaStream stream)
        {
            return string.Equals(stream.Codec, HevcCodec, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHdr10Hevc(MediaStream stream)
        {
            return IsHevc(stream) && stream.ExtendedVideoType == ExtendedVideoTypes.Hdr10;
        }

        private static long PixelCount(MediaStream stream)
        {
            return (long)stream.Width.Value * stream.Height.Value;
        }

        private static bool IsHalfSize(MediaStream baseLayer, MediaStream candidate)
        {
            return Math.Abs(baseLayer.Width.Value - candidate.Width.Value * 2) <= DimensionTolerance &&
                   Math.Abs(baseLayer.Height.Value - candidate.Height.Value * 2) <= DimensionTolerance;
        }

        private static string Describe(MediaStream stream)
        {
            return $"{stream.Width}x{stream.Height} {stream.Codec}";
        }
    }
}
