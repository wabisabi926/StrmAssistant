using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using StrmAssistant.Common;
using Xunit;

namespace StrmAssistant.Tests
{
    /// <summary>
    /// HttpRangeStream：把远程文件伪装成可随机访问的流，喂给 DiscUtils 解析 UDF。
    /// 块边界 / 非对齐读 / 不支持 Range 的服务器，是最容易出错的三处，逐一钉死。
    /// </summary>
    public class HttpRangeStreamTests
    {
        private static byte[] BuildContent(int size)
        {
            var data = new byte[size];
            var seed = 0x12345678u;

            for (var i = 0; i < size; i++)
            {
                seed = seed * 1664525 + 1013904223;
                data[i] = (byte)(seed >> 24);
            }

            return data;
        }

        [Fact]
        public void Length_ComesFromServer()
        {
            var content = BuildContent(3 * 1024 * 1024 + 777);

            using var server = new RangeHttpServer(content);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                Assert.Equal(content.Length, stream.Length);
                Assert.True(stream.CanSeek);
                Assert.Equal(0, stream.Position);
            }
        }

        [Fact]
        public void Reads_AcrossBlockBoundaries()
        {
            var content = BuildContent(3 * 1024 * 1024 + 777);
            using var server = new RangeHttpServer(content);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                var read = new byte[content.Length];
                var offset = 0;
                var chunk = 300 * 1024;   // 刻意不整除 1MB 块

                while (offset < read.Length)
                {
                    var n = stream.Read(read, offset, Math.Min(chunk, read.Length - offset));
                    if (n <= 0) break;
                    offset += n;
                }

                Assert.Equal(content.Length, offset);
                Assert.Equal(content, read);
            }
        }

        [Fact]
        public void SeekThenRead_HandlesNonAlignedOffset()
        {
            var content = BuildContent(2 * 1024 * 1024 + 5);
            using var server = new RangeHttpServer(content);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                const int position = 1024 * 1024 - 500;   // 跨块边界附近
                stream.Seek(position, SeekOrigin.Begin);

                var buffer = new byte[2000];
                var n = stream.Read(buffer, 0, buffer.Length);

                Assert.Equal(2000, n);
                Assert.Equal(content.AsSpan(position, 2000).ToArray(), buffer);
                Assert.Equal(position + 2000, stream.Position);
            }
        }

        [Fact]
        public void SeekFromEnd_ReadsTail()
        {
            var content = BuildContent(1024 * 1024 + 100);
            using var server = new RangeHttpServer(content);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                stream.Seek(-10, SeekOrigin.End);

                var buffer = new byte[10];
                var n = stream.Read(buffer, 0, buffer.Length);

                Assert.Equal(10, n);
                Assert.Equal(content.AsSpan(content.Length - 10).ToArray(), buffer);
            }
        }

        [Fact]
        public void Read_AtEnd_ReturnsZero()
        {
            var content = BuildContent(1024);
            using var server = new RangeHttpServer(content);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                stream.Seek(content.Length, SeekOrigin.Begin);
                Assert.Equal(0, stream.Read(new byte[16], 0, 16));
            }
        }

        [Fact]
        public void ServerWithoutRangeSupport_ReturnsFalse()
        {
            var content = BuildContent(1024);
            using var server = new RangeHttpServer(content, supportsRange: false);

            Assert.False(HttpRangeStream.TryOpen(server.Url, out var stream, out var error));
            Assert.Null(stream);
            Assert.False(string.IsNullOrEmpty(error));
        }

        /// <summary>
        /// 回归（F2）：服务器回了 206 响应头后**不发送正文**时，块读取必须在预算内中止。
        ///
        /// 修复前 <c>source.Read</c> 是同步、无取消令牌的，且 <c>_deadline</c> 只在重试循环头检查，
        /// 一旦服务器停传就会永久阻塞（预算形同虚设），进而长期占用提取队列的并发名额。
        /// 修复后用 CancellationTokenSource 把超时覆盖到响应体读取上。
        /// </summary>
        [Fact]
        public void BodyStall_IsAbortedWithinBudget()
        {
            var content = BuildContent(2 * 1024 * 1024);
            using var server = new RangeHttpServer(content, stallBodyMs: 5000);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error,
                TimeSpan.FromMilliseconds(400)), error);

            using (stream)
            {
                var sw = Stopwatch.StartNew();
                var n = stream.Read(new byte[4096], 0, 4096);
                sw.Stop();

                Assert.Equal(0, n);   // 拿不到数据 → 上层静默回退
                Assert.True(sw.ElapsedMilliseconds < 2000,
                    $"应在预算内中止，实际耗时 {sw.ElapsedMilliseconds}ms（修复前会等到服务器发数据，约 5000ms）");
            }
        }

        /// <summary>
        /// 回归（F2）：正文正常时必须照旧读满，确认超时改造没有把正常路径改坏。
        /// </summary>
        [Fact]
        public void NormalBody_StillReadsFully_AfterTimeoutChange()
        {
            var content = BuildContent(1024 * 1024 + 321);
            using var server = new RangeHttpServer(content);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                var buffer = new byte[content.Length];
                var offset = 0;

                while (offset < buffer.Length)
                {
                    var n = stream.Read(buffer, offset, buffer.Length - offset);
                    if (n <= 0) break;
                    offset += n;
                }

                Assert.Equal(content.Length, offset);
                Assert.Equal(content, buffer);
            }
        }

        /// <summary>
        /// 端到端：真实 ISO 通过 HTTP Range 提供，走 HttpRangeStream → UdfTitleResolver 解析章节。
        /// 这正是远程 ISO（strm → http）的完整链路，只是把远端换成本地回环。
        /// </summary>
        [IsoSampleFact]
        public void RemoteIso_ResolvesPlaylistChapters_OverRangeStream()
        {
            var isoPath = IsoSample.ResolvedPath;

            using var server = new RangeHttpServer(isoPath);

            Assert.True(HttpRangeStream.TryOpen(server.Url, out var stream, out var error), error);

            using (stream)
            {
                Assert.Equal(new FileInfo(isoPath).Length, stream.Length);

                var title = UdfTitleResolver.Resolve(stream, server.Url);

                Assert.NotNull(title);
                Assert.True(title.IsBluray, "应识别为 Blu-ray 结构");
                Assert.Contains("STREAM", title.RelativePath, StringComparison.OrdinalIgnoreCase);
                Assert.NotEmpty(title.Chapters);
            }
        }

        /// <summary>极简 HTTP 服务器：支持 HEAD / Range（可关闭），足以覆盖被测行为。</summary>
        private sealed class RangeHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly byte[] _content;
            private readonly string _filePath;
            private readonly long _length;
            private readonly bool _supportsRange;
            private readonly int _stallBodyMs;
            private readonly Thread _acceptThread;
            private volatile bool _disposed;

            public string Url { get; }

            public RangeHttpServer(byte[] content, bool supportsRange = true, int stallBodyMs = 0)
            {
                _content = content;
                _length = content.Length;
                _supportsRange = supportsRange;
                _stallBodyMs = stallBodyMs;

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Url = $"http://127.0.0.1:{port}/test.iso";

                _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
                _acceptThread.Start();
            }

            /// <summary>文件模式：按需读取区间，不把整个 ISO 读进内存。</summary>
            public RangeHttpServer(string filePath)
            {
                _filePath = filePath;
                _length = new FileInfo(filePath).Length;
                _supportsRange = true;
                _stallBodyMs = 0;

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Url = $"http://127.0.0.1:{port}/test.iso";

                _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
                _acceptThread.Start();
            }

            private void AcceptLoop()
            {
                while (!_disposed)
                {
                    TcpClient client;

                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    ThreadPool.QueueUserWorkItem(_ => Handle(client));
                }
            }

            private void Handle(TcpClient client)
            {
                try
                {
                    HandleCore(client);
                }
                catch (Exception)
                {
                    // 客户端提前断开 / 文件不可读：服务器线程不能因此把整个测试进程带崩
                }
                finally
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                }
            }

            private void HandleCore(TcpClient client)
            {
                using (var stream = client.GetStream())
                {
                    var request = ReadHeaders(stream);
                    if (request == null) return;

                    var isHead = request.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase);
                    var rangeMatch = Regex.Match(request, @"Range:\s*bytes=(\d+)-(\d*)", RegexOptions.IgnoreCase);
                    var hasRange = rangeMatch.Success && _supportsRange;

                    long start = 0;
                    long end = _length - 1;

                    if (hasRange)
                    {
                        start = long.Parse(rangeMatch.Groups[1].Value);
                        end = rangeMatch.Groups[2].Value.Length > 0
                            ? long.Parse(rangeMatch.Groups[2].Value)
                            : _length - 1;
                        end = Math.Min(end, _length - 1);
                    }

                    var length = (int)Math.Max(0, end - start + 1);

                    var header = new StringBuilder();
                    header.Append(hasRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                    header.Append("Accept-Ranges: bytes\r\n");
                    header.Append($"Content-Length: {length}\r\n");
                    if (hasRange) header.Append($"Content-Range: bytes {start}-{end}/{_length}\r\n");
                    header.Append("Connection: close\r\n\r\n");

                    var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    stream.Flush();

                    if (isHead || length <= 0) return;

                    // 模拟「回了 206 响应头但不发送正文」的云盘/代理。
                    // 只对真正的块请求生效（0-0 探针的 length 为 1），否则 TryOpen 也会被卡住。
                    if (_stallBodyMs > 0 && length > 1)
                    {
                        Thread.Sleep(_stallBodyMs);
                        return;
                    }

                    if (_filePath == null)
                    {
                        stream.Write(_content, (int)start, length);
                    }
                    else
                    {
                        using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
                        file.Seek(start, SeekOrigin.Begin);

                        var buffer = new byte[64 * 1024];
                        var remaining = length;

                        while (remaining > 0)
                        {
                            var n = file.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                            if (n <= 0) break;
                            stream.Write(buffer, 0, n);
                            remaining -= n;
                        }
                    }

                    stream.Flush();
                }
            }

            private static string ReadHeaders(NetworkStream stream)
            {
                var buffer = new byte[8192];
                var sb = new StringBuilder();

                while (true)
                {
                    var n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) return null;

                    sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    if (sb.ToString().Contains("\r\n\r\n")) return sb.ToString();
                    if (sb.Length > 16384) return null;
                }
            }

            public void Dispose()
            {
                _disposed = true;

                try
                {
                    _listener.Stop();
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
        }
    }
}
