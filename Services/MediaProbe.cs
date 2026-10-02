using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http;

namespace Jicun.Desktop.Services;

/// <summary>
/// 读视频文件本身，问出它的真实分辨率。
///
/// 为什么需要它：上游那条路只给抖音的根节点带 width/height；快手的「原画」那一档
/// 应答里**一个分辨率字段都没有**（label/quality 都是「原画」两个字），只有 720p
/// 那几档带宽高。想让用户看到真实分辨率，就只能自己去文件头上解。
///
/// 只读 MP4 的 moov/tkhd，不下载视频内容：先 Range 取头部 1MB，moov 不在头就去取尾部
/// 2MB（很多 mp4 没做 faststart，moov 压在文件尾）。两次都拿不到就认输，标签保持原样。
/// </summary>
public static class MediaProbe
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // 同一份文件在一次会话里可能被问好几遍（根节点和 video_backup 是同一个片源的不同
    // 地址）。缓存 key 用「主机 + 路径」，把 query 里的签名去掉 —— 签名会变，路径不会。
    private static readonly ConcurrentDictionary<string, (int Width, int Height)?> Cache = new();

    private const int HeadBytes = 1 << 20;   // 1MB
    private const int TailBytes = 2 << 20;   // 2MB

    /// <summary>
    /// 问出这个地址的分辨率。失败返回 null（调用方保持原标签，不要瞎猜）。
    /// </summary>
    public static async Task<(int Width, int Height)?> ResolutionAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var key = CacheKey(url);
        if (key is not null && Cache.TryGetValue(key, out var hit)) return hit;

        (int Width, int Height)? found = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            found = await ProbeAsync(url, linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 探不到就当没有 —— 这条路是锦上添花，绝不能让解析失败。
        }

        if (key is not null) Cache[key] = found;
        return found;
    }

    private static async Task<(int Width, int Height)?> ProbeAsync(string url, CancellationToken ct)
    {
        var head = await RangeAsync(url, 0, HeadBytes - 1, ct).ConfigureAwait(false);
        if (head is null) return null;

        if (FindTrackSize(head.Value.Body) is { } fromHead) return fromHead;

        // 头部没有 moov：多半压在文件尾。得先知道文件多大。
        var total = head.Value.Total;
        if (total <= 0 || total <= HeadBytes) return null;

        var from = Math.Max(0, total - TailBytes);
        var tail = await RangeAsync(url, from, total - 1, ct).ConfigureAwait(false);
        if (tail is null) return null;

        return FindTrackSize(tail.Value.Body);
    }

    /// <summary>发一次 Range 请求，最多收 <paramref name="end"/>-<paramref name="start"/>+1 字节。</summary>
    private static async Task<(byte[] Body, long Total)?> RangeAsync(string url, long start, long end, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Range", "bytes=" + start + "-" + end);

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        // Content-Range: bytes 0-1048575/7807454953 —— 末尾是文件总长。
        long total = 0;
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.Length is { } length) total = length;
        else if (contentRange?.To is { } to) total = to + 1;   // 服务端只给了区间
        else if (response.Content.Headers.ContentLength is { } cl) total = cl;

        // 别用 ReadAsByteArrayAsync：服务端可能忽略 Range 直接吐整个文件（几百 MB）。
        // 只读够我们要的那么多就收手。
        var want = (int)Math.Min(end - start + 1, HeadBytes);
        var buffer = new byte[want];
        var filled = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            while (filled < want)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, want - filled), ct).ConfigureAwait(false);
                if (read <= 0) break;
                filled += read;
            }
        }

        if (filled == 0) return null;
        if (filled < want) Array.Resize(ref buffer, filled);
        if (total <= 0) total = filled;
        return (buffer, total);
    }

    /// <summary>在 mp4 顶层盒子找 moov → trak → tkhd，取宽高（16.16 定点）。</summary>
    private static (int Width, int Height)? FindTrackSize(byte[] data)
    {
        foreach (var box in Walk(data, 0, data.Length))
        {
            if (box.Type != "moov") continue;

            foreach (var trak in Walk(data, box.Payload, box.End))
            {
                if (trak.Type != "trak") continue;

                foreach (var tkhd in Walk(data, trak.Payload, trak.End))
                {
                    if (tkhd.Type != "tkhd") continue;
                    if (ReadTkhd(data, tkhd.Payload, tkhd.End) is { } size) return size;
                }
            }
        }
        return null;
    }

    private static (int Width, int Height)? ReadTkhd(byte[] data, int payload, int end)
    {
        if (payload + 4 > end) return null;
        var version = data[payload];

        // version 1 的创建/修改/时长字段是 8 字节，宽高整体后移 12。
        var widthAt = payload + (version == 1 ? 88 : 76);
        if (widthAt + 8 > end) return null;

        var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(widthAt, 4)) / 65536f;
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(widthAt + 4, 4)) / 65536f;

        var w = (int)Math.Round(width);
        var h = (int)Math.Round(height);
        return w > 0 && h > 0 ? (w, h) : null;
    }

    private readonly record struct Box(string Type, int Payload, int End);

    /// <summary>按 [size(4)][type(4)] 逐个走盒子。size==1 走 64 位扩展长度，size==0 表示到结尾。</summary>
    private static IEnumerable<Box> Walk(byte[] data, int start, int limit)
    {
        var offset = start;
        while (offset + 8 <= limit)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            var header = 8;

            if (size == 1)
            {
                if (offset + 16 > limit) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = limit - offset;
            }

            if (size < header || offset + size > limit) yield break;
            yield return new Box(type, offset + header, (int)(offset + size));
            offset += (int)size;
        }
    }

    /// <summary>短边 + P。竖屏 1080x1920 也归成 1080P，和标签归一化同一套规矩。</summary>
    public static string Label(int width, int height)
    {
        var shortSide = Math.Min(width, height);
        return shortSide > 0 ? shortSide + "P" : "";
    }

    private static string? CacheKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Host + uri.AbsolutePath;
    }
}
