using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// URL 脱敏工具：日志里只保留 <c>scheme://host:port/path</c>。
    ///
    /// 网盘的访问签名、pickcode、临时凭据几乎都在 query 里；写进日志后，
    /// 一旦日志被上传做诊断或转给别人，等同于把可用链接一起交出去。
    /// 降低日志级别不能替代脱敏，所以这里直接不记。
    ///
    /// 纯函数、无外部依赖，便于单测。
    /// </summary>
    public static class UrlSanitizer
    {
        public static string ToLogSafe(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(empty)";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(unparsable)";

            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString();

            // 不用 UriBuilder：它会把 userinfo 一并带回来
            return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
        }
    }
}
