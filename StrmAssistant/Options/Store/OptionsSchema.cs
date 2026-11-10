using MediaBrowser.Model.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace StrmAssistant.Options.Store
{
    /// <summary>
    /// 配置 schema 版本门：只决定「要不要跑迁移」，不执行迁移本身。
    /// 版本只在配置结构变化时递增，与插件版本无关。
    ///
    /// 缺失或损坏的版本文件按 v0 处理（迁移幂等，最坏情况是空跑一次）；
    /// 版本高于当前插件（降级）时跳过迁移，且不回写版本。
    /// </summary>
    internal static class OptionsSchema
    {
        public const int CurrentVersion = 1;

        private const string FileName = "StrmAssistant_options_schema.json";
        private static readonly object Sync = new object();
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static bool _initialized;
        private static int _storedVersion;
        private static string _directory;

        public static bool ShouldRunMigrations { get; private set; }

        internal static int StoredVersion
        {
            get
            {
                lock (Sync) return _storedVersion;
            }
        }

        public static void Initialize(string configDirectory, ILogger logger)
        {
            lock (Sync)
            {
                _initialized = true;
                _directory = configDirectory;
                _storedVersion = ReadVersion(configDirectory, logger);
                ShouldRunMigrations = _storedVersion < CurrentVersion;

                if (_storedVersion > CurrentVersion)
                {
                    logger?.Warn($"OptionsSchema - stored schema v{_storedVersion} is newer than plugin v{CurrentVersion}; skip migrations");
                }
                else if (ShouldRunMigrations)
                {
                    logger?.Info($"OptionsSchema - schema v{_storedVersion} -> v{CurrentVersion}; migrations enabled");
                }
                else
                {
                    logger?.Info($"OptionsSchema - schema v{CurrentVersion} is current; migrations skipped");
                }
            }
        }

        public static void MarkCurrent(ILogger logger)
        {
            lock (Sync)
            {
                if (!_initialized || !ShouldRunMigrations || _storedVersion >= CurrentVersion) return;

                if (string.IsNullOrEmpty(_directory))
                {
                    logger?.Warn("OptionsSchema - config directory unavailable; schema version not written");
                    return;
                }

                try
                {
                    Directory.CreateDirectory(_directory);
                    var record = new SchemaRecord
                    {
                        Version = CurrentVersion,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        PluginVersion = Plugin.Instance?.CurrentVersion
                    };
                    File.WriteAllText(Path.Combine(_directory, FileName),
                        JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
                    _storedVersion = CurrentVersion;
                    ShouldRunMigrations = false;
                    logger?.Info($"OptionsSchema - schema updated to v{CurrentVersion}");
                }
                catch (Exception e)
                {
                    logger?.Warn("OptionsSchema - failed to write schema file: " + e.Message);
                }
            }
        }

        internal static int ReadVersion(string configDirectory, ILogger logger)
        {
            if (string.IsNullOrEmpty(configDirectory)) return 0;

            var path = Path.Combine(configDirectory, FileName);
            try
            {
                if (!File.Exists(path)) return 0;

                var record = JsonSerializer.Deserialize<SchemaRecord>(File.ReadAllText(path), ReadOptions);
                return record?.Version > 0 ? record.Version : 0;
            }
            catch (Exception e)
            {
                logger?.Warn("OptionsSchema - failed to read schema file: " + e.Message);
                return 0;
            }
        }

        internal static void ResetForTests()
        {
            lock (Sync)
            {
                _initialized = false;
                _storedVersion = 0;
                _directory = null;
                ShouldRunMigrations = false;
            }
        }

        private sealed class SchemaRecord
        {
            public int Version { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string PluginVersion { get; set; }
        }
    }
}
