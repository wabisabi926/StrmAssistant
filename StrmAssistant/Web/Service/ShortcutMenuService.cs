using System;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Web.Api;
using StrmAssistant.Web.Helper;

namespace StrmAssistant.Web.Service
{
    [Unauthenticated]
    public class ShortcutMenuService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _resultFactory;
        private readonly IServerConfigurationManager _configurationManager;

        public ShortcutMenuService(IHttpResultFactory resultFactory,
            IServerConfigurationManager configurationManager)
        {
            _resultFactory = resultFactory;
            _configurationManager = configurationManager;
        }

        public IRequest Request { get; set; }

        public object Get(GetStrmAssistantJs request)
        {
            if (!Plugin.IsPluginEnabled) return null;

            var js = ShortcutMenuHelper.StrmAssistantJs;
            if (js == null || js.Length == 0)
            {
                // 资源缺失必须显式报错：下发空体会让前端 require 拿到空模块，
                // 排查时只看到「mod 为 undefined」，很容易误判成注入逻辑本身有问题。
                var message = "StrmAssistant: embedded resource strmassistant.js is missing";
                Plugin.Instance?.Logger?.Error(message);
                throw new InvalidOperationException(message);
            }

            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)js.GetBuffer(), "application/x-javascript");
        }

        public object Get(GetShortcutMenu request)
        {
            // 这条路由覆盖了 Emby 自带的 /web/modules/shortcuts.js。
            // 插件停用时不能返回 null（会变成 204 空响应，前端模块直接废掉），必须原样下发磁盘文件。
            return ServeJs(ShortcutMenuHelper.GetShortcutsJs(_configurationManager, Plugin.IsPluginEnabled),
                "modules/shortcuts.js");
        }

        public object Get(GetMediaInfoJs request)
        {
            // 同上：/web/modules/mediainfo/mediainfo.js 被 cardbuilder.js 与 item/item.js 依赖，
            // 空响应会让首页与媒体库卡片渲染整体抛异常，只剩转圈。
            return ServeJs(ShortcutMenuHelper.GetMediaInfoJs(_configurationManager, Plugin.IsPluginEnabled),
                "modules/mediainfo/mediainfo.js");
        }

        /// <summary>
        /// 只允许下发非空内容：读不到原始文件属于安装异常，明确报错好过静默返回空模块。
        /// </summary>
        private object ServeJs(string content, string relativePath)
        {
            if (string.IsNullOrEmpty(content))
            {
                var message = $"StrmAssistant: failed to load {relativePath}";
                Plugin.Instance?.Logger?.Error(message);
                throw new InvalidOperationException(message);
            }

            return _resultFactory.GetResult(content.AsSpan(), "application/x-javascript");
        }
    }
}
