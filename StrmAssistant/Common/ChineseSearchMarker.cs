using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 中文搜索「上次见到的版本」标记。
    ///
    /// 标记文件 <c>&lt;Name&gt;_chinese_search_last_seen_version.txt</c> 有三种历史形态：
    /// <list type="bullet">
    /// <item>格式 1（隐式）：只写版本号，如 <c>3.0.0.55</c> / <c>26.3.25</c>。</item>
    /// <item>格式 2：<c>格式号|版本号</c>，如 <c>2|3.0.0.55</c>。</item>
    /// <item>格式 3：<c>格式号|索引方案号|版本号</c>，如 <c>3|2|3.0.0.65</c>。</item>
    /// </list>
    ///
    /// 抽成纯函数的原因：这里的判断决定「要不要自动关闭功能」，
    /// 判错会让用户升级后功能静默消失（或该关的时候没关），必须能脱离 Emby 单测。
    /// </summary>
    internal static class ChineseSearchMarker
    {
        /// <summary>当前写盘格式。改动标记结构时同步 +1。</summary>
        public const int CurrentFormat = 3;

        /// <summary>索引方案号未知（格式 2 及更早没有这个字段）。</summary>
        public const int UnknownIndexScheme = -1;

        /// <summary>
        /// 解析标记文本。任何解析不出的部分都退化为「未知」，**不抛异常** ——
        /// 标记文件损坏时应当按首次安装处理，而不是让插件启动失败。
        /// </summary>
        public static ChineseSearchMarkerInfo Parse(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0) return ChineseSearchMarkerInfo.Empty;

            var format = 1;
            var rest = trimmed;
            var indexScheme = UnknownIndexScheme;

            var separator = rest.IndexOf('|');
            if (separator >= 0)
            {
                if (int.TryParse(rest.Substring(0, separator), out var parsedFormat))
                {
                    format = parsedFormat;
                }

                rest = rest.Substring(separator + 1).Trim();

                if (format >= 3)
                {
                    var schemeSeparator = rest.IndexOf('|');
                    if (schemeSeparator >= 0)
                    {
                        if (int.TryParse(rest.Substring(0, schemeSeparator), out var parsedScheme))
                        {
                            indexScheme = parsedScheme;
                        }

                        rest = rest.Substring(schemeSeparator + 1).Trim();
                    }
                }
            }

            Version.TryParse(rest, out var version);

            return new ChineseSearchMarkerInfo(format, indexScheme, version);
        }

        /// <summary>按当前格式生成标记文本。</summary>
        public static string Format(int indexScheme, Version version)
        {
            return $"{CurrentFormat}|{indexScheme}|{version}";
        }
    }

    /// <summary>
    /// 标记解析结果。<see cref="IndexScheme"/> 为 <see cref="ChineseSearchMarker.UnknownIndexScheme"/>
    /// 表示旧格式没有记录方案号；<see cref="Version"/> 为 null 表示版本号无法解析。
    /// </summary>
    internal sealed class ChineseSearchMarkerInfo
    {
        public static readonly ChineseSearchMarkerInfo Empty =
            new ChineseSearchMarkerInfo(1, ChineseSearchMarker.UnknownIndexScheme, null);

        public ChineseSearchMarkerInfo(int format, int indexScheme, Version version)
        {
            Format = format;
            IndexScheme = indexScheme;
            Version = version;
        }

        public int Format { get; }

        public int IndexScheme { get; }

        public Version Version { get; }

        /// <summary>标记里是否记录了索引方案号。</summary>
        public bool HasIndexScheme => IndexScheme != ChineseSearchMarker.UnknownIndexScheme;
    }
}
