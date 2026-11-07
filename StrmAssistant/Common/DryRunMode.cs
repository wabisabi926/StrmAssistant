using MediaBrowser.Model.Logging;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 演练模式：破坏性操作只记录日志，不写库、不删文件。
    /// 由「体验增强 → 安全 → 演练模式」控制，默认关闭。
    /// </summary>
    public static class DryRunMode
    {
        public static bool Enabled =>
            Plugin.Instance?.ExperienceEnhanceStore?.GetOptions()?.Safety?.DryRun == true;

        /// <summary>开启演练时记录一条日志并返回 true，调用方应直接跳过写操作。</summary>
        public static bool Skip(string action, ILogger logger = null)
        {
            if (!Enabled) return false;

            (logger ?? Plugin.Instance?.Logger)?.Info("DryRun - skipped: " + action);
            return true;
        }
    }
}
