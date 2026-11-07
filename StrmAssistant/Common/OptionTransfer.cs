using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 配置导出/导入的反射辅助：只处理本插件 Options 命名空间下的可写标量，
    /// 不碰 UI 控件、列表与只读计算属性。导出时由调用方对敏感键做省略处理。
    /// </summary>
    public static class OptionTransfer
    {
        private static readonly string[] SensitiveMarkers =
        {
            "ApiKey", "AccessToken", "Cookie", "Password", "Secret", "Token",
            "ProxyServerUrl", "GitHubProxy"
        };

        private static readonly HashSet<string> IgnoredNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "EditorTitle", "EditorDescription", "IsModSupported", "IsNewItem", "CurrentUsageMb",
            "EditorRoot", "DefaultObject", "IsModSuccess", "ShowConflictPluginLoadedStatus",
            "ShowScrapeProxyWhitelist", "ShowProxyServerStatus", "IsMovieDbPluginLoaded",
            "MovieDbPluginVersion", "MovieDbVersion", "IsMovieDbVersionCompatible", "MovieDbVersionTier"
        };

        /// <summary>导出时按叶子属性名判断是否为密钥/凭据，命中则只列键名、不输出值。</summary>
        public static bool IsSensitive(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            var leaf = key.Substring(key.LastIndexOf('.') + 1);
            return SensitiveMarkers.Any(marker =>
                leaf.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 把选项对象拍平成 <c>Section.Property.SubProperty</c> 键值对。
        /// <paramref name="allowedTopLevel"/> 非空时只遍历这些根属性（用于 PluginOptions 排除运行时/UI 组）。
        /// </summary>
        public static SortedDictionary<string, string> Flatten(object source, string prefix,
            ICollection<string> allowedTopLevel = null)
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            Flatten(source, prefix, values, allowedTopLevel, 0);
            return values;
        }

        private static void Flatten(object source, string prefix, IDictionary<string, string> values,
            ICollection<string> allowedTopLevel, int depth)
        {
            if (source == null || depth > 4) return;

            foreach (var property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                if (!property.CanRead || !property.CanWrite) continue;
                if (IgnoredNames.Contains(property.Name)) continue;
                if (depth == 0 && allowedTopLevel != null && !allowedTopLevel.Contains(property.Name)) continue;

                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                object value;
                try
                {
                    value = property.GetValue(source);
                }
                catch
                {
                    continue;
                }

                var key = prefix + "." + property.Name;
                if (IsScalarType(type))
                {
                    if (value != null) values[key] = FormatValue(value, type);
                    continue;
                }

                if (IsOwnOptionsType(type) && value != null)
                {
                    Flatten(value, key, values, null, depth + 1);
                }
            }
        }

        /// <summary>把 <c>Child.Property</c> 形式的相对路径写回选项对象；非法路径/类型返回 false。</summary>
        public static bool TryApply(object root, string path, string raw, out string error)
        {
            error = null;
            if (root == null || string.IsNullOrWhiteSpace(path))
            {
                error = "empty path";
                return false;
            }

            var parts = path.Split('.');
            if (parts.Any(string.IsNullOrWhiteSpace))
            {
                error = "invalid path";
                return false;
            }

            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var property = current.GetType().GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length != 0 || !property.CanRead)
                {
                    error = "unknown path: " + parts[i];
                    return false;
                }

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (!IsOwnOptionsType(propertyType))
                {
                    error = "not an option group: " + parts[i];
                    return false;
                }

                var child = property.GetValue(current);
                if (child == null)
                {
                    if (!property.CanWrite)
                    {
                        error = "read-only group: " + parts[i];
                        return false;
                    }

                    child = Activator.CreateInstance(propertyType);
                    if (child == null)
                    {
                        error = "cannot create group: " + parts[i];
                        return false;
                    }

                    property.SetValue(current, child);
                }

                current = child;
            }

            var leafName = parts[parts.Length - 1];
            var leaf = current.GetType().GetProperty(leafName, BindingFlags.Public | BindingFlags.Instance);
            if (leaf == null || leaf.GetIndexParameters().Length != 0 || !leaf.CanWrite ||
                IgnoredNames.Contains(leaf.Name))
            {
                error = "unknown setting: " + leafName;
                return false;
            }

            var leafType = Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType;
            if (!IsScalarType(leafType))
            {
                error = "not a scalar setting: " + leafName;
                return false;
            }

            if (!TryParseValue(raw, leafType, out var value))
            {
                error = "invalid value for " + leafName;
                return false;
            }

            leaf.SetValue(current, value);
            return true;
        }

        public static bool IsScalarType(Type type) =>
            type != null && (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
                             type.IsEnum || type == typeof(DateTime) || type == typeof(TimeSpan) ||
                             type == typeof(Guid));

        private static bool IsOwnOptionsType(Type type) =>
            type?.Namespace != null &&
            (type.Namespace.StartsWith("StrmAssistant.Options", StringComparison.Ordinal) ||
             (type.Namespace == "StrmAssistant" && type.Name.EndsWith("Options", StringComparison.Ordinal)));

        private static string FormatValue(object value, Type type)
        {
            if (type.IsEnum) return value.ToString();
            if (value is DateTime dateTime) return dateTime.ToString("o", CultureInfo.InvariantCulture);
            if (value is TimeSpan timeSpan) return timeSpan.ToString("c", CultureInfo.InvariantCulture);
            if (value is Guid guid) return guid.ToString();
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static bool TryParseValue(string raw, Type type, out object value)
        {
            value = null;

            if (type == typeof(string))
            {
                value = raw;
                return true;
            }

            if (type == typeof(bool))
            {
                if (bool.TryParse(raw, out var boolean)) value = boolean;
                return value != null;
            }

            if (type.IsEnum)
            {
                try
                {
                    var parsed = Enum.Parse(type, raw, true);
                    if (Enum.IsDefined(type, parsed)) value = parsed;
                }
                catch
                {
                    // 非法枚举值按失败处理
                }
                return value != null;
            }

            if (type == typeof(int))
            {
                if (int.TryParse(raw, NumberStyles.Integer | NumberStyles.Float, CultureInfo.InvariantCulture, out var integer))
                    value = integer;
                return value != null;
            }

            if (type == typeof(long))
            {
                if (long.TryParse(raw, NumberStyles.Integer | NumberStyles.Float, CultureInfo.InvariantCulture, out var longValue))
                    value = longValue;
                return value != null;
            }

            if (type == typeof(double))
            {
                if (double.TryParse(raw, NumberStyles.Integer | NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                    value = doubleValue;
                return value != null;
            }

            if (type == typeof(float))
            {
                if (float.TryParse(raw, NumberStyles.Integer | NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
                    value = floatValue;
                return value != null;
            }

            if (type == typeof(decimal))
            {
                if (decimal.TryParse(raw, NumberStyles.Integer | NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
                    value = decimalValue;
                return value != null;
            }

            if (type == typeof(DateTime))
            {
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
                    value = dateTime;
                return value != null;
            }

            if (type == typeof(TimeSpan))
            {
                if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var timeSpan)) value = timeSpan;
                return value != null;
            }

            if (type == typeof(Guid))
            {
                if (Guid.TryParse(raw, out var guid)) value = guid;
                return value != null;
            }

            return false;
        }
    }
}
