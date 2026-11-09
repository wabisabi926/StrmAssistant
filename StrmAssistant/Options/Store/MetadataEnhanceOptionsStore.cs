using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Options.Store
{
    public class MetadataEnhanceOptionsStore : SimpleFileStore<MetadataEnhanceOptions>
    {
        private readonly ILogger _logger;

        public MetadataEnhanceOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger= logger;

            // 3.0.0.55：设置项由平铺改为 9 个分组嵌套。先把旧配置就地迁移为嵌套结构，
            // 否则旧键会被反序列化忽略、用户设置静默回退为默认值。
            MetadataEnhanceOptionsMigration.MigrateIfNeeded(OptionsFilePath,
                applicationHost.Resolve<IJsonSerializer>(), logger);

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public MetadataEnhanceOptions MetadataEnhanceOptions => GetOptions();

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is MetadataEnhanceOptions options)
            {
                if (string.IsNullOrEmpty(options.ChineseMetadata.FallbackLanguages))
                {
                    options.ChineseMetadata.FallbackLanguages = "zh-sg";
                }
                else
                {
                    var languages = options.ChineseMetadata.FallbackLanguages.Split(',');
                    options.ChineseMetadata.FallbackLanguages = string.Join(",",
                        LanguageUtility.MovieDbFallbackLanguages
                            .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
                            .Select(l => l.ToLowerInvariant()));
                }

                if (string.IsNullOrEmpty(options.ChineseMetadata.TvdbFallbackLanguages))
                {
                    options.ChineseMetadata.TvdbFallbackLanguages = "zhtw";
                }
                else
                {
                    var languages = options.ChineseMetadata.TvdbFallbackLanguages.Split(',');
                    options.ChineseMetadata.TvdbFallbackLanguages = string.Join(",",
                        LanguageUtility.TvdbFallbackLanguages
                            .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase)));
                }

                if (string.IsNullOrEmpty(options.MetadataRefresh.EpisodeRefreshScope))
                {
                    options.MetadataRefresh.EpisodeRefreshScope = EpisodeRefreshOption.NoOverview.ToString();
                }

                // 旧配置兼容：豆瓣覆盖开关已并入「社区评分来源」下拉（UI 不再单独显示）。
                // 首次保存时把 true 迁移为「来源=豆瓣」，之后二者不会再同时生效。
                if (options.DoubanAssist.DoubanRatingOverride &&
                    string.Equals(options.CommunityRating.CommunityRatingSource,
                        CommunityRatingSourceOption.None.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    options.CommunityRating.CommunityRatingSource = CommunityRatingSourceOption.Douban.ToString();
                    options.DoubanAssist.DoubanRatingOverride = false;
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(MetadataEnhanceOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(ChineseMetadataOptions.ChineseMovieDb)))
                {
                    if (options.ChineseMetadata.ChineseMovieDb)
                    {
                        PatchManager.ChineseMovieDb.Patch();
                    }
                    else
                    {
                        PatchManager.ChineseMovieDb.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ChineseMetadataOptions.ChineseTvdb)))
                {
                    if (options.ChineseMetadata.ChineseTvdb)
                    {
                        PatchManager.ChineseTvdb.Patch();
                    }
                    else
                    {
                        PatchManager.ChineseTvdb.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(EpisodeGroupOptions.MovieDbEpisodeGroup)))
                {
                    if (options.EpisodeGroup.MovieDbEpisodeGroup)
                    {
                        PatchManager.MovieDbEpisodeGroup.Patch();
                    }
                    else
                    {
                        PatchManager.MovieDbEpisodeGroup.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ScrapeOptimizeOptions.OptimizeMovieDbEpisodeScraping)) ||
                    changedProperties.Contains(nameof(ScrapeOptimizeOptions.DisableEpisodeImageScraping)))
                {
                    if (options.ScrapeOptimize.OptimizeMovieDbEpisodeScraping ||
                        options.ScrapeOptimize.DisableEpisodeImageScraping)
                    {
                        PatchManager.OptimizeMovieDbEpisodeScraping.Patch();
                    }
                    else
                    {
                        PatchManager.OptimizeMovieDbEpisodeScraping.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(PersonEnhanceOptions.EnhanceMovieDbPerson)))
                {
                    if (options.PersonEnhance.EnhanceMovieDbPerson)
                    {
                        PatchManager.EnhanceMovieDbPerson.Patch();
                    }
                    else
                    {
                        PatchManager.EnhanceMovieDbPerson.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(PosterSortOptions.ClearTagline)))
                {
                    if (options.PosterSort.ClearTagline && options.IsModSupported)
                    {
                        PatchManager.ClearTagline.Patch();
                    }
                    else
                    {
                        PatchManager.ClearTagline.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ChineseMetadataOptions.PreferTraditionalChinese)))
                {
                    if (options.ChineseMetadata.PreferTraditionalChinese && options.IsModSupported)
                    {
                        PatchManager.PreferTraditionalChinese.Patch();
                    }
                    else
                    {
                        PatchManager.PreferTraditionalChinese.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ChineseMetadataOptions.DefaultNewLibraryTmdb)))
                {
                    if (options.ChineseMetadata.DefaultNewLibraryTmdb && options.IsModSupported)
                    {
                        PatchManager.DefaultTmdbLibrary.Patch();
                    }
                    else
                    {
                        PatchManager.DefaultTmdbLibrary.Unpatch();
                    }
                }

                {
                    if (options.PosterSort.PreferOriginalPoster)
                    {
                        PatchManager.PreferOriginalPoster.Patch();
                    }
                    else
                    {
                        PatchManager.PreferOriginalPoster.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(PosterSortOptions.PinyinSortName)))
                {
                    if (options.PosterSort.PinyinSortName)
                    {
                        PatchManager.PinyinSortName.Patch();
                    }
                    else
                    {
                        PatchManager.PinyinSortName.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(PosterSortOptions.EnhanceNfoMetadata)))
                {
                    if (options.PosterSort.EnhanceNfoMetadata)
                    {
                        PatchManager.EnhanceNfoMetadata.Patch();
                    }
                    else
                    {
                        PatchManager.EnhanceNfoMetadata.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataCacheOptions.MetadataCacheMode)) ||
                    changedProperties.Contains(nameof(MetadataCacheOptions.MetadataCacheRootFolder)))
                {
                    if (options.MetadataCache.IsMetadataCacheEnabled)
                    {
                        var (ok, error) = LocalMetadataCache.ValidateAndInitialize(
                            options.MetadataCache.MetadataCacheRootFolder);
                        if (ok)
                        {
                            _logger.Info("MetadataCache - root initialized: {0} (mode={1})",
                                LocalMetadataCache.Root, options.MetadataCache.MetadataCacheMode);
                            PatchManager.LocalTmdbCache.Patch();
                        }
                        else
                        {
                            _logger.Warn("MetadataCache - root invalid ({0}): {1}", error,
                                options.MetadataCache.MetadataCacheRootFolder);
                            PatchManager.LocalTmdbCache.Unpatch();
                        }
                    }
                    else
                    {
                        LocalMetadataCache.Reset();
                        PatchManager.LocalTmdbCache.Unpatch();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is MetadataEnhanceOptions options)
            {
                _logger.Info("ChineseMovieDb is set to {0}", options.ChineseMetadata.ChineseMovieDb);
                _logger.Info("MovieDbFallbackLanguages is set to {0}", options.ChineseMetadata.FallbackLanguages);
                _logger.Info("ChineseTvdb is set to {0}", options.ChineseMetadata.ChineseTvdb);
                _logger.Info("TvdbFallbackLanguages is set to {0}", options.ChineseMetadata.TvdbFallbackLanguages);
                _logger.Info("BlockNonFallbackLanguage is set to {0}", options.ChineseMetadata.BlockNonFallbackLanguage);
                _logger.Info("MovieDbEpisodeGroup is set to {0}", options.EpisodeGroup.MovieDbEpisodeGroup);
                _logger.Info("OptimizeMovieDbEpisodeScraping is set to {0}",
                    options.ScrapeOptimize.OptimizeMovieDbEpisodeScraping);
                _logger.Info("DisableMovieDbEpisodeScrapingFallback is set to {0}",
                    options.ScrapeOptimize.DisableMovieDbEpisodeScrapingFallback);
                _logger.Info("DisableEpisodeImageScraping is set to {0}",
                    options.ScrapeOptimize.DisableEpisodeImageScraping);
                _logger.Info("LocalEpisodeGroup is set to {0}", options.EpisodeGroup.LocalEpisodeGroup);
                _logger.Info("EnhanceMovieDbPerson is set to {0}", options.PersonEnhance.EnhanceMovieDbPerson);
                _logger.Info("PreferOriginalPoster is set to {0}", options.PosterSort.PreferOriginalPoster);
                _logger.Info("PinyinSortName is set to {0}", options.PosterSort.PinyinSortName);
                _logger.Info("EnhanceNfoMetadata is set to {0}", options.PosterSort.EnhanceNfoMetadata);
                var episodeRefreshScope = string.Join(", ",
                    options.MetadataRefresh.EpisodeRefreshScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s =>
                            Enum.TryParse(s.Trim(), true, out EpisodeRefreshOption option)
                                ? Utility.GetEnumDescription(option)
                                : null)
                        .Where(d => d != null) ?? Enumerable.Empty<string>());
                _logger.Info("EpisodeRefreshScope is set to {0}", episodeRefreshScope);
                _logger.Info("EpisodeRefreshLookbackDays is set to {0}", options.MetadataRefresh.EpisodeRefreshLookbackDays);
                _logger.Info("EnableDoubanAssist is set to {0}", options.DoubanAssist.EnableDoubanAssist);
                _logger.Info("DoubanCookie is set to {0}",
                    !string.IsNullOrEmpty(options.DoubanAssist.DoubanCookie) ? "CONFIGURED" : "EMPTY");
                _logger.Info("DoubanAccessToken is set to {0}",
                    !string.IsNullOrEmpty(options.DoubanAssist.DoubanAccessToken) ? "CONFIGURED" : "EMPTY");
                _logger.Info("DoubanRatingOverride is set to {0}", options.DoubanAssist.DoubanRatingOverride);
                _logger.Info("DoubanCastChinese is set to {0}", options.DoubanAssist.DoubanCastChinese);
                _logger.Info("DoubanPosterFallback is set to {0}", options.DoubanAssist.DoubanPosterFallback);
                _logger.Info("EpisodeRefreshLibraryScope is set to {0}",
                    !string.IsNullOrEmpty(options.MetadataRefresh.EpisodeRefreshLibraryScope)
                        ? options.MetadataRefresh.EpisodeRefreshLibraryScope
                        : "<all>");
                _logger.Info("MetadataCacheMode is set to {0}", options.MetadataCache.MetadataCacheMode);
                _logger.Info("MetadataCacheRootFolder is set to {0}",
                    !string.IsNullOrEmpty(options.MetadataCache.MetadataCacheRootFolder)
                        ? options.MetadataCache.MetadataCacheRootFolder
                        : "EMPTY");
            }
        }
    }
}
