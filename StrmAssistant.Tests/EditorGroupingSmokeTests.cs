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
            var concurrencyIndex = items.FindIndex(i => NameOrId(i, "TaskConcurrency"));
            var memoryIndex = items.FindIndex(i => NameOrId(i, "Memory"));
            var tasksIndex = items.FindIndex(i => NameOrId(i, "ScheduledTasks"));
            var runtimeIndex = items.FindIndex(i => NameOrId(i, "RuntimeStatus"));
            var configIndex = items.FindIndex(i => NameOrId(i, "ConfigTransfer"));
            var aboutIndex = items.FindIndex(i => NameOrId(i, "AboutOptions"));
            Assert.True(chineseIndex > enableIndex);
            Assert.True(concurrencyIndex > chineseIndex);
            Assert.True(memoryIndex > concurrencyIndex);
            Assert.True(tasksIndex > memoryIndex);
            Assert.True(runtimeIndex > tasksIndex);
            Assert.True(configIndex > runtimeIndex);
            Assert.True(aboutIndex > configIndex);

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

            // 顺序：提取范围 → 提取策略 → 提取优化 → 存储与截图 → ISO 与 HDR
            Assert.Equal(
                new[] { "ExtractionScope", "AutoOnDemand", "ExtractionPerformance",
                        "StorageCapture", "IsoHdr" },
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
            Assert.True(NameOrId(storage.EditorItems.Last(), "ClearEmbeddedMetadata"));
            Assert.DoesNotContain(storage.EditorItems.Take(storage.EditorItems.Length - 1),
                i => NameOrId(i, "ClearEmbeddedMetadata"));
        }

        [Fact]
        public void Metadata_Groups_AreOrdered()
        {
            var container = (EditObjectContainer)new MetadataEnhanceOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(
                new[] { "ChineseMetadata", "CommunityRating", "DoubanAssist", "PosterPerson",
                        "ScrapeEpisodeGroup", "EpisodeRefresh", "MetadataCache" },
                groups.Select(g => g.Name).ToArray());

            var refresh = groups.First(g => NameOrId(g, "EpisodeRefresh"));
            Assert.Contains(refresh.EditorItems, i => NameOrId(i, "EpisodeRefreshScope"));
            Assert.DoesNotContain(refresh.EditorItems, i => NameOrId(i, "MetadataCacheMode"));

            var cache = groups.First(g => NameOrId(g, "MetadataCache"));
            Assert.Contains(cache.EditorItems, i => NameOrId(i, "MetadataCacheMode"));
            Assert.DoesNotContain(cache.EditorItems, i => NameOrId(i, "EpisodeRefreshScope"));

            var poster = groups.First(g => NameOrId(g, "PosterPerson"));
            var posterItems = poster.EditorItems.ToList();
            Assert.True(posterItems.FindIndex(i => NameOrId(i, "EnhanceMovieDbPerson")) <
                        posterItems.FindIndex(i => NameOrId(i, "ClearTagline")));
            Assert.True(posterItems.FindIndex(i => NameOrId(i, "ClearTagline")) <
                        posterItems.FindIndex(i => NameOrId(i, "RefreshPersonMode")));
        }

        [Fact]
        public void Intro_Groups_AreOrdered()
        {
            var container = (EditObjectContainer)new IntroSkipOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(new[] { "Playback", "Fingerprint", "Scrape" },
                groups.Select(g => g.Name).ToArray());

            var fingerprint = groups.First(g => NameOrId(g, "Fingerprint")).EditorItems.ToList();
            Assert.True(fingerprint.FindIndex(i => NameOrId(i, "ParallelMarkerComputation")) <
                        fingerprint.FindIndex(i => NameOrId(i, "MarkerEnabledLibraryScope")));
            Assert.True(fingerprint.FindIndex(i => NameOrId(i, "MarkerEnabledLibraryScope")) <
                        fingerprint.FindIndex(i => NameOrId(i, "LibraryFingerprintMinutes")));

            var scrape = groups.First(g => NameOrId(g, "Scrape")).EditorItems.ToList();
            Assert.True(scrape.FindIndex(i => NameOrId(i, "IncludeMovies")) <
                        scrape.FindIndex(i => NameOrId(i, "LibraryScope")));
        }

        [Fact]
        public void Network_Groups_AreMerged()
        {
            var container = (EditObjectContainer)new NetworkProxyOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(new[] { "NetworkDirectLink", "TmdbReplacement" },
                groups.Select(g => g.Name).ToArray());

            var network = groups.First(g => NameOrId(g, "NetworkDirectLink")).EditorItems.ToList();
            Assert.True(network.FindIndex(i => NameOrId(i, "ProxyServerStatus")) <
                        network.FindIndex(i => NameOrId(i, "ScrapeProxyWhitelist")));
        }

        [Fact]
        public void Experience_Groups_AreMerged()
        {
            var container = (EditObjectContainer)new ExperienceEnhanceOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            Assert.Equal(
                new[] { "MultipleVersions", "Playback", "ItemDisplay", "LibraryOrder", "NotificationsLogs" },
                groups.Select(g => g.Name).ToArray());

            var playback = groups.First(g => NameOrId(g, "Playback"));
            var playbackItems = playback.EditorItems.ToList();
            Assert.Contains(playbackItems, i => NameOrId(i, "DryRun"));
            Assert.Contains(playbackItems, i => NameOrId(i, "EnableDeepDelete"));
            Assert.True(playbackItems.FindIndex(i => NameOrId(i, "DryRun")) >
                        playbackItems.FindIndex(i => NameOrId(i, "EnableDeepDelete")));
            Assert.True(playbackItems.FindIndex(i => NameOrId(i, "EnableDeepDelete")) >
                        playbackItems.FindIndex(i => NameOrId(i, "LibraryScopePlayProgress")));
            Assert.DoesNotContain(groups, g => NameOrId(g, "Safety"));

            var versions = groups.First(g => NameOrId(g, "MultipleVersions")).EditorItems.ToList();
            Assert.True(versions.FindIndex(i => NameOrId(i, "MultiVersionSortOption")) <
                        versions.FindIndex(i => NameOrId(i, "SplitMoviesButton")));
            Assert.True(versions.FindIndex(i => NameOrId(i, "SplitMoviesButton")) <
                        versions.FindIndex(i => NameOrId(i, "SplitMoviesProgress")));

            var itemDisplay = groups.First(g => NameOrId(g, "ItemDisplay"));
            var notifications = groups.First(g => NameOrId(g, "NotificationsLogs"));
            Assert.Contains(itemDisplay.EditorItems, i => NameOrId(i, "ShowMediaInfo"));
            Assert.Contains(itemDisplay.EditorItems, i => NameOrId(i, "BlockSearchRecommend"));
            Assert.DoesNotContain(notifications.EditorItems, i => NameOrId(i, "ShowMediaInfo"));
            Assert.DoesNotContain(notifications.EditorItems, i => NameOrId(i, "BlockSearchRecommend"));
            Assert.Contains(notifications.EditorItems, i => NameOrId(i, "ReverseLogLines"));
        }

        [Fact]
        public void RuntimeStatus_ContainsHealthActionButtons()
        {
            // 片源体检摘要已并入根级「运行概览」，维护工具组只保留操作按钮。
            var container = (EditObjectContainer)new PluginOptions().CreateEditContainer();
            var runtime = container.EditorRoot.EditorItems.OfType<EditorGroup>()
                .FirstOrDefault(g => NameOrId(g, "RuntimeStatus"));

            Assert.NotNull(runtime);
            Assert.Empty(runtime.EditorItems.OfType<EditorStatusItem>());

            // 连续的 ButtonItem 会被合并进同一个 EditorButtonGroup：
            // 校验元数据缓存 + 立即体检 + 导出体检报告 + 清内存 + 重建索引 + 清缓存 + 优化索引 + 重启
            var buttonGroup = runtime.EditorItems.OfType<EditorButtonGroup>().FirstOrDefault();
            Assert.NotNull(buttonGroup);
            Assert.Equal(8, buttonGroup.EditorItems.Count());
        }

        [Fact]
        public void Basic_ActionButtons_AreOrdered()
        {
            var container = (EditObjectContainer)new PluginOptions().CreateEditContainer();
            var groups = container.EditorRoot.EditorItems.OfType<EditorGroup>().ToList();

            var tasks = groups.First(g => NameOrId(g, "ScheduledTasks"));
            var taskButtons = tasks.EditorItems.OfType<EditorButtonGroup>().Single().EditorItems.ToList();
            Assert.Equal(
                new[] { "ExtractMediaInfoButton", "RefreshEpisodesButton", "MergeMultiVersionButton",
                        "RebuildUserDataKeysButton" },
                taskButtons.Select(b => b.Name).ToArray());

            var maintenance = groups.First(g => NameOrId(g, "RuntimeStatus"));
            var maintenanceButtons = maintenance.EditorItems.OfType<EditorButtonGroup>().Single()
                .EditorItems.ToList();
            Assert.Equal(
                new[] { "VerifyMetadataCacheButton", "MediaHealthCheckButton", "ExportMediaHealthButton",
                        "CleanMemoryNowButton", "ScheduleIndexRebuildButton", "OptimizeSearchIndexButton",
                        "ClearMetadataCacheButton", "RestartServerButton" },
                maintenanceButtons.Select(b => b.Name).ToArray());
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
