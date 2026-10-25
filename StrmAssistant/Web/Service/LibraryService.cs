using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
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
                Task.Run(() => Plugin.LibraryApi.ExecuteDeepDelete(localMountPaths)).ConfigureAwait(false);
            }

            if (enableNotification && allMountPaths.Count > 0 && user != null)
            {
                Task.Run(() => Plugin.NotificationApi.DeepDeleteSendNotification(item, user,
                        new HashSet<string>(allMountPaths.Keys)))
                    .ConfigureAwait(false);
            }
        }

        private List<BaseItem> GetSeasonEpisodesSameVersion(Episode episode)
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
                if (!IsSameVersion(episode, ep, out var reason))
                {
                    _logger.Info(
                        $"DeleteVersion - Skip candidate (version identity not confirmed: {reason}): {ep.Name} - {ep.Path}");
                    continue;
                }

                similarEpisodes.Add(ep);
            }

            return similarEpisodes;
        }

        /// <summary>
        /// 判断候选条目与目标条目是否为「同一版本」：同目录 + 分辨率一致 + 编码/片源（若有）一致。
        /// 分辨率标记缺失时直接判为不同版本——这类文件无法可靠区分，不应进入批量删除集合。
        /// </summary>
        internal static bool IsSameVersion(Episode target, BaseItem candidate, out string reason) =>
            IsSameVersion(target.Path, target.FileNameWithoutExtension,
                candidate.Path, candidate.FileNameWithoutExtension, out reason);

        /// <summary>
        /// 纯字符串版本：便于单测（Emby 实体的 <c>FileNameWithoutExtension</c> 依赖内部状态，无法直接构造）。
        /// </summary>
        internal static bool IsSameVersion(string targetPath, string targetName,
            string candidatePath, string candidateName, out string reason)
        {
            reason = null;

            var targetDir = Path.GetDirectoryName(targetPath);
            var candidateDir = Path.GetDirectoryName(candidatePath);

            if (!string.Equals(targetDir, candidateDir, StringComparison.OrdinalIgnoreCase))
            {
                reason = "different folder";
                return false;
            }

            targetName = targetName ?? string.Empty;
            candidateName = candidateName ?? string.Empty;

            var targetResolution = ParseResolutionToken(targetName);
            var candidateResolution = ParseResolutionToken(candidateName);

            if (targetResolution == null || candidateResolution == null)
            {
                reason = "resolution token missing";
                return false;
            }

            if (!string.Equals(targetResolution, candidateResolution, StringComparison.Ordinal))
            {
                reason = $"resolution {targetResolution} vs {candidateResolution}";
                return false;
            }

            var targetCodec = ParseCodecToken(targetName);
            var candidateCodec = ParseCodecToken(candidateName);

            if (targetCodec != null && candidateCodec != null &&
                !string.Equals(targetCodec, candidateCodec, StringComparison.Ordinal))
            {
                reason = $"codec {targetCodec} vs {candidateCodec}";
                return false;
            }

            var targetSource = ParseSourceToken(targetName);
            var candidateSource = ParseSourceToken(candidateName);

            if (targetSource != null && candidateSource != null &&
                !string.Equals(targetSource, candidateSource, StringComparison.Ordinal))
            {
                reason = $"source {targetSource} vs {candidateSource}";
                return false;
            }

            return true;
        }

        private static readonly Regex ResolutionTokenRegex = new Regex(
            @"(?<![0-9])(2160p|1080p|720p|576p|480p|4k|uhd)(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodecTokenRegex = new Regex(
            @"(?<![a-z0-9])(h\.?264|x264|avc|h\.?265|x265|hevc|av1|vp9)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SourceTokenRegex = new Regex(
            @"(?<![a-z0-9])(web-?dl|webrip|blu-?ray|bdrip|remux|hdtv|dvdrip)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string ParseResolutionToken(string fileName)
        {
            var match = ResolutionTokenRegex.Match(fileName ?? string.Empty);
            if (!match.Success) return null;

            var token = match.Value.ToLowerInvariant();

            switch (token)
            {
                case "2160p":
                case "4k":
                case "uhd":
                    return "2160";
                case "1080p":
                    return "1080";
                case "720p":
                    return "720";
                case "576p":
                    return "576";
                default:
                    return "480";
            }
        }

        internal static string ParseCodecToken(string fileName)
        {
            var match = CodecTokenRegex.Match(fileName ?? string.Empty);
            if (!match.Success) return null;

            var token = match.Value.ToLowerInvariant().Replace(".", string.Empty);

            switch (token)
            {
                case "h264":
                case "x264":
                case "avc":
                    return "h264";
                case "h265":
                case "x265":
                case "hevc":
                    return "h265";
                default:
                    return token; // av1 / vp9
            }
        }

        internal static string ParseSourceToken(string fileName)
        {
            var match = SourceTokenRegex.Match(fileName ?? string.Empty);
            if (!match.Success) return null;

            var token = match.Value.ToLowerInvariant().Replace("-", string.Empty);

            switch (token)
            {
                case "webdl":
                    return "webdl";
                case "bluray":
                    return "bluray";
                default:
                    return token; // webrip / bdrip / remux / hdtv / dvdrip
            }
        }
    }
}
