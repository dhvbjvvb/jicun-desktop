using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Jicun.Desktop.Services;

/// <summary>
/// 媒体请求（下载 / 媒体探测 / 音频预览缓冲）共用的请求头。
///
/// B 站 CDN 按防盗链挑人，实测（同一台机器、同一批签名直链，curl 对照）：
/// <list type="bullet">
///   <item>不带任何头 → <b>403</b>（视频、音频都是）；</item>
///   <item>只带 Referer → 403；</item>
///   <item>只带 UA：视频那条（<c>platform=html5</c> 的 .mp4）206 ✓，音频那条（<c>platform=pc</c> 的 .m4s）还是 403；</item>
///   <item>UA + Referer → 两个都是 206 ✓。</item>
/// </list>
/// 所以这里定死两条：**UA 一律带**；**Referer 只给 B 站系域名**（别的平台不需要，发了反而奇怪）。
///
/// 系统播放器自己会带 UA，但**加不了 Referer** —— 那类音频地址只能先由我们自己的请求
/// 缓冲成本地文件再交给它播，见 <see cref="BufferToTempAsync"/>。
/// </summary>
internal static class MediaHttp
{
    /// <summary>普通的浏览器 UA。CDN 只认「像个浏览器」，具体版本不重要。</summary>
    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string BiliReferer = "https://www.bilibili.com/";

    /// <summary>B 站系域名（含它用的第三方 CDN 镜像，upos-…-mirrorakam.akamaized.net 这类）。</summary>
    private static readonly string[] BiliHosts = { "bilivideo", "bilibili", "hdslb", "akamaized" };

    /// <summary>这个地址是不是「必须带 Referer 才给」的那类（B 站系）。</summary>
    internal static bool NeedsBiliReferer(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        foreach (var token in BiliHosts)
            if (uri.Host.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>给一个请求补齐媒体头。所有发往 CDN 的请求都该过这里。</summary>
    internal static void Apply(HttpRequestMessage request, string url)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (NeedsBiliReferer(url))
            request.Headers.TryAddWithoutValidation("Referer", BiliReferer);
    }

    /// <summary>音频预览缓冲放这儿。启动时整目录清掉（见 App.OnLaunched）。</summary>
    private static string PreviewDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun", "preview");

    /// <summary>
    /// 把远端音频下成本地临时文件，返回文件路径。
    ///
    /// 为什么不让播放器直接播：<c>MediaSource.CreateFromUri</c> 由系统媒体栈自己发请求，
    /// 它只带自己的 UA、加不了 Referer，B 站那条 pc 直链就会 403（实测）。
    /// 同一个链接复用同一份文件（名字是 url 的哈希），重进页面不会重复下。
    /// </summary>
    internal static async Task<string> BufferToTempAsync(string url, string extension, CancellationToken ct)
    {
        Directory.CreateDirectory(PreviewDir);

        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant() + extension;
        var path = Path.Combine(PreviewDir, name);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromMinutes(5));   // 音频文件不大，卡住就别一直挂着

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Apply(req, url);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, overall.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var part = path + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            await src.CopyToAsync(dst, overall.Token).ConfigureAwait(false);

        File.Move(part, path, true);
        return path;
    }

    /// <summary>清掉预览缓冲。启动时顺手做一次，省得堆在 %LOCALAPPDATA% 里。</summary>
    internal static void CleanPreviewCache()
    {
        try { if (Directory.Exists(PreviewDir)) Directory.Delete(PreviewDir, true); }
        catch { /* 删不掉就算了，下次启动再清 */ }
    }
}
