using System;
using System.Collections.Generic;
using System.Globalization;

namespace StrmAssistant.Common
{
    /// <summary>
    /// HDR 静态元数据的输入（对应 ffprobe 的 Content light level / Mastering display 两类 side_data）。
    /// 纯数据模型，不依赖 Emby 类型，便于单测。
    /// </summary>
    public sealed class HdrStaticMetadataInput
    {
        public int? MaxCll { get; set; }
        public int? MaxFall { get; set; }

        public double? RedX { get; set; }
        public double? RedY { get; set; }
        public double? GreenX { get; set; }
        public double? GreenY { get; set; }
        public double? BlueX { get; set; }
        public double? BlueY { get; set; }

        public double? MinLuminance { get; set; }
        public double? MaxLuminance { get; set; }
    }

    /// <summary>
    /// 把 HDR 静态元数据格式化成一行人可读文本，写入 <c>MediaStream.Title</c>。
    ///
    /// 为什么用 Title 而不是 Comment：Comment 已被 HDR Vivid 标注占用，
    /// 且 <c>VideoRange</c> 的显示覆写依赖 Comment 等于固定值，混写会破坏判定。
    /// 视频流的 Title 在 Emby 里不参与音轨/字幕选择，是安全的载体。
    ///
    /// 输出示例：<c>CLL 1000 / FALL 400 · BT.2020 · 0.005–1000nit</c>
    /// 各项缺失时自动省略；全缺返回 null，调用方据此跳过写入。
    /// </summary>
    public static class HdrStaticMetadataFormatter
    {
        // 标准色域主色坐标（x, y），用于判定母版使用的色域
        private static readonly (string Name, double Rx, double Ry, double Gx, double Gy, double Bx, double By)[]
            Gamuts =
            {
                ("BT.2020", 0.708, 0.292, 0.170, 0.797, 0.131, 0.046),
                ("DCI-P3", 0.680, 0.320, 0.265, 0.690, 0.150, 0.060),
                ("BT.709", 0.640, 0.330, 0.300, 0.600, 0.150, 0.060)
            };

        private const double GamutTolerance = 0.03;

        /// <summary>ffprobe 的亮度字段在部分版本以 1/10000 nit 输出，超过该阈值时按此换算。</summary>
        private const double LuminanceScaleThreshold = 10000;

        public static string Format(HdrStaticMetadataInput input)
        {
            if (input == null) return null;

            var parts = new List<string>();

            if (input.MaxCll.HasValue || input.MaxFall.HasValue)
            {
                parts.Add($"CLL {FormatInt(input.MaxCll)} / FALL {FormatInt(input.MaxFall)}");
            }

            var gamut = DetectGamut(input);
            if (gamut != null) parts.Add(gamut);

            if (input.MinLuminance.HasValue || input.MaxLuminance.HasValue)
            {
                // 两个亮度字段必须用同一量纲：只要有一个明显是 1/10000 nit，就整体换算
                var scaled = (input.MaxLuminance ?? 0) > LuminanceScaleThreshold ||
                             (input.MinLuminance ?? 0) > LuminanceScaleThreshold;
                var divisor = scaled ? 10000.0 : 1.0;

                if (input.MinLuminance.HasValue && input.MaxLuminance.HasValue)
                {
                    parts.Add(
                        $"{FormatNit(input.MinLuminance.Value / divisor)}–{FormatNit(input.MaxLuminance.Value / divisor)}nit");
                }
                else if (input.MaxLuminance.HasValue)
                {
                    parts.Add($"{FormatNit(input.MaxLuminance.Value / divisor)}nit");
                }
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }

        /// <summary>按主色坐标匹配标准色域；无法匹配返回 null。</summary>
        public static string DetectGamut(HdrStaticMetadataInput input)
        {
            if (input?.RedX == null || input.RedY == null || input.GreenX == null || input.GreenY == null ||
                input.BlueX == null || input.BlueY == null)
            {
                return null;
            }

            foreach (var gamut in Gamuts)
            {
                if (Math.Abs(input.RedX.Value - gamut.Rx) <= GamutTolerance &&
                    Math.Abs(input.RedY.Value - gamut.Ry) <= GamutTolerance &&
                    Math.Abs(input.GreenX.Value - gamut.Gx) <= GamutTolerance &&
                    Math.Abs(input.GreenY.Value - gamut.Gy) <= GamutTolerance &&
                    Math.Abs(input.BlueX.Value - gamut.Bx) <= GamutTolerance &&
                    Math.Abs(input.BlueY.Value - gamut.By) <= GamutTolerance)
                {
                    return gamut.Name;
                }
            }

            return null;
        }

        private static string FormatInt(int? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "-";

        private static string FormatNit(double nit) =>
            nit >= 10
                ? nit.ToString("0.#", CultureInfo.InvariantCulture)
                : nit.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
