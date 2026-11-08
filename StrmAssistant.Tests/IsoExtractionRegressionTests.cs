using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IsoExtractionRegressionTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task DownloadRange_EnforcesHeaderAndBodyBudgets(bool sendHeaders, bool sendBody)
        {
            var path = Path.Combine(Path.GetTempPath(), "strm-range-" + Guid.NewGuid().ToString("N"));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // 在当前线程先把 Accept 挂起：不能等到 Task.Run 委托里再调用，
            // 否则高负载下线程池调度延迟可能让 finally 的 Stop() 先执行，
            // Accept 随后抛出 "Not listening"（CI 上偶发）。
            var accept = listener.AcceptTcpClientAsync();
            var server = Task.Run(async () =>
            {
                using var client = await accept;
                using var stream = client.GetStream();
                var request = new byte[8192];
                await stream.ReadAsync(request);
                if (sendHeaders)
                {
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Length: 1048576\r\nContent-Range: bytes 0-1048575/1048576\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                }
                if (sendBody) await stream.WriteAsync(new byte[1048576]);
                else await Task.Delay(1500);
            });
            try
            {
                var method = typeof(IsoMounter).GetMethod("DownloadRangeWithTimeout", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(method);
                var watch = Stopwatch.StartNew();
                var success = await Task.Run(() => (bool)method.Invoke(null, new object[]
                {
                    "http://127.0.0.1:" + port + "/fixture.iso", 0L, 1048575L, path,
                    sendBody ? 5000 : sendHeaders ? 5000 : 300, 300
                })).WaitAsync(TimeSpan.FromSeconds(8));
                var elapsed = watch.Elapsed;
                if (sendBody)
                {
                    Assert.True(success);
                    Assert.Equal(1048576, new FileInfo(path).Length);
                }
                else
                {
                    Assert.False(success);
                    Assert.True(elapsed < TimeSpan.FromMilliseconds(1400), "The stalled server should not determine completion time: " + elapsed);
                }
            }
            finally
            {
                listener.Stop();
                try
                {
                    await server.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception e) when (e is SocketException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
                {
                    // 断言已在上面完成；监听器提前关闭会打断尚未完成的 Accept，
                    // 这类清理阶段的竞态不应让用例失败。
                }
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Bluray_MultiClipNeverBecomesOneStreamWithWholeTitleChapters(int clips)
        {
            var dir = Path.Combine(Path.GetTempPath(), "strm-mpls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, @"BDMV\PLAYLIST"));
            Directory.CreateDirectory(Path.Combine(dir, @"BDMV\STREAM"));
            try
            {
                var data = BuildPlaylist(clips);
                File.WriteAllBytes(Path.Combine(dir, @"BDMV\PLAYLIST", "00001.mpls"), data);
                // The mounted filesystem adapter uses Windows separators. Mirror that lookup on Unix too.
                File.WriteAllBytes(Path.Combine(dir, @"BDMV\PLAYLIST\00001.mpls"), data);
                for (var i = 1; i <= clips; i++)
                    File.WriteAllBytes(Path.Combine(dir, @"BDMV\STREAM\" + i.ToString("D5") + ".m2ts"), new byte[] { 0x47 });
                var info = UdfTitleResolver.ResolveFromMount(dir + Path.DirectorySeparatorChar);
                Assert.NotNull(info);
                if (clips > 1)
                {
                    Assert.Null(info.RelativePath);
                    Assert.Empty(info.Chapters);
                    Assert.False(string.IsNullOrEmpty(info.UnsupportedReason));
                    var resolve = typeof(IsoMounter).GetMethod("ResolveMountedTitle", BindingFlags.NonPublic | BindingFlags.Static);
                    Assert.Null(resolve.Invoke(null, new object[] { "fixture.iso", dir + Path.DirectorySeparatorChar }));
                }
                else
                {
                    Assert.Equal(@"BDMV\STREAM\00001.m2ts", info.RelativePath);
                    Assert.Equal(new[] { 0.0, 30.0 }, info.Chapters.ToArray());
                    Assert.True(string.IsNullOrEmpty(info.UnsupportedReason));
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        private static byte[] BuildPlaylist(int clips)
        {
            const int offset = 40, itemLength = 32;
            var marks = clips == 2 ? new[] { (clip: 0, sec: 0), (clip: 1, sec: 0), (clip: 1, sec: 90) }
                : new[] { (clip: 0, sec: 0), (clip: 0, sec: 30) };
            var marker = offset + 10 + clips * (2 + itemLength);
            var data = new byte[marker + 6 + marks.Length * 14];
            void U16(int p, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(p, 2), (ushort)value);
            void U32(int p, int value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(p, 4), (uint)value);
            Encoding.ASCII.GetBytes("MPLS0100").CopyTo(data, 0);
            U32(8, offset); U32(12, marker); U16(offset + 6, clips);
            for (var i = 0; i < clips; i++)
            {
                var p = offset + 10 + i * (2 + itemLength);
                U16(p, itemLength); Encoding.ASCII.GetBytes((i + 1).ToString("D5")).CopyTo(data, p + 2);
                Encoding.ASCII.GetBytes("M2TS").CopyTo(data, p + 7);
                U32(p + 18, (i == 0 ? 60 : 120) * 45000);
            }
            U32(marker, 2 + marks.Length * 14); U16(marker + 4, marks.Length);
            for (var i = 0; i < marks.Length; i++)
            {
                var p = marker + 6 + i * 14;
                data[p + 1] = 1; U16(p + 2, marks[i].clip); U32(p + 4, marks[i].sec * 45000);
            }
            return data;
        }
    }
}
