using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Data;
using StrmAssistant.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 各媒体库内记录播放进度（对应 PRO 的 LibraryScopePlayProgress）。
    ///
    /// 挂载点（双重覆盖，靠内容幂等去重）：
    /// 1. <c>BaseItem.GetUserDataKey(IDataContext)</c> —— 4.9 起的**非虚入口**；
    /// 2. <c>Video / Episode / Season / Series</c> 的 <c>GetUserDataKeyInternal</c> 虚实现。
    ///
    /// 为什么两套都要：入口方法体极小（一次 SupportsUserData 判断 + 一次虚调用），
    /// JIT 很可能把它内联进 UserDataManager 等调用方，此时仅打入口补丁会出现
    /// 「补丁已应用、key 却没变」（已在真机复现）。虚实现方法体较大、不会被内联，
    /// 作为主路径；入口补丁作为兜底。两者都对同一 key 加前缀，靠 <see cref="UserDataScope.Apply"/>
    /// 的 StartsWith 判重保证只加一次。
    ///
    /// 生效方式：**重启生效**（补丁在启动时应用，避免已 JIT 的调用点漏掉）。
    /// </summary>
    public class LibraryScopePlayProgress : PatchBase<LibraryScopePlayProgress>
    {
        private static readonly List<MethodInfo> Targets = new List<MethodInfo>();

        // item → 顶层媒体库 InternalId。GetCollectionFolders 会走库管理器，
        // 「继续观看」列表逐行计算 key 时不做缓存会明显拖慢。
        // 条目可能被移动到另一个库；缓存不能永久有效，否则会继续用旧库前缀。
        private sealed class FolderCacheEntry
        {
            public long LibraryId;
            public DateTime CachedUtc;
        }

        private static readonly TimeSpan FolderCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<long, FolderCacheEntry> FolderCache =
            new ConcurrentDictionary<long, FolderCacheEntry>();

        private static int _diagCount;

        public LibraryScopePlayProgress()
        {
            Initialize();

            if (Targets.Count > 0 && PatchTracker.IsSupported &&
                Plugin.Instance.ExperienceEnhanceStore.GetOptions().Playback.LibraryScopePlayProgress)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var wrapper = typeof(BaseItem)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetUserDataKey" &&
                                     m.ReturnType == typeof(string) &&
                                     m.GetParameters().Length == 1);

            if (wrapper != null) Targets.Add(wrapper);

            foreach (var type in new[] { typeof(Video), typeof(Episode), typeof(Season), typeof(Series) })
            {
                var internalMethod = type
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetUserDataKeyInternal" &&
                                         m.ReturnType == typeof(string) &&
                                         m.GetParameters().Length == 1);

                if (internalMethod != null) Targets.Add(internalMethod);
            }

            if (Targets.Count == 0)
            {
                Plugin.Instance.Logger.Warn(
                    "LibraryScopePlayProgress - no GetUserDataKey target found; feature disabled");
                PatchTracker.AddError("GetUserDataKey targets not found");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                return;
            }

            Plugin.Instance.Logger.Info(
                $"LibraryScopePlayProgress targets: {string.Join(", ", Targets.Select(t => t.DeclaringType?.Name))}");
        }

        protected override void Prepare(bool apply)
        {
            foreach (var target in Targets)
            {
                PatchUnpatch(PatchTracker, apply, target, postfix: nameof(GetUserDataKeyPostfix));
            }
        }

        [HarmonyPostfix]
        private static void GetUserDataKeyPostfix(BaseItem __instance, object[] __args, ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(__result) || __instance == null) return;
                if (!Plugin.Instance.ExperienceEnhanceStore.GetOptions().Playback.LibraryScopePlayProgress) return;

                // 只处理会承载播放进度的媒体条目；文件夹/人物等保持原 key
                if (!(__instance is Video) && !(__instance is Episode) &&
                    !(__instance is Season) && !(__instance is Series)) return;

                __result = UserDataScope.Apply(__result,
                    ResolveLibraryId(__instance, __args != null && __args.Length > 0 ? __args[0] as IDataContext : null));
            }
            catch
            {
                // 任何异常都保留原 key：进度键算错比"不分离"严重得多。
            }
        }

        private static long? ResolveLibraryId(BaseItem item, IDataContext dataContext)
        {
            if (FolderCache.TryGetValue(item.InternalId, out var cached))
            {
                if (DateTime.UtcNow - cached.CachedUtc <= FolderCacheTtl) return cached.LibraryId;
                FolderCache.TryRemove(item.InternalId, out _);
            }

            var folders = dataContext != null
                ? BaseItem.LibraryManager.GetCollectionFolders(item, dataContext, CancellationToken.None)
                : BaseItem.LibraryManager.GetCollectionFolders(item, CancellationToken.None);

            var library = folders?
                .OrderBy(f => f.InternalId)
                .FirstOrDefault();

            if (library == null)
            {
                if (Interlocked.Increment(ref _diagCount) <= 5)
                {
                    Plugin.Instance.Logger.Info(
                        $"LibraryScopePlayProgress - no collection folder for {item.Name} ({item.GetType().Name}), " +
                        $"folders={(folders?.Length ?? 0)}, dataContext={(dataContext != null)}");
                }

                return null;
            }

            FolderCache[item.InternalId] = new FolderCacheEntry
            {
                LibraryId = library.InternalId,
                CachedUtc = DateTime.UtcNow
            };
            return library.InternalId;
        }

        /// <summary>开关变化或条目移动媒体库后清空映射。</summary>
        internal static void ClearCache() => FolderCache.Clear();
    }
}
