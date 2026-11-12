using MediaBrowser.Model.Logging;
using StrmAssistant.Properties;
using System;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Core
{
    /// <summary>
    /// 内存守护：定期检查进程工作集，持续超限则通知管理员并倒计时自动重启服务器。
    /// </summary>
    public static class MemoryGuard
    {
        private static readonly object Lock = new object();
        private static Timer _timer;
        private static ILogger _logger;
        private static int _thresholdMb;
        private static int _overLimitCount;
        private static volatile bool _restarting;
        private static DateTimeOffset? _restartAtUtc;

        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
        private const int SustainedLimit = 2;
        private const int RestartCountdownSeconds = 60;

        public static void Apply(ILogger logger, bool enabled, int thresholdMb)
        {
            lock (Lock)
            {
                _logger = logger;
                _thresholdMb = thresholdMb;

                if (!enabled)
                {
                    StopInternal();
                    return;
                }

                if (_timer != null) return;

                _overLimitCount = 0;
                _timer = new Timer(Check, null, CheckInterval, CheckInterval);
                logger?.Info($"MemoryGuard - Started, threshold {thresholdMb} MB, " +
                             $"sustained {SustainedLimit} checks x {CheckInterval.TotalMinutes:0} min");
            }
        }

        /// <summary>是否处于「已判定超限、正在倒计时重启」的状态（供状态页展示）。</summary>
        public static bool IsRestartPending => _restarting;

        /// <summary>倒计时结束的时刻（UTC）；未在倒计时则为 null。</summary>
        public static DateTimeOffset? RestartAtUtc => _restartAtUtc;

        /// <summary>当前配置的内存上限（MB）。</summary>
        public static int ThresholdMb => _thresholdMb;

        public static void Stop()
        {
            lock (Lock)
            {
                StopInternal();
            }
        }

        private static void StopInternal()
        {
            _timer?.Dispose();
            _timer = null;
            _overLimitCount = 0;
            _restartAtUtc = null;
        }

        private static void Check(object state)
        {
            try
            {
                if (_restarting) return;

                using var process = Process.GetCurrentProcess();
                var workingSetMb = process.WorkingSet64 / 1024 / 1024;

                if (workingSetMb < _thresholdMb)
                {
                    _overLimitCount = 0;
                    return;
                }

                _overLimitCount++;

                _logger?.Warn($"MemoryGuard - Working set {workingSetMb} MB exceeds limit {_thresholdMb} MB " +
                              $"({_overLimitCount}/{SustainedLimit})");

                if (_overLimitCount < SustainedLimit) return;

                _restarting = true;
                _restartAtUtc = DateTimeOffset.UtcNow.AddSeconds(RestartCountdownSeconds);
                _logger?.Warn($"MemoryGuard - Triggering auto restart in {RestartCountdownSeconds}s");

                Task.Run(async () =>
                {
                    try
                    {
                        var message = Resources.ResourceManager.GetString("MemoryGuard_Notify_Restart",
                            CultureInfo.CurrentUICulture);
                        Plugin.NotificationApi?.SendMessageToAdmins(
                            string.Format(message ?? string.Empty,
                                workingSetMb, _thresholdMb, RestartCountdownSeconds), 15000);

                        await Task.Delay(TimeSpan.FromSeconds(RestartCountdownSeconds)).ConfigureAwait(false);

                        if (!Plugin.Instance.MainOptionsStore.GetOptions().Memory.EnableMemoryAutoRestart)
                        {
                            _logger?.Info("MemoryGuard - Option disabled during countdown, restart cancelled");
                            _restarting = false;
                            _restartAtUtc = null;
                            _overLimitCount = 0;
                            return;
                        }

                        Plugin.Instance.ApplicationHost.Restart();
                    }
                    catch (Exception e)
                    {
                        _logger?.Error("MemoryGuard - Auto restart failed: " + e.Message);
                        _restarting = false;
                        _restartAtUtc = null;
                        _overLimitCount = 0;
                    }
                });
            }
            catch (Exception e)
            {
                _logger?.Debug("MemoryGuard - Check failed: " + e.Message);
            }
        }
    }
}
