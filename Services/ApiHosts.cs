using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jicun.Desktop.Services;

/// <summary>
/// 服务端下发的整份配置：可用域名 + 优选 IP + 平台白名单。
/// </summary>
/// <remarks>
/// 三个字段一起下发是有意的：它们来自同一份服务端配置，分两次请求只会在
/// 「域名刚换、IP 还是旧域名那套」这种窗口里制造不一致。
/// </remarks>
public sealed class ServerConfig
{
    public List<string> Hosts { get; init; } = new();
    public List<string> Ips { get; init; } = new();
    public List<string> Supported { get; init; } = new();

    public bool IsEmpty => Hosts.Count == 0 && Ips.Count == 0 && Supported.Count == 0;
}

/// <summary>
/// <c>/ips.json</c> 的解析器。
/// </summary>
/// <remarks>
/// 这是**信任边界**：内容来自网络，之后会被拼进请求地址、当作 SNI 与证书校验的
/// 目标。所以域名只认长得像域名的条目，IP 只认解得出来的，两边都有条数上限 ——
/// 一份被灌了十万条的表会让每次建连接都去开十万个 socket。
///
/// 坏数据一律跳过、不抛异常：拉不到配置不是错误，继续用内置兜底就是了。
/// </remarks>
public static class ServerConfigParser
{
    // 对应 Android 版 jicun/lib/api_host.dart:147 的 _hostPattern。
    // 必须带点：单段主机名（内网名、裸 IP）不放进来，否则会被当域名去拼 https://。
    // 用 [.] 而不是转义点，纯粹是为了让这行代码在宿主脚本里也原样好写。
    private static readonly Regex HostPattern = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?([.][a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.Compiled);

    public static ServerConfig Parse(string body, int maxHosts = 4, int maxIps = 16, int maxSupported = 512)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch
        {
            return new ServerConfig();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ServerConfig();

            return new ServerConfig
            {
                Hosts = ParseHostList(root, "hosts", maxHosts),
                Ips = ParseIpList(root, "ips", maxIps),
                // 白名单上限给得比 hosts 宽得多：服务端 50 个平台名下共 176 个域名，
                // 其中开启的 20 个平台是 80 条（2026-09-30 实测）。
                Supported = ParseHostList(root, "supported", maxSupported),
            };
        }
    }

    /// <summary>
    /// 解析域名表。只认字母数字开头、只含 <c>[a-z0-9.-]</c>、带点的条目。
    /// </summary>
    /// <remarks>
    /// 带冒号的（比如 <c>evil.com:8443</c>）一律拒绝 —— 免得把端口或
    /// <c>host:port</c> 这种形状带进请求地址。
    /// </remarks>
    public static List<string> ParseHostList(JsonElement root, string property, int max)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(property, out var raw) || raw.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in raw.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var host = (item.GetString() ?? string.Empty).Trim().ToLowerInvariant();
            if (host.Length == 0 || host.Length > 253) continue;
            if (!HostPattern.IsMatch(host)) continue;
            if (result.Contains(host)) continue;
            result.Add(host);
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>解析优选 IP 表，只认 <see cref="IPAddress.TryParse(string, out IPAddress)"/> 解得出来的条目。</summary>
    public static List<string> ParseIpList(JsonElement root, string property, int max)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(property, out var raw) || raw.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in raw.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var ip = (item.GetString() ?? string.Empty).Trim();
            if (!IPAddress.TryParse(ip, out _)) continue;
            if (result.Contains(ip)) continue;
            result.Add(ip);
            if (result.Count >= max) break;
        }
        return result;
    }
}

/// <summary>
/// 我们自己的域名池，以及「当前用哪个域名」这个全局状态。
/// </summary>
/// <remarks>
/// 背景：域名会被按 SNI 阻断（表现为连接被重置），换 IP、加优选 IP 都没用 ——
/// 阻断认的是域名。所以域名必须**能在服务端换掉而不用重新发版**：启动后从一个
/// 域名拉一份 <c>/ips.json</c>，拉到了就用它，拉不到就用这里编译进去的兜底。
///
/// 做成进程内全局状态，是因为拿不到它的地方（启动流程、命令行模式、下载器）也要
/// 能读写当前域名，一路传参只会把签名搅乱 —— 和 Android 版 jicun/lib/api_host.dart:26 同构。
/// </remarks>
public static class ApiHosts
{
    /// <summary>
    /// 兜底域名池，顺序即优先级。值在本机私有的 Services\LocalDefaults.cs 里；从仓库直接
    /// 构建时是 <c>jicun.invalid</c>（一个永远解析不出来的保留域名），那时得在
    /// server-config.json 或 LocalDefaults.cs 里配一个自己的域名，兜底那条路才通。
    /// 列表空不得 —— 空了自己都没法启动，也就没有「拉配置」这条路了。
    /// </summary>
    public static readonly string[] BuiltIn = LocalDefaults.Hosts;

    private static string _current = BuiltIn[0];

    public static string Current => _current;

    /// <summary>服务端下发过的域名候选。</summary>
    public static IReadOnlyList<string> RemoteHosts { get; private set; } = Array.Empty<string>();

    /// <summary>服务端**支持**的平台域名白名单，见 <see cref="UnsupportedMessage"/>。</summary>
    public static IReadOnlyList<string> SupportedHosts { get; private set; } = Array.Empty<string>();

    /// <summary>优选 IP。目前只做记录：Windows 侧走系统解析直连，不经这里拨号。</summary>
    public static IReadOnlyList<string> PreferredIps { get; private set; } = Array.Empty<string>();

    public static string Url(string path) => "https://" + _current + path;

    /// <summary>换域名。返回是否真的变了 —— 变了的话调用方要把新值落盘。</summary>
    public static bool TrySet(string host)
    {
        var next = (host ?? string.Empty).Trim().ToLowerInvariant();
        if (next.Length == 0 || next == _current) return false;
        _current = next;
        return true;
    }

    internal static void SetRemoteHosts(IReadOnlyList<string> hosts) => RemoteHosts = hosts;

    internal static void SetSupported(IReadOnlyList<string> hosts) => SupportedHosts = hosts;

    /// <summary>
    /// 候选顺序：<c>[当前, ...内置, ...服务端下发]</c>，去重。
    /// </summary>
    /// <remarks>
    /// 当前域名排第一：它刚成功过。内置域名排在服务端下发之前 —— 下发的域名是从
    /// 「已经连上的那个域名」拿到的，它自己失联时反而不可信，先退回更稳的内置表。
    /// </remarks>
    public static List<string> Candidates()
    {
        var tried = new List<string>();

        void Add(string? host)
        {
            if (string.IsNullOrWhiteSpace(host)) return;
            var v = host.Trim().ToLowerInvariant();
            if (v.Length == 0 || tried.Contains(v)) return;
            tried.Add(v);
        }

        Add(Current);
        foreach (var h in BuiltIn) Add(h);
        foreach (var h in RemoteHosts) Add(h);
        return tried;
    }

    /// <summary>
    /// 这条链接我们能不能解析。不能就返回给用户看的那句话，能就返回 null。
    /// </summary>
    /// <remarks>
    /// **精确匹配主机名**，和服务端 <c>UrlParser.get_platform</c> 的
    /// <c>DOMAIN_TO_NAME.get(domain)</c> 完全同构 —— 那份表把每个平台名下的子域都
    /// 单独列了（比如一条平台的 www 和裸域是两条），所以精确匹配不会漏。
    ///
    /// 别改成后缀匹配：父域会遮蔽子域。<c>weixin.qq.com</c> 属于已关的视频号，而
    /// <c>mp.weixin.qq.com</c> 属于在用的微信公众号 —— 后缀匹配会把后者一起拦掉。
    ///
    /// 列表为空（还没拉到过配置）时一律放行：本地拦是优化，不是正确性的一部分，
    /// 不做也不会错，只是多一次往返。
    ///
    /// ⚠️ 调用方必须跳过「走第三方上游」的平台：视频号在服务端是关掉的，所以**不在
    /// 这份白名单里**，但它在 Android 版里完全靠上游。等桌面版接上上游直连后，
    /// 直接拿白名单套它 = 把这个平台整个拒掉（Android 版确实这么错过一次）。
    /// </remarks>
    public static string? UnsupportedMessage(string url)
    {
        if (SupportedHosts.Count == 0) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) return null;
        return SupportedHosts.Contains(host) ? null : "暂不支持该平台";
    }

    /// <summary>生效：换域名、换优选 IP 表、记住服务端下发的域名候选。对应 Android 版的 PreferredIpUpdater._apply。</summary>
    internal static void Apply(ServerConfig config, string answeredBy)
    {
        if (config.Ips.Count > 0) PreferredIps = config.Ips;
        if (config.Hosts.Count > 0) RemoteHosts = config.Hosts;
        // 支持域名表**无条件覆盖**（不是 Count > 0 才覆盖）：服务端放开或再关掉某个
        // 平台时这份表会跟着变长变短 —— 必须整个换掉，否则被放开的平台会因为本地还
        // 留着旧的白名单而一直被拦着，用户永远看不到它已经恢复了。
        SupportedHosts = config.Supported;
        // 服务端把某个域名排在第一位 = 它希望大家都用这个域名。
        TrySet(config.Hosts.Count > 0 ? config.Hosts[0] : answeredBy);
    }
}

/// <summary>
/// 当前域名与域名候选的本地存档。
/// </summary>
/// <remarks>
/// 存它是为了冷启动：内置域名被阻断时，上次生效的那个域名是唯一能连上的入口，
/// 而「拉配置」这件事本身就要求先连上一个域名。Android 版对应
/// jicun/lib/bootstrap.dart:92（hosts 落盘）。
///
/// **白名单不存**：存档里那份可能已经过期，冷启动先拿它拦人，会把服务端刚放开的
/// 平台误拒到下次刷新为止。白名单是省一次往返的优化，宁可不生效也不要错拦。
/// </remarks>
public static class ServerConfigStore
{
    private static readonly string DirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun");

    public static string FilePath => Path.Combine(DirPath, "server-config.json");

    private sealed class Snapshot
    {
        public string? Current { get; set; }
        public List<string>? RemoteHosts { get; set; }
    }

    /// <summary>落盘。失败静默吞掉：下次照旧走内置兜底，不值得为它打断启动。</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            var snap = new Snapshot { Current = ApiHosts.Current, RemoteHosts = ApiHosts.RemoteHosts.ToList() };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 落盘只是优化。
        }
    }

    /// <summary>冷启动先读回来。</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath));
            if (snap is null) return;
            if (!string.IsNullOrWhiteSpace(snap.Current)) ApiHosts.TrySet(snap.Current);
            if (snap.RemoteHosts is { Count: > 0 }) ApiHosts.SetRemoteHosts(snap.RemoteHosts);
        }
        catch
        {
            // 存档坏了就当没有。
        }
    }
}

/// <summary>
/// 从 <c>/ips.json</c> 拉当前配置。对应 Android 版的 PreferredIpUpdater.fetch()。
/// </summary>
public sealed class HostUpdater
{
    // 10 秒不是随便给的：冷启动第一次握手（DNS + TLS + Cloudflare）实测能在 6 秒
    // 边界上抖过去，而这条请求是后台 fire-and-forget 的，等久一点不伤任何人的体验；
    // 反过来，误判成「拉不到」就会一直用内置兜底，等于热更白做了。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>最近一次成功拉到的配置，没拉到过就是 null。</summary>
    public ServerConfig? Last { get; private set; }

    /// <summary>冷启动：先把上次生效的域名读回来（同步、很快），再后台刷新一次，不阻塞界面。</summary>
    public void Boot()
    {
        ServerConfigStore.Load();
        _ = RefreshAsync();
    }

    /// <summary>
    /// 拉一份配置。按 <c>[当前, ...内置, ...服务端下发]</c> 去重逐个试，第一个通的就用。
    /// 全部不通返回 null —— 拉不到配置不是错误，继续用内置兜底就行了。
    /// </summary>
    public async Task<ServerConfig?> RefreshAsync(CancellationToken ct = default)
    {
        foreach (var host in ApiHosts.Candidates())
        {
            try
            {
                using var resp = await Http.GetAsync("https://" + host + "/ips.json", ct).ConfigureAwait(false);
                if (resp.StatusCode != HttpStatusCode.OK) continue;

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var config = ServerConfigParser.Parse(body);
                if (config.IsEmpty) continue;

                ApiHosts.Apply(config, host);
                ServerConfigStore.Save();
                Last = config;
                return config;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                // 这个域名不通，换下一个。DNS 失败、TLS 被重置、超时都走这里。
            }
        }
        return null;
    }
}
