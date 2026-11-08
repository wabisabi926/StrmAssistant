using StrmAssistant.Properties;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 体检报告的落盘与读取。报告只写插件配置目录，不进媒体库、不进 mediainfo 备份链路——
    /// 它是「一次体检的快照」，不是条目元数据的一部分。
    /// </summary>
    public static class MediaHealthService
    {
        private const string ReportFileName = "media-health-report.json";
        private const string MarkdownFileName = "media-health-report.md";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string ReportPath => ResolvePath(ReportFileName);

        public static string MarkdownPath => ResolvePath(MarkdownFileName);

        private static string ResolvePath(string fileName)
        {
            var folder = Plugin.Instance?.ApplicationPaths?.PluginConfigurationsPath;
            return string.IsNullOrEmpty(folder) ? fileName : Path.Combine(folder, fileName);
        }

        public static bool Save(MediaHealthReport report)
        {
            if (report == null) return false;

            try
            {
                var json = JsonSerializer.Serialize(report, JsonOptions);
                File.WriteAllText(ReportPath, json, new UTF8Encoding(false));
                File.WriteAllText(MarkdownPath, BuildMarkdown(report), new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn("MediaHealth - 报告写入失败: " + e.Message);
                return false;
            }
        }

        public static MediaHealthReport Load()
        {
            try
            {
                var path = ReportPath;
                if (!File.Exists(path)) return null;

                return JsonSerializer.Deserialize<MediaHealthReport>(File.ReadAllText(path, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        public static string BuildSummary(MediaHealthReport report)
        {
            if (report == null) return Resources.Health_Status_Empty;

            return string.Format(Resources.Health_Status_Summary,
                report.GeneratedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                report.Critical, report.Warning, report.Info);
        }

        public static string BuildMarkdown(MediaHealthReport report)
        {
            if (report == null) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("# " + Resources.Health_Status_Caption);
            sb.AppendLine();
            sb.AppendLine(string.Format(Resources.Health_Markdown_Meta,
                report.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                report.ItemsChecked, report.ItemsWithIssues));
            sb.AppendLine();
            sb.AppendLine(string.Format(Resources.Health_Status_Summary,
                report.GeneratedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                report.Critical, report.Warning, report.Info));
            sb.AppendLine();
            sb.AppendLine("| " + Resources.Health_Markdown_Header + " |");
            sb.AppendLine("|---|---|---|---|");

            foreach (var issue in report.Issues
                         .OrderBy(i => i.Severity)
                         .ThenBy(i => i.ItemName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"| {SeverityLabel(issue.Severity)} | {Escape(issue.ItemName)} | {Escape(issue.FileName)} | {Escape(issue.Detail)} |");
            }

            if (report.Issues.Count == 0)
            {
                sb.AppendLine("| - | - | - | " + Resources.Health_Status_Empty + " |");
            }

            return sb.ToString();
        }

        public static string SeverityLabel(MediaHealthSeverity severity)
        {
            switch (severity)
            {
                case MediaHealthSeverity.Critical:
                    return Resources.Health_Severity_Critical;
                case MediaHealthSeverity.Warning:
                    return Resources.Health_Severity_Warning;
                default:
                    return Resources.Health_Severity_Info;
            }
        }

        private static string Escape(string value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Replace("|", "\\|");
    }
}
