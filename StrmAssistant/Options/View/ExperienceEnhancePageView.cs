using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistant.Common;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.UIBaseClasses.Views;
using StrmAssistant.Properties;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
{
    internal class ExperienceEnhancePageView : PluginPageView
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ExperienceEnhanceOptionsStore _store;

        public ExperienceEnhancePageView(PluginInfo pluginInfo, ILibraryManager libraryManager,
            ExperienceEnhanceOptionsStore store) : base(pluginInfo.Id)
        {
            _libraryManager = libraryManager;
            _store = store;
            ContentData = store.GetOptions();
            ExperienceEnhanceOptions.ItemDisplay.Initialize();
            ExperienceEnhanceOptions.LibraryOrder.Initialize();
        }

        public ExperienceEnhanceOptions ExperienceEnhanceOptions => ContentData as ExperienceEnhanceOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            _store.SetOptions(ExperienceEnhanceOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "SplitMovies":
                    Task.Run(HandleSplitMovieButton).FireAndForget(Plugin.Instance.Logger);
                    return Task.FromResult<IPluginUIView>(this);
            }

            return base.RunCommand(itemId, commandId, data);
        }

        private async Task HandleSplitMovieButton()
        {
            ExperienceEnhanceOptions.Merge.SplitMoviesButton.IsEnabled = false;
            ExperienceEnhanceOptions.Merge.SplitMoviesProgress.Clear();
            var progressItem = new GenericListItem
            {
                Icon = IconNames.work_outline,
                IconMode = ItemListIconMode.SmallRegular,
                Status = ItemStatus.InProgress,
                HasPercentage = true
            };
            ExperienceEnhanceOptions.Merge.SplitMoviesProgress.Add(progressItem);
            RaiseUIViewInfoChanged();
            await Task.Delay(10.ms());

            var movies = Plugin.LibraryApi.FetchSplitMovieItems();
            progressItem.PercentComplete = 20;
            RaiseUIViewInfoChanged();
            await Task.Delay(10.ms());

            var total = movies.Count;
            var dryRun = DryRunMode.Skip("split multi-version movies", Plugin.Instance.Logger);
            var current = 0;

            foreach (var item in movies)
            {
                if (!dryRun) _libraryManager.SplitItems(item);
                current++;
                var percentDone = current * 100 / total;
                var adjustedProgress = 20 + percentDone * 80 / 100;
                progressItem.PercentComplete = adjustedProgress;
                Plugin.Instance.Logger.Info(
                    $"MergeMovie - Split group{(dryRun ? " (dry run)" : string.Empty)} " +
                    $"{current}/{total} - {item.Path}");
                RaiseUIViewInfoChanged();
                await Task.Delay(10.ms());
            }

            ExperienceEnhanceOptions.Merge.SplitMoviesButton.IsEnabled = true;
            progressItem.HasPercentage = false;
            progressItem.SecondaryText = dryRun
                ? $"演练：将拆分 {total} 组"
                : Resources.Operation_Success;
            progressItem.Icon = IconNames.info;
            progressItem.Status = ItemStatus.Succeeded;
            RaiseUIViewInfoChanged();
            await Task.Delay(2000);
            ExperienceEnhanceOptions.Merge.SplitMoviesProgress.Clear();
            RaiseUIViewInfoChanged();
        }
    }
}
