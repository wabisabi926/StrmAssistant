using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// 插件在线更新：默认每周检查一次，只通知；开启「自动下载更新」后才会替换当前 DLL。
    /// 只接受本仓库正式 Release 的版本化资产，并在备份、校验通过后才替换；不自动重启 Emby。
    /// </summary>
    public class PluginUpdateTask : IScheduledTask
    {
        private readonly IActivityManager _activityManager;

        public PluginUpdateTask(IActivityManager activityManager)
        {
            _activityManager = activityManager;
        }

        public string Key => "PluginUpdateTask";

        public string Name =>
            Resources.ResourceManager.GetString("PluginUpdateTask_Name_Check_Plugin_Updates",
                Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "PluginUpdateTask_Description_Checks_GitHub_Releases_and_optionally_updates",
            Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = (DayOfWeek)new Random().Next(7),
                TimeOfDayTicks = TimeSpan.FromMinutes(new Random().Next(24 * 4) * 15).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);

            if (!Plugin.IsPluginEnabled)
            {
                progress.Report(100);
                return;
            }

            var currentVersion = PluginUpdateService.ParseVersion(Plugin.Instance.CurrentVersion);
            var status = new PluginUpdateStatus
            {
                LastCheckUtc = DateTimeOffset.UtcNow,
                CurrentVersion = currentVersion?.ToString()
            };

            string tempFile = null;
            var keepTempFile = false;

            try
            {
                var options = Plugin.Instance.MainOptionsStore.GetOptions().PluginUpdate;
                var release = await GetLatestReleaseAsync(options, cancellationToken).ConfigureAwait(false);
                progress.Report(20);

                var latestVersion = PluginUpdateService.ParseVersion(release?.TagName);
                if (latestVersion == null)
                {
                    throw new InvalidOperationException("GitHub Release 没有可识别的版本号");
                }

                status.LatestVersion = latestVersion.ToString();

                if (!PluginUpdateService.IsNewer(currentVersion, latestVersion))
                {
                    status.State = PluginUpdateState.UpToDate;
                    SaveStatus(status);
                    Plugin.Instance.Logger.Info($"PluginUpdate - 已是最新版本 {currentVersion}");
                    progress.Report(100);
                    return;
                }

                if (!PluginUpdateService.IsSameMajor(currentVersion, latestVersion))
                {
                    status.State = PluginUpdateState.MajorAvailable;
                    status.Message = string.Format(Resources.PluginUpdate_Notify_MajorAvailable, latestVersion);
                    SaveStatus(status);
                    Notify(status.Message);
                    LogActivity(Resources.PluginUpdate_Activity_Failed, status.Message, LogSeverity.Warn);
                    progress.Report(100);
                    return;
                }

                if (!options.AutoDownloadUpdate)
                {
                    status.State = PluginUpdateState.UpdateAvailable;
                    status.Message = string.Format(Resources.PluginUpdate_Notify_UpdateAvailable, latestVersion);
                    SaveStatus(status);
                    Notify(status.Message);
                    LogActivity(Resources.PluginUpdate_Activity_UpdateAvailable, status.Message, LogSeverity.Info);
                    progress.Report(100);
                    return;
                }

                var asset = PluginUpdateService.SelectAsset(release.Assets, latestVersion);

                if (asset == null)
                {
                    throw new InvalidOperationException($"Release {latestVersion} 中没有匹配的 DLL 资产");
                }

                var localDll = ResolveLocalDll();
                if (string.IsNullOrEmpty(localDll))
                {
                    throw new InvalidOperationException("无法定位当前插件 DLL");
                }

                tempFile = Path.Combine(Path.GetDirectoryName(localDll) ?? string.Empty,
                    "." + Path.GetFileName(localDll) + ".update-" + Guid.NewGuid().ToString("N") + ".tmp");

                await DownloadAsync(asset, tempFile, options, cancellationToken).ConfigureAwait(false);
                progress.Report(60);

                VerifyDownload(asset, tempFile, currentVersion, latestVersion);
                progress.Report(80);

                var backup = localDll + ".bak-" + currentVersion + "-" +
                             DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(localDll, backup, true);

                try
                {
                    File.Move(tempFile, localDll, true);
                }
                catch (Exception e)
                {
                    keepTempFile = true;
                    throw new IOException(
                        $"替换插件文件失败，新文件已保留在 {tempFile}，原文件备份在 {backup}", e);
                }

                status.State = PluginUpdateState.Updated;
                status.Message = string.Format(Resources.PluginUpdate_Notify_Updated, latestVersion);
                SaveStatus(status);
                Notify(status.Message);
                LogActivity(Resources.PluginUpdate_Activity_Updated, status.Message, LogSeverity.Info);
                Plugin.Instance.ApplicationHost.NotifyPendingRestart();
                Plugin.Instance.Logger.Info($"PluginUpdate - 已更新到 {latestVersion}，等待重启生效");
                progress.Report(100);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                status.State = PluginUpdateState.Failed;
                status.Message = e.Message;
                SaveStatus(status);
                var message = string.Format(Resources.PluginUpdate_Notify_Failed, e.Message);
                Notify(message);
                LogActivity(Resources.PluginUpdate_Activity_Failed, message, LogSeverity.Error);
                Plugin.Instance.Logger.Error("PluginUpdate - 检查/更新失败: " + e.Message);
                progress.Report(100);
            }
            finally
            {
                if (!keepTempFile && !string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                        // 临时文件清理失败不影响结果
                    }
                }
            }
        }

        private static async Task<PluginReleaseInfo> GetLatestReleaseAsync(PluginUpdateOptions options,
            CancellationToken cancellationToken)
        {
            var request = new HttpRequestOptions
            {
                Url = WithProxy(PluginUpdateService.LatestReleaseUrl, options.GitHubProxy),
                CancellationToken = cancellationToken,
                AcceptHeader = "application/vnd.github+json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent,
                EnableDefaultUserAgent = false
            };

            if (!string.IsNullOrWhiteSpace(options.GitHubToken))
            {
                request.RequestHeaders["Authorization"] = "Bearer " + options.GitHubToken.Trim();
            }

            using var response = await Plugin.HttpClient.SendAsync(request, "GET").ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"GitHub API 返回 {(int)response.StatusCode} {response.StatusCode}");
            }

            using var document = await JsonDocument.ParseAsync(response.Content, default, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
            {
                throw new InvalidOperationException("最新 Release 是草稿或预发布版本");
            }

            var release = new PluginReleaseInfo
            {
                TagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null
            };

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    release.Assets.Add(new PluginReleaseAsset
                    {
                        Name = asset.TryGetProperty("name", out var name) ? name.GetString() : null,
                        DownloadUrl = asset.TryGetProperty("browser_download_url", out var url)
                            ? url.GetString()
                            : null,
                        Digest = asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null,
                        Size = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var length)
                            ? length
                            : (long?)null
                    });
                }
            }

            return release;
        }

        private static async Task DownloadAsync(PluginReleaseAsset asset, string tempFile,
            PluginUpdateOptions options, CancellationToken cancellationToken)
        {
            var request = new HttpRequestOptions
            {
                Url = WithProxy(asset.DownloadUrl, options.GitHubProxy),
                CancellationToken = cancellationToken,
                UserAgent = Plugin.Instance.UserAgent,
                EnableDefaultUserAgent = false
            };

            if (!string.IsNullOrWhiteSpace(options.GitHubToken))
            {
                request.RequestHeaders["Authorization"] = "Bearer " + options.GitHubToken.Trim();
            }

            using var response = await Plugin.HttpClient.Get(request).ConfigureAwait(false);
            await using var file = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None,
                81920, true);
            await response.CopyToAsync(file, 81920, cancellationToken).ConfigureAwait(false);
        }

        private static void VerifyDownload(PluginReleaseAsset asset, string tempFile, Version currentVersion,
            Version latestVersion)
        {
            if (asset.Size.HasValue && asset.Size.Value > 0 &&
                new FileInfo(tempFile).Length != asset.Size.Value)
            {
                throw new InvalidOperationException("下载文件大小与 Release 资产不一致");
            }

            var expected = PluginUpdateService.GetDigestHex(asset.Digest);
            if (!string.IsNullOrEmpty(expected))
            {
                var actual = PluginUpdateService.ComputeSha256(tempFile);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("下载文件 SHA-256 校验失败");
                }
            }

            var assemblyName = AssemblyName.GetAssemblyName(tempFile);
            var currentName = Assembly.GetExecutingAssembly().GetName().Name;
            if (!string.Equals(assemblyName.Name, currentName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("下载文件不是当前插件程序集");
            }

            if (assemblyName.Version != latestVersion || !PluginUpdateService.IsNewer(currentVersion, latestVersion))
            {
                throw new InvalidOperationException("下载文件版本与 Release 不一致");
            }
        }

        /// <summary>
        /// Emby 可能从内存加载插件，Assembly.Location 为空；此时按程序集名在 plugins 目录查找。
        /// </summary>
        private static string ResolveLocalDll()
        {
            var assembly = Assembly.GetExecutingAssembly();
            if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
            {
                return assembly.Location;
            }

            var pluginsPath = Plugin.Instance?.ApplicationPaths?.PluginsPath;
            if (string.IsNullOrEmpty(pluginsPath) || !Directory.Exists(pluginsPath)) return null;

            var assemblyName = assembly.GetName().Name;
            var preferred = Path.Combine(pluginsPath, assemblyName + ".dll");
            if (File.Exists(preferred)) return preferred;

            foreach (var file in Directory.EnumerateFiles(pluginsPath, "*.dll"))
            {
                try
                {
                    if (string.Equals(AssemblyName.GetAssemblyName(file).Name, assemblyName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
                catch
                {
                    // 非 .NET DLL 或损坏文件，跳过
                }
            }

            return null;
        }

        private static string WithProxy(string url, string proxy)
        {
            if (string.IsNullOrWhiteSpace(proxy)) return url;
            return proxy.TrimEnd('/') + "/" + url.TrimStart('/');
        }

        private static void SaveStatus(PluginUpdateStatus status)
        {
            if (!PluginUpdateService.Save(status))
            {
                Plugin.Instance.Logger.Warn("PluginUpdate - 更新状态写入失败");
            }
        }

        private static void Notify(string message) =>
            Plugin.NotificationApi?.SendMessageToAdmins(
                $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {message}", 12000);

        private void LogActivity(string name, string overview, LogSeverity severity)
        {
            try
            {
                _activityManager?.Create(new ActivityLogEntry
                {
                    Name = $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {name}",
                    Type = "PluginUpdate",
                    Overview = overview,
                    Severity = severity
                });
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("PluginUpdate - 活动日志写入失败: " + e.Message);
            }
        }

        private sealed class PluginReleaseInfo
        {
            public string TagName { get; set; }
            public List<PluginReleaseAsset> Assets { get; } = new List<PluginReleaseAsset>();
        }
    }
}
