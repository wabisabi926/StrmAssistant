using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    public class ChapterService : BaseApiService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        public ChapterService(ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
        }

        public void Post(ClearIntro request)
        {
            if (!Plugin.IsPluginEnabled) return;

            var itemById = _libraryManager.GetItemById(request.Id);

            if (!(itemById is Series || itemById is Season)) return;

            var episodes = Plugin.ChapterApi.FetchClearTaskItems(new List<BaseItem> { itemById });

            if (DryRunMode.Skip($"clear intro/credits markers for {episodes.Count} episode(s)", _logger))
                return;

            foreach (var item in episodes)
            {
                Plugin.ChapterApi.RemoveIntroCreditsMarkers(item);
                _logger.Info("IntroSkipClear - " + item.Name + " - " + item.Path);
            }
        }

        public void Post(ClearChapterImages request)
        {
            if (!Plugin.IsPluginEnabled) return;

            var itemById = _libraryManager.GetItemById(request.Id);

            if (itemById is null) return;

            var targets = itemById is Series || itemById is Season
                ? Plugin.ChapterApi.FetchClearTaskItems(new List<BaseItem> { itemById })
                : new List<BaseItem> { itemById };

            if (DryRunMode.Skip($"clear chapter images for {targets.Count} item(s)", _logger))
                return;

            foreach (var item in targets.OfType<Video>())
            {
                Plugin.ChapterApi.ClearChapterImages(item);
            }
        }
    }
}
