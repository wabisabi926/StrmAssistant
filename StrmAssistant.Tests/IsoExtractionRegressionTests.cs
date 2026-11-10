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
using StrmAssistant.Mod;
using Xunit;

namespace StrmAssistant.Tests
{
    public class IsoExtractionRegressionTests
    {
        [Theory]
        [InlineData(30.7, 30.7, true)]
        [InlineData(30.0, 35.0, true)]
        [InlineData(30.0, 35.1, false)]
        [InlineData(30.7, 90.0, false)]
        [InlineData(0.0, 30.0, false)]
        [InlineData(30.0, 0.0, false)]
        public void RuntimesMatch_ToleratesFiveSeconds(double probedSeconds, double playlistSeconds, bool expected)
        {
            Assert.Equal(expected, IsoExtraction.RuntimesMatch(probedSeconds, playlistSeconds));
        }

        [IsoSampleFact]
        public void LocalIsoSample_BuildsProbeFileAndHint()
        {
            var isoPath = IsoSample.ResolvedPath;

            var sample = IsoMounter.GetLocalProbePath(isoPath);

            Assert.NotNull(sample);
            Assert.True(File.Exists(sample));
            Assert.True(new FileInfo(sample).Length > 0);
            Assert.True(IsoMounter.TryGetTitleHint(isoPath, out var hint));
            Assert.True(hint.IsProbeSample);
            Assert.True(hint.DurationSeconds > 0);

            // 同一 ISO 第二次应命中缓存，不再生成新文件
            Assert.Equal(sample, IsoMounter.GetLocalProbePath(isoPath));

            File.Delete(sample);
        }

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
                // stalled server 故意拖得足够久：CI 共享 runner 的线程调度抖动可达数秒，
                // 判定阈值必须远大于抖动、又远小于 stall，否则「客户端是否在预算内自行中止」会误判。
                else await Task.Delay(12000);
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
                })).WaitAsync(TimeSpan.FromSeconds(20));
                var elapsed = watch.Elapsed;
                if (sendBody)
                {
                    Assert.True(success);
                    Assert.Equal(1048576, new FileInfo(path).Length);
                }
                else
                {
                    Assert.False(success);
                    Assert.True(elapsed < TimeSpan.FromSeconds(10), "The stalled server should not determine completion time: " + elapsed);
                }
            }
            finally
            {
                listener.Stop();
                // server 仍在模拟 stalled body，不等待它结束；只需观察异常，避免未观察任务异常。
                _ = server.ContinueWith(t => { var _ = t.Exception; }, TaskScheduler.Default);
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
