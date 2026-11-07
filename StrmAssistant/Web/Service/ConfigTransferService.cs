using Emby.Web.GenericEdit;
using MediaBrowser.Controller.Api;
using StrmAssistant.Common;
using StrmAssistant.Options;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EmbyJsonSerializerOptions = MediaBrowser.Model.Serialization.JsonSerializerOptions;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// 配置导出/导入。导出默认省略 API Key、Token、Cookie、密码与代理地址；
    /// 导入只接受本插件 Options 命名空间内的可写标量，并在克隆上修改后保存，
    /// 避免直接改当前实例导致 Store 的变更检测失效。
    /// </summary>
    public class ConfigTransferService : BaseApiService
    {
        private const int SchemaVersion = 1;
        private const int MaxImportBytes = 1024 * 1024;
        private const int MaxValues = 1000;
        private const int MaxIssues = 20;

        private sealed class Section
        {
            public Section(string name, Type type, Func<object> get, Action<object> set,
                HashSet<string> allowedTopLevel = null)
            {
                Name = name;
                Type = type;
                Get = get;
                Set = set;
                AllowedTopLevel = allowedTopLevel;
            }

            public string Name { get; }
            public Type Type { get; }
            public Func<object> Get { get; }
            public Action<object> Set { get; }
            public HashSet<string> AllowedTopLevel { get; }
        }

        private static readonly Section[] Sections =
        {
            new Section("PluginOptions", typeof(PluginOptions),
                () => Plugin.Instance.MainOptionsStore.GetOptions(),
                value => Plugin.Instance.MainOptionsStore.SetOptions((PluginOptions)value),
                new HashSet<string>(StringComparer.Ordinal)
                {
                    nameof(PluginOptions.EnablePlugin),
                    nameof(PluginOptions.TaskConcurrency),
                    nameof(PluginOptions.Memory),
                    nameof(PluginOptions.ChineseSearch)
                }),
            new Section("MediaInfoExtractOptions", typeof(MediaInfoExtractOptions),
                () => Plugin.Instance.MediaInfoExtractStore.GetOptions(),
                value => Plugin.Instance.MediaInfoExtractStore.SetOptions((MediaInfoExtractOptions)value)),
            new Section("MetadataEnhanceOptions", typeof(MetadataEnhanceOptions),
                () => Plugin.Instance.MetadataEnhanceStore.GetOptions(),
                value => Plugin.Instance.MetadataEnhanceStore.SetOptions((MetadataEnhanceOptions)value)),
            new Section("IntroSkipOptions", typeof(IntroSkipOptions),
                () => Plugin.Instance.IntroSkipStore.GetOptions(),
                value => Plugin.Instance.IntroSkipStore.SetOptions((IntroSkipOptions)value)),
            new Section("ExperienceEnhanceOptions", typeof(ExperienceEnhanceOptions),
                () => Plugin.Instance.ExperienceEnhanceStore.GetOptions(),
                value => Plugin.Instance.ExperienceEnhanceStore.SetOptions((ExperienceEnhanceOptions)value)),
            new Section("NetworkProxyOptions", typeof(NetworkProxyOptions),
                () => Plugin.Instance.NetworkProxyStore.GetOptions(),
                value => Plugin.Instance.NetworkProxyStore.SetOptions((NetworkProxyOptions)value))
        };

        public class ConfigExportResponse
        {
            public int SchemaVersion { get; set; }
            public string PluginVersion { get; set; }
            public string ExportedAt { get; set; }
            public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
            public List<string> Redacted { get; set; } = new List<string>();
        }

        public class ConfigImportResponse
        {
            public bool Ok { get; set; }
            public int SchemaVersion { get; set; }
            public int ValuesRestored { get; set; }
            public bool RestartRequired { get; set; }
            public string Error { get; set; }
            public List<string> Rejected { get; set; } = new List<string>();
            public List<string> Failed { get; set; } = new List<string>();
            public List<string> Mismatched { get; set; } = new List<string>();
        }

        /// <summary>供设置页调用：直接生成与 HTTP 端点一致的导出结果。</summary>
        public static ConfigExportResponse Export() =>
            (ConfigExportResponse)new ConfigTransferService().Get(new GetConfigExport());

        /// <summary>供设置页调用：把导出结果序列化成可落盘的 JSON。</summary>
        public static string ExportJson()
        {
            return JsonSerializer.Serialize(Export(), new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        /// <summary>供设置页调用：导入一段 JSON，行为与 HTTP 端点一致。</summary>
        public static ConfigImportResponse ImportJson(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty));
            return (ConfigImportResponse)new ConfigTransferService().Post(new PostConfigImport
            {
                RequestStream = stream
            });
        }

        public object Get(GetConfigExport request)
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var redacted = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var section in Sections)
            {
                var flattened = OptionTransfer.Flatten(section.Get(), section.Name, section.AllowedTopLevel);
                foreach (var pair in flattened)
                {
                    if (OptionTransfer.IsSensitive(pair.Key)) redacted.Add(pair.Key);
                    else values[pair.Key] = pair.Value;
                }
            }

            return new ConfigExportResponse
            {
                SchemaVersion = SchemaVersion,
                PluginVersion = Plugin.Instance?.CurrentVersion,
                ExportedAt = DateTimeOffset.UtcNow.ToString("o"),
                Values = values.ToDictionary(pair => pair.Key, pair => pair.Value),
                Redacted = redacted.ToList()
            };
        }

        public object Post(PostConfigImport request)
        {
            var response = new ConfigImportResponse { SchemaVersion = SchemaVersion };

            if (request?.RequestStream == null)
            {
                response.Error = "empty request body";
                return response;
            }

            string body;
            try
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = request.RequestStream.ReadAsync(chunk, 0, chunk.Length).GetAwaiter().GetResult()) > 0)
                {
                    if (buffer.Length + read > MaxImportBytes)
                    {
                        response.Error = "request body too large";
                        return response;
                    }

                    buffer.Write(chunk, 0, read);
                }

                body = Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (Exception e)
            {
                response.Error = "read failed: " + e.Message;
                return response;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                response.Error = "empty request body";
                return response;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (!TryGetValuesElement(document.RootElement, out var valuesElement) ||
                    valuesElement.ValueKind != JsonValueKind.Object)
                {
                    response.Error = "missing values object";
                    return response;
                }

                var clones = new Dictionary<Section, object>();
                var changedKeys = new Dictionary<Section, List<string>>();
                var expected = new Dictionary<Section, Dictionary<string, string>>();

                const string proxyEnableKey = "NetworkProxyOptions.Network.EnableProxyServer";
                const string proxyUrlKey = "NetworkProxyOptions.Network.ProxyServerUrl";
                var blocked = new HashSet<string>(StringComparer.Ordinal);
                if (valuesElement.TryGetProperty(proxyEnableKey, out var proxyEnableElement) &&
                    string.Equals(ReadJsonScalar(proxyEnableElement), "true", StringComparison.OrdinalIgnoreCase) &&
                    !HasNonEmptyString(valuesElement, proxyUrlKey) &&
                    string.IsNullOrWhiteSpace(Plugin.Instance.NetworkProxyStore.GetOptions().Network.ProxyServerUrl))
                {
                    blocked.Add(proxyEnableKey);
                }

                foreach (var entry in valuesElement.EnumerateObject())
                {
                    if (blocked.Contains(entry.Name))
                    {
                        AddLimited(response.Rejected, entry.Name + ": proxy URL was omitted and target has none");
                        continue;
                    }

                    if (response.ValuesRestored + response.Rejected.Count + response.Failed.Count >= MaxValues)
                    {
                        AddLimited(response.Rejected, entry.Name + ": too many values");
                        continue;
                    }

                    var section = Sections.FirstOrDefault(candidate =>
                        entry.Name.StartsWith(candidate.Name + ".", StringComparison.Ordinal));
                    if (section == null)
                    {
                        AddLimited(response.Rejected, entry.Name + ": unknown section");
                        continue;
                    }

                    var path = entry.Name.Substring(section.Name.Length + 1);
                    var topLevel = path.Split('.')[0];
                    if (section.AllowedTopLevel != null && !section.AllowedTopLevel.Contains(topLevel))
                    {
                        AddLimited(response.Rejected, entry.Name + ": not exportable");
                        continue;
                    }

                    var raw = ReadJsonScalar(entry.Value);
                    if (raw == null)
                    {
                        AddLimited(response.Rejected, entry.Name + ": null or non-scalar");
                        continue;
                    }

                    if (!clones.TryGetValue(section, out var clone))
                    {
                        clone = CloneOptions(section.Get(), section.Type);
                        if (clone == null)
                        {
                            AddLimited(response.Failed, section.Name + ": clone failed");
                            continue;
                        }

                        clones[section] = clone;
                    }

                    try
                    {
                        if (!OptionTransfer.TryApply(clone, path, raw, out var error))
                        {
                            AddLimited(response.Rejected, entry.Name + ": " + error);
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        AddLimited(response.Failed, entry.Name + ": " + e.Message);
                        continue;
                    }

                    response.ValuesRestored++;

                    if (!changedKeys.TryGetValue(section, out var keys))
                    {
                        keys = new List<string>();
                        changedKeys[section] = keys;
                        expected[section] = new Dictionary<string, string>(StringComparer.Ordinal);
                    }

                    var fullKey = section.Name + "." + path;
                    keys.Add(fullKey);

                    var canonical = OptionTransfer.Flatten(clone, section.Name, section.AllowedTopLevel);
                    expected[section][fullKey] = canonical.TryGetValue(fullKey, out var value) ? value : raw;
                }

                var savedSections = 0;
                foreach (var pair in clones)
                {
                    var section = pair.Key;
                    var clone = pair.Value;
                    if (!changedKeys.TryGetValue(section, out var keys) || keys.Count == 0) continue;

                    var before = OptionTransfer.Flatten(section.Get(), section.Name, section.AllowedTopLevel);
                    var after = OptionTransfer.Flatten(clone, section.Name, section.AllowedTopLevel);
                    var changed = keys.Any(key =>
                        !before.TryGetValue(key, out var oldValue) ||
                        !after.TryGetValue(key, out var newValue) ||
                        !string.Equals(oldValue, newValue, StringComparison.Ordinal));
                    if (!changed) continue;

                    try
                    {
                        section.Set(clone);
                        savedSections++;
                    }
                    catch (Exception e)
                    {
                        AddLimited(response.Failed, section.Name + ": " + e.Message);
                        continue;
                    }

                    var current = OptionTransfer.Flatten(section.Get(), section.Name, section.AllowedTopLevel);
                    foreach (var key in keys)
                    {
                        if (!expected[section].TryGetValue(key, out var expectedValue)) continue;
                        if (!current.TryGetValue(key, out var currentValue) ||
                            !string.Equals(expectedValue, currentValue, StringComparison.Ordinal))
                        {
                            AddLimited(response.Mismatched, key);
                        }
                    }
                }

                response.RestartRequired = savedSections > 0;
                response.Ok = response.ValuesRestored > 0 && response.Failed.Count == 0 &&
                              response.Mismatched.Count == 0;

                if (savedSections > 0)
                {
                    Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                }
            }
            catch (JsonException e)
            {
                response.Error = "invalid json: " + e.Message;
            }
            catch (Exception e)
            {
                response.Error = "import failed: " + e.Message;
            }

            return response;
        }

        /// <summary>
        /// 兼容 Emby/System.Text.Json 的 PascalCase 与手写 JSON 的 camelCase：
        /// 导出文件当前是 "Values"，历史/手工文件可能是 "values"。
        /// </summary>
        public static bool TryGetValuesElement(JsonElement root, out JsonElement values)
        {
            values = default;
            if (root.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "values", StringComparison.OrdinalIgnoreCase))
                {
                    values = property.Value;
                    return true;
                }
            }

            return false;
        }

        private static string ReadJsonScalar(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Number:
                    return value.GetRawText();
                default:
                    return null;
            }
        }

        private static bool HasNonEmptyString(JsonElement values, string key) =>
            values.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString());

        private static void AddLimited(List<string> list, string value)
        {
            if (list.Count < MaxIssues) list.Add(value);
        }

        private static object CloneOptions(object source, Type type)
        {
            try
            {
                var method = typeof(ConfigTransferService).GetMethod(nameof(CloneOptionsGeneric),
                    BindingFlags.NonPublic | BindingFlags.Static);
                return method?.MakeGenericMethod(type).Invoke(null, new[] { source });
            }
            catch
            {
                return null;
            }
        }

        private static T CloneOptionsGeneric<T>(T source) where T : EditableOptionsBase, new()
        {
            using var stream = new MemoryStream();
            Plugin.JsonSerializer.SerializeToStream(source, stream, new EmbyJsonSerializerOptions { Indent = false });
            stream.Position = 0;
            var clone = new T();
            return clone.DeserializeFromJsonStream(stream, Plugin.JsonSerializer) as T;
        }
    }
}
