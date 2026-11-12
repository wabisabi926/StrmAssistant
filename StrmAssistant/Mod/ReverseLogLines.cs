using HarmonyLib;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// 「日志最新在前」：把 /System/Logs/{Name}/Lines 的分页结果按从新到旧返回。
    ///
    /// 前端日志页是按 StartIndex 增量加载的（虚拟列表），只在 DOM 层反转第一段无法保证
    /// 整份日志的顺序，因此改在 API 层做反向分页：客户端要第 S 段，就从文件末尾的对应
    /// 位置读取再反转。日志文件本身保持原样。
    /// </summary>
    public class ReverseLogLines : PatchBase<ReverseLogLines>
    {
        private static MethodInfo _getLogLinesMethod;
        private static PropertyInfo _nameProperty;
        private static PropertyInfo _startIndexProperty;
        private static PropertyInfo _limitProperty;
        private static PropertyInfo _enableTotalProperty;

        private static readonly ConcurrentDictionary<string, LineCountEntry> LineCountCache =
            new ConcurrentDictionary<string, LineCountEntry>();

        private static readonly ConditionalWeakTable<object, StrongBox<int>> PendingTotals =
            new ConditionalWeakTable<object, StrongBox<int>>();

        private static readonly TimeSpan LineCountCacheDuration = TimeSpan.FromSeconds(30);

        private sealed class LineCountEntry
        {
            public int Lines;
            public DateTime CachedUtc;
        }

        public ReverseLogLines()
        {
            try
            {
                Initialize();

                if (_getLogLinesMethod != null && PatchTracker.FallbackPatchApproach != PatchApproach.None)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                Plugin.Instance?.Logger?.Warn($"ReverseLogLines 初始化失败，已禁用该功能: {e.Message}");
            }
        }

        protected override void OnInitialize()
        {
            var assembly = Assembly.Load("Emby.Api");
            var serviceType = assembly?.GetType("Emby.Api.System.SystemService", false);
            var requestType = assembly?.GetType("Emby.Api.System.GetLogLines", false);

            if (serviceType != null && requestType != null)
            {
                _getLogLinesMethod = serviceType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType == requestType);

                _nameProperty = requestType.GetProperty("Name");
                _startIndexProperty = requestType.GetProperty("StartIndex");
                _limitProperty = requestType.GetProperty("Limit");
                _enableTotalProperty = requestType.GetProperty("EnableTotalRecordCount");
            }

            if (_getLogLinesMethod == null || _nameProperty == null || _startIndexProperty == null ||
                _limitProperty == null || _enableTotalProperty == null)
            {
                PatchTracker.AddError("SystemService.Get(GetLogLines) target not found");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                return;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getLogLinesMethod,
                prefix: nameof(GetLogLinesPrefix), postfix: nameof(GetLogLinesPostfix));
        }

        private static bool IsEnabled() =>
            Plugin.Instance?.ExperienceEnhanceStore?.GetOptions()?.SearchLog?.ReverseLogLines == true;

        [HarmonyPrefix]
        private static bool GetLogLinesPrefix(object __0, ref Task<object> __result)
        {
            if (!IsEnabled() || __0 == null) return true;

            try
            {
                var name = _nameProperty.GetValue(__0) as string;
                var limit = Convert.ToInt32(_limitProperty.GetValue(__0) ?? 0);

                // Limit<=0 是「只取总数」的请求，保持原实现即可
                if (limit <= 0) return true;

                var total = GetLineCount(name);
                if (total <= 0) return true;

                var start = Math.Max(0, Convert.ToInt32(_startIndexProperty.GetValue(__0) ?? 0));
                var chunk = Math.Min(limit, Math.Max(0, total - start));

                if (chunk <= 0)
                {
                    __result = Task.FromResult<object>(new QueryResult<string>
                    {
                        Items = Array.Empty<string>(),
                        TotalRecordCount = total
                    });
                    return false;
                }

                var from = Math.Max(0, total - start - chunk);

                _startIndexProperty.SetValue(__0, from);
                _limitProperty.SetValue(__0, chunk);
                // 反向分页不需要再扫描到文件末尾，总数由 postfix 补回
                _enableTotalProperty.SetValue(__0, false);

                PendingTotals.Remove(__0);
                PendingTotals.Add(__0, new StrongBox<int>(total));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("ReverseLogLines - prefix failed: " + e.Message);
                return true;
            }
        }

        [HarmonyPostfix]
        private static void GetLogLinesPostfix(object __0, ref Task<object> __result)
        {
            if (__0 == null || !PendingTotals.TryGetValue(__0, out var box)) return;

            PendingTotals.Remove(__0);
            var total = box.Value;
            var original = __result;

            if (original == null) return;
            __result = ReverseAsync(original, total);
        }

        private static async Task<object> ReverseAsync(Task<object> original, int total)
        {
            var result = await original.ConfigureAwait(false);

            if (result is QueryResult<string> queryResult && queryResult.Items != null)
            {
                // 显式复制后 Array.Reverse：数组实例上的 Reverse() 会绑定到
                // MemoryExtensions.Reverse(Span<T>)（void），并不是 Enumerable.Reverse。
                var items = (string[])queryResult.Items.Clone();
                Array.Reverse(items);
                queryResult.Items = items;
                queryResult.TotalRecordCount = total;
            }

            return result;
        }

        private static int GetLineCount(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;

            string path;

            try
            {
                var logDirectory = Plugin.Instance?.ApplicationPaths?.LogDirectoryPath;
                if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory)) return 0;

                path = Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file => string.Equals(Path.GetFileName(file), name,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return 0;
            }

            if (string.IsNullOrEmpty(path)) return 0;

            var now = DateTime.UtcNow;

            if (LineCountCache.TryGetValue(path, out var cached) &&
                now - cached.CachedUtc < LineCountCacheDuration)
            {
                return cached.Lines;
            }

            try
            {
                var lines = 0;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    while (reader.ReadLine() != null) lines++;
                }

                LineCountCache[path] = new LineCountEntry { Lines = lines, CachedUtc = now };
                return lines;
            }
            catch
            {
                return 0;
            }
        }
    }
}
