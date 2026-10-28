using HarmonyLib;
using System;
using System.Net;
using System.Reflection;
using System.Text;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 自定义本地发现地址（<c>NetworkOptions.LocalDiscoveryUrl</c>）。
    ///
    /// 局域网客户端通过 Emby 的 UDP 发现协议（「who is EmbyServer?」）获取服务器地址；
    /// 当部署在反向代理之后时，希望发现响应返回代理地址而不是内网直连地址。
    ///
    /// 挂钩点：<c>Emby.Server.Implementations.Udp.UdpServer.SendMessage(string localUrl, IPEndPoint, Encoding)</c>
    /// —— 前缀改写 <c>localUrl</c>。**只影响 UDP 发现响应体**，不改动全局 <c>GetLocalApiUrl</c>，
    /// 因此不会波及网页/API/播放等其它本地地址。
    ///
    /// 地址为空时不打补丁，零行为变化；方法缺失静默跳过。
    /// </summary>
    public class CustomLocalDiscovery : PatchBase<CustomLocalDiscovery>
    {
        private static MethodInfo _sendMessage;

        public CustomLocalDiscovery()
        {
            Initialize();

            if (!string.IsNullOrWhiteSpace(GetOverrideUrl())) Patch();
        }

        private static string GetOverrideUrl() =>
            Plugin.Instance.NetworkProxyStore.GetOptions().Network.LocalDiscoveryUrl;

        protected override void OnInitialize()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Server.Implementations");
                var udpServer = assembly.GetType("Emby.Server.Implementations.Udp.UdpServer");

                _sendMessage = udpServer?.GetMethod("SendMessage",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(string), typeof(IPEndPoint), typeof(Encoding) }, null);

                if (_sendMessage == null)
                {
                    Plugin.Instance.Logger.Warn(
                        "CustomLocalDiscovery - UdpServer.SendMessage not found; custom discovery url disabled");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("CustomLocalDiscovery init failed: " + ex.Message);
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_sendMessage != null)
            {
                PatchUnpatch(PatchTracker, apply, _sendMessage, prefix: nameof(SendMessagePrefix));
            }
        }

        [HarmonyPrefix]
        private static void SendMessagePrefix(ref string localUrl)
        {
            try
            {
                var custom = GetOverrideUrl();
                if (string.IsNullOrWhiteSpace(custom)) return;

                custom = custom.Trim().TrimEnd('/');
                if (string.Equals(custom, localUrl, StringComparison.OrdinalIgnoreCase)) return;

                localUrl = custom;

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"CustomLocalDiscovery - replaced discovery url: {custom}");
                }
            }
            catch
            {
                // 忽略，保持原地址
            }
        }
    }
}
