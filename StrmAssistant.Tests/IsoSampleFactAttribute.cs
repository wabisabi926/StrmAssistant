using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 依赖真实 ISO 样本的用例。
    ///
    /// xUnit 2.x 没有运行时跳过 API（<c>Assert.Skip</c> 是 v3 的），旧写法用「样本缺失就 return」，
    /// 结果被框架记为**通过**，让「链路未验证」和「链路已验证」看起来一样。
    /// 这里用自定义 FactAttribute 在发现阶段就标记 Skip，报告里能明确区分。
    /// </summary>
    public sealed class IsoSampleFactAttribute : FactAttribute
    {
        public IsoSampleFactAttribute()
        {
            if (IsoSample.IsAvailable) return;

            Skip = IsoSample.UnavailableReason;
        }
    }

    internal static class IsoSample
    {
        private static readonly string[] LegacyCandidates =
        {
            @"D:\视频\小号\杜比视界测试：双层 FEL2.iso",
            @"D:\视频\小号\最近接收\杜比视界测试：双层 FEL2.iso"
        };

        static IsoSample()
        {
            var candidates = new List<string>();

            // 优先用环境变量，便于 CI 或其它机器挂载样本
            var fromEnv = Environment.GetEnvironmentVariable("STRM_ISO_SAMPLE");
            if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);

            candidates.AddRange(LegacyCandidates);

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate)) continue;

                    using var probe = File.OpenRead(candidate);   // 真正读一次，确认权限
                    ResolvedPath = candidate;
                    IsAvailable = true;
                    return;
                }
                catch (Exception)
                {
                    // 不可读 → 试下一个
                }
            }

            UnavailableReason =
                "未找到可读的 ISO 样本：请设置环境变量 STRM_ISO_SAMPLE 指向一个 ISO 文件";
        }

        public static bool IsAvailable { get; }

        public static string ResolvedPath { get; }

        public static string UnavailableReason { get; } = "未找到可读的 ISO 样本";
    }
}
