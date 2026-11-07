using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using static StrmAssistant.Common.VersionLabelBuilder;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.MultipleVersionsOptions;

namespace StrmAssistant.Web.Service
{
    public class MultiVersionService : BaseApiService
    {
        public class MultiVersionSource
        {
            public string Container { get; set; }
            public long? Size { get; set; }
            public int? Width { get; set; }
            public int? Height { get; set; }
            public string Label { get; set; }
            public string Hdr { get; set; }
            public string Audio { get; set; }
            public long? Bitrate { get; set; }
        }

        public class MultiVersionItemInfo : MultiVersionSource
        {
            public string ItemId { get; set; }
        }

        public class MultiVersionResponse
        {
            public bool Enabled { get; set; }
            public List<MultiVersionSource> Sources { get; set; } = new List<MultiVersionSource>();
            public List<MultiVersionItemInfo> Items { get; set; } = new List<MultiVersionItemInfo>();
        }

        private readonly ILibraryManager _libraryManager;

        public MultiVersionService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetMultiVersionInfo request)
        {
            if (!Plugin.IsPluginEnabled) return null;

            var response = new MultiVersionResponse();
            var user = GetUserForRequest(null, true);
            if (user == null) return response;

            if (!Plugin.Instance.ExperienceEnhanceStore.GetOptions().Display.EnableMultiVersionEnhance)
            {
                return response;
            }

            response.Enabled = true;

            if (!string.IsNullOrWhiteSpace(request?.Id) && long.TryParse(request.Id.Trim(), out var itemId))
            {
                var item = _libraryManager.GetItemById(itemId);

                if (item != null && item.IsVisible(user))
                {
                    response.Sources = GetSources(item, user);
                }

                return response;
            }

            if (!string.IsNullOrWhiteSpace(request?.Ids))
            {
                var itemIds = request.Ids
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => long.TryParse(i, out _))
                    .Take(80)
                    .ToList();

                foreach (var id in itemIds)
                {
                    var item = _libraryManager.GetItemById(long.Parse(id));

                    if (item == null || !item.IsVisible(user)) continue;

                    var sources = GetSources(item, user);
                    var primary = sources.FirstOrDefault();

                    if (primary == null) continue;

                    response.Items.Add(new MultiVersionItemInfo
                    {
                        ItemId = id,
                        Container = primary.Container,
                        Size = primary.Size,
                        Width = primary.Width,
                        Height = primary.Height,
                        Label = primary.Label,
                        Hdr = primary.Hdr,
                        Bitrate = primary.Bitrate
                    });
                }
            }

            return response;
        }

        private List<MultiVersionSource> GetSources(BaseItem item, User user)
        {
            var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(item);
            var uiOptions = Plugin.Instance.ExperienceEnhanceStore.GetOptions();

            var sources = item.GetMediaSources(false, false, libraryOptions)
                .Where(source => IsSourceVisible(source, item, user))
                .Select(source =>
                {
                    var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                    var path = source.Path;

                    var label = uiOptions.Display.MultiVersionDisplayOption switch
                    {
                        MultiVersionDisplayOption.FileNameWithoutExtension => string.IsNullOrEmpty(path)
                            ? null
                            : GetFileNameLabel(path),
                        MultiVersionDisplayOption.SystemDefault => null,
                        MultiVersionDisplayOption.MediaInfoExtended =>
                            BuildMediaInfoLabel(video, source.Bitrate, source.Size),
                        _ => GetQualityLabel(video?.Height) // MediaInfo（默认）
                    };

                    return new
                    {
                        Path = path,
                        Source = new MultiVersionSource
                        {
                        Container = source.Container,
                        Size = source.Size,
                        Width = video?.Width,
                        Height = video?.Height,
                        Label = label,
                        Hdr = GetHdrLabel(source.MediaStreams),
                        Audio = GetAudioLabel(source.MediaStreams, path),
                        Bitrate = source.Bitrate
                        }
                    };
                })
                .ToList();

            sources = uiOptions.Display.MultiVersionSortOption switch
            {
                MultiVersionSortOption.HighToLowQuality => sources
                    .OrderByDescending(s => QualityScore(s.Source.Height, s.Source.Hdr))
                    .ToList(),
                MultiVersionSortOption.LowToHighQuality => sources
                    .OrderBy(s => QualityScore(s.Source.Height, s.Source.Hdr))
                    .ToList(),
                MultiVersionSortOption.PathAscending => sources.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                MultiVersionSortOption.PathDescending => sources.OrderByDescending(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => sources // SystemDefault：保持 Emby 原始顺序
            };

            return sources.Select(s => s.Source).ToList();
        }

        private bool IsSourceVisible(MediaSourceInfo source, BaseItem owner, User user)
        {
            // Local alternate versions may belong to another library. Respect their own visibility too.
            var sourceItemId = string.IsNullOrEmpty(source.ItemId) ? source.Id : source.ItemId;
            if (!long.TryParse(sourceItemId, out var id) || id == owner.InternalId) return true;
            var sourceItem = _libraryManager.GetItemById(id);
            return sourceItem != null && sourceItem.IsVisible(user);
        }
    }
}
