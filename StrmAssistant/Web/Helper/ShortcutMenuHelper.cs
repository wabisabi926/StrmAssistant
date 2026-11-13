using MediaBrowser.Controller.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Web.Helper
{
    internal static class ShortcutMenuHelper
    {
        public static string ModifiedShortcutsString { get; private set; }

        /// <summary>
        /// modules/mediainfo/mediainfo.js 末尾追加徽章引导代码后的内容。
        /// shortcuts.js 只在右键菜单场景按需加载，详情页/列表页不会加载它，
        /// 而 mediainfo.js 被 cardbuilder.js 与 item/item.js 依赖，这两类页面都会加载。
        /// </summary>
        public static string ModifiedMediaInfoString { get; private set; }

        /// <summary>
        /// 磁盘上的原始文件内容（未注入）。这两个 URL 覆盖了 Emby 自带的静态文件路由，
        /// 插件停用或注入串生成失败时必须回退到这里；否则浏览器拿到的是 0 字节模块，
        /// cardbuilder.js 解析失败会导致首页与媒体库卡片整体渲染崩溃。
        /// </summary>
        public static string OriginalShortcutsString { get; private set; }

        public static string OriginalMediaInfoString { get; private set; }

        public static MemoryStream StrmAssistantJs { get; private set; }

        private static readonly string ShortcutsRelativePath = Path.Combine("modules", "shortcuts.js");

        private static readonly string MediaInfoRelativePath =
            Path.Combine("modules", "mediainfo", "mediainfo.js");

        private static IServerConfigurationManager _configurationManager;

        public static void Initialize(IServerConfigurationManager configurationManager)
        {
            try
            {
                _configurationManager = configurationManager;
                StrmAssistantJs = GetResourceStream("strmassistant.js");
                ModifyShortcutMenu(configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuHelper)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        /// <summary>
        /// UI 选项变更时重新生成注入字符串（读原文件幂等），下次前端请求即生效。
        /// </summary>
        public static void Refresh()
        {
            if (_configurationManager == null) return;
            try
            {
                ModifyShortcutMenu(_configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuHelper)} Refresh Failed");
                Plugin.Instance.Logger.Error(e.Message);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Web.Resources." + resourceName;
            var manifestResourceStream = typeof (ShortcutMenuHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int) manifestResourceStream.Length);
            manifestResourceStream.CopyTo((Stream) destination);
            return destination;
        }

        /// <summary>
        /// 解析 Emby Web 前端（dashboard-ui）根目录。
        /// </summary>
        public static string ResolveDashboardSourcePath(IServerConfigurationManager configurationManager)
        {
            if (configurationManager == null) return null;

            var customPath = configurationManager.Configuration?.DashboardSourcePath;
            if (!string.IsNullOrEmpty(customPath)) return customPath;

            return Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath, "dashboard-ui");
        }

        /// <summary>
        /// 返回下发给浏览器的 shortcuts.js。
        /// 注入版不可用时回退到磁盘原文——这两个端点覆盖了 Emby 自带静态文件路由，
        /// 「什么都不返回」等价于把 Emby 的前端模块打空，比不注入严重得多。
        /// </summary>
        public static string GetShortcutsJs(IServerConfigurationManager configurationManager, bool inject)
        {
            if (inject && !string.IsNullOrEmpty(ModifiedShortcutsString)) return ModifiedShortcutsString;

            return OriginalShortcutsString ?? LoadOriginal(configurationManager, ShortcutsRelativePath);
        }

        public static string GetMediaInfoJs(IServerConfigurationManager configurationManager, bool inject)
        {
            if (inject && !string.IsNullOrEmpty(ModifiedMediaInfoString)) return ModifiedMediaInfoString;

            return OriginalMediaInfoString ?? LoadOriginal(configurationManager, MediaInfoRelativePath);
        }

        /// <summary>
        /// 插件在启动时就被禁用时 <see cref="Initialize"/> 不会执行，原始内容尚未缓存，
        /// 这里按需读盘兜底，保证端点始终有内容可下发。
        /// </summary>
        private static string LoadOriginal(IServerConfigurationManager configurationManager, string relativePath)
        {
            if (configurationManager == null) return null;

            try
            {
                var dashboardSourcePath = ResolveDashboardSourcePath(configurationManager);
                if (string.IsNullOrEmpty(dashboardSourcePath)) return null;

                var path = Path.Combine(dashboardSourcePath, relativePath);
                if (!File.Exists(path)) return null;

                var text = File.ReadAllText(path);
                if (relativePath == ShortcutsRelativePath)
                {
                    OriginalShortcutsString = text;
                }
                else
                {
                    OriginalMediaInfoString = text;
                }

                return text;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Error(
                    $"{nameof(ShortcutMenuHelper)} - Failed to load original {relativePath}: {e.Message}");
                return null;
            }
        }

        private static void ModifyShortcutMenu(IServerConfigurationManager configurationManager)
        {
            var dashboardSourcePath = ResolveDashboardSourcePath(configurationManager);

            const string injectShortcutCommand = @"
const strmAssistantCommandSource = {
    getCommands: function(options) {
        const locale = this.globalize.getCurrentLocale().toLowerCase();
        const cjk = ['zh', 'ja', 'ko'].some(lang => locale.startsWith(lang));
        const lockCommandName = ({
            'zh-cn': '\u9501\u5B9A',
            'zh-hk': '\u9396\u5B9A',
            'zh-tw': '\u9396\u5B9A'
        }[locale] || 'Lock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const unlockCommandName = ({
            'zh-cn': '\u89E3\u9501',
            'zh-hk': '\u89E3\u9396',
            'zh-tw': '\u89E3\u9396'
        }[locale] || 'Unlock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const extractMediaInfoName = locale === 'zh-cn' ? '\u63D0\u53D6\u5A92\u4F53\u4FE1\u606F' :
            (['zh-hk', 'zh-tw'].includes(locale) ? '\u63D0\u53D6\u5A92\u9AD4\u8A0A\u606F' : 'Extract Media Info');
        const deleteMediaInfoName = locale === 'zh-cn' ? '\u5220\u9664\u5A92\u4F53\u4FE1\u606F' :
            (['zh-hk', 'zh-tw'].includes(locale) ? '\u522A\u9664\u5A92\u9AD4\u8A0A\u606F' : 'Delete Media Info');

        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType !== 'boxsets' && options.items[0].CollectionType !== 'playlists') {
            const commandName = (locale === 'zh-cn') ? '\u590D\u5236' : (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u8F38' : 'Copy');
            return [{ name: commandName, id: 'copy', icon: 'content_copy' }];
        }
        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType === 'boxsets') {
            return [{ name: this.globalize.translate('Remove'), id: 'remove', icon: 'remove_circle_outline' }];
        }
        if (options.items?.length === 1) {
            const result = [];
            if (options.items[0].Type === 'Movie') {
                result.push({ name: this.globalize.translate('HeaderScanLibraryFiles'), id: 'traverse', icon: 'refresh' });
            }
            if ((options.items[0].Type === 'Movie' || options.items[0].Type === 'Episode') &&
                 options.items[0].CanDelete && options.mediaSourceId && options.items[0].MediaSources.length > 1) {
                result.push({
                    name: cjk
                        ? this.globalize.translate('Delete') + this.globalize.translate('Version')
                        : this.globalize.translate('Delete') + ' ' + this.globalize.translate('Version'),
                    id: 'delver_' + options.mediaSourceId,
                    icon: 'remove'
                });
            }
            if (options.items[0].hasOwnProperty('LockData') && options.items[0].Type !== 'CollectionFolder' &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                if (options.items[0].LockData) {
                    result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
                } else {
                    result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                }
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                result.push({ name: commandName, id: 'clear_intro', icon: 'clear_all' });
            }
            if (['Movie', 'Episode', 'Series', 'Season'].includes(options.items[0].Type) &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
                result.push({ name: commandName, id: 'clear_chapter', icon: 'hide_image' });
            }
            if (['Movie', 'Episode', 'Series', 'Season'].includes(options.items[0].Type) &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                result.push({ name: extractMediaInfoName, id: 'extract_mediainfo', icon: 'manage_search' });
                result.push({ name: deleteMediaInfoName, id: 'delete_mediainfo', icon: 'delete' });
            }
            return result;
        }
        if (!options.multiSelect && options.items?.length > 1 && options.items[0].Type !== 'CollectionFolder' &&
            ((options.users && Object.values(options.users)[0]?.Policy.IsAdministrator) || false)) {
            const result = [];
            const types = [...new Set(options.items.map(item => item.Type))];
            const allMovies = types.length === 1 && types[0] === 'Movie';
            const allSeriesScope = types.every(t => t === 'Series' || t === 'Season');
            const allMediaScope = types.every(t => ['Movie', 'Episode', 'Series', 'Season'].includes(t));
            const allClearScope = types.every(t => ['Movie', 'Episode', 'Series', 'Season'].includes(t));

            if (allMovies) {
                result.push({ name: this.globalize.translate('HeaderScanLibraryFiles'), id: 'traverse', icon: 'refresh' });
            }
            if (allSeriesScope) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                result.push({ name: commandName, id: 'clear_intro', icon: 'clear_all' });
            }
            if (allClearScope) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
                result.push({ name: commandName, id: 'clear_chapter', icon: 'hide_image' });
            }
            if (allMediaScope) {
                result.push({ name: extractMediaInfoName, id: 'extract_mediainfo', icon: 'manage_search' });
                result.push({ name: deleteMediaInfoName, id: 'delete_mediainfo', icon: 'delete' });
            }
            result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
            result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            return result;
        }
        return [];
    },
    executeCommand: function(command, items) {
        if (!command || !items?.length) return;
        const actions = {
            copy: 'copy',
            remove: 'remove',
            lock: 'lock',
            unlock: 'unlock'
        };
        if (command.startsWith('delver_')) {
            const mediaSourceId = command.replace('delver_', '');
            const mediaSources = items[0].MediaSources || [];
            const matchingItem = mediaSources.find(source => source.Id === mediaSourceId);
            const itemId = matchingItem?.ItemId;
            const itemName = matchingItem?.Name;
            if (itemId && itemName) {
                return require(['components/strmassistant/strmassistant']).then(responses => {
                    return responses[0].delver(itemId, itemName, items[0].Type);
                });
            }
        }
        if (command === actions.lock || command === actions.unlock) {
            const lockData = command === actions.lock;
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const promises = items.map(item => responses[0].lock(item.Id, lockData));
                return Promise.all(promises);
            });
        }
        if (command === 'traverse') {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const parentIds = [...new Set(items.map(item => item.ParentId).filter(Boolean))];
                return Promise.all(parentIds.map(id => responses[0].traverse(id)));
            });
        }
        if (command === 'clear_intro' || command === 'clear_chapter' || command === 'clear_mediainfo') {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                return responses[0][command](items.map(item => item.Id));
            });
        }
        if (command === 'extract_mediainfo' || command === 'delete_mediainfo') {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                return responses[0][command](items.map(item => item.Id));
            });
        }
        if (actions[command]) {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                return responses[0][actions[command]](items[0].Id, items[0].Name);
            });
        }
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/globalize.js').then(globalize => {
        strmAssistantCommandSource.globalize = globalize;
        Emby.importModule('./modules/common/itemmanager/itemmanager.js').then(itemmanager => {
            itemmanager.registerCommandSource(strmAssistantCommandSource);
        });
    });
}, 3000);
    ";
            var dataExplorer2Assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Emby.DataExplorer2");

            OriginalShortcutsString = File.ReadAllText(Path.Combine(dashboardSourcePath, ShortcutsRelativePath));
            ModifiedShortcutsString = OriginalShortcutsString + injectShortcutCommand;

            const string injectDoubanBadgeBootstrap = @"
// StrmAssistant Douban badge bootstrap
// 版本日志：前端排障用——浏览器 console 里能直接确认加载的是哪次构建的脚本，
// 避免「部署了新 DLL 但页面仍驻留旧模块」时误判为功能失效。
// 整段包在 IIFE + try/catch 中：这段代码被追加在 Emby 模块文件末尾，
// 一旦它抛出未捕获异常，整个模块会被判定加载失败，连带卡片渲染一起崩。
(function () {
    try {
        console.log('[StrmAssistant] web ui script v' + 'SA_SCRIPT_VERSION');
        window.__saUI = { replaceUnplayedWithTotal: REPLACE_UNPLAYED_TOTAL, showLibraryItemCount: SHOW_LIB_COUNT, reverseLogLines: REVERSE_LOGS, blockSearchRecommend: BLOCK_SEARCH_RECS, showMediaInfo: SHOW_MEDIA_INFO };
        if (typeof require !== 'function') {
            console.warn('StrmAssistant: AMD require unavailable, skip UI bootstrap');
            return;
        }
        require(['components/strmassistant/strmassistant']).then(function (mods) {
            var mod = mods && mods[0];
            if (!mod || !mod.badges) { console.error('StrmAssistant: strmassistant module not loaded', mods); return; }
            try { mod.badges.init(); } catch (e) { console.error('StrmAssistant: badges init failed', e); }
            if (mod.versions) { try { mod.versions.init(); } catch (e) { console.error('StrmAssistant: versions init failed', e); } }
            if (mod.ui) { try { mod.ui.init(); } catch (e) { console.error('StrmAssistant: ui init failed', e); } }
        }).catch(function (e) { console.error('StrmAssistant: module load failed', e); });
    } catch (e) {
        console.error('StrmAssistant: bootstrap failed (Emby module unaffected)', e);
    }
})();
";
            var uiOptions = Plugin.Instance.ExperienceEnhanceStore.GetOptions();
            var scriptVersion = typeof(ShortcutMenuHelper).Assembly.GetName().Version?.ToString() ?? "unknown";
            var uiConfig = injectDoubanBadgeBootstrap
                .Replace("SA_SCRIPT_VERSION", scriptVersion)
                .Replace("REPLACE_UNPLAYED_TOTAL", uiOptions.ItemDisplay.ReplaceUnplayedWithTotal ? "true" : "false")
                .Replace("SHOW_LIB_COUNT", uiOptions.LibraryOrder.ShowLibraryItemCount ? "true" : "false")
                .Replace("REVERSE_LOGS", uiOptions.SearchLog.ReverseLogLines ? "true" : "false")
                .Replace("BLOCK_SEARCH_RECS", uiOptions.SearchLog.BlockSearchRecommend ? "true" : "false")
                .Replace("SHOW_MEDIA_INFO", uiOptions.SearchLog.ShowMediaInfo ? "true" : "false");
            ModifiedShortcutsString += uiConfig;

            // shortcuts.js 在详情页/列表页不会加载（4.10 里它是按需导入的 itemShortcuts），
            // 因此把同一份引导代码追加到 mediainfo.js，保证徽章在渲染评分的页面必定初始化。
            try
            {
                OriginalMediaInfoString = File.ReadAllText(Path.Combine(dashboardSourcePath, MediaInfoRelativePath));
                ModifiedMediaInfoString = OriginalMediaInfoString + uiConfig;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuHelper)} - Media Info injection failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
                // 保留上一次成功生成的注入串：置空会让端点下发空内容，直接打断整个首页渲染。
                // 原始文件已缓存在 OriginalMediaInfoString，端点会按需回退。
            }

            if (dataExplorer2Assembly != null)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"{nameof(ShortcutMenuHelper)} - Emby.DataExplorer2 plugin is installed");
                }

                var contextMenuHelperType = dataExplorer2Assembly.GetType("Emby.DataExplorer2.Api.ContextMenuHelper");
                var modifiedShortcutsProperty = contextMenuHelperType?.GetProperty("ModifiedShortcutsString",
                    BindingFlags.Static | BindingFlags.Public);
                var setMethod = modifiedShortcutsProperty?.GetSetMethod(true);

                if (setMethod != null)
                {
                    const string injectDataExplorerCommand = @"
const dataExplorerCommandSource = {
    getCommands(options) {
        const commands = [];
        if (options.items?.length === 1 && options.items[0].ProviderIds) {
            commands.push({
                name: 'Explore Item Data',
                id: 'dataexplorer',
                icon: 'manage_search'
            });
        }
        return commands;
    },
    executeCommand(command, items) {
        return require(['components/dataexplorer/dataexplorer']).then((responses) => {
            return responses[0].show(items[0].Id);
        });
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/itemmanager/itemmanager.js').then((itemmanager) => {
        itemmanager.registerCommandSource(dataExplorerCommandSource);
    });
}, 5000);
";
                    ModifiedShortcutsString += injectDataExplorerCommand;
                    setMethod.Invoke(null, new object[] { ModifiedShortcutsString });
                }
            }
        }
    }
}
