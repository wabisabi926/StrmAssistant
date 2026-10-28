using HarmonyLib;
using StrmAssistant.Common;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 强制用户显示偏好（<c>SeriesDisplay</c> 分支）：
    /// 把「首位管理员」的季集显示样式（Emby DisplayPreferences 自定义项 <c>seriesDisplay</c>）
    /// 强制应用到其他用户。
    ///
    /// 两个挂钩点（均在 <c>Emby.Server.Implementations.Library.UserManager</c>）：
    ///   1. <c>UpdatePartialUserSettings(long userId, Dictionary&lt;string,string&gt; values)</c> 前缀：
    ///      当首位管理员保存偏好时，捕获其 <c>seriesDisplay</c> 值。
    ///   2. <c>GetUserSettings(long userId, CancellationToken)</c> 后缀：
    ///      对非管理员用户，把捕获到的值注入返回的设置字典（**只改内存返回值，不落库**，避免误写用户偏好）。
    ///
    /// 仅当 <c>EnforceUserPreferencesScope</c> 含 <c>SeriesDisplay</c> 时才打补丁；默认 OFF 零变化。
    /// 注：LibraryOrder 分支由既有的 EnforceLibraryOrder 模块负责。
    /// </summary>
    public class EnforceUserPreferences : PatchBase<EnforceUserPreferences>
    {
        private const string SeriesDisplayKey = "seriesDisplay";

        private static MethodInfo _getUserSettings;
        private static MethodInfo _updatePartialUserSettings;

        private static string _enforcedSeriesDisplay;

        public EnforceUserPreferences()
        {
            Initialize();

            if (IsSeriesDisplayEnabled()) Patch();
        }

        private static string Scope =>
            Plugin.Instance.ExperienceEnhanceStore.GetOptions().LibraryOrder.EnforceUserPreferencesScope;

        private static bool IsSeriesDisplayEnabled() =>
            Scope?.IndexOf(nameof(LibraryOrderOptions.EnforceUserDisplayOption.SeriesDisplay),
                StringComparison.OrdinalIgnoreCase) >= 0;

        protected override void OnInitialize()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var userManager = assembly.GetType("Emby.Server.Implementations.Library.UserManager");

                _getUserSettings = userManager?.GetMethod("GetUserSettings",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(long), typeof(System.Threading.CancellationToken) }, null);

                _updatePartialUserSettings = userManager?.GetMethod("UpdatePartialUserSettings",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(long), typeof(Dictionary<string, string>) }, null);

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"EnforceUserPreferences: GetUserSettings={_getUserSettings != null}, " +
                        $"UpdatePartialUserSettings={_updatePartialUserSettings != null}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("EnforceUserPreferences init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_getUserSettings != null)
                PatchUnpatch(PatchTracker, apply, _getUserSettings, postfix: nameof(GetUserSettingsPostfix));

            if (_updatePartialUserSettings != null)
                PatchUnpatch(PatchTracker, apply, _updatePartialUserSettings,
                    prefix: nameof(UpdatePartialUserSettingsPrefix));
        }

        /// <summary>拦截首位管理员的偏好保存，记录其季集显示样式。</summary>
        [HarmonyPrefix]
        private static void UpdatePartialUserSettingsPrefix(long userId, Dictionary<string, string> values)
        {
            try
            {
                if (values == null) return;
                if (LibraryApi.FirstAdminInternalId == 0 || userId != LibraryApi.FirstAdminInternalId) return;
                if (!values.TryGetValue(SeriesDisplayKey, out var value) || string.IsNullOrEmpty(value)) return;

                _enforcedSeriesDisplay = value;
                Plugin.Instance.Logger.Info($"EnforceUserPreferences - captured admin seriesDisplay: {value}");
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>对非管理员用户注入被强制的季集显示样式（仅内存返回值）。</summary>
        [HarmonyPostfix]
        private static void GetUserSettingsPostfix(long userId, Dictionary<string, string> __result)
        {
            try
            {
                if (__result == null || string.IsNullOrEmpty(_enforcedSeriesDisplay)) return;
                if (LibraryApi.FirstAdminInternalId != 0 && userId == LibraryApi.FirstAdminInternalId) return;

                if (!string.Equals(__result.TryGetValue(SeriesDisplayKey, out var current) ? current : null,
                        _enforcedSeriesDisplay, StringComparison.Ordinal))
                {
                    __result[SeriesDisplayKey] = _enforcedSeriesDisplay;

                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug(
                            $"EnforceUserPreferences - enforced seriesDisplay={_enforcedSeriesDisplay} for user {userId}");
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }
    }
}
