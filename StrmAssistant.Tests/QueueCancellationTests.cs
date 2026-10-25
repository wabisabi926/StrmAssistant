using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// 并发许可生命周期（回归 SA-04）。
    ///
    /// 旧写法先 <c>await Semaphore.WaitAsync(token)</c> 取得许可，再把同一个 token 传给
    /// <c>Task.Run(action, token)</c>，释放逻辑只写在 delegate 的 <c>finally</c> 里。
    /// 一旦 token 在「取得许可之后、delegate 开始之前」被取消，delegate 可能根本不执行，
    /// <c>finally</c> 随之消失 → 许可永久丢失（容量为 1 时后续任务全部卡住）。
    ///
    /// 下面用最小样例把这条语义钉死：这是「为什么必须用 CancellationToken.None 调度」的证据。
    /// </summary>
    public class QueueCancellationTests
    {
        [Fact]
        public async Task CancelledToken_PreventsDelegateFromRunning()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ran = false;

            var task = Task.Run(() => { ran = true; }, cts.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

            // 旧写法的致命点：delegate 没跑，里面的 finally 自然也没跑
            Assert.False(ran);
        }

        [Fact]
        public async Task NoneToken_AlwaysRunsDelegate()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var ran = false;

            // 新写法：调度时不给取消令牌，取消判断放在 delegate 内部
            await Task.Run(() => { ran = true; }, CancellationToken.None);

            Assert.True(ran);
        }

        [Fact]
        public async Task PermitIsReleased_EvenWhenCancelledBeforeDelegate()
        {
            using var semaphore = new SemaphoreSlim(1, 1);
            using var cts = new CancellationTokenSource();

            await semaphore.WaitAsync(cts.Token);   // 取得唯一许可
            Assert.Equal(0, semaphore.CurrentCount);

            cts.Cancel();

            var released = 0;

            await Task.Run(() =>
            {
                try
                {
                    if (cts.IsCancellationRequested) return;
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref released);
                }
            }, CancellationToken.None);

            Assert.Equal(1, released);
            Assert.Equal(1, semaphore.CurrentCount);   // 容量守恒，后续任务仍能拿到许可
        }

        [Fact]
        public async Task ReleasingTwice_IsGuardedByCaller()
        {
            // 记录约束：许可只释放一次，重复释放会抛 SemaphoreFullException。
            // QueueManager 的 delegate 只在 finally 里释放一次，且 delegate 必然执行，
            // 所以不存在重复释放路径；这里把 SemaphoreSlim 的边界固定下来。
            using var semaphore = new SemaphoreSlim(1, 1);

            await semaphore.WaitAsync();
            semaphore.Release();

            Assert.Throws<SemaphoreFullException>(() => semaphore.Release());
        }
    }
}
