using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 从 TheIntroDB 批量刮削缺失的片头片尾标记（剧集 + 电影），供手动按需执行。
    /// </summary>
    public class ScrapeIntroDbTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public ScrapeIntroDbTask(ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.IsPluginEnabled) return;

            if (!Plugin.Instance.IntroSkipStore.GetOptions().Scrape.EnableIntroDbScrape)
            {
                _logger.Warn("IntroDbScrape - Feature is disabled in options, task skipped");
                return;
            }

            var scrapeOptions = Plugin.Instance.IntroSkipStore.GetOptions().Scrape;
            var scrapePaths = Plugin.IntroDbApi.ScrapeLibraryPaths;

            // 选了范围但解析不到任何有效库时，宁可什么都不刮，也不能退化成全库。
            if (!string.IsNullOrWhiteSpace(scrapeOptions.LibraryScope) && scrapePaths.Count == 0)
            {
                _logger.Warn("IntroDbScrape - Scrape library scope resolved to no libraries; task skipped");
                return;
            }

            var seriesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Series) }
            };
            if (scrapePaths.Count > 0) seriesQuery.PathStartsWithAny = scrapePaths.ToArray();

            var series = _libraryManager.GetItemList(seriesQuery)
                .OfType<Series>()
                .Where(s => !string.IsNullOrEmpty(s.GetProviderId(MetadataProviders.Tmdb)))
                .ToList();

            var movies = new List<Movie>();
            if (scrapeOptions.IncludeMovies)
            {
                movies = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(Movie) }
                    })
                    .OfType<Movie>()
                    .Where(m => !string.IsNullOrEmpty(m.GetProviderId(MetadataProviders.Tmdb)) &&
                                !Plugin.ChapterApi.HasIntro(m))
                    .ToList();
            }

            var targets = series.Concat<BaseItem>(movies).ToList();
            _logger.Info("IntroDbScrape - Scheduled Task Start: {0} series, {1} movies, IncludeMovies={2}",
                series.Count, movies.Count, scrapeOptions.IncludeMovies);

            var completed = 0;
            var marked = 0;

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (target is Series s)
                    {
                        marked += await Plugin.IntroDbApi.ScrapeSeriesAsync(s, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (target is Movie m)
                    {
                        if (await Plugin.IntroDbApi.ScrapeMovieAsync(m, cancellationToken).ConfigureAwait(false))
                        {
                            marked++;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.Error("IntroDbScrape - Failed for {0}: {1}", target.Name, e.Message);
                    _logger.Debug(e.StackTrace);
                }

                completed++;
                progress.Report(completed * 100.0 / targets.Count);
            }

            Plugin.IntroDbApi.LogRequestSummary();
            _logger.Info("IntroDbScrape - Scheduled Task Complete: {0} item(s) marked", marked);
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "ScrapeIntroDbTask";

        public string Description => Resources.ResourceManager.GetString(
            "ScrapeIntroDbTask_Description_Fills_missing_intro_credits_markers_from_TheIntroDB",
            Plugin.Instance.DefaultUICulture);

        public string Name =>
            Resources.ResourceManager.GetString("ScrapeIntroDbTask_Name_Scrape_Intro_Credits_from_IntroDb",
                Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
