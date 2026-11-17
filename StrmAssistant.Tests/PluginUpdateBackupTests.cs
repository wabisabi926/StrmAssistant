using System;
using System.IO;
using StrmAssistant.ScheduledTask;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 在线更新的 DLL 备份保留策略：只留最近 N 份，避免每次更新永久累积 .bak。
    /// </summary>
    public class PluginUpdateBackupTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dll;

        public PluginUpdateBackupTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "strm-backup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dll = Path.Combine(_dir, "StrmAssistant.dll");
            File.WriteAllText(_dll, "current");
        }

        [Fact]
        public void Prune_KeepsNewestN_DeletesRest()
        {
            // 数组顺序 = 时间从旧到新；保留最近 2 份
            var names = new[]
            {
                "StrmAssistant.dll.bak-4.0.0.6-20261020-100000",
                "StrmAssistant.dll.bak-4.0.0.7-20261028-100000",
                "StrmAssistant.dll.bak-4.0.0.8-20261101-100000",
                "StrmAssistant.dll.bak-4.0.0.9-20261105-100000",
                "StrmAssistant.dll.bak-4.0.1.0-20261117-100000",
            };
            // 显式设置写入时间，保证排序确定（按 LastWriteTimeUtc）
            for (var i = 0; i < names.Length; i++)
            {
                var path = Path.Combine(_dir, names[i]);
                File.WriteAllText(path, names[i]);
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i));
            }

            var deleted = PluginUpdateTask.PruneOldBackups(_dll, 2);

            Assert.Equal(3, deleted);
            var remaining = Directory.GetFiles(_dir, "StrmAssistant.dll.bak-*");
            Assert.Equal(2, remaining.Length);
            Assert.Contains(Path.Combine(_dir, "StrmAssistant.dll.bak-4.0.1.0-20261117-100000"), remaining);
            Assert.Contains(Path.Combine(_dir, "StrmAssistant.dll.bak-4.0.0.9-20261105-100000"), remaining);
            // 当前 DLL 与本次备份不受影响
            Assert.True(File.Exists(_dll));
        }

        [Fact]
        public void Prune_DoesNotTouchUnrelatedFiles()
        {
            var backup = Path.Combine(_dir, "StrmAssistant.dll.bak-4.0.1.0-20261117-100000");
            var other = Path.Combine(_dir, "OtherPlugin.dll.bak-1.0.0.0-20261117-100000");
            var updateTmp = Path.Combine(_dir, ".StrmAssistant.dll.update-abc.tmp");
            File.WriteAllText(backup, "backup");
            File.WriteAllText(other, "other");
            File.WriteAllText(updateTmp, "tmp");

            var deleted = PluginUpdateTask.PruneOldBackups(_dll, 3);

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(backup));
            Assert.True(File.Exists(other));
            Assert.True(File.Exists(updateTmp));
        }

        [Fact]
        public void Prune_KeepZero_DeletesAllBackups()
        {
            File.WriteAllText(Path.Combine(_dir, "StrmAssistant.dll.bak-4.0.1.0-20261117-100000"), "x");
            File.WriteAllText(Path.Combine(_dir, "StrmAssistant.dll.bak-4.0.0.9-20261105-100000"), "y");

            var deleted = PluginUpdateTask.PruneOldBackups(_dll, 0);

            Assert.Equal(2, deleted);
            Assert.Empty(Directory.GetFiles(_dir, "StrmAssistant.dll.bak-*"));
            Assert.True(File.Exists(_dll));
        }

        [Fact]
        public void Prune_MissingDirOrDll_ReturnsZero()
        {
            Assert.Equal(0, PluginUpdateTask.PruneOldBackups(null, 3));
            Assert.Equal(0, PluginUpdateTask.PruneOldBackups("   ", 3));
            Assert.Equal(0, PluginUpdateTask.PruneOldBackups(Path.Combine(_dir, "nonexistent", "X.dll"), 3));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            }
            catch
            {
                // 临时目录清理失败不影响测试
            }
        }
    }
}
