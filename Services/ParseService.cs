using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
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
    // 超时一律由**调用方**按请求给（见 GetAsync 里那个 linked CTS）：上游 12 秒 / 兜底每域名
    // 20 秒 / 兜底总预算 25 秒，三种都不一样。别在这里设 HttpClient.Timeout —— 那是个全局上限，
    // 会把「按请求给的超时」悄悄压到它下面，排查起来很难看（这也是另外几个 HttpClient 的规矩）。
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // 整段分享文案也能解析：从里面挑第一条链接出来
    private static readonly Regex UrlRegex = new(@"https?://[^\s，。、（）()【】\[\]""'<>|]+", RegexOptions.Compiled);

    // 兜底那条路的超时。media-parser 要打第三方平台，给宽一点。
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 整趟兜底的总预算。每个域名各给 20 秒，但「一个都不通」不能叠成 20×N 秒 ——
    /// 用户盯着转圈等半分钟，最后等到的只是一句「网络连接失败」。
    /// </summary>
    private static readonly TimeSpan FallbackBudget = TimeSpan.FromSeconds(25);

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
            catch (ParseException fallbackError)
            {
                // 两边都挂了，挑最有用的那句给用户：**内容级**那句才是真实原因（「链接失效」
                // 「平台不支持」）；传输级的「服务器异常 / 网络连接失败」换个时间还能成。
                // 上游的内容级结论最权威（它真正认得这条链接），所以它排最前面。
                if (upstreamError is { Failure: ParseFailure.Content }) throw upstreamError;
                if (fallbackError.Failure == ParseFailure.Content) throw;   // 就是它，裸 throw 保住堆栈
                if (upstreamError is not null) throw upstreamError;
                throw;                                                       // 兜底自己的传输级失败
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

    /// <summary>
    /// media-parser 那条路：按域名池逐个试，通了就记住。
    /// 单个域名最多 <see cref="FallbackTimeout"/>，整趟不超过 <see cref="FallbackBudget"/>。
    /// </summary>
    private async Task<ParseResult> FallbackAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        var deadline = DateTime.UtcNow + FallbackBudget;

        foreach (var host in ApiHosts.Candidates())
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                // 预算用完就收尾，但**不要**拿一句通用的「解析超时」把手头最真实的原因盖掉：
                // 上一个域名到底为什么没成（域名解析不了 / TLS 被重置 / 它自己超时），
                // 那就是这次该告诉用户的实话。
                break;
            }

            var timeout = remaining < FallbackTimeout ? remaining : FallbackTimeout;

            try
            {
                var endpoint = "https://" + host + "/parse?url=" + Uri.EscapeDataString(url);
                var bytes = await GetAsync(endpoint, null, timeout, ct).ConfigureAwait(false);

                // 这个域名通（哪怕应答是 400），记住它：后面的请求就不用再从头试。
                // 别忘了落盘 —— 只改内存的话，冷启动又会从内置域名一个个试起。
                if (ApiHosts.TrySet(host)) ServerConfigStore.Save();
                return ParseBody(bytes, fromUpstream: false, platformLabel: "");
            }
            catch (TimeoutException)
            {
                last = new ParseException("解析超时，请重试", ParseFailure.Transport);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户按了取消：这不是「这个域名不通」，不能接着试下一个
                throw;
            }
            catch (ParseException ex) when (ex.Failure == ParseFailure.Content)
            {
                // 服务器给了明确结论（链接失效 / 平台不支持）：换域名也是一样的答案，别白试。
                throw;
            }
            catch (ParseException ex)
            {
                // 传输级失败（网关错误页 / 429 / 应答不像 JSON）：是这个入口坏了，接着试下一个。
                last = ex;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        // last 就是最后一个域名的真实失败原因（DNS / TLS / 超时…）。一次都没试成走不到这里：
        // 候选表至少有当前域名和内置域名，第一轮必有域名拿到机会。
        // 界面上只露中文那句话；原始异常挂在内层，排障时还捞得到。
        if (last is ParseException known) throw known;
        throw last is null
            ? new ParseException("网络连接失败", ParseFailure.Transport)
            : new ParseException(DescribeTransport(last), ParseFailure.Transport, last);
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
            throw new ParseException(error.Message, ParseFailure.Transport);
        }
        catch (ParseException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户按了取消：绝不能翻成「网络连接失败」—— 那样会被当成「上游这条路失败」，
            // 接着再跑一趟兜底，取消就白按了。
            throw;
        }
        catch (Exception error)
        {
            throw new ParseException(DescribeTransport(error), ParseFailure.Transport, error);
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
            // 429 也算传输级：限流多半按账号或出口算，换域名不一定有用，但也不该让一个域名
            // 把整条路判死 —— 一次快速失败，最多多打一次而已。
            if ((int)response.StatusCode == 429)
                throw new ParseException("请求太频繁，请稍后再试", ParseFailure.Transport);

            byte[] bytes;
            try
            {
                bytes = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 收 body 的途中超时：和发请求超时一样要能换下一个域名再试，
                // 也不能让「A task was canceled.」这种原文漏到界面上。
                throw new TimeoutException("解析超时，请重试");
            }

            // 非 JSON 响应（网关错误页之类）只能靠状态码说话。这里只做一次探测，
            // 真正的解析在 ParseBody 里；不探测的话「HTML 错误页」会被说成
            // 「服务器异常（200）」，前后矛盾。
            if (!IsJson(bytes))
            {
                // 网关错误页 / 反代吐的 HTML：属于「这个入口的链路有问题」，换个域名再试有意义。
                throw new ParseException((int)response.StatusCode == 200
                    ? "返回内容无法识别"
                    : "服务器异常（" + (int)response.StatusCode + "），请稍后再试",
                    ParseFailure.Transport);
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

    /// <summary>
    /// 应答看起来是不是 JSON。只看第一个非空白字符是不是 <c>{</c> / <c>[</c> —— 这里要问的
    /// 只是「网关吐的是不是一张 HTML 错误页」。别为它先把整份应答解一遍：调用方拿到字节后
    /// 马上还会在 <see cref="ParseBody"/> 里真正解一次，两次全量解析等于白花一倍 CPU。
    /// 开头是 <c>{</c> 但内容坏掉的，仍然会被 ParseBody 判成「返回内容无法识别」。
    /// </summary>
    private static bool IsJson(byte[] bytes)
    {
        var at = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) at = 3; // BOM

        for (; at < bytes.Length; at++)
        {
            var b = bytes[at];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b is (byte)'{' or (byte)'[';
        }

        return false;
    }

    /// <summary>
    /// 把传输层的异常翻成中文人话 —— 界面上不该出现 .NET 的英文原文（那句话对用户没意义）。
    /// **必须挖到最内层**：HttpRequestException 自己只会说「发送请求时出错」，
    /// 真正的病因在 InnerException 里（TLS 握手失败 / 连不上 / 超时）。
    /// 原始异常会被挂到 <see cref="ParseException"/> 的内层，要排障时还捞得出来。
    /// </summary>
    internal static string DescribeTransport(Exception error)
    {
        var cause = error;
        while (cause.InnerException is { } inner) cause = inner;

        return cause switch
        {
            AuthenticationException => "安全连接建立失败（域名可能被阻断，或中间有代理 / 防火墙）",
            SocketException => "连不上服务器（网络不通，或域名解析不了）",
            TimeoutException => "请求超时",
            OperationCanceledException => "请求被取消或超时",
            IOException => "网络读写中断",
            _ => "网络连接失败",
        };
    }

    private static ParseResult ParseBody(byte[] bytes, bool fromUpstream, string platformLabel)
    {
        // 首字节像 JSON 却解不开 = 应答被截断或掺了别的东西：同样归传输级，换个入口可能就好。
        using var doc = DecodeJson(bytes)
            ?? throw new ParseException("返回内容无法识别", ParseFailure.Transport);

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
