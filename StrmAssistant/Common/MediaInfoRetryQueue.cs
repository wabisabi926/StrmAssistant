using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 媒体信息提取失败重试队列：提取失败或文件暂时不可达的条目落盘记录，
    /// 下次扫库后的自动补提取优先处理；累计达到上限后移出队列，避免永久失败项反复重跑。
    /// </summary>
    public static class MediaInfoRetryQueue
    {
        private const int MaxEntries = 5000;
        private static readonly object _lock = new object();
        private static Dictionary<string, RetryEntry> _entries;
        private static string _retryPath;

        private static ILogger Logger => Plugin.Instance.Logger;

        public class RetryEntry
        {
            public int Attempts { get; set; }
            public string LastError { get; set; }
            public DateTime LastAt { get; set; }
        }

        private static string RetryPath => _retryPath ??= Path.Combine(
            Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
            "strmassistant_mediainfo_retry.json");

        private static void EnsureLoaded()
        {
            if (_entries != null) return;

            _entries = new Dictionary<string, RetryEntry>();

            try
            {
                if (File.Exists(RetryPath))
                {
                    var persisted = Plugin.JsonSerializer
                        .DeserializeFromFile<Dictionary<string, RetryEntry>>(RetryPath);
                    if (persisted != null) _entries = persisted;
                }
            }
            catch (Exception e)
            {
                Logger.Debug("MediaInfoRetry - Queue load failed: {0}", e.Message);
            }
        }

        /// <summary>记录一次失败（线程安全，写盘按 10 秒节流）。</summary>
        public static void RecordFailure(long internalId, string error)
        {
            // 「自动重试失败的提取」关闭时不落盘，避免无用的重试队列
            if (!Plugin.Instance.MediaInfoExtractStore.GetOptions().AutoCatchup.AutoRetryFailed) return;

            try
            {
                lock (_lock)
                {
                    EnsureLoaded();

                    var key = internalId.ToString();

                    if (_entries.TryGetValue(key, out var entry))
                    {
                        entry.Attempts++;
                        entry.LastError = error;
                        entry.LastAt = DateTime.UtcNow;
                    }
                    else
                    {
                        if (_entries.Count >= MaxEntries) return;
                        _entries[key] = new RetryEntry
                        {
                            Attempts = 1, LastError = error, LastAt = DateTime.UtcNow
                        };
                    }
                }

                Save();
            }
            catch (Exception e)
            {
                Logger.Debug("MediaInfoRetry - RecordFailure failed: {0}", e.Message);
            }
        }

        /// <summary>提取成功：从队列移除（若存在）。</summary>
        public static void RecordSuccess(long internalId)
        {
            try
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    if (_entries.Remove(internalId.ToString())) Save();
                }
            }
            catch (Exception e)
            {
                Logger.Debug("MediaInfoRetry - RecordSuccess failed: {0}", e.Message);
            }
        }

        /// <summary>待重试条目（失败次数未达上限），按最近失败时间升序。</summary>
        public static List<long> GetPendingItemIds(int maxAttempts)
        {
            try
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    return _entries
                        .Where(kv => kv.Value.Attempts < Math.Max(1, maxAttempts))
                        .OrderBy(kv => kv.Value.LastAt)
                        .Select(kv => long.TryParse(kv.Key, out var id) ? id : 0)
                        .Where(id => id > 0)
                        .ToList();
                }
            }
            catch (Exception e)
            {
                Logger.Debug("MediaInfoRetry - GetPending failed: {0}", e.Message);
                return new List<long>();
            }
        }

        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoaded();
                    return _entries.Count;
                }
            }
        }

        private static DateTime _lastSaveUtc = DateTime.MinValue;

        private static void Save()
        {
            // 批量提取时逐条写盘代价高：10 秒节流，任务收尾由 Save(force) 兜底
            if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 10) return;
            SaveInternal();
        }

        public static void Save(bool force)
        {
            if (force) SaveInternal();
            else Save();
        }

        private static void SaveInternal()
        {
            try
            {
                lock (_lock)
                {
                    if (_entries == null) return;
                    Plugin.JsonSerializer.SerializeToFile(_entries, RetryPath);
                    _lastSaveUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                Logger.Debug("MediaInfoRetry - Queue save failed: {0}", e.Message);
            }
        }
    }
}
