// Emby 4.10 的 AMD 加载器（alameda）配置里没有 paths 映射，裸 id 会被解析成 /web/<id>.js 而 404，
// 导致整个模块加载失败、导出为空。这里必须与 Emby 自身模块一致，改用相对于本模块路径的依赖。
define([
    '../../modules/emby-apiclient/connectionmanager.js',
    '../../modules/common/globalize.js',
    '../../modules/loading/loading.js',
    '../../modules/toast/toast.js',
    '../../modules/common/dialogs/confirm.js',
    '../../modules/dialog/dialog.js'
], function (connectionManager, globalize, loading, toast, confirm, dialog) {

    // Emby 4.10 的模块导出统一挂在 .default 上，Emby 自身模块也是这么取的
    // （例如 globalize.js 内部写 _connectionmanager.default.xxx）。
    // 这里统一解包一次，下面所有裸用法即恢复正常。
    connectionManager = connectionManager && (connectionManager.default || connectionManager);
    globalize = globalize && (globalize.default || globalize);
    loading = loading && (loading.default || loading);
    toast = toast && (toast.default || toast);
    confirm = confirm && (confirm.default || confirm);
    dialog = dialog && (dialog.default || dialog);

    // 共享扫描调度：三个子模块（badges / versions / ui）原先各自持有
    // MutationObserver + 防抖计时器，一次 DOM 变更会触发三次全量扫描。
    // 现在改为单一 observer + 单一防抖，各子模块通过 registerScan 注册扫描函数。
    const scanCallbacks = [];
    let sharedScanTimer = null;
    let sharedObserverStarted = false;

    function scheduleSharedScan() {
        if (sharedScanTimer) clearTimeout(sharedScanTimer);
        sharedScanTimer = setTimeout(function () {
            sharedScanTimer = null;
            for (const fn of scanCallbacks) {
                try {
                    fn();
                } catch (e) {
                    // 单个子模块异常不影响其他模块
                }
            }
        }, 600);
    }

    function registerScan(fn) {
        if (scanCallbacks.indexOf(fn) < 0) scanCallbacks.push(fn);
    }

    function startSharedObserver() {
        if (sharedObserverStarted || !window.MutationObserver) return;

        // 模块可能在 body 尚未就绪时加载（设置页搜索框在顶层立即注册），延迟重试避免抛错
        if (!document.body) {
            setTimeout(startSharedObserver, 100);
            return;
        }

        sharedObserverStarted = true;
        new MutationObserver(scheduleSharedScan).observe(document.body,
            { childList: true, subtree: true });
    }

    // ===== 全局：UI 命令 500 友好提示 =====
    // Emby 的插件设置页依赖「页面会话（setup stage）」。服务器重启/插件更新后，
    // 旧页面点保存或按钮会返回 500 "No active setup stage"，界面只显示英文错误，
    // 用户不知道要刷新。这里拦截该响应并给出中文提示。
    (function hookUiCommandErrors() {
        if (window.__saUiCommandHooked || !window.fetch) return;
        window.__saUiCommandHooked = true;

        const origFetch = window.fetch;

        window.fetch = function (input) {
            const url = typeof input === 'string' ? input : (input && input.url) || '';
            const result = origFetch.apply(this, arguments);

            if (url.indexOf('/UI/Command') < 0) return result;

            return result.then(function (res) {
                if (!res || res.status !== 500) return res;
                try {
                    res.clone().text().then(function (text) {
                        if (text && text.indexOf('No active setup stage') >= 0) showStalePageHint();
                    }).catch(function () { });
                } catch (e) {
                    // ignore
                }
                return res;
            });
        };

        function showStalePageHint() {
            if (document.querySelector('.sa-stale-hint')) return;

            const bar = document.createElement('div');
            bar.className = 'sa-stale-hint';
            bar.textContent = '服务器已重启或插件已更新，当前页面状态已失效，请刷新页面（Ctrl+F5）后重试。';
            bar.setAttribute('style', 'position:fixed;top:0;left:0;right:0;z-index:99999;' +
                'background:#b3261e;color:#fff;font-size:13px;padding:8px 12px;text-align:center;' +
                'box-shadow:0 2px 6px rgba(0,0,0,.3);cursor:pointer;');
            bar.title = '点击刷新页面';
            bar.addEventListener('click', function () { location.reload(); });
            document.body.appendChild(bar);

            setTimeout(function () {
                if (bar.parentNode) bar.parentNode.removeChild(bar);
            }, 20000);
        }
    })();

    // ===== 插件设置页：选项搜索 =====
    // 设置页有 6 个 Tab、30+ 分组，选项多且分散。这里提供一个浮动搜索框：
    // 输入关键词后按选项文本过滤，DOM 重渲染后自动重新应用过滤。
    (function setupOptionsSearch() {
        if (window.__saOptionsSearchHooked) return;
        window.__saOptionsSearchHooked = true;

        const CONTAINER_SELECTORS = ['.fieldset-field', '.inputContainer', '.emby-input-container'];
        const PAGE_HINT = 'configurationpage';

        let box = null;
        let keyword = '';

        function isSettingsPage() {
            return (location.hash || '').toLowerCase().indexOf(PAGE_HINT) >= 0;
        }

        function optionContainers() {
            for (let i = 0; i < CONTAINER_SELECTORS.length; i++) {
                const els = document.querySelectorAll(CONTAINER_SELECTORS[i]);
                if (els.length >= 3) return Array.prototype.slice.call(els);
            }
            return [];
        }

        function applyFilter(value) {
            const items = optionContainers();
            if (!items.length) return 0;

            const q = (value || '').trim().toLowerCase();
            let hits = 0;

            items.forEach(function (el) {
                if (!q) {
                    el.style.display = '';
                    return;
                }

                const match = (el.textContent || '').toLowerCase().indexOf(q) >= 0;
                el.style.display = match ? '' : 'none';
                if (match) hits++;
            });

            return hits;
        }

        function removeBox() {
            applyFilter('');
            if (box && box.parentNode) box.parentNode.removeChild(box);
            box = null;
            keyword = '';
        }

        function ensureBox() {
            if (box && document.body.contains(box)) return box;
            if (!optionContainers().length) return null;

            box = document.createElement('div');
            box.className = 'sa-options-search';
            box.setAttribute('style', 'position:fixed;right:18px;top:88px;z-index:9999;' +
                'background:rgba(28,28,30,.96);border:1px solid rgba(255,255,255,.15);' +
                'border-radius:10px;padding:8px 10px;display:flex;align-items:center;gap:8px;' +
                'box-shadow:0 4px 14px rgba(0,0,0,.35);');

            const input = document.createElement('input');
            input.type = 'search';
            input.placeholder = '搜索设置项…';
            input.setAttribute('style', 'width:200px;background:transparent;border:none;' +
                'outline:none;color:#fff;font-size:13px;');
            input.value = keyword;

            const hint = document.createElement('span');
            hint.setAttribute('style', 'color:rgba(255,255,255,.6);font-size:12px;min-width:30px;');

            input.addEventListener('input', function () {
                keyword = input.value;
                const hits = applyFilter(keyword);
                hint.textContent = keyword ? hits + ' 项' : '';
            });

            const close = document.createElement('span');
            close.textContent = '×';
            close.title = '关闭搜索';
            close.setAttribute('style', 'cursor:pointer;color:rgba(255,255,255,.6);' +
                'font-size:16px;line-height:1;');
            close.addEventListener('click', removeBox);

            box.appendChild(input);
            box.appendChild(hint);
            box.appendChild(close);
            document.body.appendChild(box);

            return box;
        }

        function tick() {
            try {
                if (!isSettingsPage()) {
                    if (box) removeBox();
                    return;
                }

                ensureBox();

                // Emby 的 GenericUI 会整块重渲染选项，过滤状态会被重置 → 每次扫描重新应用
                if (keyword) applyFilter(keyword);
            } catch (e) {
                // ignore
            }
        }

        registerScan(tick);
        startSharedObserver();
    })();

    return {
        copy: function (libraryId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let copyApi = apiClient.getUrl('Library/VirtualFolders/Copy');

            apiClient.ajax({
                type: "POST",
                url: copyApi,
                data: JSON.stringify({ Id: libraryId }),
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const locale = globalize.getCurrentLocale().toLowerCase();
                const confirmMessage = (locale === 'zh-cn') ? '\u590d\u5236\u5a92\u4f53\u5e93\u6210\u529f' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u88fd\u5a92\u9ad4\u5eab\u6210\u529f' : 'Copy Library Success');
                toast(confirmMessage);
                const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                if (itemsContainer) {
                    itemsContainer.notifyRefreshNeeded(true);
                }
            });
        },

        remove: function (libraryId, libraryName) {
            confirm({
                text: globalize.translate('MessageAreYouSureYouWishToRemoveLibrary').replace('{0}', libraryName),
                title: globalize.translate('HeaderRemoveLibrary'),
                confirmText: globalize.translate('Remove'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();

                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl('Library/VirtualFolders/Delete');

                apiClient.ajax({
                    type: "POST",
                    url: deleteApi + "?refreshLibrary=false&id=" + libraryId,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5408\u96c6\u5220\u9664\u6210\u529f' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u5408\u96C6\u5236\u9662\u6210\u529F' : 'Delete Collections Success');
                    toast(confirmMessage);
                    const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                    if (itemsContainer) {
                        itemsContainer.notifyRefreshNeeded(true);
                    }
                });
            });
        },

        traverse: function (itemId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let scanApi = apiClient.getUrl(`Items/${itemId}/Refresh`);
            let queryParams = {
                Recursive: true,
                ImageRefreshMode: 'Default',
                MetadataRefreshMode: 'Default',
                ReplaceAllImages: false,
                ReplaceAllMetadata: false
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${scanApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const confirmMessage = globalize.translate('ScanningLibraryFilesDots');
                toast(confirmMessage);
            });
        },

        delver: function (itemId, itemName, itemType) {
            if (itemType === 'Movie') {
                confirm({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    confirmText: globalize.translate('Delete'),
                    primary: 'cancel',
                    centerText: !1
                })
                .then(function() {
                    deleteVersion(itemId);
                });
            } else {
                const locale = globalize.getCurrentLocale().toLowerCase();
                const deleteEpisode = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Episode')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Episode');
                const deleteSeason = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Season')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Season');
                dialog({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    buttons: [
                        { name: globalize.translate("Cancel"), id: "cancel", type: "submit" },
                        { name: deleteEpisode, id: "deleteepisode", type: "cancel" },
                        { name: deleteSeason, id: "deleteseason", type: "cancel" }
                    ],
                    centerText: !1
                })
                .then(function(id) {
                    if (id === 'deleteepisode') {
                        deleteVersion(itemId);
                    } else if (id === 'deleteseason') {
                        deleteVersion(itemId, true);
                    }
                });
            }
            function deleteVersion(itemId, deleteParent = false) {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl(`Items/${itemId}/DeleteVersion${deleteParent ? `?DeleteParent=true` : ''}`);
                apiClient.ajax({
                    type: "POST",
                    url: deleteApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5220\u9664\u7248\u672C\u6210\u529F' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u524A\u9664\u7248\u672C\u6210\u529F' : 'Delete Version Success');
                    toast(confirmMessage);
                });
            }
        },

        lock: function (itemId, lockData) {
            let apiClient = connectionManager.currentApiClient();
            let lockApi = apiClient.getUrl(`Items/${itemId}/Lock`);
            let queryParams = {
                LockData: lockData
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${lockApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            });
        },

        clear_intro: function (itemIds) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
            batchClear(itemIds, 'ClearIntro', commandName);
        },

        clear_chapter: function (itemIds) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
            batchClear(itemIds, 'ClearChapterImages', commandName);
        },

        clear_mediainfo: function (itemIds) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u5A92\u4F53\u4FE1\u606F' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A' : 'Clear Media Info');
            batchClear(itemIds, 'ClearMediaInfo', commandName);
        },

        extract_mediainfo: function (itemIds) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '提取媒体信息' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '提取媒體訊息' : 'Extract Media Info');
            batchMediaInfo(itemIds, 'StrmAssistant/Items/ExtractMediaInfo', commandName, false);
        },

        delete_mediainfo: function (itemIds) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '删除媒体信息' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '刪除媒體訊息' : 'Delete Media Info');
            batchMediaInfo(itemIds, 'StrmAssistant/Items/DeleteMediaInfo', commandName, true);
        },

        badges: (function () {
            let initialized = false;
            let enabled = null;

            function apiClient() {
                return connectionManager.currentApiClient();
            }

            function checkEnabled() {
                if (enabled !== null) return Promise.resolve(enabled);
                return apiClient().getJSON(apiClient().getUrl('Douban/Ratings')).then(function (res) {
                    enabled = !!(res && (res.Enabled !== undefined ? res.Enabled : res.enabled));
                    return enabled;
                }).catch(function () {
                    enabled = false;
                    return false;
                });
            }

            function fetchRatings(ids) {
                return apiClient().getJSON(apiClient().getUrl('Douban/Ratings', { Ids: ids.join(',') }))
                    .then(function (res) { return (res && (res.Ratings || res.ratings)) || []; })
                    .catch(function () { return []; });
            }

            function ratingOf(info) {
                return info && (info.Rating !== undefined ? info.Rating : info.rating);
            }

            function idOf(info) {
                return info && (info.ItemId !== undefined ? info.ItemId : info.itemId);
            }

            // 豆瓣官方 logo（32x32 PNG，取自 https://www.douban.com/favicon.ico），
            // 以 data URI 内联，避免运行时依赖外网资源。
            const DOUBAN_LOGO = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAACxIAAAsSAdLdfvwAAAEFSURBVFhH7ZK9CsJAEISt7WzFws7Cwso3shY7QSysJIKN72EhiKCI4iOIEvCnkSAKFmIZsDhZ8I64ExR/siGSga+Z3Z0bQhKJakqFChjSgCENGNJwY3d2VFCibP5eXAAKiAOGNGDcmR+W/At+LMri+dEqsDptVWNsvQXd/KxAzx7A/BV0Exf4nwKh/4TfKpoFNMP1xAQ5l73KWoWn0I4W3fI8AAxGqVs2gaRcuwg7Gpp5Rbd8BwCDkWnmH0Ir/RrsaGjmFd3yHQAMHxZH24SONlOYa2imRTd87gsYPrRmHRPsXl2VrKdhhzyaadEN3/GFG0GLvxcXgALigCENGNKAIQ0YwtwAQLQd2sJ3hRwAAAAASUVORK5CYII=';

            function makeDoubanMark(size, verticalAlign) {
                const mark = document.createElement('img');
                mark.className = 'douban-mark';
                mark.alt = '\u8C46\u74E3';
                mark.src = DOUBAN_LOGO;
                mark.setAttribute('style', 'display:inline-block;border-radius:3px;margin-right:4px;' +
                    'width:' + size + ';height:' + size + ';vertical-align:' + verticalAlign + ';');
                return mark;
            }

            // 已确认有豆瓣评分的条目（ItemId -> info），供评分行换图标使用。
            const doubanIds = {};

            function hashId() {
                const m = (location.hash || '').match(/[?&]id=(\d+)/);
                return m ? m[1] : null;
            }

            // 取评分行中「星标图标以外」的文本所显示的数字（详情页可能是 TMDB 分或豆瓣分）。
            function displayedRating(container, star) {
                let text = '';
                for (const node of container.childNodes) {
                    if (node === star) continue;
                    text += node.textContent || '';
                }
                const m = text.match(/\d+(?:\.\d+)?/);
                return m ? parseFloat(m[0]) : null;
            }

            // 评分行：只有后端确认「该条目当前显示的评分确由豆瓣写入」（Applied=true）时，
            // 才把红五星替换为豆瓣 logo——保证「图标与数字同源」（严格语义）。
            // 若条目有豆瓣 ID 但 CommunityRating 未写入豆瓣分（超出回看期 / 未勾选评分字段 /
            // 覆盖开关关闭），Applied 为 false，此时保留红五星，避免「豆瓣图标 + 他源数字」。
            // 采用全量 DOM 扫描 + 幂等替换：既能覆盖卡片/列表/详情页，
            // 也不怕「评分行晚于卡片渲染」的时序问题——下一次 scan 会重新命中并替换。
            function ratingApplied(info) {
                if (!info) return false;
                const applied = info.Applied !== undefined ? info.Applied : info.applied;
                return applied === true;
            }

            function applyStars() {
                const detailId = hashId();
                const containers = document.querySelectorAll('.starRatingContainer');
                for (const container of containers) {
                    const star = container.querySelector('.starIcon');
                    if (!star) continue;
                    const holder = container.closest('.card[data-id]') || container.closest('[data-id]');
                    const id = holder ? holder.getAttribute('data-id') : detailId;
                    if (!id || !doubanIds[id]) continue;
                    const info = doubanIds[id];
                    if (!ratingApplied(info)) continue;
                    const shown = displayedRating(container, star);
                    const douban = Number(ratingOf(info));
                    // 二次校验：页面可能缓存着写入前的旧数字
                    if (shown === null || isNaN(douban) || Math.abs(shown - douban) > 0.05) continue;
                    container.replaceChild(makeDoubanMark('1.15em', '-0.15em'), star);
                }
            }

            // 卡片只登记豆瓣分，供评分行换图标使用；不再往封面右上角盖「豆 7.8」角标：
            // 详情页评分行已承载「豆瓣 logo + 分数」，卡片再标一次属于信息重复，
            // 且 absolute 定位会压住竖版海报的标题/年份区域。
            function decorateCard(card, info) {
                if (card.getAttribute('data-douban-badge')) return;
                card.setAttribute('data-douban-badge', '1');
                const cardId = card.getAttribute('data-id');
                if (cardId) doubanIds[cardId] = info;
            }

            let detailCheckedId = null;

            function decorateDetail() {
                const id = hashId();
                if (!id || detailCheckedId === id) return;
                detailCheckedId = id;

                fetchRatings([id]).then(function (list) {
                    if (hashId() !== id) return;
                    const info = list && list[0];
                    if (!info || !ratingOf(info)) return;
                    doubanIds[String(idOf(info))] = info;
                    // 详情页评分行换豆瓣 logo；不再额外追加 pill，避免与评分行重复。
                    applyStars();
                }).catch(function () {});
            }

            function scan() {
                try {
                    decorateDetail();
                    applyStars();

                    const cards = Array.prototype.slice.call(
                        document.querySelectorAll('.card[data-id]:not([data-douban-badge])'));
                    if (!cards.length) return;

                    const seen = {};
                    const ids = [];
                    for (const card of cards) {
                        const id = card.getAttribute('data-id');
                        if (!id || seen[id]) continue;
                        seen[id] = true;
                        ids.push(id);
                        if (ids.length >= 60) break;
                    }
                    if (!ids.length) return;

                    fetchRatings(ids).then(function (list) {
                        const byId = {};
                        (list || []).forEach(function (info) { byId[idOf(info)] = info; });
                        cards.forEach(function (card) {
                            const info = byId[card.getAttribute('data-id')];
                            if (info && ratingOf(info)) decorateCard(card, info);
                            else card.setAttribute('data-douban-badge', '1');
                        });
                        applyStars();
                    }).catch(function () {});
                } catch (e) {
                    // ignore
                }
            }


            function init() {
                if (initialized) return;
                initialized = true;

                checkEnabled().then(function (on) {
                    if (!on) return;
                    scan();
                    registerScan(scan);
                    startSharedObserver();
                });
            }

            return { init: init };
        })(),

        versions: (function () {
            let initialized = false;
            let enabled = null;
            let currentId = null;
            let currentRes = null;

            function apiClient() {
                return connectionManager.currentApiClient();
            }

            function checkEnabled() {
                if (enabled !== null) return Promise.resolve(enabled);
                return apiClient().getJSON(apiClient().getUrl('MultiVersion/Info')).then(function (res) {
                    enabled = !!(res && (res.Enabled !== undefined ? res.Enabled : res.enabled));
                    return enabled;
                }).catch(function () {
                    enabled = false;
                    return false;
                });
            }

            // 与后端 MultiVersionService.QualityScore/GetHdrLabel 保持一致的档位顺序（前=最优）。
            // 注意：顺序必须含 DV 细分档（P7/P8/P9/P5，对应后端 520/515/510/505）与 "HDR Vivid"（350 分），
            // 且未识别的取值应归入最低档，不能因为 indexOf 返回 -1 被 Math.max(0, ...) 误判为最高档。
            function rankOf(list, value) {
                if (!value) return list.length;
                const idx = list.indexOf(value);
                return idx < 0 ? list.length : idx;
            }

            function scoreOf(info) {
                if (!info) return -1;
                const labels = ['8K', '4K', '1440p', '1080p', '720p', 'SD'];
                const hdrs = ['DV P7', 'DV P8', 'DV P9', 'DV P5', 'DV', 'HDR10+', 'HDR Vivid', 'HDR'];
                const rawLabel = info.Label || info.label;
                // 复合标签（如 "4K HEVC 20.4Mbps 45.2GB"）取首个空格分词作为画质档位参与排序，
                // 否则整串匹配不到 labels 会被误判为最低档（MediaInfoExtended 模式）。
                const label = rawLabel ? String(rawLabel).split(' ')[0] : rawLabel;
                const hdr = info.Hdr || info.hdr;
                const lr = rankOf(labels, label);
                const hr = rankOf(hdrs, hdr);
                const bitrate = (info.Bitrate || info.bitrate || 0) / 1000000;
                return (labels.length - lr) * 10000 + (hdrs.length + 1 - hr) * 100 + Math.min(bitrate, 99);
            }

            function makeChip(info) {
                const chip = document.createElement('span');
                chip.className = 'mediaInfoItem mv-quality-badge';
                const label = info.Label || info.label;
                const hdr = info.Hdr || info.hdr;
                chip.textContent = label + (hdr ? ' \u00b7 ' + hdr : '');
                chip.setAttribute('style', 'color:#7ee787;background:rgba(46,160,67,.18);font-weight:600;');
                return chip;
            }

            function decorateSources(page, res) {
                const container = page.querySelector('.mediaSources');
                if (!container || container.getAttribute('data-mv-done')) return;

                const sources = (res && (res.Sources || res.sources)) || [];
                container.setAttribute('data-mv-done', '1');

                if (!sources.length) return;

                const rows = Array.prototype.slice.call(container.querySelectorAll('.mediaSource'));
                const entries = rows.map(function (row, i) {
                    return {
                        row: row,
                        wrapper: row.parentElement === container ? row : row.parentElement,
                        info: sources[i]
                    };
                });

                entries.forEach(function (e) {
                    if (!e.info || e.row.querySelector('.mv-quality-badge')) return;
                    const items = e.row.querySelector('.mediaInfoItems');
                    const label = e.info.Label || e.info.label;
                    if (!items || !label) return;
                    items.appendChild(makeChip(e.info));
                });

                const sorted = entries.slice().sort(function (a, b) { return scoreOf(b.info) - scoreOf(a.info); });
                const changed = entries.some(function (e, i) { return sorted[i].row !== e.row; });
                if (changed) sorted.forEach(function (e) { container.appendChild(e.wrapper); });
            }

            function decorateCards(page) {
                const cards = Array.prototype.slice.call(
                    page.querySelectorAll('.additionalPartsSection .card[data-id]:not([data-mv-card])'));
                if (!cards.length) return;

                const ids = [];
                const seen = {};
                cards.forEach(function (c) {
                    const id = c.getAttribute('data-id');
                    if (id && !seen[id]) { seen[id] = true; ids.push(id); }
                });
                if (!ids.length) return;

                apiClient().getJSON(apiClient().getUrl('MultiVersion/Info', { Ids: ids.join(',') })).then(function (res) {
                    const byId = {};
                    ((res && (res.Items || res.items)) || []).forEach(function (info) {
                        byId[info.ItemId !== undefined ? info.ItemId : info.itemId] = info;
                    });
                    cards.forEach(function (card) {
                        card.setAttribute('data-mv-card', '1');
                        const info = byId[card.getAttribute('data-id')];
                        const label = info && (info.Label || info.label);
                        if (!label) return;
                        const host = card.querySelector('.cardScalable') || card.querySelector('.cardBox') || card;
                        if (!host) return;
                        if (host.style.position !== 'absolute' && !host.style.position) host.style.position = 'relative';
                        const hdr = info.Hdr || info.hdr;
                        const pill = document.createElement('div');
                        pill.className = 'mv-quality-pill';
                        pill.textContent = label + (hdr ? ' \u00b7 ' + hdr : '');
                        pill.setAttribute('style', 'position:absolute;top:4px;left:4px;z-index:5;background:rgba(46,160,67,.85);' +
                            'color:#fff;font-size:11px;line-height:15px;padding:0 5px;border-radius:6px;pointer-events:none;white-space:nowrap;');
                        host.appendChild(pill);
                    });
                }).catch(function () {});
            }

            function scan() {
                try {
                    // Emby 4.10 已无 .itemDetailPage，详情页根容器为 .itemView（仅 item.html 使用）。
                    const page = document.querySelector('.itemView');
                    if (!page) return;

                    const match = (location.hash || '').match(/[?&]id=(\d+)/);
                    if (!match) return;

                    decorateCards(page);

                    const container = page.querySelector('.mediaSources');
                    if (!container || container.getAttribute('data-mv-done')) return;

                    if (currentId !== match[1]) {
                        currentId = match[1];
                        currentRes = null;

                        apiClient().getJSON(apiClient().getUrl('MultiVersion/Info', { Id: match[1] })).then(function (res) {
                            if (res && (res.Enabled === false || res.enabled === false)) {
                                enabled = false;
                                return;
                            }
                            currentRes = res;
                            if (currentRes) decorateSources(page, currentRes);
                        }).catch(function () { currentId = null; });
                    } else if (currentRes) {
                        decorateSources(page, currentRes);
                    }
                } catch (e) {
                    // ignore
                }
            }


            function init() {
                if (initialized) return;
                initialized = true;

                checkEnabled().then(function (on) {
                    if (!on) return;
                    scan();
                    registerScan(scan);
                    startSharedObserver();
                });
            }

            return { init: init };
        })(),

        ui: (function () {
            let initialized = false;
            let countCache = {};
            let logReversed = false;

            function cfg() { return window.__saUI || {}; }

            function apiClient() { return connectionManager.currentApiClient(); }


            function anyEnabled() {
                const c = cfg();
                return c.replaceUnplayedWithTotal || c.showLibraryItemCount ||
                    c.reverseLogLines || c.blockSearchRecommend || c.showMediaInfo;
            }

            // ShowMediaInfo：搜索结果按当前 tab 追加媒体字段。
            //
            // 独立实现：在 ListController.getViewSettings() 出口统一补字段。
            // Emby 的 getBaseQuery() 与卡片渲染都会读这个结果，因此请求的 Fields
            // 和最终展示保持一致；不修改 ItemsView.getViewSettingDefaults()。
            //
            // tab -> 追加字段：评分类用于影视/季，容器格式用于可出现单集/视频的 tab。
            const SEARCH_MEDIA_FIELDS = {
                Movie: ['Container', 'CommunityRating', 'CriticRating'],
                Series: ['CommunityRating', 'CriticRating'],
                Season: ['CommunityRating', 'CriticRating'],
                Episode: ['Container'],
                Video: ['Container']
            };

            let mediaInfoPatched = false;

            function activeSearchTab(view) {
                const tab = view && view.querySelector('.emby-searchable-tab-button.emby-tab-button-active');
                return tab ? tab.getAttribute('data-searchtype') : null;
            }

            function appendSearchMediaFields(settings, tab) {
                const extra = SEARCH_MEDIA_FIELDS[tab];
                if (!extra || !settings) return;

                const fields = Array.isArray(settings.fields) ? settings.fields : (settings.fields = []);
                for (let i = 0; i < extra.length; i++) {
                    if (fields.indexOf(extra[i]) < 0) fields.push(extra[i]);
                }
            }

            // Emby 4.10.2 的 ItemsView 模块 id 是 ListPage（./list/list.js）；
            // 旧版本使用 components/items/itemsview。按候选顺序探测，避免单一路径 404。
            const ITEMSVIEW_MODULE_IDS = ['ListPage', 'components/items/itemsview', '../../list/list.js'];

            function resolveItemsViewModule(module) {
                const candidate = module && (module.default || module);
                return candidate && candidate.prototype ? candidate : null;
            }

            function loadItemsView(onLoaded) {
                let index = 0;

                function attempt() {
                    if (index >= ITEMSVIEW_MODULE_IDS.length) return;

                    const id = ITEMSVIEW_MODULE_IDS[index++];

                    try {
                        require([id], function (module) {
                            const ItemsView = resolveItemsViewModule(module);
                            if (ItemsView) onLoaded(ItemsView);
                            else attempt();
                        }, function () {
                            attempt();
                        });
                    } catch (e) {
                        attempt();
                    }
                }

                attempt();
            }

            function patchSearchMediaInfo() {
                if (mediaInfoPatched || !cfg().showMediaInfo) return;

                loadItemsView(function (ItemsView) {
                    const proto = ItemsView.prototype;
                    if (!proto || typeof proto.getViewSettings !== 'function' ||
                        proto.__saSearchMediaPatched) return;

                    const original = proto.getViewSettings;
                    proto.getViewSettings = function () {
                        const settings = original.apply(this, arguments);
                        try {
                            const params = this.params;
                            if (params && params.type === 'search') {
                                appendSearchMediaFields(settings, activeSearchTab(this.view));
                            }
                        } catch (e) {
                            // 字段补充失败时保持 Emby 原始设置
                        }
                        return settings;
                    };
                    proto.__saSearchMediaPatched = true;
                    mediaInfoPatched = true;
                });
            }

            function scanTotalEpisodes() {
                if (!cfg().replaceUnplayedWithTotal) return;
                const cards = Array.prototype.slice.call(
                    document.querySelectorAll('.card[data-type="Series"][data-id]:not([data-sa-total])'));
                if (!cards.length) return;

                const ids = [];
                cards.forEach(function (c) {
                    const id = c.getAttribute('data-id');
                    if (id && ids.indexOf(id) < 0) ids.push(id);
                    c.setAttribute('data-sa-total', 'pending');
                });
                if (!ids.length) return;

                apiClient().getJSON(apiClient().getUrl('Items', {
                    Ids: ids.join(','), Fields: 'RecursiveItemCount', Limit: ids.length,
                    EnableTotalRecordCount: false
                })).then(function (res) {
                    const byId = {};
                    ((res && res.Items) || []).forEach(function (it) {
                        byId[it.Id] = it.RecursiveItemCount;
                    });
                    cards.forEach(function (card) {
                        const total = byId[card.getAttribute('data-id')];
                        if (total === undefined) { card.setAttribute('data-sa-total', '1'); return; }
                        card.setAttribute('data-sa-total', '1');
                        const ind = card.querySelector('.cardIndicators .indicator') ||
                            card.querySelector('.indicator');
                        if (ind && /^\d+$/.test((ind.textContent || '').trim())) {
                            ind.textContent = total;
                            ind.title = total + ' Episodes';
                        }
                    });
                }).catch(function () { });
            }

            function applyCount(card, count) {
                card.setAttribute('data-sa-count', '1');
                const host = card.querySelector('.cardScalable') || card.querySelector('.cardBox') || card;
                if (!host) return;
                const pill = document.createElement('div');
                pill.className = 'sa-lib-count';
                pill.textContent = count;
                pill.setAttribute('style', 'position:absolute;top:4px;left:4px;z-index:5;' +
                    'background:rgba(0,0,0,.72);color:#fff;font-size:11px;line-height:16px;' +
                    'padding:0 6px;border-radius:8px;pointer-events:none;');
                if (!host.style.position) host.style.position = 'relative';
                host.appendChild(pill);
            }

            function scanLibraryCounts() {
                if (!cfg().showLibraryItemCount) return;
                const cards = Array.prototype.slice.call(
                    document.querySelectorAll('.card[data-type="CollectionFolder"][data-id]:not([data-sa-count])'));
                if (!cards.length) return;

                cards.forEach(function (card) {
                    const id = card.getAttribute('data-id');
                    if (!id) { card.setAttribute('data-sa-count', '1'); return; }
                    card.setAttribute('data-sa-count', 'pending');

                    const cached = countCache[id];
                    if (cached && Date.now() - cached.ts < 300000) {
                        applyCount(card, cached.count);
                        return;
                    }

                    apiClient().getJSON(apiClient().getUrl('Items', {
                        ParentId: id, Recursive: true, Limit: 0, EnableTotalRecordCount: true
                    })).then(function (res) {
                        const total = res && res.TotalRecordCount;
                        if (typeof total !== 'number') { card.setAttribute('data-sa-count', '1'); return; }
                        countCache[id] = { count: total, ts: Date.now() };
                        applyCount(card, total);
                    }).catch(function () { card.setAttribute('data-sa-count', '1'); });
                });
            }

            function scanLogLines() {
                if (!cfg().reverseLogLines || logReversed) return;
                const hash = location.hash || '';
                if (hash.toLowerCase().indexOf('log') < 0) return;
                const container = document.querySelector('.listItemContainer, [data-role="listview"]');
                if (!container || container.children.length < 2) return;
                if (container.getAttribute('data-sa-reversed')) { logReversed = true; return; }
                container.setAttribute('data-sa-reversed', '1');
                const items = Array.prototype.slice.call(container.children);
                for (let i = items.length - 1; i >= 0; i--) container.appendChild(items[i]);
                logReversed = true;
            }

            function scanSearchRecs() {
                if (!cfg().blockSearchRecommend) return;
                const recs = document.querySelectorAll(
                    '.searchSuggestions, [class*="searchSuggestion"], #searchSuggestions');
                for (const el of recs) {
                    if (el.style.display !== 'none') el.style.display = 'none';
                }
            }

            function scan() {
                try {
                    scanTotalEpisodes();
                    scanLibraryCounts();
                    scanLogLines();
                    scanSearchRecs();
                } catch (e) {
                    // ignore
                }
            }

            function init() {
                if (initialized) return;
                initialized = true;

                if (!anyEnabled()) return;
                patchSearchMediaInfo();
                scan();
                    registerScan(scan);
                    startSharedObserver();
            }

            return { init: init };
        })()
    };


    function batchMediaInfo(itemIds, endpoint, commandName, isDelete) {
        const ids = Array.isArray(itemIds) ? itemIds : [itemIds];
        if (!ids.length) return;

        const locale = globalize.getCurrentLocale().toLowerCase();
        const isZh = locale === 'zh-cn';
        const isZhHant = ['zh-hk', 'zh-tw'].includes(locale);

        const run = function() {
            loading.show();
            const apiClient = connectionManager.currentApiClient();
            return apiClient.ajax({
                type: "POST",
                url: apiClient.getUrl(endpoint),
                data: JSON.stringify({ Ids: ids }),
                contentType: "application/json",
                dataType: "json"
            }).then(function(res) {
                loading.hide();
                const total = res && (res.Total !== undefined ? res.Total : res.total);
                const queued = res && (res.Queued !== undefined ? res.Queued : res.queued);
                const succeeded = res && (res.Succeeded !== undefined ? res.Succeeded : res.succeeded);
                const failed = res && (res.Failed !== undefined ? res.Failed : res.failed);
                const skipped = res && (res.Skipped !== undefined ? res.Skipped : res.skipped);
                let message;
                if (isDelete) {
                    message = isZh ? `删除完成：成功 ${succeeded || 0}，失败 ${failed || 0}，跳过 ${skipped || 0}` :
                        (isZhHant ? `刪除完成：成功 ${succeeded || 0}，失敗 ${failed || 0}，跳過 ${skipped || 0}` :
                            `Deleted: ${succeeded || 0} succeeded, ${failed || 0} failed, ${skipped || 0} skipped`);
                } else {
                    const count = queued || total || ids.length;
                    message = isZh ? `已加入提取队列：${count} 项` :
                        (isZhHant ? `已加入提取佇列：${count} 項` : `Queued ${count} item(s) for extraction`);
                }
                toast(message);
            }).catch(function() {
                loading.hide();
            });
        };

        if (isDelete) {
            const text = isZh ? '删除媒体信息会清除 DB 中的媒体流、时长、容器和本地 JSON；季/剧会处理其下所有集，之后需要重新提取。' :
                (isZhHant ? '刪除媒體訊息會清除 DB 中的媒體串流、時長、容器和本地 JSON；季/劇會處理其下所有集，之後需要重新提取。' :
                    'Deleting media info clears DB media streams, runtime, container and local JSON; Season/Series selections process all episodes. Re-extraction is required afterwards.');
            confirm({
                text: text,
                title: commandName,
                confirmText: isZh ? '删除' : (isZhHant ? '刪除' : 'Delete'),
                primary: 'cancel'
            }).then(run);
        } else {
            run();
        }
    }

    function batchClear(itemIds, endpoint, commandName) {
        const ids = Array.isArray(itemIds) ? itemIds : [itemIds];
        if (!ids.length) return;

        confirm({
            text: globalize.translate('AreYouSureToContinue'),
            title: commandName,
            confirmText: globalize.translate('Clear'),
            primary: 'cancel'
        })
        .then(function() {
            loading.show();
            let apiClient = connectionManager.currentApiClient();
            return Promise.all(ids.map(id => apiClient.ajax({
                type: "POST",
                url: apiClient.getUrl(`Items/${id}/${endpoint}`),
                data: {},
                contentType: "application/json"
            })));
        })
        .then(function() {
            loading.hide();
            const locale = globalize.getCurrentLocale().toLowerCase();
            const confirmMessage = (locale === 'zh-cn') ? commandName + '\u6210\u529F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? commandName + '\u6210\u529F' : commandName + ' Success');
            toast(confirmMessage);
        })
        .catch(function() {
            loading.hide();
        });
    }
});
