using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 媒体项目显示顺序优化（<c>OptimizeItemOrderPreferences</c> 四选）：
    ///   - NaturalSort                    → 由 PinyinSortName 的 SortName 数字补零实现（本模块不处理）
    ///   - ReverseSeasonOrder             → 剧集页「季」倒序（Series.SetAutoSorting 后翻转 IndexNumber）
    ///   - ReverseEpisodeOrder            → 季页「集」倒序（Season 继承 Folder.SetAutoSorting，翻转 IndexNumber）
    ///   - CollectionPerItemDateCreated   → 合集内条目按创建日期排序（BoxSet.SetSorting 后改写 OrderBy）
    ///
    /// 仅当至少选中一个本模块负责的选项时才打补丁；默认空 = 零行为变化。
    /// 挂钩点均为 Emby 4.10 的既有实现，方法缺失时逐项跳过（不影响其它模块）。
    /// </summary>
    public class OptimizeItemOrder : PatchBase<OptimizeItemOrder>
    {
        private static MethodInfo _seriesSetAutoSorting;
        private static MethodInfo _folderSetAutoSorting;
        private static MethodInfo _boxSetSetSorting;

        public OptimizeItemOrder()
        {
            Initialize();

            if (IsModuleEnabled()) Patch();
        }

        private static string Preferences =>
            Plugin.Instance.ExperienceEnhanceStore.GetOptions().LibraryOrder.OptimizeItemOrderPreferences;

        private static bool Has(string option) =>
            Preferences?.IndexOf(option, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>本模块负责的 3 个选项是否有任一被选中（NaturalSort 由 PinyinSortName 处理）。</summary>
        private static bool IsModuleEnabled() =>
            Has(nameof(LibraryOrderOptions.ItemOrderOption.ReverseSeasonOrder)) ||
            Has(nameof(LibraryOrderOptions.ItemOrderOption.ReverseEpisodeOrder)) ||
            Has(nameof(LibraryOrderOptions.ItemOrderOption.CollectionPerItemDateCreated));

        protected override void OnInitialize()
        {
            try
            {
                var autoSortSig = new[] { typeof(InternalItemsQuery), typeof(List<(string, SortOrder)>) };

                _seriesSetAutoSorting = typeof(Series).GetMethod("SetAutoSorting",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, autoSortSig, null);

                _folderSetAutoSorting = typeof(Folder).GetMethod("SetAutoSorting",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, autoSortSig, null);

                _boxSetSetSorting = typeof(BoxSet).GetMethod("SetSorting",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(InternalItemsQuery) }, null);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"OptimizeItemOrder: Series.SetAutoSorting={_seriesSetAutoSorting != null}, " +
                        $"Folder.SetAutoSorting={_folderSetAutoSorting != null}, BoxSet.SetSorting={_boxSetSetSorting != null}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("OptimizeItemOrder init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            // 逐项判空：PatchUnpatch 对 null 目标会把整个 tracker 置为 None
            if (_seriesSetAutoSorting != null)
                PatchUnpatch(PatchTracker, apply, _seriesSetAutoSorting, postfix: nameof(SetAutoSortingPostfix));

            if (_folderSetAutoSorting != null)
                PatchUnpatch(PatchTracker, apply, _folderSetAutoSorting, postfix: nameof(SetAutoSortingPostfix));

            if (_boxSetSetSorting != null)
                PatchUnpatch(PatchTracker, apply, _boxSetSetSorting, postfix: nameof(SetSortingPostfix));
        }

        [HarmonyPostfix]
        private static void SetAutoSortingPostfix(object __instance, InternalItemsQuery query,
            List<(string, SortOrder)> orderBy)
        {
            try
            {
                if (orderBy == null) return;

                if (__instance is Series)
                {
                    // 非递归 = 季列表
                    if (!query.Recursive && Has(nameof(LibraryOrderOptions.ItemOrderOption.ReverseSeasonOrder)))
                        FlipToDescending(orderBy, ItemSortBy.IndexNumber);
                }
                else if (__instance is Season)
                {
                    if (Has(nameof(LibraryOrderOptions.ItemOrderOption.ReverseEpisodeOrder)))
                        FlipToDescending(orderBy, ItemSortBy.IndexNumber);
                }
            }
            catch
            {
                // 排序钩子失败不影响主流程
            }
        }

        [HarmonyPostfix]
        private static void SetSortingPostfix(InternalItemsQuery query)
        {
            try
            {
                if (query == null) return;
                if (!Has(nameof(LibraryOrderOptions.ItemOrderOption.CollectionPerItemDateCreated))) return;

                // 合集条目按创建日期升序（需求未规定方向，这里取“创建时间顺序”）
                query.OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Ascending) };
            }
            catch
            {
                // 同上
            }
        }

        private static void FlipToDescending(List<(string, SortOrder)> orderBy, string field)
        {
            for (var i = 0; i < orderBy.Count; i++)
            {
                if (string.Equals(orderBy[i].Item1, field, StringComparison.OrdinalIgnoreCase) &&
                    orderBy[i].Item2 != SortOrder.Descending)
                {
                    orderBy[i] = (orderBy[i].Item1, SortOrder.Descending);
                }
            }
        }
    }
}
