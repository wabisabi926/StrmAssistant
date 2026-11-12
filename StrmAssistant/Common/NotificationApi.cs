using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class NotificationApi
    {
        private readonly ILogger _logger;
        private readonly INotificationManager _notificationManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;

        public NotificationApi(INotificationManager notificationManager, IUserManager userManager, ISessionManager sessionManager)
        {
            _logger = Plugin.Instance.Logger;
            _notificationManager = notificationManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
        }

        public void FavoritesUpdateSendNotification(BaseItem item)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var users = Plugin.LibraryApi.GetUsersByFavorites(item);
            foreach (var user in users)
            {
                var request = new NotificationRequest
                {
                    Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    EventId = "favorites.update",
                    User = user,
                    Item = item,
                    Description =
                        string.Format(
                            Resources.Notification_CatchupUpdate_EventDescription.Replace("\\n",
                                Environment.NewLine), item.Path, user)
                };
                _notificationManager.SendNotification(request);
            }
        }

        public void DeepDeleteSendNotification(BaseItem item, User user, HashSet<string> mountPaths)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var mountPathList = string.Join(Environment.NewLine, mountPaths);

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_DeepDelete_EventName,
                EventId = "deep.delete",
                User = user,
                Item = item,
                Description =
                    string.Format(
                        Resources.Notification_DeepDelete_EventDescription.Replace("\\n", Environment.NewLine),
                        item.Name, item.Path, mountPathList)
            };

            _notificationManager.SendNotification(request);
        }

        public async Task IntroUpdateSendNotification(Episode episode, SessionInfo session, string introStartTime,
            string introEndTime)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_IntroUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title =
                    Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = "introskip.update",
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_IntroUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), introStartTime, introEndTime,
                    session.UserName)
            };
            _notificationManager.SendNotification(request);
        }

        public async Task CreditsUpdateSendNotification(Episode episode, SessionInfo session, string creditsDuration)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_CreditsUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title =
                    Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = "introskip.update",
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_CreditsUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), creditsDuration, session.UserName)

            };
            _notificationManager.SendNotification(request);
        }

        /// <summary>
        /// 元数据更新通知（metadata.update）：发给管理员。
        /// </summary>
        public void MetadataUpdateSendNotification(BaseItem item, string description)
        {
            SendAdminItemNotification("metadata.update", item, description);
        }

        /// <summary>
        /// 图像更新通知（image.update）：发给管理员。
        /// </summary>
        public void ImageUpdateSendNotification(BaseItem item)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;
            SendAdminItemNotification("image.update", item,
                string.Format(Resources.Notification_ImageUpdate_EventDescription.Replace("\\n", Environment.NewLine),
                    item.Name, item.Path));
        }

        /// <summary>
        /// 合集变更通知（boxset.update）：发给管理员。
        /// </summary>
        public void BoxsetUpdateSendNotification(BaseItem boxset, BaseItem changedItem, bool added)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;
            SendAdminItemNotification("boxset.update", boxset,
                string.Format(Resources.Notification_BoxsetUpdate_EventDescription.Replace("\\n", Environment.NewLine),
                    boxset.Name, changedItem.Name, added ? "+" : "-"));
        }

        private void SendAdminItemNotification(string eventId, BaseItem item, string description)
        {
            try
            {
                Resources.Culture = Thread.CurrentThread.CurrentUICulture;

                var admins = LibraryApi.AllUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();

                foreach (var admin in admins)
                {
                    var request = new NotificationRequest
                    {
                        Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                        EventId = eventId,
                        User = admin,
                        Item = item,
                        Description = description
                    };
                    _notificationManager.SendNotification(request);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Notification " + eventId + " failed: " + ex.Message);
            }
        }

        public async Task SendMessageToAdmins(string text, long? timeout)
        {
            var message = new MessageCommand
            {
                Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                Text = text,
                TimeoutMs = timeout
            };

            var admins = LibraryApi.AllUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key);
            var sessions = _sessionManager.Sessions.Where(CanDisplayMessage)
                .Where(s => admins.Any(u => s.ContainsUser(u.InternalId)));

            foreach (var session in sessions)
            {
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }
        }

        private bool CanDisplayMessage(SessionInfo session)
        {
            return session.SupportedCommands.Contains("DisplayMessage");
        }
    }
}
