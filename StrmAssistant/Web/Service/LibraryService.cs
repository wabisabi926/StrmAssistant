using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Common.LanguageUtility;

namespace StrmAssistant.Web.Service
{
    public class LibraryService : BaseApiService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public LibraryService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
        }

        public void Any(DeleteVersion request)
        {
            if (!Plugin.IsPluginEnabled) return;

            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Video video) || !(video is Movie || video is Episode) || !video.IsFileProtocol ||
                video.GetAlternateVersionIds().Count == 0)
            {
                return;
            }
            
            var user = GetUserForRequest(null);
            var collectionFolders = _libraryManager.GetCollectionFolders(item);

            if (user is null)
            {
                if (!item.CanDelete())
                {
                    return;
                }
            }
            else if (!item.CanDelete(user, collectionFolders))
            {
                return;
            }

            var enableDeepDelete = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Playback.EnableDeepDelete;
            var enableNotification = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Notification.EnhanceNotificationSystem;

            var deleteItems = item is Episode episode && request.DeleteParent
                ? GetSeasonEpisodesSameVersion(episode)
                : new List<BaseItem> { item };

            var allMountPaths = new Dictionary<string, bool?>();

            foreach (var deleteItem in deleteItems)
            {
                var mountPaths = enableDeepDelete || enableNotification
                    ? Plugin.LibraryApi.PrepareDeepDelete(deleteItem)
                    : null;
                
                var proceedToDelete = true;
                var deletePaths = Plugin.LibraryApi.GetDeletePaths(deleteItem);

                foreach (var path in deletePaths)
                {
                    try
                    {
                        if (!path.IsDirectory)
                        {
                            _logger.Info("DeleteVersion - Attempting to delete file: " + path.FullName);
                            _fileSystem.DeleteFile(path.FullName, true);
                        }
                    }
                    catch (Exception e)
                    {
                        if (e is IOException || e is UnauthorizedAccessException)
                        {
                            proceedToDelete = false;
                            _logger.Error("DeleteVersion - Failed to delete file: " + path.FullName);
                            _logger.Error(e.Message);
                            _logger.Debug(e.StackTrace);
                        }
                    }
                }

                if (proceedToDelete)
                {
                    _itemRepository.DeleteItems(new[] { deleteItem });

                    try
                    {
                        _fileSystem.DeleteDirectory(item.GetInternalMetadataPath(), true, true);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                if (proceedToDelete && mountPaths != null)
                {
                    allMountPaths = allMountPaths
                        .Concat(mountPaths)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);;
                }
            }

            var localMountPaths = new HashSet<string>(allMountPaths.Where(kv => kv.Value is true).Select(kv => kv.Key));

            if (enableDeepDelete && localMountPaths.Count > 0)
            {
                _ = BackgroundTaskRunner.Run("DeepDelete.Execute", () => Plugin.LibraryApi.ExecuteDeepDelete(localMountPaths));
            }

            if (enableNotification && allMountPaths.Count > 0 && user != null)
            {
                _ = BackgroundTaskRunner.Run("DeepDelete.Notify", () => Plugin.NotificationApi.DeepDeleteSendNotification(item, user,
                    new HashSet<string>(allMountPaths.Keys)));
            }
        }

        /// <summary>
        /// 只读预览：列出「删除整季该版本」会删掉哪些条目、哪些候选因版本身份不明被跳过。
        /// 不执行任何删除。权限判断与删除入口保持一致，避免向无删除权限的用户泄露路径。
        /// </summary>
        public object Any(DeleteVersionPreview request)
        {
            var result = new DeleteVersionPreviewResult
            {
                ItemId = request?.Id,
                DeleteParent = request?.DeleteParent ?? false
            };

            if (!Plugin.IsPluginEnabled)
            {
                result.Message = "插件未启用";
                return result;
            }

            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Episode episode))
            {
                result.Message = "仅支持对单集预览";
                return result;
            }

            if (!request.DeleteParent)
            {
                result.Message = "DeleteParent=false：只会删除该单集本身";
                result.WillDelete.Add(new DeleteVersionPreviewEntry
                {
                    Id = episode.Id.ToString(),
                    Name = episode.Name,
                    Path = episode.Path
                });
                return result;
            }

            var user = GetUserForRequest(null);
            var collectionFolders = _libraryManager.GetCollectionFolders(item);

            if (user is null)
            {
                if (!item.CanDelete())
                {
                    result.Message = "无删除权限";
                    return result;
                }
            }
            else if (!item.CanDelete(user, collectionFolders))
            {
                result.Message = "无删除权限";
                return result;
            }

            foreach (var target in GetSeasonEpisodesSameVersion(episode, result.Skipped))
            {
                result.WillDelete.Add(new DeleteVersionPreviewEntry
                {
                    Id = target.Id.ToString(),
                    Name = target.Name,
                    Path = target.Path
                });
            }

            result.Message = $"将删除 {result.WillDelete.Count} 个条目，跳过 {result.Skipped.Count} 个候选";
            return result;
        }

        private List<BaseItem> GetSeasonEpisodesSameVersion(Episode episode,
            List<DeleteVersionPreviewEntry> skipped = null)
        {
            var seasonFolderChildren = episode.Parent.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    Recursive = false,
                    GroupByPresentationUniqueKey = false,
                })
                .ToList();

            var seasonEpisodesCount = episode.Season.GetEpisodeIds(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) }, GroupByPresentationUniqueKey = true
                })
                .Length;

            if (seasonFolderChildren.Count == seasonEpisodesCount)
            {
                return seasonFolderChildren;
            }

            var targetCleaned = CleanEpisodeName(episode.FileNameWithoutExtension);

            var allEpisodes = episode.Season
                .GetEpisodes(new InternalItemsQuery
                {
                    GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false
                })
                .Items.ToList();

            var similarEpisodes = new List<BaseItem>();

            foreach (var ep in allEpisodes)
            {
                var cleanedName = CleanEpisodeName(ep.FileNameWithoutExtension);
                var similarity = LevenshteinDistance(targetCleaned, cleanedName);

                if (similarity <= 0.92) continue;

                // 名称相似度不足以证明版本身份：1080p 与 2160p 在长文件名里的相似度可达 0.94+，
                // 仅凭相似度会把两个版本判成同一版，进而删掉另一版本的真实文件。
                // 删除不可逆，判不明一律排除（宁可少删）。
                if (!VersionIdentity.IsSameVersion(episode.Path, episode.FileNameWithoutExtension,
                    ep.Path, ep.FileNameWithoutExtension, out var reason))
                {
                    _logger.Info(
                        $"DeleteVersion - Skip candidate (version identity not confirmed: {reason}): {ep.Name} - {ep.Path}");

                    skipped?.Add(new DeleteVersionPreviewEntry
                    {
                        Id = ep.Id.ToString(),
                        Name = ep.Name,
                        Path = ep.Path,
                        Reason = reason
                    });

                    continue;
                }

                similarEpisodes.Add(ep);
            }

            return similarEpisodes;
        }
    }

    /// <summary>删除预览结果。只读，不含任何副作用。</summary>
    public class DeleteVersionPreviewResult
    {
        public string ItemId { get; set; }
        public bool DeleteParent { get; set; }
        public string Message { get; set; }
        public List<DeleteVersionPreviewEntry> WillDelete { get; set; } = new List<DeleteVersionPreviewEntry>();
        public List<DeleteVersionPreviewEntry> Skipped { get; set; } = new List<DeleteVersionPreviewEntry>();
    }

    /// <summary>预览中的单个条目；<see cref="Reason"/> 仅在被跳过时有值。</summary>
    public class DeleteVersionPreviewEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Reason { get; set; }
    }

}
