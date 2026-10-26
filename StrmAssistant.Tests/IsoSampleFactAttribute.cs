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
        /// <summary>
        /// 内置最小 BD 结构镜像（约 566KB）：真实测试盘的 <c>MPLS</c> / <c>CLPI</c> / <c>index.bdmv</c>
        /// 加一个占位 <c>m2ts</c>（解析器只要求文件存在，不读内容）。
        /// 有了它，依赖 ISO 的用例在任意机器（含 CI）都能跑，而不是永远 Skip。
        /// </summary>
        private const string FixtureRelativePath = "fixtures/bd-minimal.iso";

        static IsoSample()
        {
            var candidates = new List<string>();

            // 环境变量优先：本地挂真实盘或 CI 提供真盘时，用它覆盖 fixture 做真盘验证
            var fromEnv = Environment.GetEnvironmentVariable("STRM_ISO_SAMPLE");
            if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);

            // 内置 fixture：随测试输出复制，正常情况一定存在
            candidates.Add(Path.Combine(AppContext.BaseDirectory, FixtureRelativePath));

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
                "未找到可读的 ISO 样本：内置 fixture 缺失，且 STRM_ISO_SAMPLE 未指向有效文件";
        }

        public static bool IsAvailable { get; }

        public static string ResolvedPath { get; }

        public static string UnavailableReason { get; } = "未找到可读的 ISO 样本";
    }
}
