using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using System;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 标记写入后通知客户端刷新条目。
    ///
    /// Emby 的 SaveChapters 只写 Chapters3，不更新条目、不触发 LibraryChanged；
    /// Emby for Kodi 因此要等下一次库同步才看到新的片头/片尾标记。这里用一次
    /// MetadataEdit 更新触发 LibraryChanged，同时用 AsyncLocal 让本插件的
    /// 元数据更新通知跳过这次内部更新，避免刷屏。
    /// </summary>
    internal static class IntroMarkerNotifier
    {
        private static readonly AsyncLocal<bool> MarkerUpdate = new AsyncLocal<bool>();

        public static bool IsMarkerUpdate => MarkerUpdate.Value;

        public static void Notify(BaseItem item)
        {
            if (item == null) return;

            try
            {
                MarkerUpdate.Value = true;
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("IntroMarkerNotifier - notify failed: " + e.Message);
            }
            finally
            {
                MarkerUpdate.Value = false;
            }
        }
    }
}
