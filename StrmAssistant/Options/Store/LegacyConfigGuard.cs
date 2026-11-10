using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StrmAssistant.Options.Store
{
    /// <summary>
    /// 4.0 起不再内置旧布局迁移。启动时只做一次轻量检测：
    /// 在**根级**出现旧平铺键即判定为旧配置，要求先安装 3.0.0.78 完成迁移。
    ///
    /// 只检查根级键，避免把新版嵌套结构里的同名字段误判为旧配置。
    /// JSON 损坏时不在这里拦截，交给 Store 自身的解析逻辑处理。
    /// </summary>
    internal static class LegacyConfigGuard
    {
        private static readonly Dictionary<string, string[]> LegacyRootKeys =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["StrmAssistant.json"] = new[]
                {
                    "GeneralOptions", "ModOptions", "CatchupMode",
                    "EnableMemoryAutoRestart", "EnableMemoryCleanup",
                    "MaxConcurrentCount", "Tier2MaxConcurrentCount"
                },
                ["StrmAssistant_MediaInfoExtractOptions.json"] = new[]
                {
                    "EnableImageCapture", "ImageCapturePosition",
                    "ForcePortraitPoster", "EnsureEpisode16x9",
                    "EnableIsoExtraction", "CustomProbePath", "CustomEncoderPath",
                    "PersistMediaInfoMode"
                },
                ["StrmAssistant_MetadataEnhanceOptions.json"] = new[]
                {
                    "EnableDoubanAssist", "DoubanRatingOverride",
                    "ChineseMovieDb", "ChineseTvdb",
                    "MovieDbEpisodeGroup", "MdbListApiKey"
                },
                ["StrmAssistant_IntroSkipOptions.json"] = new[]
                {
                    "UnlockIntroSkip", "ParallelMarkerComputation",
                    "IntroDetectionFingerprintMinutes", "EnableIntroDbScrape", "EnableIntroSkip"
                },
                ["StrmAssistant_ExperienceEnhanceOptions.json"] = new[]
                {
                    "MergeMultiVersion", "UIFunctionOptions",
                    "EnableMultiVersionEnhance", "EnableAutoMarkWatched", "EnableDeepDelete"
                }
            };

        /// <summary>返回命中的旧键说明；未命中或无法解析时返回 null。</summary>
        public static string Detect(string configDirectory)
        {
            if (string.IsNullOrEmpty(configDirectory)) return null;

            foreach (var pair in LegacyRootKeys)
            {
                var path = Path.Combine(configDirectory, pair.Key);
                if (!File.Exists(path)) continue;

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;

                    foreach (var key in pair.Value)
                    {
                        if (document.RootElement.TryGetProperty(key, out _))
                        {
                            return $"{pair.Key} 含根级旧键 \"{key}\"";
                        }
                    }
                }
                catch
                {
                    // 损坏的 JSON 不按旧配置处理，交给 Store 报错
                }
            }

            return null;
        }
    }
}
