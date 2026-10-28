using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
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

        private static string GetFileNameLabel(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                path = uri.AbsolutePath;
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private bool IsSourceVisible(MediaSourceInfo source, BaseItem owner, User user)
        {
            // Local alternate versions may belong to another library. Respect their own visibility too.
            var sourceItemId = string.IsNullOrEmpty(source.ItemId) ? source.Id : source.ItemId;
            if (!long.TryParse(sourceItemId, out var id) || id == owner.InternalId) return true;
            var sourceItem = _libraryManager.GetItemById(id);
            return sourceItem != null && sourceItem.IsVisible(user);
        }

        /// <summary>
        /// 画质评分：分辨率为主，HDR/DV 加成（用于排序比较）。
        /// 档位顺序须与 <c>strmassistant.js</c> 的 <c>hdrs</c> 列表保持一致（前 = 最优）。
        /// DV 细分档：原盘双层 P7 &gt; 单层 P8 &gt; P9 &gt; 流媒体 P5 &gt; 未识别 DV。
        /// </summary>
        private static int QualityScore(int? height, string hdr)
        {
            var score = height ?? 0;
            if (string.IsNullOrEmpty(hdr)) return score;

            if (hdr == "DV P7") score += 520;
            else if (hdr == "DV P8") score += 515;
            else if (hdr == "DV P9") score += 510;
            else if (hdr == "DV P5") score += 505;
            else if (hdr == "DV") score += 500;
            else if (hdr == "HDR10+") score += 400;
            else if (hdr == "HDR Vivid") score += 350;
            else if (hdr == "HDR") score += 300;

            return score;
        }

        private static string GetQualityLabel(int? height)
        {
            if (!height.HasValue || height.Value <= 0) return null;

            if (height >= 3800) return "8K";
            if (height >= 2100) return "4K";
            if (height >= 1400) return "1440p";
            if (height >= 1000) return "1080p";
            if (height >= 700) return "720p";

            return "SD";
        }

        /// <summary>
        /// 「画质+体积」标签：画质档位 + 视频编码 + 码率 + 文件体积，
        /// 形如 <c>4K HEVC 20.4Mbps 45.2GB</c>。任一缺项自动省略，全缺则返回 null。
        /// 注意：档位词必须留在**首位**，前端 <c>strmassistant.js</c> 的排序按首个空格分词取档位。
        /// </summary>
        private static string BuildMediaInfoLabel(MediaStream video, long? bitrate, long? size)
        {
            var parts = new List<string>();

            var quality = GetQualityLabel(video?.Height);
            if (quality != null) parts.Add(quality);

            var codec = FormatCodec(video?.Codec);
            if (codec != null) parts.Add(codec);

            var bitRate = bitrate ?? video?.BitRate;
            if (bitRate.HasValue && bitRate.Value > 0) parts.Add(FormatBitrate(bitRate.Value));

            if (size.HasValue && size.Value > 0) parts.Add(FormatSize(size.Value));

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }

        /// <summary>把 Emby 的编码短名转成常见写法（hevc→HEVC、h264→H.264…）。</summary>
        private static string FormatCodec(string codec)
        {
            if (string.IsNullOrWhiteSpace(codec)) return null;

            switch (codec.Trim().ToLowerInvariant())
            {
                case "hevc":
                case "h265":
                    return "HEVC";
                case "h264":
                case "avc":
                    return "H.264";
                case "av1":
                    return "AV1";
                case "mpeg2video":
                    return "MPEG-2";
                case "vc1":
                    return "VC-1";
                case "vp9":
                    return "VP9";
                default:
                    return codec.Trim().ToUpperInvariant();
            }
        }

        /// <summary>码率（bit/s → Mbps），整数不留小数点（20.4 → <c>20.4Mbps</c>、20.0 → <c>20Mbps</c>）。</summary>
        private static string FormatBitrate(long bitsPerSecond)
        {
            var mbps = bitsPerSecond / 1000000.0;
            return mbps >= 100 ? $"{mbps:0}Mbps" : $"{mbps:0.#}Mbps";
        }

        /// <summary>文件体积（字节 → 人类可读），1024 进制。</summary>
        private static string FormatSize(long bytes)
        {
            const double kb = 1024.0;
            var gb = bytes / (kb * kb * kb);

            if (gb >= 1024) return $"{gb / 1024:0.##}TB";
            if (gb >= 1) return $"{gb:0.#}GB";

            return $"{bytes / (kb * kb):0.#}MB";
        }

        /// <summary>
        /// HDR 标签。优先按 DV Profile 细分档：
        /// 双层 DV Profile 7 原盘的**增强层**会被 <c>DolbyVisionProfile</c> 标注为
        /// <c>ExtendedVideoSubType = DoviProfile76</c>，而基础层仍是 HDR10，
        /// 因此这里需要扫描**全部视频流**取最优档位，只看主视频流会漏掉 DV 信息。
        /// 档位顺序须与 <c>strmassistant.js</c> 的 <c>hdrs</c> 列表保持一致（前 = 最优）。
        /// </summary>
        private static string GetHdrLabel(IEnumerable<MediaStream> streams)
        {
            var videos = streams?
                .Where(s => s != null && s.Type == MediaStreamType.Video)
                .ToList();

            if (videos == null || videos.Count == 0) return null;

            // HDR Vivid 由插件探测后固化在 MediaStream.Comment 上；Comment 优先于类型分支
            if (videos.Any(v => string.Equals(v.Comment, "HDR Vivid", StringComparison.Ordinal)))
            {
                return "HDR Vivid";
            }

            foreach (var video in videos)
            {
                var dvLabel = GetDvProfileLabel(video.ExtendedVideoSubType);
                if (dvLabel != null) return dvLabel;
            }

            var videoRange = videos[0].VideoRange;
            if (string.IsNullOrEmpty(videoRange)) return null;

            if (videoRange.IndexOf("DoVi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                videoRange.IndexOf("Dovi", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DV";
            }

            if (videoRange.IndexOf("HDR10Plus", StringComparison.OrdinalIgnoreCase) >= 0 ||
                videoRange.IndexOf("HDR10+", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDR10+";
            }

            if (videoRange.IndexOf("Vivid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDR Vivid";
            }

            if (videoRange.IndexOf("HDR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HDR";
            }

            return null;
        }

        /// <summary>
        /// Emby 的 <c>ExtendedVideoSubTypes</c> 命名规则为「profile × 10 + level」，
        /// 例如 <c>DoviProfile76</c> = Profile 7。此处按 Profile 归并出对外标签。
        /// 其余 Dolby Vision 取值（如 Profile 4/6）返回 null，回落为笼统的 "DV"。
        /// </summary>
        private static string GetDvProfileLabel(ExtendedVideoSubTypes subType)
        {
            switch (subType)
            {
                case ExtendedVideoSubTypes.DoviProfile76:
                    return "DV P7";
                case ExtendedVideoSubTypes.DoviProfile81:
                case ExtendedVideoSubTypes.DoviProfile82:
                case ExtendedVideoSubTypes.DoviProfile83:
                case ExtendedVideoSubTypes.DoviProfile84:
                case ExtendedVideoSubTypes.DoviProfile85:
                    return "DV P8";
                case ExtendedVideoSubTypes.DoviProfile92:
                    return "DV P9";
                case ExtendedVideoSubTypes.DoviProfile50:
                    return "DV P5";
                default:
                    return null;
            }
        }
    }
}
