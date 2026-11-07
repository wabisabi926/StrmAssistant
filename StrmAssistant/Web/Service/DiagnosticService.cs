using MediaBrowser.Controller.Api;
using StrmAssistant.Core;
using StrmAssistant.Mod;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// 诊断端点：把已有的 HealthCheck + PatchManager 诊断能力对外暴露，
    /// 补丁出问题时 curl 即可自查，不必翻 Debug 日志或反编译。
    /// 仅管理员（含服务端 API Key）可访问：返回值含内部补丁状态与异常原因。
    /// </summary>
    public class DiagnosticService : BaseApiService
    {
        public class PatchSummary
        {
            public int Total { get; set; }
            public int Supported { get; set; }
            public int Running { get; set; }
            public int Failed { get; set; }
            public int Unsupported { get; set; }
        }

        public class HealthReportResponse
        {
            public bool Enabled { get; set; }
            public string OverallStatus { get; set; }
            public Dictionary<string, string> Components { get; set; }
            public List<string> Issues { get; set; }
            public PatchSummary Patches { get; set; }
            public string DiagnosticReport { get; set; }
        }

        public class PatchStatus
        {
            public string Name { get; set; }
            public bool IsSupported { get; set; }
            public bool IsRunning { get; set; }
            public bool IsCoreFeature { get; set; }
            public string Approach { get; set; }
            public bool HasErrors { get; set; }
            public List<string> Errors { get; set; }
        }

        public class HookSelfTestResponse
        {
            public bool Enabled { get; set; }
            public PatchSummary Summary { get; set; }
            public List<PatchStatus> Patches { get; set; }
        }

        private bool IsAdmin()
        {
            var user = GetUserForRequest(null, true);
            return user == null || user.Policy.IsAdministrator;
        }

        public object Get(GetHealthReport request)
        {
            if (!IsAdmin()) return null;

            var resp = new HealthReportResponse
            {
                Enabled = Plugin.IsPluginEnabled,
                Components = new Dictionary<string, string>(),
                Issues = new List<string>()
            };

            try
            {
                var hc = HealthCheck.Instance.PerformHealthCheck();
                resp.OverallStatus = hc.OverallStatus.ToString();
                resp.Components = hc.ComponentStatus
                    .ToDictionary(c => c.Key, c => c.Value.ToString());
                resp.Issues = hc.Issues.ToList();
            }
            catch (Exception ex)
            {
                resp.OverallStatus = "Critical";
                resp.Issues.Add("HealthCheck unavailable: " + ex.Message);
            }

            try
            {
                resp.Patches = BuildSummary(PatchManager.PatchTrackerList);
            }
            catch (Exception ex)
            {
                resp.Patches = new PatchSummary { Total = -1 };
                resp.Issues.Add("PatchManager unavailable: " + ex.Message);
            }

            try
            {
                resp.DiagnosticReport = HealthCheck.Instance.GenerateDiagnosticReport();
            }
            catch
            {
            }

            return resp;
        }

        public object Get(GetHookSelfTest request)
        {
            if (!IsAdmin()) return null;

            var resp = new HookSelfTestResponse
            {
                Enabled = Plugin.IsPluginEnabled,
                Patches = new List<PatchStatus>()
            };

            try
            {
                var list = PatchManager.PatchTrackerList;
                resp.Summary = BuildSummary(list);
                resp.Patches = list
                    .OrderBy(p => p.PatchType.Name)
                    .Select(p => new PatchStatus
                    {
                        Name = p.PatchType.Name,
                        IsSupported = p.IsSupported,
                        IsRunning = p.IsRunning,
                        IsCoreFeature = p.IsCoreFeature,
                        Approach = p.FallbackPatchApproach == p.DefaultPatchApproach
                            ? p.DefaultPatchApproach.ToString()
                            : $"{p.DefaultPatchApproach} -> {p.FallbackPatchApproach}",
                        HasErrors = p.HasErrors,
                        Errors = p.ErrorMessages.Take(3).ToList()
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                resp.Summary = new PatchSummary { Total = -1 };
                resp.Patches.Add(new PatchStatus
                {
                    Name = "HookSelfTest",
                    HasErrors = true,
                    Errors = new List<string> { "PatchManager unavailable: " + ex.Message }
                });
            }

            return resp;
        }

        private static PatchSummary BuildSummary(List<PatchTracker> list)
        {
            var supported = list.Where(p => p.IsSupported).ToList();
            return new PatchSummary
            {
                Total = list.Count,
                Supported = supported.Count,
                Running = supported.Count(p => p.IsRunning),
                Failed = supported.Count(p => !p.IsRunning),
                Unsupported = list.Count(p => !p.IsSupported)
            };
        }
    }
}
