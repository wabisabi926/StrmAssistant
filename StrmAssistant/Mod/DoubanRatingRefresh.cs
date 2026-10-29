using HarmonyLib;
using System;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 详情页按需刷新豆瓣评分：补丁 Emby 的单条目详情接口（Web / App / TV 共用），
    /// 使「打开过详情页」的条目在冷却到期后于后台更新豆瓣分，不再只依赖播放。
    /// Kodi 库视图读取本地数据库、不发该请求，由 <see cref="Plugin.OnPlaybackStart"/> 兜底。
    /// </summary>
    public class DoubanRatingRefresh : PatchBase<DoubanRatingRefresh>
    {
        private static MethodInfo _getItem;
        private static PropertyInfo _getItemId;

        public DoubanRatingRefresh()
        {
            Initialize();

            // 常驻补丁：内部只做廉价判定，豆瓣辅助关闭时立即返回
            Patch();
        }

        protected override void OnInitialize()
        {
            var serviceType = AccessTools.TypeByName("Emby.Api.UserLibrary.UserLibraryService") ??
                              LoadApiType("Emby.Api.UserLibrary.UserLibraryService");
            var requestType = AccessTools.TypeByName("Emby.Api.UserLibrary.GetItem") ??
                              LoadApiType("Emby.Api.UserLibrary.GetItem");

            if (serviceType == null || requestType == null)
            {
                Plugin.Instance.Logger.Warn(
                    "DoubanRatingRefresh: Emby.Api types not found, detail-page refresh unavailable");
                return;
            }

            _getItem = serviceType.GetMethod("Get", new[] { requestType });
            _getItemId = requestType.GetProperty("Id");

            if (_getItem == null || _getItemId == null)
            {
                Plugin.Instance.Logger.Warn(
                    "DoubanRatingRefresh: UserLibraryService.Get(GetItem) not found, detail-page refresh unavailable");
            }
        }

        /// <summary>Emby.Api 在插件初始化阶段可能尚未加载，按需触发一次加载。</summary>
        private static Type LoadApiType(string typeName)
        {
            try
            {
                return Assembly.Load("Emby.Api").GetType(typeName);
            }
            catch
            {
                return null;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getItem, postfix: nameof(GetItemPostfix));
        }

        [HarmonyPostfix]
        private static void GetItemPostfix(object __0)
        {
            try
            {
                if (Plugin.DoubanAssist == null || !Plugin.IsPluginEnabled) return;

                if (!(_getItemId?.GetValue(__0) is string id) || !long.TryParse(id, out var itemId)) return;

                var item = Plugin.LibraryManager.GetItemById(itemId);
                if (item != null) Plugin.DoubanAssist.TryScheduleRatingRefresh(item, "DetailPage");
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode) Plugin.Instance.Logger.Debug($"DoubanRatingRefresh: {e.Message}");
            }
        }
    }
}
