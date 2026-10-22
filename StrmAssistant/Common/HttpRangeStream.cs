using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 只读、可 Seek 的 HTTP 字节流：按 1MB 块懒加载，块级 LRU 缓存。
    ///
    /// 用途：把远程 ISO（strm → http）伪装成"本地可随机访问的文件"，喂给 <c>DiscUtils.Udf</c>。
    /// DiscUtils 构造时会校验分区长度 ≤ 流长度，所以 **<see cref="Length"/> 必须是真实总长**
    /// （旧方案只下载 8MB 头部窗口，长度不对 → 直接抛
    /// <c>Substream extends beyond end of parent stream</c>）。
    ///
    /// 不支持 Range 的服务器（对 Range 请求回 200 全量）→ <see cref="TryOpen"/> 返回 false，
    /// 调用方应静默回退，不能因为拿不到章节把探测搞挂。
    /// </summary>
    internal sealed class HttpRangeStream : Stream
    {
        /// <summary>块大小。UDF 元数据区通常几百 KB，1MB 块足够覆盖一次解析的读放大。</summary>
        private const int BlockSize = 1 << 20;

        /// <summary>块缓存上限（64 块 = 64MB）。超出按 LRU 淘汰。</summary>
        private const int MaxCachedBlocks = 64;

        private const int MaxAttempts = 3;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>整个流的解析预算：超过后块读取直接返回 null（UDF 解析随之失败，静默回退）。</summary>
        private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(90);

        /// <summary>单次读取允许的最长空闲：连续这么久没有新字节就放弃本次尝试。</summary>
        private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(20);

        private readonly HttpClient _client;
        private readonly string _url;
        private readonly long _length;
        private readonly DateTime _deadline;
        private readonly object _sync = new object();

        /// <summary>外部中止（Dispose）时打断在途读取，避免留下阻塞线程。</summary>
        private readonly CancellationTokenSource _abort = new CancellationTokenSource();

        private readonly Dictionary<long, LinkedListNode<BlockEntry>> _blocks =
            new Dictionary<long, LinkedListNode<BlockEntry>>();

        private readonly LinkedList<BlockEntry> _lru = new LinkedList<BlockEntry>();
        private long _position;

        private sealed class BlockEntry
        {
            public long Index;
            public byte[] Data;
        }

        private HttpRangeStream(HttpClient client, string url, long length, TimeSpan budget)
        {
            _client = client;
            _url = url;
            _length = length;
            _deadline = DateTime.UtcNow + budget;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        /// <summary>
        /// 探测远程文件是否支持 Range 并取到真实总长。
        /// 失败（不支持 Range / 无长度 / 网络错误）返回 false，<paramref name="stream"/> 为 null。
        /// </summary>
        public static bool TryOpen(string url, out HttpRangeStream stream, out string error, TimeSpan? budget = null)
        {
            stream = null;
            error = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                error = "empty url";
                return false;
            }

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None
            };

            var client = new HttpClient(handler) { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Emby/4.10 StrmAssistant");

            try
            {
                if (!TryGetLength(client, url, out var length, out error))
                {
                    client.Dispose();
                    return false;
                }

                stream = new HttpRangeStream(client, url, length, budget ?? DefaultBudget);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                client.Dispose();
                return false;
            }
        }

        /// <summary>
        /// 取真实总长，并**验证服务器确实支持 Range**。
        ///
        /// 只拿到 Content-Length 不够：不支持 Range 的服务器对块请求会回 200 全量，
        /// 流会读不出数据（还会白拉整个文件）。所以必须发一次 <c>bytes=0-0</c> 探针确认 206。
        /// </summary>
        private static bool TryGetLength(HttpClient client, string url, out long length, out string error)
        {
            length = 0;
            error = null;

            long? headLength = null;

            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = client.Send(head, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is long value && value > 0)
                {
                    headLength = value;
                }
            }
            catch (Exception)
            {
                // HEAD 不被支持，下面用 Range 探针兜底
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 0);

                using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    error = $"server does not honor Range (status {(int)response.StatusCode})";
                    return false;
                }

                if (response.Content.Headers.ContentRange?.Length is long total && total > 0)
                {
                    length = total;
                    return true;
                }

                if (headLength.HasValue)
                {
                    length = headLength.Value;
                    return true;
                }

                error = "Content-Range missing length";
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            var total = 0;

            while (count > 0 && _position < _length)
            {
                var blockIndex = _position / BlockSize;
                var blockOffset = (int)(_position % BlockSize);
                var block = GetBlock(blockIndex);

                if (block == null || blockOffset >= block.Length) break;

                var take = Math.Min(count, block.Length - blockOffset);
                Buffer.BlockCopy(block, blockOffset, buffer, offset, take);

                _position += take;
                offset += take;
                count -= take;
                total += take;
            }

            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (target < 0) throw new IOException("attempt to seek before beginning of stream");

            _position = target;
            return _position;
        }

        public override void Flush()
        {
            // 只读流
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private byte[] GetBlock(long index)
        {
            lock (_sync)
            {
                if (_blocks.TryGetValue(index, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.Data;
                }
            }

            var data = FetchBlock(index);
            if (data == null) return null;

            lock (_sync)
            {
                if (_blocks.TryGetValue(index, out var existing)) return existing.Value.Data;

                var node = _lru.AddFirst(new BlockEntry { Index = index, Data = data });
                _blocks[index] = node;

                while (_lru.Count > MaxCachedBlocks)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _blocks.Remove(last.Value.Index);
                }
            }

            return data;
        }

        /// <summary>取一个块；失败重试 <see cref="MaxAttempts"/> 次，仍失败返回 null（读取端据此提前结束）。</summary>
        private byte[] FetchBlock(long index)
        {
            var start = index * BlockSize;
            var end = Math.Min(start + BlockSize, _length) - 1;
            if (end < start) return null;

            var expected = (int)(end - start + 1);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // 总预算耗尽：不再重试，让上层静默回退（避免把探测线程拖死）
                if (DateTime.UtcNow > _deadline) return null;

                // 关键：超时令牌必须覆盖「响应头 + 响应体」全过程。
                // ResponseHeadersRead 下 HttpClient.Timeout 只管到读完响应头，正文读取需要自带令牌，
                // 否则服务器回了 206 头后停传会让 source.Read 永久阻塞，_deadline 再无机会被检查。
                try
                {
                    if (_abort.IsCancellationRequested) return null;

                    var remaining = _deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return null;

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
                    cts.CancelAfter(remaining < ReadIdleTimeout ? remaining : ReadIdleTimeout);

                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.Headers.Range = new RangeHeaderValue(start, end);

                    using var response = _client
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .GetAwaiter().GetResult();

                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        // 服务器忽略了 Range（回 200 全量）：读放大不可控，直接放弃
                        Plugin.Instance?.Logger?.Debug(
                            $"HttpRangeStream - block {index} ignored Range (status {(int)response.StatusCode})");
                        return null;
                    }

                    using var source = response.Content.ReadAsStream();
                    var buffer = new byte[expected];
                    var read = 0;

                    while (read < expected)
                    {
                        var n = source.ReadAsync(buffer, read, expected - read, cts.Token)
                                        .GetAwaiter().GetResult();
                        if (n <= 0) break;
                        read += n;

                        // 有进展就重置空闲计时；总预算仍在每次循环里守
                        if (DateTime.UtcNow > _deadline) return null;
                        cts.CancelAfter(ReadIdleTimeout);
                    }

                    if (read == 0) return null;
                    if (read == expected) return buffer;

                    // 短读：截断到实际长度返回（末尾块可能短于块大小）
                    var trimmed = new byte[read];
                    Buffer.BlockCopy(buffer, 0, trimmed, 0, read);
                    return trimmed;
                }
                catch (OperationCanceledException)
                {
                    // 空闲超时 / 预算耗尽 / 外部中止：重试只会重复同样的等待，直接放弃
                    Plugin.Instance?.Logger?.Debug(
                        $"HttpRangeStream - block {index} aborted by timeout/cancel (attempt {attempt})");
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    // Dispose 与在途读取竞争：静默放弃
                    return null;
                }
                catch (Exception e)
                {
                    if (attempt == MaxAttempts)
                    {
                        // 不吞掉原因：解析失败时靠这条 Debug 定位是超时、断流还是代理拒绝
                        Plugin.Instance?.Logger?.Debug(
                            $"HttpRangeStream - block {index} failed after {attempt} attempts: {e.Message}");
                        return null;
                    }

                    Thread.Sleep(200 * attempt);
                }
            }

            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 先中止在途读取，再释放客户端，避免留下阻塞在 socket 读上的线程
                try { _abort.Cancel(); } catch { /* ignored */ }
                _client.Dispose();
                _abort.Dispose();

                lock (_sync)
                {
                    _blocks.Clear();
                    _lru.Clear();
                }
            }

            base.Dispose(disposing);
        }
    }
}
