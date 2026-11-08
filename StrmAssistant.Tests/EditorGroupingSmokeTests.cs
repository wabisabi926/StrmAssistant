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

            var chineseIndex = items.FindIndex(i => NameOrId(i, "ChineseSearch"));
            var tasksIndex = items.FindIndex(i => NameOrId(i, "ScheduledTasks"));
            var concurrencyIndex = items.FindIndex(i => NameOrId(i, "TaskConcurrency"));
            Assert.True(chineseIndex > enableIndex);
            Assert.True(tasksIndex > chineseIndex);
            Assert.True(concurrencyIndex > tasksIndex);

            var concurrency = items.OfType<EditorGroup>()
                .FirstOrDefault(g => NameOrId(g, "TaskConcurrency"));
            Assert.NotNull(concurrency);
            Assert.Contains(concurrency.EditorItems, i => NameOrId(i, "MaxConcurrentCount"));
            Assert.DoesNotContain(concurrency.EditorItems, i => NameOrId(i, "CatchupMode"));

            var configTransfer = items.OfType<EditorGroup>()
                .FirstOrDefault(g => NameOrId(g, "ConfigTransfer"));
            Assert.NotNull(configTransfer);
            Assert.Contains(configTransfer.EditorItems, i => NameOrId(i, "ConfigTransferFolder"));
            var configButtons = configTransfer.EditorItems.OfType<EditorButtonGroup>().FirstOrDefault();
            Assert.NotNull(configButtons);
            Assert.Equal(2, configButtons.EditorItems.Count());
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

            // 顺序：提取范围 → 自动与按需 → 存储与截图 → ISO 与 HDR → 提取性能
            Assert.Equal(
                new[] { "ExtractionScope", "AutoOnDemand", "StorageCapture",
                        "IsoHdr", "ExtractionPerformance" },
                groups.Select(g => g.Name).ToArray());

            // 不再有运行时合并组
            Assert.DoesNotContain(groups, g => NameOrId(g, "MediaInfoStoragePerformance"));

            // 独占提取排在性能组最前（独立控制项紧随其后）
            var perf = groups.First(g => NameOrId(g, "ExtractionPerformance"));
            Assert.True(NameOrId(perf.EditorItems.First(), "ExclusiveExtract"));
            Assert.Contains(perf.EditorItems, i => NameOrId(i, "MediaInfoExtractionTimeoutMs"));
            Assert.Contains(perf.EditorItems, i => NameOrId(i, "ImageExtractionTimeoutMultiplier"));

            // 存储与截图只放持久化/截图相关项，不再混入独占/超时
            var storage = groups.First(g => NameOrId(g, "StorageCapture"));
            Assert.Contains(storage.EditorItems, i => NameOrId(i, "PersistMediaInfoMode"));
            Assert.Contains(storage.EditorItems, i => NameOrId(i, "EnableImageCapture"));
            Assert.DoesNotContain(storage.EditorItems, i => NameOrId(i, "ExclusiveExtract"));
            Assert.DoesNotContain(storage.EditorItems, i => NameOrId(i, "MediaInfoExtractionTimeoutMs"));
        }

        [Fact]
        public void Metadata_Groups_AreMerged()
        {
            var container = (EditObjectContainer)new MetadataEnhanceOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(
                new[] { "ChineseMetadata", "DoubanAssist", "PosterPerson",
                        "ScrapeEpisodeGroup", "RefreshCache" },
                groups.Select(g => g.Name).ToArray());
        }

        [Fact]
        public void Intro_Groups_AreOrdered()
        {
            var container = (EditObjectContainer)new IntroSkipOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(new[] { "Playback", "Fingerprint", "Scrape" },
                groups.Select(g => g.Name).ToArray());
        }

        [Fact]
        public void Network_Groups_AreMerged()
        {
            var container = (EditObjectContainer)new NetworkProxyOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(new[] { "NetworkDirectLink", "TmdbReplacement" },
                groups.Select(g => g.Name).ToArray());
        }

        [Fact]
        public void Experience_Groups_AreMerged()
        {
            var container = (EditObjectContainer)new ExperienceEnhanceOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(
                new[] { "MultipleVersions", "ItemDisplay", "LibraryOrder", "Playback", "NotificationsLogs" },
                groups.Select(g => g.Name).ToArray());

            var playback = groups.First(g => NameOrId(g, "Playback"));
            Assert.Contains(playback.EditorItems, i => NameOrId(i, "DryRun"));
            Assert.Contains(playback.EditorItems, i => NameOrId(i, "EnableDeepDelete"));
            Assert.DoesNotContain(groups, g => NameOrId(g, "Safety"));
        }

        [Fact]
        public void RuntimeStatus_ContainsHealthStatusAndButtons()
        {
            // 片源体检的摘要行是项目里第一次把 StatusItem 放进子组。
            // Emby 的 IsCompoundEditorType(StatusItem)=true，会走 CreateEditorStatusItem 生成
            // EditorStatusItem（EditorBase），因此与根级状态行走同一条渲染路径。
            var container = (EditObjectContainer)new PluginOptions().CreateEditContainer();
            var runtime = container.EditorRoot.EditorItems.OfType<EditorGroup>()
                .FirstOrDefault(g => NameOrId(g, "RuntimeStatus"));

            Assert.NotNull(runtime);

            // 状态项：Emby 会把子组里的 StatusItem 转成 EditorStatusItem（与根级同一路径）
            Assert.Single(runtime.EditorItems.OfType<EditorStatusItem>());

            // 按钮：连续的 ButtonItem 会被合并进同一个 EditorButtonGroup，
            // 组内应包含「校验元数据缓存」+「立即体检」+「导出体检报告」三个
            var buttonGroup = runtime.EditorItems.OfType<EditorButtonGroup>().FirstOrDefault();
            Assert.NotNull(buttonGroup);
            Assert.Equal(3, buttonGroup.EditorItems.Count());
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
