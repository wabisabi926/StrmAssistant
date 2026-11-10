using System;
using System.Linq;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 片源体检报告：上次对比与 Markdown 输出口径。
    /// 不依赖 Plugin.Instance，避免落盘路径影响判据。
    /// </summary>
    public class MediaHealthServiceTests
    {
        private static MediaHealthIssue Issue(string code, string item = "Item", string file = "file.mkv",
            string detail = "detail") =>
            new MediaHealthIssue
            {
                Severity = MediaHealthSeverity.Warning,
                Code = code,
                ItemName = item,
                FileName = file,
                Detail = detail
            };

        private static MediaHealthReport Report(DateTimeOffset at, params MediaHealthIssue[] issues) =>
            new MediaHealthReport { GeneratedAt = at, Issues = issues.ToList() };

        [Fact]
        public void ApplyComparison_CountsNewAndResolved()
        {
            var at = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(8));
            var previous = Report(at, Issue("Fake4K", "A"), Issue("NoAudio", "B"));
            var current = Report(at.AddDays(1), Issue("Fake4K", "A"), Issue("FakeHdr", "C"));

            MediaHealthService.ApplyComparison(current, previous);

            Assert.Equal(1, current.NewIssues);
            Assert.Equal(1, current.ResolvedIssues);
            Assert.Equal(at, current.PreviousGeneratedAt);
        }

        [Fact]
        public void ApplyComparison_IgnoresDetailChanges()
        {
            var previous = Report(DateTimeOffset.Now,
                Issue("UndSubtitle", "A", detail: "有 1 条外挂字幕语言未识别"));
            var current = Report(DateTimeOffset.Now,
                Issue("UndSubtitle", "A", detail: "有 3 条外挂字幕语言未识别"));

            MediaHealthService.ApplyComparison(current, previous);

            Assert.Equal(0, current.NewIssues);
            Assert.Equal(0, current.ResolvedIssues);
        }

        [Fact]
        public void ApplyComparison_IgnoresInfoRowsFromLegacyReport()
        {
            var info = Issue("NoSubtitle", "A");
            info.Severity = MediaHealthSeverity.Info;
            var previous = Report(DateTimeOffset.Now, info, Issue("Fake4K", "B"));
            var current = Report(DateTimeOffset.Now.AddDays(1));

            MediaHealthService.ApplyComparison(current, previous);

            Assert.Equal(1, current.ResolvedIssues);
        }

        [Fact]
        public void ApplyComparison_WithoutPrevious_ReportsNoDiff()
        {
            var current = Report(DateTimeOffset.Now, Issue("Fake4K"));

            MediaHealthService.ApplyComparison(current, null);

            Assert.Equal(0, current.NewIssues);
            Assert.Equal(0, current.ResolvedIssues);
            Assert.Null(current.PreviousGeneratedAt);
        }

        [Fact]
        public void BuildMarkdown_InfoOnlyReport_HasNoIssueRows()
        {
            var report = new MediaHealthReport
            {
                GeneratedAt = DateTimeOffset.Now,
                ItemsChecked = 10,
                Info = 3
            };

            var markdown = MediaHealthService.BuildMarkdown(report);
            var tableLines = markdown.Split('\n').Count(line => line.StartsWith("| "));

            Assert.Equal(2, tableLines);
        }

        [Fact]
        public void BuildMarkdown_AddsDiffLineOnlyWithPreviousReport()
        {
            var report = new MediaHealthReport
            {
                GeneratedAt = DateTimeOffset.Now,
                ItemsChecked = 1,
                NewIssues = 1,
                ResolvedIssues = 2
            };

            var withoutPrevious = MediaHealthService.BuildMarkdown(report);
            report.PreviousGeneratedAt = DateTimeOffset.Now.AddDays(-1);
            var withPrevious = MediaHealthService.BuildMarkdown(report);

            Assert.Equal(withoutPrevious.Split('\n').Length + 2, withPrevious.Split('\n').Length);
        }
    }
}
