using System.IO;
using System.Text.Json;

namespace Jicun.Desktop.Services;

/// <summary>
/// 上游聚合服务的凭据。
///
/// 读取顺序（先命中先用）：
///   1. 环境变量 JICUN_UPSTREAM_KEY / JICUN_UPSTREAM_BASE
///   2. %LocalAppData%\Jicun\secrets.json → {"upstreamKey": "…", "upstreamBase": "https://…"}
///   3. 内置默认值 <see cref="DefaultApiKey"/> / <see cref="DefaultBaseUrl"/> —— 取自本机私有的
///      Services\LocalDefaults.cs（那个文件不进仓库），做到开箱即用。
///
/// 前两处是**覆盖通道**：换 key 不必重新构建，二进制里也不必留新线索。两样都拿到才算配好
/// （<see cref="IsConfigured"/>）。
/// </summary>
public static class Secrets
{
    /// <summary>
    /// 内置默认凭据，值在本机私有的 Services\LocalDefaults.cs 里；从仓库直接构建时
    /// 是空的（见 LocalDefaults.Fallback.cs），那时只走环境变量和 secrets.json。
    /// 编进来是为了开箱即用；环境变量和 secrets.json 仍然优先，这两样只是最后的兜底。
    /// </summary>
    private const string DefaultApiKey = LocalDefaults.ApiKey;

    private const string DefaultBaseUrl = LocalDefaults.BaseUrl;

    public static string ApiKey { get; private set; } = "";
    public static string BaseUrl { get; private set; } = "";

    /// <summary>密钥是从哪读来的（给人看的，排障时一眼看出有没有被覆盖）。</summary>
    public static string KeySource { get; private set; } = "(未配置)";

    /// <summary>上游基址是从哪读来的。</summary>
    public static string BaseSource { get; private set; } = "(未配置)";

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun", "secrets.json");

    /// <summary>两样都有才算配好。BaseUrl 还必须是 http(s)，免得把一句占位文字当成域名。</summary>
    public static bool IsConfigured =>
        ApiKey.Length > 0 && (BaseUrl.StartsWith("https://") || BaseUrl.StartsWith("http://"));

    static Secrets() => Reload();

    public static void Reload()
    {
        var key = Env("JICUN_UPSTREAM_KEY");
        var host = Env("JICUN_UPSTREAM_BASE");
        string? keyFrom = key.Length > 0 ? "环境变量" : null;
        string? hostFrom = host.Length > 0 ? "环境变量" : null;

        if (key.Length == 0 || host.Length == 0)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                    var root = doc.RootElement;
                    if (key.Length == 0 && root.TryGetProperty("upstreamKey", out var k) && k.ValueKind == JsonValueKind.String)
                        key = (k.GetString() ?? "").Trim();
                    if (host.Length == 0 && root.TryGetProperty("upstreamBase", out var b) && b.ValueKind == JsonValueKind.String)
                        host = (b.GetString() ?? "").Trim();
                    if (keyFrom is null && key.Length > 0) keyFrom = "配置文件";
                    if (hostFrom is null && host.Length > 0) hostFrom = "配置文件";
                }
            }
            catch
            {
                // 读不动就当没配：四条上游路自然落到兜底，不影响解析本身
            }
        }

        // 前两处都没有才用内置默认值：值在本机私有的 LocalDefaults.cs 里，换 key 不必重新构建。
        if (key.Length == 0) key = DefaultApiKey;
        if (host.Length == 0) host = DefaultBaseUrl;

        ApiKey = key;
        BaseUrl = host.TrimEnd('/');
        // 默认值是空的时候别说「内置默认」，不然 --secrets 看着像配好了，其实是没配。
        KeySource = keyFrom ?? (key.Length == 0 ? "(未配置)" : "内置默认");
        BaseSource = hostFrom ?? (host.Length == 0 ? "(未配置)" : "内置默认");
    }

    private static string Env(string name) => (Environment.GetEnvironmentVariable(name) ?? "").Trim();
}
