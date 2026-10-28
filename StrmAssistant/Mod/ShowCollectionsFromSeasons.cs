using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using StrmAssistant.Options;
using System;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 剧集页显示合集时同时考虑季的合集归属。
    ///
    /// 挂钩 <c>Emby.Api.UserLibrary.ItemsService.GetQueryResult(GetItems, ...)</c>（项查询入口）。
    /// 当客户端以 <c>IncludeItemTypes=BoxSet</c>、<c>Limit=12</c>、<c>ListItemIds=单个 id</c>
    /// 请求"某条目所属合集"时：若该 id 是剧集，则把其**季的 InternalId 追加进 ListItemIds**，
    /// 使"包含任一季度"（而非仅整剧）的合集也能在剧集页显示。
    ///
    /// 仅当选项开启时打补丁；默认 OFF = 零行为变化。方法/类型缺失时静默跳过。
    /// </summary>
    public class ShowCollectionsFromSeasons : PatchBase<ShowCollectionsFromSeasons>
    {
        // 与 Emby 客户端"合集"区域的请求特征一致
        private const int CollectionsLimit = 12;
        private const string BoxSetItemType = "BoxSet";

        private static MethodInfo _getQueryResult;
        private static Type _getItemsRequestType;

        public ShowCollectionsFromSeasons()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().ItemDisplay.ShowCollectionsFromSeasons)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                var embyApi = Assembly.Load("Emby.Api");
                _getItemsRequestType = embyApi.GetType("Emby.Api.UserLibrary.GetItems");

                var itemsService = embyApi.GetType("Emby.Api.UserLibrary.ItemsService");
                _getQueryResult = itemsService?
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "GetQueryResult");

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"ShowCollectionsFromSeasons: GetQueryResult={_getQueryResult != null}, " +
                        $"GetItemsType={_getItemsRequestType != null}");
                }

                if (_getQueryResult == null || _getItemsRequestType == null)
                {
                    Plugin.Instance.Logger.Warn(
                        "ShowCollectionsFromSeasons - target not found; feature inactive on this Emby build");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("ShowCollectionsFromSeasons init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getQueryResult, prefix: nameof(GetQueryResultPrefix));
        }

        [HarmonyPrefix]
        private static void GetQueryResultPrefix(object request, object user)
        {
            try
            {
                if (request == null || _getItemsRequestType == null) return;
                if (!_getItemsRequestType.IsInstanceOfType(request)) return;

                var reqType = request.GetType();

                var limit = reqType.GetProperty("Limit")?.GetValue(request) as int?;
                if (limit != CollectionsLimit) return;

                var includeTypes = reqType.GetProperty("IncludeItemTypes")?.GetValue(request) as string;
                if (!string.Equals(includeTypes, BoxSetItemType, StringComparison.OrdinalIgnoreCase)) return;

                var listItemIds = reqType.GetProperty("ListItemIds")?.GetValue(request) as string;
                if (string.IsNullOrEmpty(listItemIds)) return;

                var ids = listItemIds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length != 1) return;

                if (!(Plugin.LibraryManager.GetItemById(ids[0]) is Series series)) return;

                var seasonIds = series.GetChildren(user as User)
                    .Where(c => c is Season)
                    .Select(c => c.InternalId.ToString())
                    .ToList();

                if (seasonIds.Count == 0) return;

                reqType.GetProperty("ListItemIds")
                    ?.SetValue(request, ids[0] + "," + string.Join(",", seasonIds));

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"ShowCollectionsFromSeasons - series {ids[0]} +{seasonIds.Count} season id(s) appended");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("ShowCollectionsFromSeasons prefix failed: " + ex.Message);
            }
        }
    }
}
