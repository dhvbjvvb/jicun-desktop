using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jicun.Desktop.Models;

namespace Jicun.Desktop.Services;

/// <summary>
/// 解析一条分享链接。两条路：
///
/// - **上游聚合直连**（先试）：抖音 / 快手 / 豆包 / 微信视频号各有一条第三方接口，
///   需要密钥（见 <see cref="Secrets"/>）。上游每个平台一条独立接口，拿错平台的链接
///   去问会回 422「解析参数与该平台不匹配」。
/// - **media-parser**（兜底）：域名池来自 <see cref="ApiHosts"/>，内置兜底 + 服务端
///   /ips.json 热更下发的候选。
///
/// 上游那一趟只要没拿到能用的结果就**串行**回落 —— 串行不是抢跑：两个上游都可能
/// 收费，抢跑等于每次都付两份钱。
/// </summary>
public sealed class ParseService
{
    private static readonly HttpClient Http = new();

    // 整段分享文案也能解析：从里面挑第一条链接出来
    private static readonly Regex UrlRegex = new(@"https?://[^\s，。、（）()【】\[\]""'<>|]+", RegexOptions.Compiled);

    // 兜底那条路的超时。media-parser 要打第三方平台，给宽一点。
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(20);

    // 上游那条路的超时，比兜底短：这条路失败还要接着走兜底，两次串起来不能让用户
    // 等 40 秒。上游自己也打第三方平台，常态 1~3 秒。
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(12);

    /// <summary>解析实际上走了哪条路。只给命令行探针和排障用，界面不依赖它。</summary>
    public string? LastRoute { get; private set; }

    public static string? ExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = UrlRegex.Match(text);
        return m.Success ? m.Value : null;
    }

    public async Task<ParseResult> ParseAsync(string rawInput, CancellationToken ct = default)
    {
        var url = ExtractUrl(rawInput) ?? rawInput.Trim();
        if (url.Length == 0) throw new ParseException("先粘贴一条链接");

        var platform = UpstreamMapping.DetectPlatform(url);
        var upstreamPath = UpstreamMapping.UpstreamPathOf(platform);

        // 白名单只做本地拦，省一次注定失败的往返 —— 但**只对本来就要打到我们服务器的
        // 平台**（不走上游的那些）。
        //
        // 走第三方上游的平台绝不能在这里拦。服务端关掉一个平台只代表「media-parser
        // 解析不了它」，不代表这个平台整个不能用：微信视频号就是活例子，它完全靠上游，
        // 服务端关它反而是必然的，所以它**根本不在支持名单里** —— 拿白名单直接套它
        // 会把它整个拒掉。（Dart 注释原话：改前确实这么错过一次。）
        // 上游没配密钥 = 这条路根本用不了，那它和「这个平台本来就要打到我们服务器」没区别，
        // 白名单照样适用。
        var upstreamUsable = upstreamPath is not null && Secrets.IsConfigured;

        if (!upstreamUsable && ApiHosts.UnsupportedMessage(url) is { } unsupported)
        {
            LastRoute = "blocked-local";
            throw new ParseException(unsupported);
        }

        if (upstreamUsable)
        {
            ParseException? upstreamError = null;
            var label = UpstreamMapping.LabelOf(platform);
            var name = platform.ToString().ToLowerInvariant();

            try
            {
                var result = await RequestAsync(
                    Secrets.BaseUrl + upstreamPath!, url, UpstreamTimeout,
                    fromUpstream: true, platformLabel: label, ct).ConfigureAwait(false);

                // 上游对部分链接会回 200 + 空结果，那不是解析成功 —— 拿它当结论
                // 用户会看到一张空卡片。
                if (result.HasVideo || result.HasImages || result.HasAudio)
                {
                    LastRoute = "upstream:" + name;
                    return await FinalizeAsync(result, ct).ConfigureAwait(false);
                }
                LastRoute = "upstream:" + name + "-empty";
            }
            catch (ParseException error)
            {
                // 上游自己的失败理由（链接失效、平台不支持…）先留着：兜底那条路说出来的
                // 话才是这一趟真正的结论，真到两条都失败那一步才拿它垫底。
                LastRoute = "upstream:" + name + "-failed";
                upstreamError = error;
            }

            try
            {
                var result = await FallbackAsync(url, ct).ConfigureAwait(false);
                LastRoute = LastRoute + "→fallback";
                return await FinalizeAsync(result, ct).ConfigureAwait(false);
            }
            catch (ParseException)
            {
                // 兜底也挂了：抛上游那句更准确 —— 它多半是「链接失效」「平台不支持」
                // 这类真实原因，兜底只会说「服务器异常」。
                throw upstreamError ?? new ParseException("解析失败，换个链接或稍后再试");
            }
        }

        LastRoute = "fallback-only";
        return await FinalizeAsync(await FallbackAsync(url, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 收尾：把还挂着纯名字标签（上游的「原画」）的档拿去文件头上问真实分辨率。
    ///
    /// 上游只给抖音的根节点配了 width/height；快手的「原画」那档应答里一个分辨率字段都
    /// 没有（label 和 quality 都是「原画」两个字），只能去读文件本身。只补**标签里没有
    /// 数字**的档，最多 3 档、整体 6 秒：这一步是锦上添花，不能让一次解析为了几个标签
    /// 多等太久，探不到就保持原名。
    /// </summary>
    private static async Task<ParseResult> FinalizeAsync(ParseResult result, CancellationToken ct)
    {
        var targets = new List<VideoVariant>();
        foreach (var video in result.Videos)
        {
            if (video.Url.Length == 0) continue;
            if (video.Label is { Length: > 0 } label && Regex.IsMatch(label, @"\d")) continue;
            targets.Add(video);
            if (targets.Count == 3) break;
        }

        if (targets.Count == 0) return result;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(6));

        var probes = targets.Select(async video =>
        {
            var size = await MediaProbe.ResolutionAsync(video.Url, TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false);
            if (size is { } value) video.Label = MediaProbe.Label(value.Width, value.Height);
        });

        try
        {
            await Task.WhenAll(probes).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 探不到就保持原标签，解析本身已经成功了。
        }

        return result;
    }

    /// <summary>media-parser 那条路：按域名池逐个试，通了就记住。</summary>
    private async Task<ParseResult> FallbackAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var host in ApiHosts.Candidates())
        {
            try
            {
                var endpoint = "https://" + host + "/parse?url=" + Uri.EscapeDataString(url);
                var bytes = await GetAsync(endpoint, null, FallbackTimeout, ct).ConfigureAwait(false);

                // 这个域名通（哪怕应答是 400），记住它：后面的请求就不用再从头试。
                ApiHosts.TrySet(host);
                return ParseBody(bytes, fromUpstream: false, platformLabel: "");
            }
            catch (TimeoutException)
            {
                last = new ParseException("解析超时，请重试");
            }
            catch (ParseException)
            {
                // 服务器其实答了，只是答的是失败 —— 换域名也是一样的失败，别白试。
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new ParseException("网络连接失败：" + (last?.Message ?? "未知错误"));
    }

    /// <summary>
    /// 打一趟解析接口，把应答翻成 <see cref="ParseResult"/>。
    ///
    /// <paramref name="fromUpstream"/> 为真表示这是上游那条路：应答结构是另一套
    /// （见 <see cref="UpstreamMapping.FromUpstream"/>），而且要带上密钥头。
    /// </summary>
    private static async Task<ParseResult> RequestAsync(
        string endpoint, string url, TimeSpan timeout,
        bool fromUpstream, string platformLabel, CancellationToken ct)
    {
        // 只有上游那条路要密钥：media-parser 的密钥由我们自己在 nginx 上注入，
        // 客户端手里没有（也不该有）。
        var headers = fromUpstream
            ? new[] { new KeyValuePair<string, string>("X-API-Key", Secrets.ApiKey) }
            : null;

        // 分享链接必须以 ?url=… 的形式交给服务端。上游那条路不认别的写法，漏了就回
        // 「缺少接口参数: url」。Dart 侧是 Uri.parse(endpoint).replace(queryParameters:)
        // 干的这件事 —— 移植时把这一步丢了，端点只剩 /api/dyjx，白打一趟才回落到兜底。
        var target = endpoint + (endpoint.Contains('?') ? '&' : '?') + "url=" + Uri.EscapeDataString(url);

        byte[] bytes;
        try
        {
            bytes = await GetAsync(target, headers, timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            throw new ParseException(error.Message);
        }
        catch (ParseException)
        {
            throw;
        }
        catch
        {
            throw new ParseException("网络连接失败，请检查网络后重试");
        }

        return ParseBody(bytes, fromUpstream, platformLabel);
    }

    private static async Task<byte[]> GetAsync(
        string endpoint, IReadOnlyList<KeyValuePair<string, string>>? headers, TimeSpan timeout, CancellationToken ct)
    {
        // 单次请求的超时，不改 HttpClient.Timeout（那是全体的）：上游 12 秒、兜底
        // 20 秒，两条路的要求不一样。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (headers is not null)
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 超时（不是用户取消）。用专门的类型：调用方要能把它和「服务器答了失败」
            // 区分开 —— 前者值得换下一个域名再试，后者没必要。
            throw new TimeoutException("解析超时，请重试");
        }

        using (response)
        {
            if ((int)response.StatusCode == 429) throw new ParseException("请求太频繁，请稍后再试");

            var bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);

            // 非 JSON 响应（网关错误页之类）只能靠状态码说话。这里只做一次探测，
            // 真正的解析在 ParseBody 里；不探测的话「HTML 错误页」会被说成
            // 「服务器异常（200）」，前后矛盾。
            if (!IsJson(bytes))
            {
                throw new ParseException((int)response.StatusCode == 200
                    ? "返回内容无法识别"
                    : "服务器异常（" + (int)response.StatusCode + "），请稍后再试");
            }

            return bytes;
        }
    }

    /// <summary>用 bodyBytes 手工解 UTF-8：上游若没在 Content-Type 里写 charset，中文标题会乱码。</summary>
    private static JsonDocument? DecodeJson(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes); }
        catch { return null; }
    }

    private static bool IsJson(byte[] bytes)
    {
        try { using var doc = JsonDocument.Parse(bytes); return true; }
        catch { return false; }
    }

    private static ParseResult ParseBody(byte[] bytes, bool fromUpstream, string platformLabel)
    {
        using var doc = DecodeJson(bytes)
            ?? throw new ParseException("返回内容无法识别");

        var root = doc.RootElement;

        if (!IsSuccess(root) || !TryGetData(root, out var data))
        {
            // 关键：上游解析失败时用的是 HTTP 400 + retdesc，不是 200 + succ:false，
            // 所以不能拿状态码当结论 —— 那样只会弹出一句没用的「服务器异常（400）」，
            // 把 retdesc 里真正的原因（版权限制、链接失效、平台不支持）全丢掉。
            throw new ParseException(MessageOf(root) ?? "解析失败，换个链接或稍后再试");
        }

        return fromUpstream
            ? UpstreamMapping.FromUpstream(data, platformLabel)
            : ParseResult.FromJson(data);
    }

    /// <summary>
    /// 应答是不是「成功」。
    ///
    /// 我们先接的是 media-parser 那套（succ: true）。上游是另一套代码，判定字段得
    /// 容错：succ 真、或者 code 是 200/0 —— 两者都没有时只要带了 data 对象也认，
    /// 免得因为少一个字段把一整条能用的结果判死。
    /// </summary>
    private static bool IsSuccess(JsonElement root)
    {
        if (root.TryGetProperty("succ", out var succ) && succ.ValueKind == JsonValueKind.True) return true;

        foreach (var key in new[] { "code", "retcode", "status" })
        {
            if (!root.TryGetProperty(key, out var v)) continue;

            if (v.ValueKind == JsonValueKind.Number)
                return v.TryGetInt32(out var code) && (code == 200 || code == 0);

            if (v.ValueKind == JsonValueKind.String)
            {
                var text = v.GetString()?.Trim().ToLowerInvariant();
                return text is "200" or "0" or "ok";
            }
        }

        // 三个字段一个都没有：带了 data 对象就认
        return TryGetData(root, out _);
    }

    /// <summary>结果体。两个上游一个叫 data，一个叫 result，都认。</summary>
    private static bool TryGetData(JsonElement root, out JsonElement data)
    {
        if (root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object) return true;
        return root.TryGetProperty("result", out data) && data.ValueKind == JsonValueKind.Object;
    }

    private static string? MessageOf(JsonElement root)
    {
        foreach (var key in new[] { "retdesc", "error", "message", "msg" })
        {
            if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }
}
