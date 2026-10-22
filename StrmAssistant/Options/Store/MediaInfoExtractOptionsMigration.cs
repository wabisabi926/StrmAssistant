using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Options.Store
{
    /// <summary>
    /// 「媒体信息提取」设置由**平铺**改为**分组嵌套**（5 个分组小标题）。
    /// 本类负责把旧版平铺 JSON 就地迁移为嵌套结构，避免用户已配置的选项被重置为默认值。
    /// 迁移完成后：旧内容备份为 <c>*.legacy.bak</c>，并写入 flag 文件避免重复迁移。
    /// </summary>
    internal static class MediaInfoExtractOptionsMigration
    {
        private const string FlagFileName = "StrmAssistant_MediaInfoExtractOptions_grouped_v1.flag";
        private const string PostProcessFlagFileName = "StrmAssistant_MediaInfoExtractOptions_postprocess_v2.flag";
        private const string HdrTaggingFlagFileName = "StrmAssistant_MediaInfoExtractOptions_hdrtagging_v1.flag";

        /// <summary>
        /// 旧字段 → 新分组 的映射表（Group 取新分组属性名，用 nameof 保证编译期同步）。
        /// </summary>
        private static readonly (string Group, string[] Keys)[] Layout =
        {
            (nameof(MediaInfoExtractOptions.ExtractionScope), new[]
            {
                "LibraryScope", "IncludeExtra",
                "MediaInfoExcludeKeywords", "MediaInfoExcludeMediaContainers",
                "VideoThumbnailExcludeKeywords", "VideoThumbnailExcludeMediaContainers"
            }),
            (nameof(MediaInfoExtractOptions.ImageCapture), new[]
            {
                "EnableImageCapture", "ImageCapturePosition", "ForcePortraitPoster", "EnsureEpisode16x9",
                "ImageCaptureExcludeMediaContainers"
            }),
            (nameof(MediaInfoExtractOptions.IsoTool), new[]
            {
                "EnableIsoExtraction", "CustomProbePath", "CustomEncoderPath"
            }),
            (nameof(MediaInfoExtractOptions.MediaInfoPersist), new[]
            {
                "ClearEmbeddedMetadata", "PersistMediaInfoMode", "MediaInfoJsonRootFolder"
            }),
            (nameof(MediaInfoExtractOptions.ExtractionPerformance), new[]
            {
                "MediaInfoExtractionTimeoutMs", "ImageExtractionTimeoutMultiplier",
                "ExclusiveExtract", "ExclusiveControlFeatures"
            })
        };

        /// <summary>新结构才会出现的根级分组键（用于判断文件是否已经是嵌套格式）。</summary>
        private static readonly string[] GroupKeys =
        {
            nameof(MediaInfoExtractOptions.ExtractionScope),
            nameof(MediaInfoExtractOptions.ImageCapture),
            nameof(MediaInfoExtractOptions.IsoTool),
            nameof(MediaInfoExtractOptions.MediaInfoPersist),
            nameof(MediaInfoExtractOptions.ExtractionPerformance)
        };

        public static void MigrateIfNeeded(string optionsFilePath, IJsonSerializer jsonSerializer, ILogger logger)
        {
            MigrateToGroupedLayout(optionsFilePath, jsonSerializer, logger);
            MigrateImageCapturePostProcess(optionsFilePath, jsonSerializer, logger);
            MigrateHdrTagging(optionsFilePath, jsonSerializer, logger);
        }

        /// <summary>
        /// HDR 标注（双层杜比视界 P7 / HDR Vivid）此前沿用 <c>EnableIsoExtraction</c>。
        /// 3.0.0.62 起独立成组：首次升级时把旧开关的值搬过来，避免用户升级后标签集体消失。
        /// </summary>
        private static void MigrateHdrTagging(string optionsFilePath, IJsonSerializer jsonSerializer, ILogger logger)
        {
            try
            {
                var directory = Path.GetDirectoryName(optionsFilePath);

                if (string.IsNullOrEmpty(directory)) return;

                var flagPath = Path.Combine(directory, HdrTaggingFlagFileName);

                if (File.Exists(flagPath) || !File.Exists(optionsFilePath)) return;

                var text = File.ReadAllText(optionsFilePath);

                // 已存在新分组（用户已用新版本保存过设置）：无需改写，只写 flag
                if (text.Contains("\"HdrTagging\""))
                {
                    File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
                    return;
                }

                var options = jsonSerializer.DeserializeFromString<MediaInfoExtractOptions>(text);

                // 旧用户沿用 ISO 提取的开关状态，保证升级前后标签表现一致
                options.HdrTagging.EnableHdrTagging = options.IsoTool.EnableIsoExtraction;

                byte[] bytes;

                using (var stream = new MemoryStream())
                {
                    jsonSerializer.SerializeToStream(options, stream, new JsonSerializerOptions { Indent = true });
                    bytes = stream.ToArray();
                }

                var backupPath = optionsFilePath + ".hdrtagging.bak";
                File.WriteAllText(backupPath, text);
                File.WriteAllBytes(optionsFilePath, bytes);
                File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));

                logger.Info(
                    "MediaInfoExtractOptions: migrated HdrTagging.EnableHdrTagging from EnableIsoExtraction = {0}. Backup: {1}",
                    options.HdrTagging.EnableHdrTagging, backupPath);
            }
            catch (Exception e)
            {
                logger.Warn("MediaInfoExtractOptions HdrTagging migration failed: " + e.Message);
            }
        }

        private static void MigrateToGroupedLayout(string optionsFilePath, IJsonSerializer jsonSerializer, ILogger logger)
        {
            try
            {
                var directory = Path.GetDirectoryName(optionsFilePath);

                if (string.IsNullOrEmpty(directory)) return;

                var flagPath = Path.Combine(directory, FlagFileName);

                if (File.Exists(flagPath) || !File.Exists(optionsFilePath)) return;

                var legacyText = File.ReadAllText(optionsFilePath);

                if (IsGroupedLayout(legacyText) || !HasLegacyKeys(legacyText))
                {
                    File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
                    return;
                }

                var legacy = jsonSerializer.DeserializeFromString<LegacyMediaInfoExtractOptions>(legacyText);
                var migrated = new MediaInfoExtractOptions();
                MapLegacyValues(legacy, migrated);

                // 先在内存中完成序列化，避免写文件中途失败损坏配置
                byte[] bytes;

                using (var stream = new MemoryStream())
                {
                    jsonSerializer.SerializeToStream(migrated, stream, new JsonSerializerOptions { Indent = true });
                    bytes = stream.ToArray();
                }

                var backupPath = optionsFilePath + ".legacy.bak";
                File.WriteAllText(backupPath, legacyText);
                File.WriteAllBytes(optionsFilePath, bytes);
                File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));

                logger.Info(
                    "MediaInfoExtractOptions: migrated legacy flat layout to grouped layout (5 groups). Legacy backup: {0}",
                    backupPath);
            }
            catch (Exception e)
            {
                logger.Warn("MediaInfoExtractOptions legacy migration failed: " + e.Message);
            }
        }

        /// <summary>
        /// 3.0.0.56 前「截图后处理」是 ForcePortraitPoster / EnsureEpisode16x9 两个独立开关，
        /// 之后合并为多选 ImageCapturePostProcessMode。此处把已嵌套的旧开关折叠进新字段，
        /// 避免升级后两个开关保持 true 与用户在新的多选里取消勾选的结果互相冲突。
        /// </summary>
        private static void MigrateImageCapturePostProcess(string optionsFilePath, IJsonSerializer jsonSerializer,
            ILogger logger)
        {
            try
            {
                var directory = Path.GetDirectoryName(optionsFilePath);

                if (string.IsNullOrEmpty(directory)) return;

                var flagPath = Path.Combine(directory, PostProcessFlagFileName);

                if (File.Exists(flagPath) || !File.Exists(optionsFilePath)) return;

                var text = File.ReadAllText(optionsFilePath);

                if (!text.Contains("\"ForcePortraitPoster\"") && !text.Contains("\"EnsureEpisode16x9\""))
                {
                    File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));
                    return;
                }

                var options = jsonSerializer.DeserializeFromString<MediaInfoExtractOptions>(text);
                var imageCapture = options.ImageCapture;

                var modes = (imageCapture.ImageCapturePostProcessMode ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                if (imageCapture.ForcePortraitPoster &&
                    !modes.Contains(nameof(ImageCaptureOptions.ImageCapturePostProcess.EnsureAspect2By3)))
                {
                    modes.Add(nameof(ImageCaptureOptions.ImageCapturePostProcess.EnsureAspect2By3));
                }

                if (imageCapture.EnsureEpisode16x9 &&
                    !modes.Contains(nameof(ImageCaptureOptions.ImageCapturePostProcess.EnsureAspect16By9)))
                {
                    modes.Add(nameof(ImageCaptureOptions.ImageCapturePostProcess.EnsureAspect16By9));
                }

                imageCapture.ImageCapturePostProcessMode = string.Join(",", modes);
                imageCapture.ForcePortraitPoster = false;
                imageCapture.EnsureEpisode16x9 = false;

                byte[] bytes;

                using (var stream = new MemoryStream())
                {
                    jsonSerializer.SerializeToStream(options, stream, new JsonSerializerOptions { Indent = true });
                    bytes = stream.ToArray();
                }

                var backupPath = optionsFilePath + ".postprocess.bak";
                File.WriteAllText(backupPath, text);
                File.WriteAllBytes(optionsFilePath, bytes);
                File.WriteAllText(flagPath, DateTime.UtcNow.ToString("O"));

                logger.Info(
                    "MediaInfoExtractOptions: migrated image post-process flags to ImageCapturePostProcessMode. Backup: {0}",
                    backupPath);
            }
            catch (Exception e)
            {
                logger.Warn("MediaInfoExtractOptions image post-process migration failed: " + e.Message);
            }
        }

        private static bool IsGroupedLayout(string text)
        {
            foreach (var key in GroupKeys)
            {
                if (text.Contains("\"" + key + "\"")) return true;
            }

            return false;
        }

        private static bool HasLegacyKeys(string text)
        {
            foreach (var (_, keys) in Layout)
            {
                foreach (var key in keys)
                {
                    if (text.Contains("\"" + key + "\"")) return true;
                }
            }

            return false;
        }

        private static void MapLegacyValues(LegacyMediaInfoExtractOptions legacy, MediaInfoExtractOptions options)
        {
            var scope = options.ExtractionScope;
            scope.LibraryScope = legacy.LibraryScope ?? string.Empty;
            scope.IncludeExtra = legacy.IncludeExtra;
            scope.MediaInfoExcludeKeywords = legacy.MediaInfoExcludeKeywords ?? string.Empty;
            scope.MediaInfoExcludeMediaContainers = legacy.MediaInfoExcludeMediaContainers ?? string.Empty;
            scope.VideoThumbnailExcludeKeywords = legacy.VideoThumbnailExcludeKeywords ?? string.Empty;
            scope.VideoThumbnailExcludeMediaContainers = legacy.VideoThumbnailExcludeMediaContainers ?? string.Empty;

            var imageCapture = options.ImageCapture;
            imageCapture.EnableImageCapture = legacy.EnableImageCapture;
            imageCapture.ImageCapturePosition = legacy.ImageCapturePosition;
            imageCapture.ImageCapturePostProcessMode = BuildPostProcessMode(legacy.ForcePortraitPoster,
                legacy.EnsureEpisode16x9);
            imageCapture.ImageCaptureExcludeMediaContainers = legacy.ImageCaptureExcludeMediaContainers ?? string.Empty;

            var isoTool = options.IsoTool;
            isoTool.EnableIsoExtraction = legacy.EnableIsoExtraction;
            isoTool.CustomProbePath = legacy.CustomProbePath ?? string.Empty;
            isoTool.CustomEncoderPath = legacy.CustomEncoderPath ?? string.Empty;

            var persist = options.MediaInfoPersist;
            persist.ClearEmbeddedMetadata = legacy.ClearEmbeddedMetadata;
            persist.PersistMediaInfoMode = string.IsNullOrEmpty(legacy.PersistMediaInfoMode)
                ? MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString()
                : legacy.PersistMediaInfoMode;
            persist.MediaInfoJsonRootFolder = legacy.MediaInfoJsonRootFolder ?? string.Empty;

            var performance = options.ExtractionPerformance;
            performance.MediaInfoExtractionTimeoutMs = legacy.MediaInfoExtractionTimeoutMs;
            performance.ImageExtractionTimeoutMultiplier = legacy.ImageExtractionTimeoutMultiplier;
            performance.ExclusiveExtract = legacy.ExclusiveExtract;
            performance.ExclusiveControlFeatures = legacy.ExclusiveControlFeatures ?? string.Empty;
        }

        private static string BuildPostProcessMode(bool forcePortraitPoster, bool ensureEpisode16x9)
        {
            var modes = new List<string>();

            if (forcePortraitPoster) modes.Add(nameof(ImageCaptureOptions.ImageCapturePostProcess.EnsureAspect2By3));
            if (ensureEpisode16x9) modes.Add(nameof(ImageCaptureOptions.ImageCapturePostProcess.EnsureAspect16By9));

            return string.Join(",", modes);
        }

        /// <summary>
        /// 旧版的平铺配置 DTO，仅用于一次性迁移读取。
        /// 属性默认值与旧版选项默认值保持一致，避免旧 JSON 缺键时行为漂移。
        /// </summary>
        private class LegacyMediaInfoExtractOptions
        {
            public string LibraryScope { get; set; } = string.Empty;
            public bool IncludeExtra { get; set; } = false;
            public string MediaInfoExcludeKeywords { get; set; } = string.Empty;
            public string MediaInfoExcludeMediaContainers { get; set; } = string.Empty;
            public string VideoThumbnailExcludeKeywords { get; set; } = string.Empty;
            public string VideoThumbnailExcludeMediaContainers { get; set; } = string.Empty;

            public bool EnableImageCapture { get; set; } = false;
            public int ImageCapturePosition { get; set; } = 10;
            public bool ForcePortraitPoster { get; set; } = false;
            public bool EnsureEpisode16x9 { get; set; } = false;
            public string ImageCaptureExcludeMediaContainers { get; set; } = string.Empty;

            public bool EnableIsoExtraction { get; set; } = false;
            public string CustomProbePath { get; set; } = string.Empty;
            public string CustomEncoderPath { get; set; } = string.Empty;

            public bool ClearEmbeddedMetadata { get; set; } = true;
            public string PersistMediaInfoMode { get; set; } = "None";
            public string MediaInfoJsonRootFolder { get; set; } = string.Empty;

            public int MediaInfoExtractionTimeoutMs { get; set; } = 0;
            public int ImageExtractionTimeoutMultiplier { get; set; } = 1;
            public bool ExclusiveExtract { get; set; } = false;
            public string ExclusiveControlFeatures { get; set; } = string.Empty;
        }
    }
}
