using MediaBrowser.Model.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace StrmAssistant.Options.Store
{
    /// <summary>
    /// 配置 schema 版本记录。4.0 起不再内置旧布局迁移，只记录当前结构版本；
    /// 升级自旧布局的配置由 <see cref="LegacyConfigGuard"/> 拦截，要求先安装 3.0.0.78 完成迁移。
    ///
    /// 缺失/损坏的版本文件不代表旧布局：3.0.0.78 用户没有该文件，视为当前结构。
    /// 版本高于当前插件（降级）时不回写、不降版本。
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

        public static bool HasRecordedVersion { get; private set; }

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

                var read = Read(configDirectory, logger);
                HasRecordedVersion = read.Found;
                _storedVersion = read.Version;

                if (!read.Found)
                {
                    logger?.Info($"OptionsSchema - schema file missing; assume current v{CurrentVersion}");
                }
                else if (_storedVersion > CurrentVersion)
                {
                    logger?.Warn($"OptionsSchema - stored schema v{_storedVersion} is newer than plugin v{CurrentVersion}; keep as-is");
                }
                else if (_storedVersion < CurrentVersion)
                {
                    logger?.Warn($"OptionsSchema - stored schema v{_storedVersion} is older than v{CurrentVersion}; no migrations registered");
                }
                else
                {
                    logger?.Info($"OptionsSchema - schema v{CurrentVersion} is current");
                }
            }
        }

        public static void MarkCurrent(ILogger logger)
        {
            lock (Sync)
            {
                if (!_initialized || _storedVersion >= CurrentVersion) return;

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
                    HasRecordedVersion = true;
                    logger?.Info($"OptionsSchema - schema recorded as v{CurrentVersion}");
                }
                catch (Exception e)
                {
                    logger?.Warn("OptionsSchema - failed to write schema file: " + e.Message);
                }
            }
        }

        internal static (bool Found, int Version) Read(string configDirectory, ILogger logger)
        {
            if (string.IsNullOrEmpty(configDirectory)) return (false, 0);

            var path = Path.Combine(configDirectory, FileName);
            try
            {
                if (!File.Exists(path)) return (false, 0);

                var record = JsonSerializer.Deserialize<SchemaRecord>(File.ReadAllText(path), ReadOptions);
                return (true, record?.Version > 0 ? record.Version : 0);
            }
            catch (Exception e)
            {
                logger?.Warn("OptionsSchema - failed to read schema file: " + e.Message);
                return (false, 0);
            }
        }

        internal static void ResetForTests()
        {
            lock (Sync)
            {
                _initialized = false;
                _storedVersion = 0;
                _directory = null;
                HasRecordedVersion = false;
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
