using Emby.Naming.Common;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Core;
using StrmAssistant.Mod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class SubtitleApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(SubtitleApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);
        private readonly object _subtitleResolver;
        private readonly MethodInfo _getExternalSubtitleStreams;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        public SubtitleApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;

            try
            {
                var embyProviders = EmbyVersionAdapter.Instance.TryLoadAssembly("Emby.Providers");
                if (embyProviders == null)
                {
                    _logger.Error($"{nameof(SubtitleApi)} - Failed to load Emby.Providers assembly");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    return;
                }

                var subtitleResolverType = EmbyVersionAdapter.Instance.TryGetType("Emby.Providers", "Emby.Providers.MediaInfo.SubtitleResolver");
                if (subtitleResolverType != null)
                {
                    var subtitleResolverConstructor = subtitleResolverType.GetConstructor(new[]
                    {
                        typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                    });
                    
                    if (subtitleResolverConstructor != null)
                    {
                        _subtitleResolver = subtitleResolverConstructor.Invoke(new object[]
                        {
                            localizationManager, fileSystem, libraryManager
                        });
                        
                        var methods = subtitleResolverType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.Name == "GetExternalSubtitleStreams" || 
                                       m.Name == "GetExternalStreams" || 
                                       m.Name == "GetExternalTracks")
                            .ToArray();
                        
                        if (methods.Length > 0)
                        {
                            _getExternalSubtitleStreams = methods.OrderByDescending(m => m.GetParameters().Length).First();
                            
                            var paramCount = _getExternalSubtitleStreams.GetParameters().Length;
                            var paramTypes = string.Join(", ", _getExternalSubtitleStreams.GetParameters().Select(p => p.ParameterType.Name));
                            _logger.Info($"{nameof(SubtitleApi)}: Found {_getExternalSubtitleStreams.Name} with {paramCount} parameters: {paramTypes}");
                        }
                    }
                }

                var ffProbeSubtitleInfoType = EmbyVersionAdapter.Instance.TryGetType("Emby.Providers", "Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                if (ffProbeSubtitleInfoType != null)
                {
                    var ffProbeSubtitleInfoConstructor = ffProbeSubtitleInfoType.GetConstructor(new[]
                    {
                        typeof(IMediaProbeManager)
                    });
                    
                    if (ffProbeSubtitleInfoConstructor != null)
                    {
                        _ffProbeSubtitleInfo = ffProbeSubtitleInfoConstructor.Invoke(new object[] { mediaProbeManager });
                        _updateExternalSubtitleStream = ffProbeSubtitleInfoType.GetMethod("UpdateExternalSubtitleStream");
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"{nameof(SubtitleApi)} - Failed to initialize reflection components: {e.Message}");
            }

            if (_subtitleResolver is null || _getExternalSubtitleStreams is null ||
                _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                _logger.Info($"{nameof(SubtitleApi)} - Using fallback approach.");
            }
            else if (Plugin.Instance.IsModSupported)
            {
                // 执行 Harmony 逆向打补丁
                var patch1 = PatchManager.ReversePatch(PatchTracker, _getExternalSubtitleStreams,
                    nameof(GetExternalSubtitleStreamsStub));
                var patch2 = PatchManager.ReversePatch(PatchTracker, _updateExternalSubtitleStream,
                    nameof(UpdateExternalSubtitleStreamStub));
                
                if ((patch1 || patch2) && PatchTracker.FallbackPatchApproach == PatchApproach.Harmony)
                {
                    _logger.Info($"{nameof(SubtitleApi)} - Harmony patches applied successfully (Target: Emby 4.9.1+)");
                }
                else if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug($"[SubtitleApi] patch1={patch1}, patch2={patch2}, Fallback={PatchTracker.FallbackPatchApproach}");
                }
            }

            // ★ debug 日志：SubtitleApi 初始化完成状态
            if (Plugin.Instance.DebugMode)
            {
                _logger.Debug($"[SubtitleApi] ✓ Initialize done - SubtitleResolver={( _subtitleResolver != null ? "OK" : "NULL")}, Fallback={PatchTracker.FallbackPatchApproach}");
            }
        }

        // --- 核心修改点 1: Stub 增加 LibraryOptions ---
        [HarmonyReversePatch]
        private static List<MediaStream> GetExternalSubtitleStreamsStub(object instance, BaseItem item, int startIndex,
            IDirectoryService directoryService, LibraryOptions libraryOptions, NamingOptions namingOptions, bool clearCache) =>
            throw new NotImplementedException();

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            switch (PatchTracker.FallbackPatchApproach)
            {
                // --- 核心修改点 2: 调用时传入 LibraryOptions ---
                case PatchApproach.Harmony:
                    try
                    {
                        var libraryOptions = _libraryManager.GetLibraryOptions(item);
                        return GetExternalSubtitleStreamsStub(_subtitleResolver, item, startIndex, directoryService,
                            libraryOptions, namingOptions, clearCache);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Harmony stub failed in GetExternalSubtitleStreams: {ex.Message}");
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache);
                    }
                    
                case PatchApproach.Reflection:
                    if (_subtitleResolver == null || _getExternalSubtitleStreams == null) return new List<MediaStream>();
                    try
                    {
                        var paramCount = _getExternalSubtitleStreams.GetParameters().Length;
                        object[] args;
                        
                        if (paramCount == 6)
                        {
                            var libraryOptions = _libraryManager.GetLibraryOptions(item);
                            args = new object[] { item, startIndex, directoryService, libraryOptions, namingOptions, clearCache };
                        }
                        else
                        {
                            args = new object[] { item, startIndex, directoryService, namingOptions, clearCache };
                        }
                        
                        var result = _getExternalSubtitleStreams.Invoke(_subtitleResolver, args);
                        return result as List<MediaStream> ?? new List<MediaStream>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed: {ex.Message}");
                        return new List<MediaStream>();
                    }
                    
                default:
                    return new List<MediaStream>();
            }
        }

        [HarmonyReversePatch]
        private static Task<bool> UpdateExternalSubtitleStreamStub(object instance, BaseItem item,
            MediaStream subtitleStream, MetadataRefreshOptions options, LibraryOptions libraryOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        private Task<bool> UpdateExternalSubtitleStream(BaseItem item, MediaStream subtitleStream,
            MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    try
                    {
                        return UpdateExternalSubtitleStreamStub(_ffProbeSubtitleInfo, item, subtitleStream, options,
                            libraryOptions, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Harmony stub failed in UpdateExternalSubtitleStream: {ex.Message}");
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return UpdateExternalSubtitleStream(item, subtitleStream, options, cancellationToken);
                    }
                    
                case PatchApproach.Reflection:
                    if (_ffProbeSubtitleInfo == null || _updateExternalSubtitleStream == null) return Task.FromResult(false);
                    try
                    {
                        var result = _updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                            new object[] { item, subtitleStream, options, libraryOptions, cancellationToken });
                        return result as Task<bool> ?? Task.FromResult(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed in UpdateExternalSubtitleStream: {ex.Message}");
                        return Task.FromResult(false);
                    }
                default:
                    return Task.FromResult(false);
            }
        }

        public MetadataRefreshOptions GetExternalSubtitleRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            var currentExternalSubtitleFiles = item.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal)
                .Select(s => s.Path)
                .ToArray();
            var currentSet = new HashSet<string>(currentExternalSubtitleFiles, StringComparer.Ordinal);

            try
            {
                var newExternalSubtitleFiles = GetExternalSubtitleStreams(item, 0, directoryService, clearCache)
                    .Select(i => i.Path)
                    .ToArray();
                var newSet = new HashSet<string>(newExternalSubtitleFiles, StringComparer.Ordinal);

                return !currentSet.SetEquals(newSet);
            }
            catch { return false; }
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i => !(i.IsExternal && i.Type == MediaStreamType.Subtitle && i.Protocol == MediaProtocol.File));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache) is { } externalSubtitleStreams)
            {
                foreach (var subtitleStream in externalSubtitleStreams)
                {
                    var extension = Path.GetExtension(subtitleStream.Path);
                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                if (IsLanguageNormalizeEnabled())
                {
                    foreach (var subtitleStream in externalSubtitleStreams)
                    {
                        NormalizeStreamTags(subtitleStream);
                    }
                }

                currentStreams.AddRange(externalSubtitleStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true, "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }

        #region External Audio Tracks

        /// <summary>
        /// 外挂音轨扩展名（与视频同目录同名前缀的独立音频文件）。
        /// </summary>
        /// <summary>
        /// 关闭开关时沿用的原始映射，保证「关掉 = 行为与升级前逐字节一致」。
        /// 开启开关后改由 <see cref="SubtitleLanguageNormalizer"/> 处理。
        /// </summary>
        private static readonly Dictionary<string, string> AudioLanguageAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "chs", "chi" }, { "sc", "chi" }, { "zho", "chi" }, { "zh", "chi" },
                { "cht", "chi" }, { "tc", "chi" },
                { "en", "eng" }, { "jp", "jpn" }, { "kr", "kor" }
            };

        private static readonly HashSet<string> AudioExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mka", ".m4a", ".ac3", ".eac3", ".dts", ".dtshd", ".thd", ".truehd",
                ".flac", ".opus", ".wav", ".aac", ".mp3"
            };

        /// <summary>
        /// 枚举与条目同目录、文件名同前缀（或条目名后缀式命名）的外挂音频文件。
        /// </summary>
        private List<MediaStream> GetExternalAudioStreams(BaseItem item, int startIndex)
        {
            var result = new List<MediaStream>();

            if (item.IsShortcut || !item.IsFileProtocol || string.IsNullOrEmpty(item.Path)) return result;

            var containingFolder = item.ContainingFolderPath;
            var namePrefix = Path.GetFileNameWithoutExtension(item.Path);

            if (string.IsNullOrEmpty(containingFolder) || !Directory.Exists(containingFolder)) return result;

            foreach (var file in Directory.EnumerateFiles(containingFolder, namePrefix + "*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(file);
                if (!AudioExtensions.Contains(extension)) continue;

                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase))
                    continue;

                // 前缀必须完整匹配条目名，后缀仅允许语言标记段（如 xxx.chs.mka）
                var stem = Path.GetFileNameWithoutExtension(file);
                var languageCode = string.Empty;

                if (!string.Equals(stem, namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (stem.StartsWith(namePrefix + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        languageCode = stem.Substring(namePrefix.Length + 1).Trim();
                        if (languageCode.Contains('.')) continue; // 多余段一律不认
                    }
                    else
                    {
                        continue;
                    }
                }

                var stream = new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    IsExternal = true,
                    Protocol = MediaProtocol.File,
                    Path = file,
                    Index = startIndex + result.Count,
                    SupportsExternalStream = true
                };

                if (!string.IsNullOrEmpty(languageCode))
                {
                    if (IsLanguageNormalizeEnabled())
                    {
                        // chs / cht 不再一律折成 chi 而丢失简繁信息；判不出来时退回原始段
                        var normalization = SubtitleLanguageNormalizer.Normalize(languageCode, null, null);
                        stream.Language = normalization.Language ?? languageCode.ToLowerInvariant();
                        stream.Title = normalization.Title ?? languageCode.ToUpperInvariant();
                    }
                    else
                    {
                        // 开关关闭：保持升级前的映射与标题写法，行为完全不变
                        stream.Language = AudioLanguageAliases.TryGetValue(languageCode, out var lang)
                            ? lang
                            : languageCode.ToLowerInvariant();
                        stream.Title = languageCode.ToUpperInvariant();
                    }
                }

                result.Add(stream);
            }

            return result;
        }

        public bool HasExternalAudioChanged(BaseItem item)
        {
            var current = item.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Audio && s.IsExternal)
                .Select(s => s.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<MediaStream> detected;
            try
            {
                detected = GetExternalAudioStreams(item, 0);
            }
            catch
            {
                return false;
            }

            var detectedPaths = detected.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return !current.SetEquals(detectedPaths);
        }

        /// <summary>
        /// 同步外挂音轨：移除已消失的旧外挂音轨，追加新发现的外挂音轨后整体保存。
        /// </summary>
        public void UpdateExternalAudioTracks(BaseItem item, bool persistMediaInfo)
        {
            var existingStreams = item.GetMediaStreams();

            var currentStreams = existingStreams
                .Where(i => !(i.IsExternal && i.Type == MediaStreamType.Audio && i.Protocol == MediaProtocol.File))
                .ToList();

            if (currentStreams.Count == existingStreams.Count && !GetExternalAudioStreams(item, 0).Any())
            {
                return;
            }

            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            var externalAudioStreams = GetExternalAudioStreams(item, startIndex);

            currentStreams.AddRange(externalAudioStreams);
            _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

            _logger.Info("ExternalAudio - {0} external audio track(s) saved for {1}",
                externalAudioStreams.Count, item.Name);

            if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
            {
                _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, null, true, "External Audio Update")
                    .ConfigureAwait(false);
            }
        }

        #endregion

        #region Track Language Normalize

        /// <summary>
        /// 开关读取。放在静态方法里，便于任务与补丁路径共用同一判定。
        /// </summary>
        internal static bool IsLanguageNormalizeEnabled()
        {
            try
            {
                return Plugin.Instance?.ExperienceEnhanceStore?.GetOptions()?.Playback?.NormalizeTrackLanguage == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 就地改写单个外挂轨道的语言标签。判定不出来或与现状一致时不动并返回 false。
        /// 只处理外挂轨道：内嵌轨道的语言来自 ffprobe，通常比文件名可靠，不应被覆盖。
        /// </summary>
        internal static bool NormalizeStreamTags(MediaStream stream)
        {
            if (stream == null || string.IsNullOrEmpty(stream.Path)) return false;

            var normalization = SubtitleLanguageNormalizer.Normalize(stream.Path, stream.Language, stream.Title,
                stream.IsForced, stream.IsHearingImpaired);

            if (!normalization.HasChange) return false;

            stream.Language = normalization.Language;
            stream.Title = normalization.Title;
            if (normalization.IsForced.HasValue) stream.IsForced = normalization.IsForced.Value;
            if (normalization.IsHearingImpaired.HasValue) stream.IsHearingImpaired = normalization.IsHearingImpaired.Value;

            return true;
        }

        /// <summary>
        /// 规范化一个条目上全部外挂音轨与字幕，返回实际修正的轨道数。
        /// 仅在确有变化时落库，并按需同步重写 *-mediainfo.json——
        /// 否则 RestoreMediaInfoTask 会用旧 json 把修正覆盖回去。
        /// </summary>
        public int NormalizeTrackLanguages(BaseItem item, bool persistMediaInfo)
        {
            if (item == null) return 0;
            if (!IsLanguageNormalizeEnabled()) return 0;

            var streams = item.GetMediaStreams();
            if (streams == null || streams.Count == 0) return 0;

            var changed = 0;
            foreach (var stream in streams)
            {
                if (!stream.IsExternal) continue;
                if (stream.Type != MediaStreamType.Audio && stream.Type != MediaStreamType.Subtitle) continue;
                if (NormalizeStreamTags(stream)) changed++;
            }

            if (changed == 0) return 0;

            _itemRepository.SaveMediaStreams(item.InternalId, streams, CancellationToken.None);

            if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
            {
                _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, null, true, "Track Language Normalize")
                    .ConfigureAwait(false);
            }

            return changed;
        }

        #endregion
    }
}