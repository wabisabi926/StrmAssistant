using System.Globalization;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 外部命令参数构造。抽成纯函数的原因：参数错误不会抛异常，只会静默失败，
    /// 而这类缺陷（流选择写错、输出扩展名写错）正是外部审查里反复出现的类型。
    /// </summary>
    public static class ProbeCommand
    {
        /// <summary>
        /// 首帧补探的 ffprobe 参数。
        ///
        /// ⚠️ <c>probe.index</c> 是**容器内的绝对流索引**，而 <c>v:N</c> 表示「第 N 条视频流」。
        /// 二者只在「视频恰好是第 0 条视频且前面没有其他流」时等价。MKV 里音轨常排在视频之前，
        /// 此时视频绝对索引 ≥1，用 <c>v:1</c> 会匹配不到任何流 → 静默漏标。
        /// 所以这里直接用绝对索引（<c>-select_streams 1</c>），索引缺失时才退回 <c>v:0</c>。
        /// </summary>
        public static string BuildFrameProbeArgs(string target, int? probeIndex)
        {
            var selector = probeIndex.HasValue
                ? probeIndex.Value.ToString(CultureInfo.InvariantCulture)
                : "v:0";

            return $"-v error -print_format json -show_frames -read_intervals \"%+#1\" -select_streams {selector} \"{target}\"";
        }
    }
}
