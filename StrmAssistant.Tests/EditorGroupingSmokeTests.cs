using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using StrmAssistant.Options;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace StrmAssistant.Tests
{
    public class EditorGroupingSmokeTests
    {
        private readonly ITestOutputHelper _output;

        public EditorGroupingSmokeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void BasicSettings_Regrouping_IsApplied()
        {
            var container = (EditObjectContainer)new PluginOptions().CreateEditContainer();
            Dump(container.EditorRoot, 0);

            var items = container.EditorRoot.EditorItems.ToList();
            var enableIndex = items.FindIndex(i => NameOrId(i, "EnablePlugin"));
            Assert.True(enableIndex >= 0);
            Assert.True(NameOrId(items[enableIndex + 1], "CatchupMode"));

            var concurrency = items.OfType<EditorGroup>()
                .FirstOrDefault(g => NameOrId(g, "TaskConcurrency"));
            Assert.NotNull(concurrency);
            Assert.Contains(concurrency.EditorItems, i => NameOrId(i, "MaxConcurrentCount"));
            Assert.DoesNotContain(concurrency.EditorItems, i => NameOrId(i, "CatchupMode"));
        }

        [Fact]
        public void MultipleVersions_Regrouping_IsApplied()
        {
            var container = (EditObjectContainer)new ExperienceEnhanceOptions().CreateEditContainer();
            Dump(container.EditorRoot, 0);

            var multipleVersions = container.EditorRoot.EditorItems.OfType<EditorGroup>()
                .FirstOrDefault(g => NameOrId(g, "MultipleVersions"));
            Assert.NotNull(multipleVersions);
            Assert.Contains(multipleVersions.EditorItems, i => NameOrId(i, "MergeMultiVersion"));
            Assert.Contains(multipleVersions.EditorItems, i => NameOrId(i, "EnableMultiVersionEnhance"));
            Assert.DoesNotContain(multipleVersions.EditorItems.OfType<EditorGroup>(),
                g => NameOrId(g, "Merge") || NameOrId(g, "Display"));
        }

        [Fact]
        public void MediaInfo_Groups_AreSplitAndOrdered()
        {
            var container = (EditObjectContainer)new MediaInfoExtractOptions().CreateEditContainer();
            Dump(container.EditorRoot, 0);

            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            // 顺序：提取范围 → ISO 提取 → 图像捕获 → HDR 标注 → 独占与性能 → 信息持久化
            Assert.Equal(
                new[] { "ExtractionScope", "IsoTool", "ImageCapture", "HdrTagging",
                        "ExtractionPerformance", "MediaInfoPersist" },
                groups.Select(g => g.Name).ToArray());

            // 不再有运行时合并组
            Assert.DoesNotContain(groups, g => NameOrId(g, "MediaInfoStoragePerformance"));

            // 独占提取排在性能组最前（独立控制项紧随其后）
            var perf = groups.First(g => NameOrId(g, "ExtractionPerformance"));
            Assert.True(NameOrId(perf.EditorItems.First(), "ExclusiveExtract"));
            Assert.Contains(perf.EditorItems, i => NameOrId(i, "MediaInfoExtractionTimeoutMs"));
            Assert.Contains(perf.EditorItems, i => NameOrId(i, "ImageExtractionTimeoutMultiplier"));

            // 持久化组只放持久化相关项，不再混入独占/超时
            var persist = groups.First(g => NameOrId(g, "MediaInfoPersist"));
            Assert.Contains(persist.EditorItems, i => NameOrId(i, "PersistMediaInfoMode"));
            Assert.DoesNotContain(persist.EditorItems, i => NameOrId(i, "ExclusiveExtract"));
            Assert.DoesNotContain(persist.EditorItems, i => NameOrId(i, "MediaInfoExtractionTimeoutMs"));
        }

        private static bool NameOrId(EditorBase editor, string name)
        {
            if (editor == null) return false;
            if (editor.Name == name) return true;
            var id = editor.Id;
            return id == name || id?.EndsWith("." + name) == true;
        }

        private void Dump(EditorBase editor, int depth)
        {
            _output.WriteLine(new string(' ', depth * 2) +
                              $"{editor.GetType().Name} name={editor.Name} display={editor.DisplayName} id={editor.Id}");
            if (editor is EditorGroup group && group.EditorItems != null)
            {
                foreach (var child in group.EditorItems) Dump(child, depth + 1);
            }
        }
    }
}
