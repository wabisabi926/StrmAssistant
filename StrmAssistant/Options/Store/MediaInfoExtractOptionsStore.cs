using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Options.Store
{
    public class MediaInfoExtractOptionsStore : SimpleFileStore<MediaInfoExtractOptions>
    {
        private readonly ILogger _logger;

        public MediaInfoExtractOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            // 3.0.0.56：设置项由平铺改为 5 个分组嵌套。先把旧配置就地迁移为嵌套结构，
            // 否则旧键会被反序列化忽略、用户设置静默回退为默认值。
            MediaInfoExtractOptionsMigration.MigrateIfNeeded(OptionsFilePath,
                applicationHost.Resolve<IJsonSerializer>(), logger);

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public MediaInfoExtractOptions MediaInfoExtractOptions => GetOptions();

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is MediaInfoExtractOptions options)
            {
                // 自定义 ffmpeg/ffprobe 路径保存期校验：不存在时立即告警，避免用户以为已生效
                foreach (var (label, path) in new[]
                         {
                             ("CustomProbePath", options.IsoTool.CustomProbePath),
                             ("CustomEncoderPath", options.IsoTool.CustomEncoderPath)
                         })
                {
                    if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
                    {
                        _logger.Warn($"MediaInfoExtractOptions - {label} does not exist and will be ignored: {path}");
                    }
                }

                // LibraryList 为空说明页面尚未 Initialize（例如启动期的迁移保存）：
                // 此时无法校验库是否仍存在，直接过滤会把用户已选范围全部清空，故跳过。
                if (options.ExtractionScope.LibraryList.Count > 0)
                {
                    options.ExtractionScope.LibraryScope = string.Join(",",
                        options.ExtractionScope.LibraryScope
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(v => options.ExtractionScope.LibraryList.Any(option => option.Value == v)) ??
                        Enumerable.Empty<string>());
                }

                var controlFeatures = options.ExtractionPerformance.ExclusiveControlFeatures;
                var selectedFeatures = controlFeatures.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f =>
                        !(controlFeatures.Contains(ExclusiveControl.CatchAllBlock.ToString()) &&
                          (f == ExclusiveControl.CatchAllAllow.ToString() ||
                           f == ExclusiveControl.ExtractOnFileChange.ToString())) &&
                        !(controlFeatures.Contains(ExclusiveControl.IgnoreFileChange.ToString()) &&
                          f == ExclusiveControl.ExtractOnFileChange.ToString()))
                    .ToList();
                options.ExtractionPerformance.ExclusiveControlFeatures = string.Join(",", selectedFeatures);

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(MediaInfoExtractOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(MediaInfoPersistOptions.PersistMediaInfoMode)))
                {
                    if (options.IsModSupported)
                    {
                        if (options.MediaInfoPersist.PersistMediaInfoMode != PersistMediaInfoOption.None.ToString())
                        {
                            PatchManager.ChapterChangeTracker.Patch();
                        }
                        else
                        {
                            PatchManager.ChapterChangeTracker.Unpatch();
                        }
                    }
                }

                if (changedProperties.Contains(nameof(ImageCaptureOptions.EnableImageCapture)))
                {
                    if (options.ImageCapture.EnableImageCapture)
                    {
                        PatchManager.EnableImageCapture.Patch();
                        if (Plugin.Instance.MainOptionsStore.GetOptions().TaskConcurrency.MaxConcurrentCount !=
                            EnableImageCapture.SemaphoreFFmpegMaxCount)
                        {
                            Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                        }
                    }
                    else
                    {
                        PatchManager.EnableImageCapture.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExtractionPerformanceOptions.ExclusiveExtract)))
                {
                    if (options.ExtractionPerformance.ExclusiveExtract)
                    {
                        PatchManager.ExclusiveExtract.Patch();
                    }
                    else
                    {
                        PatchManager.ExclusiveExtract.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExtractionPerformanceOptions.ExclusiveControlFeatures)) ||
                    changedProperties.Contains(nameof(MediaInfoPersistOptions.PersistMediaInfoMode)))
                {
                    UpdateExclusiveControlFeatures(options);
                }

                if (changedProperties.Contains(nameof(ExtractionScopeOptions.LibraryScope)))
                {
                    Plugin.LibraryApi.UpdateLibraryPathsInScope(options.ExtractionScope.LibraryScope);
                }

                if (changedProperties.Contains(nameof(IsoToolOptions.EnableIsoExtraction)))
                {
                    if (options.IsoTool.EnableIsoExtraction)
                    {
                        PatchManager.IsoExtraction.Patch();
                    }
                    else
                    {
                        PatchManager.IsoExtraction.Unpatch();
                        Common.IsoMounter.DismountAll();
                    }
                }

                if (changedProperties.Contains(nameof(IsoToolOptions.CustomProbePath)) ||
                    changedProperties.Contains(nameof(IsoToolOptions.CustomEncoderPath)))
                {
                    Common.FfmpegPathOverride.Apply(options.IsoTool.CustomProbePath,
                        options.IsoTool.CustomEncoderPath);
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is MediaInfoExtractOptions options)
            {
                _logger.Info("PersistMediaInfoMode is set to {0}", options.MediaInfoPersist.PersistMediaInfoMode);
                _logger.Info("MediaInfoJsonRootFolder is set to {0}",
                    !string.IsNullOrEmpty(options.MediaInfoPersist.MediaInfoJsonRootFolder)
                        ? options.MediaInfoPersist.MediaInfoJsonRootFolder
                        : "EMPTY");
                _logger.Info("IncludeExtra is set to {0}", options.ExtractionScope.IncludeExtra);
                _logger.Info("EnableImageCapture is set to {0}", options.ImageCapture.EnableImageCapture);
                _logger.Info("ImageCapturePosition is set to {0}", options.ImageCapture.ImageCapturePosition);
                _logger.Info("ForcePortraitPoster is set to {0}", options.ImageCapture.ForcePortraitPoster);
                _logger.Info("EnsureEpisode16x9 is set to {0}", options.ImageCapture.EnsureEpisode16x9);
                _logger.Info("ImageCapturePostProcessMode is set to {0}",
                    string.IsNullOrEmpty(options.ImageCapture.ImageCapturePostProcessMode)
                        ? "EMPTY"
                        : options.ImageCapture.ImageCapturePostProcessMode);
                _logger.Info("ExclusiveExtract is set to {0}", options.ExtractionPerformance.ExclusiveExtract);
                _logger.Info("ClearEmbeddedMetadata is set to {0}", options.MediaInfoPersist.ClearEmbeddedMetadata);
                _logger.Info("HdrTagging is set to {0} (DV P7={1}, Vivid={2})",
                    options.HdrTagging.EnableHdrTagging,
                    options.HdrTagging.EnableDolbyVisionProfileTagging,
                    options.HdrTagging.EnableHdrVividTagging);

                var controlFeatures = GetSelectedExclusiveFeatureDescription();
                _logger.Info("ExclusiveExtract - ControlFeatures is set to {0}",
                    string.IsNullOrEmpty(controlFeatures) ? "EMPTY" : controlFeatures);

                var libraryScope = string.Join(", ",
                    options.ExtractionScope.LibraryScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.ExtractionScope.LibraryList.FirstOrDefault(option => option.Value == v)
                            ?.Name) ??
                    Enumerable.Empty<string>());
                _logger.Info("MediaInfoExtract - LibraryScope is set to {0}",
                    string.IsNullOrEmpty(libraryScope) ? "ALL" : libraryScope);
            }
        }
    }
}
