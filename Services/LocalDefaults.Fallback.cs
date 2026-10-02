namespace Jicun.Desktop.Services;

/// <summary>
/// 内置默认值的**占位实现**，仓库里放的就是这一份，值一律留空。
///
/// 真正的值（上游密钥、上游基址、兜底域名）属于部署私货，不进仓库：本机在
/// Services\LocalDefaults.cs 里填一份（复制 LocalDefaults.cs.example）。那个文件在的时候，
/// csproj 会把这一份从编译里排除（见 Jicun.Desktop.csproj 的 Compile Remove）。
///
/// 兜底域名给的是 <c>jicun.invalid</c> —— RFC 2606 保留域名，永远解析不出来，故意的：
/// 从仓库直接构建时兜底那条路必然不通，得在 server-config.json 或 LocalDefaults.cs 里
/// 配一个自己的域名。列表空不得，ApiHosts 拿它初始化「当前域名」。
/// </summary>
internal static class LocalDefaults
{
    internal const string ApiKey = "";
    internal const string BaseUrl = "";
    internal static readonly string[] Hosts = { "jicun.invalid" };
}
