using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 追剧体验：当前集播放进度达到阈值时，自动把同剧前几集标记为该用户已看。
    /// </summary>
    public class AutoMarkWatched
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, byte> _markedSessions =
            new ConcurrentDictionary<string, byte>();

        public AutoMarkWatched(ILibraryManager libraryManager, IUserManager userManager,
            ISessionManager sessionManager, IUserDataManager userDataManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
        }

        public void Initialize()
        {
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _logger.Info("AutoMarkWatched - Initialized");
        }

        public void Dispose()
        {
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            HandlePlayback(e.Item, e.PlaybackPositionTicks, e.Session, e.PlaySessionId);
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            // 部分客户端（如 AndroidTv 的「Unnamed item」会话）上报的 PlaySessionId 为空，
            // ConcurrentDictionary 不接受 null 键，直接 TryRemove 会抛 ArgumentNullException。
            if (!string.IsNullOrEmpty(e.PlaySessionId))
            {
                _markedSessions.TryRemove(e.PlaySessionId, out _);
            }

            HandlePlayback(e.Item, e.PlaybackPositionTicks, e.Session, e.PlaySessionId);
        }

        private void HandlePlayback(BaseItem item, long? positionTicks, SessionInfo session, string playSessionId)
        {
            if (!(item is Episode episode) || !positionTicks.HasValue || !episode.RunTimeTicks.HasValue ||
                string.IsNullOrEmpty(playSessionId) || string.IsNullOrEmpty(session?.UserId))
                return;

            if (_markedSessions.ContainsKey(playSessionId)) return;

            var options = Plugin.Instance.ExperienceEnhanceStore.GetOptions();
            var threshold = Math.Min(Math.Max(options.Playback.AutoMarkWatchedThreshold, 5), 100);

            if (positionTicks.Value * 100.0 / episode.RunTimeTicks.Value < threshold) return;

            if (!_markedSessions.TryAdd(playSessionId, 0)) return;

            var user = _userManager.GetUserById(session.UserId);

            if (user == null) return;

            try
            {
                var marked = MarkPreviousEpisodesWatched(episode, user, options.Playback.AutoMarkWatchedExcludeFavorites);

                if (marked > 0)
                {
                    _logger.Info(
                        "AutoMarkWatched - Marked {0} previous episode(s) as watched for user {1} since {2} S{3:00}E{4:00}",
                        marked, user.Name, episode.SeriesName, episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("AutoMarkWatched - Failed: " + ex.Message);
                _logger.Debug(ex.StackTrace);
            }
        }

        private int MarkPreviousEpisodesWatched(Episode episode, User user, bool excludeFavorites)
        {
            var series = episode.Series;
            if (series == null) return 0;

            var currentSeason = episode.ParentIndexNumber ?? 0;
            var currentIndex = episode.IndexNumber ?? 0;

            var previousEpisodes = series
                .GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { nameof(Episode) }
                })
                .OfType<Episode>()
                .Where(e => e.IndexNumber.HasValue)
                .OrderBy(e => e.ParentIndexNumber ?? 0)
                .ThenBy(e => e.IndexNumber ?? 0)
                .TakeWhile(e => (e.ParentIndexNumber ?? 0) < currentSeason ||
                                ((e.ParentIndexNumber ?? 0) == currentSeason && (e.IndexNumber ?? 0) < currentIndex))
                .ToList();

            var marked = 0;

            foreach (var previous in previousEpisodes)
            {
                var userData = _userDataManager.GetUserData(user, previous);

                if (userData.Played) continue;
                if (excludeFavorites && userData.IsFavorite) continue;

                userData.Played = true;
                userData.LastPlayedDate = DateTime.UtcNow;

                _userDataManager.SaveUserData(user, previous, userData, UserDataSaveReason.TogglePlayed,
                    CancellationToken.None);
                marked++;
            }

            return marked;
        }
    }
}
