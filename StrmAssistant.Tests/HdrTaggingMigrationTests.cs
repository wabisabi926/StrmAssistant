using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using StrmAssistant.Options.Store;
using Xunit;
using MbJsonOptions = MediaBrowser.Model.Serialization.JsonSerializerOptions;
using StjOptions = System.Text.Json.JsonSerializerOptions;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// HDR 标注开关从 ISO 提取解耦时的配置迁移。
    ///
    /// 规则：旧配置没有 HdrTagging 分组 → 沿用 IsoTool.EnableIsoExtraction 的值；
    /// 已有该分组 → 只写 flag、不改内容；整个过程幂等，并留 .hdrtagging.bak 备份。
    /// 这条迁移是「升级后标签不集体消失」的唯一保障，必须钉住。
    /// </summary>
    public class HdrTaggingMigrationTests : IDisposable
    {
        private const string FlagName = "StrmAssistant_MediaInfoExtractOptions_hdrtagging_v1.flag";

        private readonly string _dir;

        public HdrTaggingMigrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "strm_hdrtagging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* ignored */ }
        }

        private string ConfigPath => Path.Combine(_dir, "StrmAssistant_MediaInfoExtractOptions.json");
        private string BackupPath => ConfigPath + ".hdrtagging.bak";
        private string FlagPath => Path.Combine(_dir, FlagName);

        private string Run(string json)
        {
            File.WriteAllText(ConfigPath, json);
            MediaInfoExtractOptionsMigration.MigrateIfNeeded(ConfigPath, new StubSerializer(), new SilentLogger());
            return File.ReadAllText(ConfigPath);
        }

        private static MediaInfoExtractOptions Read(string json) =>
            new StubSerializer().DeserializeFromString<MediaInfoExtractOptions>(json);

        [Fact]
        public void Inherits_Iso_Switch_When_Enabled()
        {
            var text = Run(@"{
  ""IsoTool"": { ""EnableIsoExtraction"": true, ""CustomProbePath"": """", ""CustomEncoderPath"": """" },
  ""MediaInfoPersist"": { ""PersistMediaInfoMode"": ""None"" }
}");

            Assert.True(Read(text).HdrTagging.EnableHdrTagging);
            Assert.True(File.Exists(BackupPath));
            Assert.True(File.Exists(FlagPath));
        }

        [Fact]
        public void Inherits_Iso_Switch_When_Disabled()
        {
            var text = Run(@"{
  ""IsoTool"": { ""EnableIsoExtraction"": false },
  ""MediaInfoPersist"": { ""PersistMediaInfoMode"": ""None"" }
}");

            Assert.False(Read(text).HdrTagging.EnableHdrTagging);
            Assert.True(File.Exists(FlagPath));
        }

        [Fact]
        public void Keeps_Existing_HdrTagging_Values()
        {
            // 已有新分组：即便 ISO 开关为 true，也不能覆盖用户显式关掉的 HDR 标注
            var text = Run(@"{
  ""IsoTool"": { ""EnableIsoExtraction"": true },
  ""HdrTagging"": { ""EnableHdrTagging"": false, ""EnableHdrVividTagging"": false }
}");

            var options = Read(text);
            Assert.False(options.HdrTagging.EnableHdrTagging);
            Assert.False(options.HdrTagging.EnableHdrVividTagging);
            Assert.False(File.Exists(BackupPath));   // 未改写内容，不应产生备份
            Assert.True(File.Exists(FlagPath));
        }

        [Fact]
        public void SubSwitches_Default_To_Enabled()
        {
            var text = Run(@"{ ""IsoTool"": { ""EnableIsoExtraction"": true } }");
            var options = Read(text);

            Assert.True(options.HdrTagging.EnableDolbyVisionProfileTagging);
            Assert.True(options.HdrTagging.EnableHdrVividTagging);
        }

        [Fact]
        public void Is_Idempotent()
        {
            Run(@"{ ""IsoTool"": { ""EnableIsoExtraction"": true } }");
            var firstBackup = File.ReadAllText(BackupPath);

            // 第二次迁移：flag 已存在，直接返回，不覆盖备份
            MediaInfoExtractOptionsMigration.MigrateIfNeeded(ConfigPath, new StubSerializer(), new SilentLogger());

            Assert.Equal(firstBackup, File.ReadAllText(BackupPath));
        }
    }

    /// <summary>用 System.Text.Json 实现的最小序列化器，仅供迁移测试使用。</summary>
    internal sealed class StubSerializer : IJsonSerializer
    {
        private static readonly StjOptions Options = new StjOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public void SerializeToStream(object obj, Stream stream) => JsonSerializer.Serialize(stream, obj, Options);

        public void SerializeToStream(object obj, Stream stream, MbJsonOptions options) =>
            JsonSerializer.Serialize(stream, obj, Options);

        public void SerializeToFile(object obj, string file) => File.WriteAllText(file, SerializeToString(obj));

        public void SerializeToFile(object obj, string file, MbJsonOptions options) =>
            File.WriteAllText(file, SerializeToString(obj, options));

        public T DeserializeFromFile<T>(string file) where T : class =>
            DeserializeFromString<T>(File.ReadAllText(file));

        public Task<T> DeserializeFromFileAsync<T>(string file) where T : class =>
            Task.FromResult(DeserializeFromFile<T>(file));

        public Task<object> DeserializeFromFileAsync(Type type, string file) =>
            Task.FromResult(DeserializeFromString(File.ReadAllText(file), type));

        public T DeserializeFromStream<T>(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return DeserializeFromString<T>(reader.ReadToEnd());
        }

        public Task<T> DeserializeFromStreamAsync<T>(Stream stream) =>
            Task.FromResult(DeserializeFromStream<T>(stream));

        public T DeserializeFromString<T>(string text) =>
            JsonSerializer.Deserialize<T>(text, Options);

        public object DeserializeFromStream(Stream stream, Type type)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return DeserializeFromString(reader.ReadToEnd(), type);
        }

        public Task<object> DeserializeFromStreamAsync(Stream stream, Type type) =>
            Task.FromResult(DeserializeFromStream(stream, type));

        public object DeserializeFromString(string json, Type type) => JsonSerializer.Deserialize(json, type, Options);

        public string SerializeToString(object obj) => JsonSerializer.Serialize(obj, Options);

        public string SerializeToString(object obj, MbJsonOptions options) => SerializeToString(obj);

        public ReadOnlySpan<char> SerializeToSpan(object obj) => SerializeToString(obj).AsSpan();

        public T DeserializeFromSpan<T>(ReadOnlySpan<char> text) =>
            JsonSerializer.Deserialize<T>(text, Options);

        public object DeserializeFromSpan(ReadOnlySpan<char> json, Type type) =>
            JsonSerializer.Deserialize(json, type, Options);

        public object DeserializeFromBytes(ReadOnlySpan<byte> bytes, Type type) =>
            JsonSerializer.Deserialize(bytes, type, Options);

        public T DeserializeFromBytes<T>(ReadOnlySpan<byte> bytes) =>
            JsonSerializer.Deserialize<T>(bytes, Options);

        /// <summary>迁移流程不涉及局部反序列化。</summary>
        public void DeserializePartialJsonInto(string json, object obj) { }
    }

    /// <summary>静默日志，迁移测试不关心日志内容。</summary>
    internal sealed class SilentLogger : ILogger
    {
        public void Info(string message, params object[] paramList) { }
        public void Error(string message, params object[] paramList) { }
        public void Warn(string message, params object[] paramList) { }
        public void Debug(string message, params object[] paramList) { }
        public void Fatal(string message, params object[] paramList) { }
        public void FatalException(string message, Exception exception, params object[] paramList) { }
        public void ErrorException(string message, Exception exception, params object[] paramList) { }
        public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
        public void Log(LogSeverity severity, string message, params object[] paramList) { }
        public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        public void Error(ReadOnlyMemory<char> message) { }
        public void Warn(ReadOnlyMemory<char> message) { }
        public void Info(ReadOnlyMemory<char> message) { }
        public void Debug(ReadOnlyMemory<char> message) { }
    }
}
