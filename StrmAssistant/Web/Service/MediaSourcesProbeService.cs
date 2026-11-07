using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// 多版本服务端化的前置探针。
    ///
    /// 背景：版本画质/HDR/音轨标签目前只由 <see cref="MultiVersionService"/> + 前端 JS 渲染，
    /// 只在 Emby Web 生效；Kodi、Apple TV 等客户端读的是 <see cref="MediaSourceInfo.Name"/>，
    /// 那里现在还是文件名或空。
    ///
    /// 本接口只读，返回每个源当前的名字、分辨率、码率、HDR 线索、音轨线索，
    /// 用来确认：① 名字当前是什么；② 改写点（GetMediaSources 出参 vs DTO 层）；
    /// ③ 有没有客户端依赖 Name 做播放决策（观察 PlaybackInfo 是否复用同一对象）。
    /// </summary>
    public class MediaSourcesProbeService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public MediaSourcesProbeService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(ProbeMediaSources request)
        {
            if (!long.TryParse(request?.Id?.Trim(), out var itemId))
                return new { Error = "query parameter Id (item internal id) is required" };

            var item = _libraryManager.GetItemById(itemId);
            if (item == null) return new { Error = $"item {itemId} not found" };

            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            var sources = item.GetMediaSources(false, false, libraryOptions);

            return new
            {
                Item = new
                {
                    item.InternalId,
                    item.Name,
                    Type = item.GetType().Name,
                    Path = Summarize(item.Path)
                },
                EnableMultiVersionEnhance = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Display.EnableMultiVersionEnhance,
                Sources = (sources ?? new List<MediaSourceInfo>()).Select((source, index) =>
                {
                    var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                    var audio = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

                    return new
                    {
                        Index = index,
                        source.Id,
                        source.Name,
                        source.Container,
                        source.Size,
                        source.Bitrate,
                        Path = Summarize(source.Path),
                        Width = video?.Width,
                        Height = video?.Height,
                        VideoRange = video?.VideoRange,
                        ExtendedVideoSubType = video?.ExtendedVideoSubType.ToString(),
                        HdrHint = video?.VideoRange,
                        AudioCodec = audio?.Codec,
                        AudioTitle = audio?.Title,
                        AudioHint = AudioTagDetector.ToLabel(
                            AudioTagDetector.Detect(audio?.Title, audio?.Codec, source.Path))
                    };
                }).ToList()
            };
        }

        /// <summary>路径只回显尾部，避免把完整网络地址打到日志/响应里。</summary>
        private static string Summarize(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            var name = path.Split('/', '\\').LastOrDefault();
            return string.IsNullOrEmpty(name) ? path : "…/" + name;
        }
    }
}
