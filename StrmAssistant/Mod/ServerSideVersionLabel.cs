using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 多版本美化 · 服务端标签。
    ///
    /// 网页端的版本角标由 <c>strmassistant.js</c> 在 DOM 上渲染，只有浏览器能看到；
    /// Kodi、Apple TV、手机 App 的版本下拉读的是服务端下发的
    /// <see cref="MediaSourceInfo.Name"/>（默认值 = 去掉扩展名的文件名）。
    /// 这里在 <c>MediaSourceManager.GetStaticMediaSources</c> 出参上改写该字段，
    /// 让所有客户端看到同一套标签。
    ///
    /// 安全性：
    /// - 只改 <c>Name</c>，**不改 <c>Path</c>** —— Path 参与条目身份与播放，动了会出大问题。
    /// - 只在「同一部片有 2 个及以上版本」时生效：单版本没有版本下拉，改了纯属副作用。
    /// - 出参对象由 <c>BaseItem.GetVersionInfo</c> 每次新建，不是条目缓存里的同一引用，
    ///   因此改写不会污染库缓存。
    /// - 任何异常都吞掉并保留原值，绝不能因为打标签让详情页 500。
    /// </summary>
    public class ServerSideVersionLabel : PatchBase<ServerSideVersionLabel>
    {
        private static MethodInfo _target;

        public ServerSideVersionLabel()
        {
            Initialize();

            if (IsEnabled()) Patch();
        }

        private static bool IsEnabled()
        {
            var display = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Display;
            return display.EnableMultiVersionEnhance && display.EnableServerSideVersionLabel;
        }

        protected override void OnInitialize()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var manager = assembly.GetType("Emby.Server.Implementations.Library.MediaSourceManager");

                // 只取最内层重载（10 参）：其余重载都会转到它，打一个点即可覆盖 DTO 与 PlaybackInfo 两条路径
                _target = manager?
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetStaticMediaSources" && m.GetParameters().Length == 10);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn($"ServerSideVersionLabel - resolve target failed: {e.Message}");
            }

            if (_target == null)
            {
                Plugin.Instance.Logger.Warn(
                    "ServerSideVersionLabel - MediaSourceManager.GetStaticMediaSources(10 params) not found");

                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _target, postfix: nameof(GetStaticMediaSourcesPostfix));
        }

        [HarmonyPostfix]
        private static void GetStaticMediaSourcesPostfix(BaseItem __0, List<MediaSourceInfo> __result)
        {
            try
            {
                if (__result == null || __result.Count < 2) return;
                if (!(__0 is Video)) return;

                var display = Plugin.Instance.ExperienceEnhanceStore.GetOptions().Display;
                if (!display.EnableMultiVersionEnhance || !display.EnableServerSideVersionLabel) return;

                foreach (var source in __result)
                {
                    var name = VersionLabelBuilder.BuildSourceName(
                        source, display.MultiVersionDisplayOption, includeHdr: true, includeAudio: true);

                    if (!string.IsNullOrEmpty(name)) source.Name = name;
                }
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"ServerSideVersionLabel - postfix skipped: {e.Message}");
                }
            }
        }
    }
}
