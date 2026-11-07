using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistant.Common;
using StrmAssistant.Options;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    public class ItemService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;

        public ItemService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
        }

        public void Post(LockItem request)
        {
            if (!Plugin.IsPluginEnabled) return;

            var itemById = _libraryManager.GetItemById(request.ItemId);

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                PresentationUniqueKey = itemById.PresentationUniqueKey
            });

            foreach (var item in items)
            {
                if (item.IsLocked != request.LockData)
                {
                    item.IsLocked = request.LockData;
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                }

                if (item is Folder folder)
                {
                    foreach (var child in folder.GetItemList(new InternalItemsQuery { Recursive = true }))
                    {
                        if (child.IsLocked != request.LockData)
                        {
                            child.IsLocked = request.LockData;
                            child.UpdateToRepository(ItemUpdateType.MetadataEdit);
                        }
                    }
                }
            }
        }

        public void Post(ClearMediaInfo request)
        {
            if (!Plugin.IsPluginEnabled) return;

            var itemById = _libraryManager.GetItemById(request.Id);

            if (itemById is null) return;

            if (DryRunMode.Skip($"clear media info for {itemById.Name}", Plugin.Instance.Logger)) return;

            Plugin.MediaInfoApi.ClearMediaInfoJson(itemById, "Clear Media Info Menu");
        }

        public MediaInfoMenuResponse Post(ExtractMediaInfo request)
        {
            var response = new MediaInfoMenuResponse();
            if (!Plugin.IsPluginEnabled)
            {
                response.Message = "plugin disabled";
                return response;
            }

            var items = ExpandToTargetItems(request?.Ids);
            response.Total = items.Count;
            if (items.Count == 0)
            {
                response.Message = "no supported items";
                return response;
            }

            var mode = Plugin.Instance.MediaInfoExtractStore.GetOptions().MediaInfoPersist.PersistMediaInfoMode;
            var writeBackup = mode == MediaInfoExtractOptions.PersistMediaInfoOption.Default.ToString();
            response.Queued = items.Count;
            response.Message = "queued";

            var logger = Plugin.Instance.Logger;
            logger.Info($"MediaInfoMenu - Extract queued: {items.Count} item(s), writeBackup={writeBackup}");

            _ = Task.Run(async () =>
            {
                foreach (var item in items)
                {
                    try
                    {
                        await QueueManager.MasterSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            await Plugin.LibraryApi
                                .OrchestrateMediaInfoProcessAsync(item, "Shortcut Menu Extract",
                                    CancellationToken.None, true, writeBackup)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            QueueManager.MasterSemaphore.Release();
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error("MediaInfoMenu - Extract failed: " + (item.Path ?? item.Name));
                        logger.Error(e.Message);
                        logger.Debug(e.StackTrace);
                    }
                }

                logger.Info($"MediaInfoMenu - Extract queue completed: {items.Count} item(s)");
            });

            return response;
        }

        public MediaInfoMenuResponse Post(DeleteMediaInfo request)
        {
            var response = new MediaInfoMenuResponse();
            if (!Plugin.IsPluginEnabled)
            {
                response.Message = "plugin disabled";
                return response;
            }

            var items = ExpandToTargetItems(request?.Ids);
            response.Total = items.Count;
            if (items.Count == 0)
            {
                response.Message = "no supported items";
                return response;
            }

            if (DryRunMode.Skip($"delete media info for {items.Count} item(s)", Plugin.Instance.Logger))
            {
                response.Message = "dry run";
                response.Processed = items.Count;
                response.Skipped = items.Count;
                return response;
            }

            var directoryService = new DirectoryService(Plugin.Instance.Logger, _fileSystem);
            var logger = Plugin.Instance.Logger;

            foreach (var item in items)
            {
                response.Processed++;
                try
                {
                    var hadMediaInfo = Plugin.LibraryApi.HasMediaInfo(item);
                    var jsonPath = MediaInfoApi.GetMediaInfoJsonReadPath(item, directoryService);
                    var hadJson = directoryService.GetFile(jsonPath)?.Exists == true;
                    if (!hadMediaInfo && !hadJson)
                    {
                        response.Skipped++;
                        continue;
                    }

                    Plugin.MediaInfoApi.DeleteMediaInfoJson(item, directoryService, "Shortcut Menu Delete");
                    _itemRepository.SaveMediaStreams(item.InternalId, new List<MediaStream>(),
                        CancellationToken.None);
                    item.MediaStreams = new List<MediaStream>();
                    item.RunTimeTicks = null;
                    item.TotalBitrate = 0;
                    item.Container = null;
                    item.Size = 0;
                    item.UpdateToRepository(ItemUpdateType.MetadataImport);
                    response.Succeeded++;
                }
                catch (Exception e)
                {
                    response.Failed++;
                    logger.Error("MediaInfoMenu - Delete failed: " + (item.Path ?? item.Name));
                    logger.Error(e.Message);
                    logger.Debug(e.StackTrace);
                }
            }

            response.Message = "ok";
            logger.Info($"MediaInfoMenu - Delete result: total={response.Total}, processed={response.Processed}, " +
                        $"succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}");
            return response;
        }

        private List<BaseItem> ExpandToTargetItems(IEnumerable<string> ids)
        {
            var results = new List<BaseItem>();
            var seen = new HashSet<long>();
            if (ids == null) return results;

            foreach (var id in ids)
            {
                if (!long.TryParse(id, out var itemId)) continue;
                var item = _libraryManager.GetItemById(itemId);
                if (item == null) continue;

                if (item is Series || item is Season)
                {
                    var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        AncestorIds = new[] { item.InternalId },
                        Recursive = true,
                        MediaTypes = new[] { MediaType.Video },
                        IncludeItemTypes = new[] { "Episode" }
                    });

                    foreach (var episode in episodes)
                    {
                        if (episode.ExtraType == null && seen.Add(episode.InternalId))
                            results.Add(episode);
                    }
                }
                else if (item is Video video && video.ExtraType == null)
                {
                    if (seen.Add(video.InternalId)) results.Add(video);
                }
            }

            return results;
        }
    }
}
