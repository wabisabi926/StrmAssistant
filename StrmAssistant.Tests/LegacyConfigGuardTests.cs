using System;
using System.IO;
using StrmAssistant.Options.Store;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 4.0 旧配置保护：只认根级旧键，不能把新版嵌套结构误判为旧配置。
    /// </summary>
    public class LegacyConfigGuardTests : IDisposable
    {
        private readonly string _configDir;

        public LegacyConfigGuardTests()
        {
            _configDir = Path.Combine(Path.GetTempPath(), "sa-legacy-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_configDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }

        private void Write(string fileName, string content) =>
            File.WriteAllText(Path.Combine(_configDir, fileName), content);

        [Fact]
        public void Detects_Flat_Main_Options()
        {
            Write("StrmAssistant.json", "{\"GeneralOptions\":{},\"EnablePlugin\":true}");

            Assert.NotNull(LegacyConfigGuard.Detect(_configDir));
        }

        [Fact]
        public void Detects_Flat_Intro_Options()
        {
            Write("StrmAssistant_IntroSkipOptions.json", "{\"UnlockIntroSkip\":true}");

            Assert.NotNull(LegacyConfigGuard.Detect(_configDir));
        }

        [Fact]
        public void Ignores_Current_Grouped_Config()
        {
            Write("StrmAssistant.json", "{\"ChineseSearch\":{\"EnhanceChineseSearch\":true}}");
            Write("StrmAssistant_IntroSkipOptions.json",
                "{\"Fingerprint\":{\"UnlockIntroSkip\":true},\"Playback\":{},\"Scrape\":{}}");

            Assert.Null(LegacyConfigGuard.Detect(_configDir));
        }

        [Fact]
        public void Ignores_Corrupt_Config()
        {
            Write("StrmAssistant.json", "not-json");

            Assert.Null(LegacyConfigGuard.Detect(_configDir));
        }

        [Fact]
        public void Missing_Directory_Returns_Null()
        {
            Assert.Null(LegacyConfigGuard.Detect(Path.Combine(_configDir, "missing")));
        }
    }
}
