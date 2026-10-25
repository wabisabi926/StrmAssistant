using System;
using System.Threading.Tasks;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 后台任务统一出口：异常必须被吞掉并记录，任务计数必须归零。
    /// 之前 `_ = Task.Run(...)` 丢弃任务引用，内部异常没人观察，表现为「功能没生效但日志干净」。
    /// </summary>
    public class BackgroundTaskRunnerTests
    {
        [Fact]
        public async Task RunsBody()
        {
            var ran = false;

            await BackgroundTaskRunner.Run("test.runs", () => { ran = true; });

            Assert.True(ran);
        }

        [Fact]
        public async Task SwallowsExceptions_AndRestoresCounter()
        {
            await BackgroundTaskRunner.Run("test.throws", () => throw new InvalidOperationException("boom"));

            // 不抛到调用方，且计数归零（等待后应稳定为 0）
            await Task.Delay(50);
            Assert.Equal(0, BackgroundTaskRunner.RunningCount);
        }

        [Fact]
        public async Task HandlesNullBody()
        {
            await BackgroundTaskRunner.Run("test.null", (Action)null);
            await BackgroundTaskRunner.Run("test.null2", (Func<Task>)null);

            Assert.Equal(0, BackgroundTaskRunner.RunningCount);
        }

        [Fact]
        public async Task RunsAsyncBody()
        {
            var value = 0;

            await BackgroundTaskRunner.Run("test.async", async () =>
            {
                await Task.Yield();
                value = 42;
            });

            Assert.Equal(42, value);
        }
    }
}
