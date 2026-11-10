using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Model.Logging;
using StrmAssistant.Options.Store;
using StrmAssistant.Tests.Support;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 配置 schema 版本记录：4.0 起只记录版本，不再执行旧布局迁移。
    /// </summary>
    public class OptionsSchemaTests : IDisposable
    {
        private readonly string _configDir;
        private readonly ILogger _logger = InterfaceProxy.Create<ILogger>((_, __) => null);

        public OptionsSchemaTests()
        {
            _configDir = Path.Combine(Path.GetTempPath(), "sa-schema-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_configDir);
            OptionsSchema.ResetForTests();
        }

        public void Dispose()
        {
            OptionsSchema.ResetForTests();
            try
            {
                if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }

        private string SchemaPath => Path.Combine(_configDir, "StrmAssistant_options_schema.json");

        private void WriteSchema(string content) => File.WriteAllText(SchemaPath, content);

        private int ReadSchemaVersion() =>
            JsonDocument.Parse(File.ReadAllText(SchemaPath)).RootElement.GetProperty("Version").GetInt32();

        [Fact]
        public void MissingFile_IsNotRecorded_AndCanBeMarked()
        {
            OptionsSchema.Initialize(_configDir, _logger);

            Assert.False(OptionsSchema.HasRecordedVersion);
            Assert.Equal(0, OptionsSchema.StoredVersion);

            OptionsSchema.MarkCurrent(_logger);

            Assert.True(OptionsSchema.HasRecordedVersion);
            Assert.Equal(1, ReadSchemaVersion());
        }

        [Fact]
        public void CurrentVersion_IsRecorded()
        {
            WriteSchema("{\"Version\":1}");

            OptionsSchema.Initialize(_configDir, _logger);

            Assert.True(OptionsSchema.HasRecordedVersion);
            Assert.Equal(1, OptionsSchema.StoredVersion);
        }

        [Fact]
        public void OlderVersion_IsMarkedCurrent()
        {
            WriteSchema("{\"Version\":0}");

            OptionsSchema.Initialize(_configDir, _logger);
            Assert.Equal(0, OptionsSchema.StoredVersion);

            OptionsSchema.MarkCurrent(_logger);

            Assert.Equal(1, ReadSchemaVersion());
        }

        [Fact]
        public void NewerVersion_DoesNotDowngradeFile()
        {
            WriteSchema("{\"Version\":2}");

            OptionsSchema.Initialize(_configDir, _logger);
            Assert.True(OptionsSchema.HasRecordedVersion);

            OptionsSchema.MarkCurrent(_logger);

            Assert.Equal(2, ReadSchemaVersion());
        }

        [Fact]
        public void CorruptFile_IsNotRecorded_AndCanBeMarked()
        {
            WriteSchema("not-json");

            OptionsSchema.Initialize(_configDir, _logger);
            Assert.False(OptionsSchema.HasRecordedVersion);

            OptionsSchema.MarkCurrent(_logger);

            Assert.Equal(1, ReadSchemaVersion());
        }

        [Fact]
        public void MarkCurrent_DoesNotRewriteWhenAlreadyCurrent()
        {
            WriteSchema("{\"Version\":1,\"PluginVersion\":\"manual\"}");
            var before = File.ReadAllText(SchemaPath);

            OptionsSchema.Initialize(_configDir, _logger);
            OptionsSchema.MarkCurrent(_logger);

            Assert.Equal(before, File.ReadAllText(SchemaPath));
        }
    }
}
