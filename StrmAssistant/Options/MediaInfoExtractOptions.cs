using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 媒体信息提取设置根节点。
    /// 所有设置项按功能拆到 5 个分组类中，本类只负责：
    /// ① 声明分组（嵌套 <see cref="EditableOptionsBase"/> 会被 Emby 渲染成带 &lt;h2&gt; 小标题的分区）；
    /// ② 委派各分组的 <c>Initialize()</c>（框架不会自动调用嵌套对象）；
    /// ③ 持有跨分组共享的枚举（<see cref="ExclusiveControl"/> / <see cref="PersistMediaInfoOption"/>），
    ///    供 Store 与各分组类引用，避免 <c>using static</c> 在多个分组类之间产生歧义。
    /// 注意：分组的属性名与分组类名不同（避免实例成员遮蔽类型名导致初始化表达式无法解析）。
    /// </summary>
    public class MediaInfoExtractOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Extract;

        /// <summary>提取范围：媒体库范围、额外内容、按关键词/容器跳过提取的排除规则。</summary>
        [DisplayNameL("MediaInfoExtractOptions_Group_ExtractionScope", typeof(Resources))]
        public ExtractionScopeOptions ExtractionScope { get; set; } = new ExtractionScopeOptions();

        /// <summary>图像捕获：无主图视频的截帧、截帧位置与画面裁剪。</summary>
        [DisplayNameL("MediaInfoExtractOptions_Group_ImageCapture", typeof(Resources))]
        public ImageCaptureOptions ImageCapture { get; set; } = new ImageCaptureOptions();

        /// <summary>ISO 与外部工具：ISO 提取开关 + 自定义 ffprobe / ffmpeg 路径。</summary>
        [DisplayNameL("MediaInfoExtractOptions_Group_IsoTool", typeof(Resources))]
        public IsoToolOptions IsoTool { get; set; } = new IsoToolOptions();

        /// <summary>HDR 标注：双层杜比视界 P7 与 HDR Vivid 的画质标签（与 ISO 提取解耦）。</summary>
        [DisplayNameL("MediaInfoExtractOptions_Group_HdrTagging", typeof(Resources))]
        public HdrTaggingOptions HdrTagging { get; set; } = new HdrTaggingOptions();

        /// <summary>结果与持久化：探测结果清洗、JSON 持久化 / 恢复模式。</summary>
        [DisplayNameL("MediaInfoExtractOptions_Group_MediaInfoPersist", typeof(Resources))]
        public MediaInfoPersistOptions MediaInfoPersist { get; set; } = new MediaInfoPersistOptions();

        /// <summary>性能与独占：提取超时、图像超时倍数、独占提取及其功能控制。</summary>
        [DisplayNameL("MediaInfoExtractOptions_Group_ExtractionPerformance", typeof(Resources))]
        public ExtractionPerformanceOptions ExtractionPerformance { get; set; } = new ExtractionPerformanceOptions();

        public enum ExclusiveControl
        {
            [DescriptionL("ExclusiveControl_IgnoreFileChange_IgnoreFileChange", typeof(Resources))]
            IgnoreFileChange,
            [DescriptionL("ExclusiveControl_ExtractOnFileChange_ExtractOnFileChange", typeof(Resources))]
            ExtractOnFileChange,
            [DescriptionL("ExclusiveControl_CatchAllAllow_CatchAllAllow", typeof(Resources))]
            CatchAllAllow,
            [DescriptionL("ExclusiveControl_CatchAllBlock_CatchAllBlock", typeof(Resources))]
            CatchAllBlock,
            [DescriptionL("ExclusiveControl_IgnoreExtSubChange_IgnoreExtSubChange", typeof(Resources))]
            IgnoreExtSubChange,
            [DescriptionL("ExclusiveControl_NoPersistIntegration_NoPersistIntegration", typeof(Resources))]
            NoPersistIntegration,
            [DescriptionL("ExclusiveControl_NoIntroProtect_NoIntroProtect", typeof(Resources))]
            NoIntroProtect,
            [DescriptionL("ExclusiveControl_NoNfoSaverOptimization_No_Nfo_Saver_Optimization", typeof(Resources))]
            NoNfoSaverOptimization,
            [DescriptionL("ExclusiveControl_ExtractAlternative_ExtractAlternative", typeof(Resources))]
            ExtractAlternative
        }

        public enum PersistMediaInfoOption
        {
            None,
            Default,
            Restore
        }

        [Browsable(false)]
        public bool IsModSupported =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = base.CreateEditContainer();
            if (!(container is EditObjectContainer editContainer) ||
                editContainer.EditorRoot?.EditorItems == null) return container;

            var root = editContainer.EditorRoot;
            var items = root.EditorItems.ToList();
            var persist = items.OfType<EditorGroup>().FirstOrDefault(g => IsEditor(g, nameof(MediaInfoPersist)));
            var performance = items.OfType<EditorGroup>().FirstOrDefault(g => IsEditor(g, nameof(ExtractionPerformance)));
            if (persist == null || performance == null) return container;

            // 独占提取放在合并组最上面。
            if (performance.EditorItems != null)
            {
                var perfItems = performance.EditorItems.ToList();
                var order = new[]
                {
                    nameof(ExtractionPerformanceOptions.ExclusiveExtract),
                    nameof(ExtractionPerformanceOptions.ExclusiveControlFeatures),
                    nameof(ExtractionPerformanceOptions.MediaInfoExtractionTimeoutMs),
                    nameof(ExtractionPerformanceOptions.ImageExtractionTimeoutMultiplier)
                };
                var ordered = new List<EditorBase>();
                foreach (var name in order)
                {
                    ordered.AddRange(perfItems.Where(i => IsEditor(i, name)));
                }
                ordered.AddRange(perfItems.Where(i => !ordered.Contains(i)));
                performance.EditorItems = ordered.ToArray();
            }

            var mergedItems = new List<EditorBase>();
            if (performance.EditorItems != null) mergedItems.AddRange(performance.EditorItems);
            if (persist.EditorItems != null) mergedItems.AddRange(persist.EditorItems);

            var storagePerformance = new EditorGroup(
                Resources.MediaInfoExtractOptions_Group_MediaInfoStoragePerformance,
                mergedItems.ToArray(), "MediaInfoStoragePerformance", root.Id, null);

            var newItems = new List<EditorBase>();
            foreach (var item in items)
            {
                if (ReferenceEquals(item, persist))
                {
                    newItems.Add(storagePerformance);
                    continue;
                }

                if (ReferenceEquals(item, performance)) continue;

                newItems.Add(item);
            }

            root.EditorItems = newItems.ToArray();
            return container;
        }

        private static bool IsEditor(EditorBase editor, string name)
        {
            if (editor == null) return false;
            if (string.Equals(editor.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            var id = editor.Id;
            return !string.IsNullOrEmpty(id) &&
                   (string.Equals(id, name, StringComparison.OrdinalIgnoreCase) ||
                    id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        }

        public void Initialize(ILibraryManager libraryManager)
        {
            ExtractionScope.Initialize(libraryManager);
            ImageCapture.Initialize();
            MediaInfoPersist.Initialize();
            ExtractionPerformance.Initialize();
        }
    }
}
