using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jicun.Desktop.Services;

/// <summary>
/// GitHub 上最新的那个正式版（<c>releases/latest</c> 应答的原样映射）。
/// </summary>
/// <remarks>
/// 版本、更新说明、装哪个包、这个包的 sha256 —— 全在一次请求里，没有第二份清单要维护：
/// <list type="bullet">
///   <item><see cref="TagName"/>（v1.0.1）就是版本号；</item>
///   <item><see cref="Body"/> 就是你在 Release 页面椭圆那块写的说明，弹窗显示的就是它；</item>
///   <item><see cref="Assets"/> 里挑安装器，它自带的 <c>digest</c> 就是 sha256。</item>
/// </list>
/// 之前那份「发版时另写一个 update/&lt;运行时&gt;.json 并提交推送」的清单去掉了：多一个手工步骤，
/// 漏提交就等于客户端永远看不到新版。现在只认 Release 本身，发完版就完事。
/// 国内可用性靠镜像垫（gh-proxy；ghfast 实测对 api 路径回 403，不能用）+ <c>JICUN_RELEASE_API</c>
/// 覆盖（排障 / 自检指本地 JSON 文件）。
/// </remarks>
public sealed class UpdateRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Release 正文（椭圆那块）。</summary>
    [JsonPropertyName("body")] public string? Body { get; set; }

    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("assets")] public List<ReleaseAsset> Assets { get; set; } = new();

    /// <summary>版本号（tag 去掉 v 前缀）。解不出来就不算数。</summary>
    [JsonIgnore] public Version? Version => UpdateService.ParseVersion(TagName);

    /// <summary>给用户看的版本号文本，也用它拼安装器的文件名。</summary>
    [JsonIgnore] public string VersionText => Version?.ToString() ?? TagName.Trim();

    /// <summary>更新说明；没写就是 null，弹窗自己会显示「没有写更新说明」占位。</summary>
    [JsonIgnore] public string? Notes => string.IsNullOrWhiteSpace(Body) ? null : Body!.Trim();

    /// <summary>草稿和预发布都不当正式版（真接口的 latest 本来就不会给这两种，只在本地文件演练时用得上）。</summary>
    [JsonIgnore] public bool IsUsable => Version is not null && !Draft && !Prerelease;

    /// <summary>这个版本自己的页面 —— 绿色版点「去下载」就开它（安装器 / 绿色包都在附件里）。</summary>
    [JsonIgnore]
    public string TagUrl => "https://github.com/" + UpdateService.Repo + "/releases/tag/" + TagName;

    /// <summary>
    /// 我们要装的那个安装器附件。**必须带 sha256**（digest）才认 —— 镜像站是第三方，
    /// 没有哈希就没法确认下回来的是不是我们的包，那就宁可当公告，别装。
    /// </summary>
    [JsonIgnore]
    public ReleaseAsset? Installer =>
        Assets.FirstOrDefault(a =>
            string.Equals(a.Name, UpdateService.SetupAssetName(VersionText), StringComparison.OrdinalIgnoreCase)
            && a.Sha256 is not null);
}

/// <summary>Release 里的一个附件。</summary>
public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>GitHub 给的 <c>sha256:&lt;64 位十六进制&gt;</c>。</summary>
    [JsonPropertyName("digest")] public string? Digest { get; set; }

    /// <summary>digest 里那段 sha256；形状不对就返回 null（当没有哈希处理，不拿它当校验值）。</summary>
    [JsonIgnore]
    public string? Sha256
    {
        get
        {
            var digest = (Digest ?? "").Trim();
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

            var hex = digest[prefix.Length..].Trim();
            return UpdateService.IsSha256(hex) ? hex.ToLowerInvariant() : null;
        }
    }
}

/// <summary>
/// 检查更新 + 下载安装器。
/// </summary>
/// <remarks>
/// 流程：<list type="number">
///   <item>拉 <c>releases/latest</c>（镜像 → 直连），版本 / 说明 / 附件 / 哈希一起拿到；</item>
///   <item>版本比当前大才继续。**安装器装的版本**才有「更新」按钮：下载安装器 → 对 sha256 →
///         静默跑它 → 装完它自己把新版拉起来。绿色版只弹说明 + 「去下载」（见 UpdateDialog）；
///   </item>
/// </list>
/// 「忽略某个版本」存在 %LOCALAPPDATA%\Jicun\update-state.json —— 只有比它更高的版本才会再弹窗。
/// </remarks>
public static class UpdateService
{
    internal const string Repo = "dhvbjvvb/jicun-desktop";

    /// <summary>发布页兜底：检查更新失败时（国内连不上 api.github.com、镜像也挂了）给用户一条出路。</summary>
    internal const string ReleasesUrl = "https://github.com/" + Repo + "/releases/latest";

    /// <summary>当前跑的是哪个运行时 —— 决定挑哪个安装器附件。</summary>
    internal static string Rid =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>
    /// 安装器附件的名字。**必须和 pack.ps1 的 SetupSuffix 规则一致**：
    /// win-x64 是 <c>Jicun-Setup-1.0.1.exe</c>，arm64 是 <c>Jicun-Setup-1.0.1-win-arm64.exe</c>。
    /// 对不上就会被当成「这个版本没带安装器」，于是只弹公告 —— 不报错，但用户装不上。
    /// </summary>
    internal static string SetupAssetName(string version) =>
        "Jicun-Setup-" + version + (Rid == "win-x64" ? "" : "-" + Rid) + ".exe";

    private static readonly string StateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun");

    private static readonly string StatePath = Path.Combine(StateDir, "update-state.json");

    /// <summary>下载的安装器放这儿。装完（或者没装成）都由下次启动顺手清掉。</summary>
    public static string UpdateDir { get; } = Path.Combine(StateDir, "update");

    // 两套超时各占一层：接口用**固定超时**（国内首次 DNS + TLS 慢，给 25 秒；就一个 JSON），下载把上限
    // 交给调用处的**整体预算** CTS（20 分钟，见 DownloadAsync）—— 大文件绝不能被一个固定超时掐死。
    private static readonly HttpClient ApiHttp = CreateApiHttp();

    /// <summary>接口走这个客户端。必须带 User-Agent：GitHub 的 API 不给没有 UA 的请求回数据。</summary>
    private static HttpClient CreateApiHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Jicun-Desktop");
        return http;
    }

    private static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>最近一次失败的原因，排障和设置页显示用。</summary>
    public static string? LastError { get; private set; }

    /// <summary>当前版本，来自 csproj 里的 &lt;Version&gt;（release.ps1 发版时改它）。</summary>
    public static Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>"v1.2.3" / "1.2.3-beta.1" 这种都算得动；解不出来返回 null。</summary>
    public static Version? ParseVersion(string? text)
    {
        var s = (text ?? "").Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        return s.Length > 0 && Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>64 位十六进制才算 sha256 —— 校验值的形状不对，就不该拿它去比。</summary>
    internal static bool IsSha256(string? value)
    {
        var s = value ?? "";
        if (s.Length != 64) return false;
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>把远端给的版本号收敛成「能安全拼进路径」的形状。</summary>
    /// <remarks>
    /// tag 是远端字符串，绝不能直接进路径：<c>1.0.1-..\..\..\Temp</c> 这种能过 <see cref="ParseVersion"/>
    /// 的截断（在 - 处切），然后被拼进下载文件名。凡是要拼进路径的版本号都先过这里。
    /// </remarks>
    internal static string PathSafeVersion(string version)
    {
        var clean = ParseVersion(version)?.ToString();
        if (string.IsNullOrEmpty(clean)) throw new FormatException("版本号不对劲：" + version);
        return clean;
    }

    #region 忽略的版本

    private sealed class State
    {
        public string? IgnoredVersion { get; set; }
    }

    private static string? _ignored;
    private static bool _loaded;

    /// <summary>被用户点过「忽略」的版本（记的是 tag，比如 v1.0.1）。比它更高的版本照样弹。</summary>
    public static string? IgnoredVersion
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try
                {
                    if (File.Exists(StatePath))
                        _ignored = JsonSerializer.Deserialize<State>(ReadText(StatePath), Json)?.IgnoredVersion;
                }
                catch
                {
                    // 存档坏了就当没忽略过
                }
            }
            return _ignored;
        }
    }

    public static void SetIgnored(string version)
    {
        _loaded = true;
        _ignored = version;
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new State { IgnoredVersion = version },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存不上就是这次不记，下次启动还会提示，不是错
        }
    }

    #endregion

    /// <summary>
    /// 这份程序是不是**安装器装的**。
    ///
    /// 只有安装器装的版本能自动更新：绿色版（解压即用）去跑安装器会被装到
    /// %LOCALAPPDATA%\Programs\Jicun —— 等于凭空多出一份，原来那份还留在原地。
    ///
    /// 判据是安装器写的卸载项：HKCU 的 Uninstall 里有一条 InstallLocation 就是当前目录
    /// （安装器是 PrivilegesRequired=lowest，只写 HKCU；它的 InstallLocation 带尾部反斜杠，
    /// 所以两边都归一化再比）。读不动注册表就按绿色版算 —— 宁可让人手动下载，
    /// 也不要往别人的安装目录里装。
    /// </summary>
    public static bool IsInstalledCopy()
    {
        var here = AppContext.BaseDirectory;

        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return false;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("InstallLocation") is not string location) continue;
                if (IsSameDir(location, here)) return true;
            }
        }
        catch
        {
            // 读不动注册表就按绿色版处理
        }

        return false;
    }

    /// <summary>目录比较用：去掉首尾空白和尾部分隔符，统一大小写（Windows 路径不区分大小写）。</summary>
    internal static string NormalizeDir(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();

    /// <summary>两个路径是不是同一个目录。空串一律判否 —— 别让空的 InstallLocation 撞上空的基准目录。</summary>
    internal static bool IsSameDir(string left, string right)
    {
        var a = NormalizeDir(left);
        return a.Length > 0 && a == NormalizeDir(right);
    }

    /// <summary>
    /// 拉最新正式版。候选按「镜像 → 直连」排，第一个通的就是答案；都拿不到返回 null。
    /// </summary>
    public static async Task<UpdateRelease?> FetchLatestAsync(CancellationToken ct = default)
    {
        foreach (var url in ReleaseApiUrls())
        {
            try
            {
                var body = await ReadMaybeLocalAsync(url, ct).ConfigureAwait(false);
                if (body is null) continue;

                var release = JsonSerializer.Deserialize<UpdateRelease>(body.TrimStart('\uFEFF'), Json);
                if (release is null || !release.IsUsable) continue;

                LastError = null;
                return release;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        return null;
    }

    /// <summary>
    /// 最新版在哪儿取。<c>JICUN_RELEASE_API</c> 指到本地 JSON 文件即可离线演练弹窗（自检也用它）。
    /// 镜像先试：直连 api.github.com 国内时通时不通。**ghfast 不能用**，实测它对 api 路径回 403。
    /// </summary>
    private static IEnumerable<string> ReleaseApiUrls()
    {
        var custom = Environment.GetEnvironmentVariable("JICUN_RELEASE_API");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            // 指定了就只走它：排障 / 自检不该再偷偷去撞真地址
            yield return custom.Trim();
            yield break;
        }

        var api = "https://api.github.com/repos/" + Repo + "/releases/latest";
        yield return "https://gh-proxy.com/" + api;
        yield return api;
    }

    /// <summary>
    /// 读一个地址。**如果这个地址其实是本机已存在的文件路径，就直接读文件** ——
    /// 这样排障和自检能在「还没发布任何版本」的情况下，把「弹窗里到底显示什么」完整跑一遍
    /// （<c>JICUN_RELEASE_API</c> 指到本地 JSON 即可）。
    /// </summary>
    private static async Task<string?> ReadMaybeLocalAsync(string url, CancellationToken ct)
    {
        if (File.Exists(url)) return await File.ReadAllTextAsync(url, ct).ConfigureAwait(false);

        using var resp = await ApiHttp.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK) return null;
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> DownloadUrls(string url)
    {
        var u = (url ?? "").Trim();
        if (!u.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
            !u.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            yield return u;
            yield break;
        }

        yield return "https://ghfast.top/" + u;
        yield return "https://gh-proxy.com/" + u;
        yield return u;
    }

    /// <summary>
    /// 下载安装器。镜像站在国内快得多，但它们毕竟是第三方，可能给回来一个坏文件 ——
    /// 所以**每下到一个候选就当场对 sha256**，对不上就删掉换下一个地址重来，全都不行才报错。
    /// 校验放这一层（不再只交给调用方）的原因：换源重试只有在这里做得了。
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateRelease release, ReleaseAsset asset,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdateDir);
        var setup = Path.Combine(UpdateDir, "Jicun-Setup-" + PathSafeVersion(release.VersionText) + ".exe");
        var expected = asset.Sha256;
        if (!IsSha256(expected)) throw new IOException("这个版本没带可校验的 sha256，不装");

        Exception? last = null;

        foreach (var url in DownloadUrls(asset.BrowserDownloadUrl ?? ""))
        {
            var part = setup + ".part";
            try
            {
                using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                overall.CancelAfter(TimeSpan.FromMinutes(20));

                using var resp = await DownloadHttp
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, overall.Token).ConfigureAwait(false);
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    last = new IOException("HTTP " + (int)resp.StatusCode);
                    continue;
                }

                var total = resp.Content.Headers.ContentLength ?? asset.Size;
                var done = 0L;

                await using (var src = await resp.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer, overall.Token).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), overall.Token).ConfigureAwait(false);
                        done += read;
                        if (total > 0) progress?.Report(Math.Clamp((double)done / total, 0d, 1d));
                    }
                }

                // 下完当场对指纹：对不上说明这个源给回来一个坏文件，删掉换下一个地址重来
                if (!await VerifyAsync(part, expected).ConfigureAwait(false))
                {
                    last = new IOException("下到的文件 sha256 对不上");
                    progress?.Report(0d);
                    TryDeleteFile(part);
                    continue;
                }

                File.Move(part, setup, true);
                progress?.Report(1d);
                LastError = null;
                return setup;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDeleteFile(part);
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDeleteFile(part);
            }
        }

        LastError = last?.Message;
        throw new IOException("下载失败：" + (last?.Message ?? "没有可用的下载地址"));
    }

    /// <summary>对 sha256。除了文件完整性，这也是防「镜像站给你塞了个别的 exe」的唯一一道。</summary>
    public static async Task<bool> VerifyAsync(string file, string? expected)
    {
        if (!IsSha256(expected)) return false;
        try
        {
            await using var fs = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs).ConfigureAwait(false));
            return string.Equals(actual, expected!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 静默跑安装器的参数。
    ///
    /// <c>/SILENT</c> 有进度窗但不用点（<c>/VERYSILENT</c> 连窗口都没有，出问题看不见）；
    /// <c>/SUPPRESSMSGBOXES</c> 别弹框把后台更新卡住；<c>/NORESTART</c> 不重启系统；
    /// <c>/CLOSEAPPLICATIONS</c> 让它自己去处理还占着文件的进程 —— 我们随后就退出了，
    /// 但「退出」和它开始复制之间还有窗口期。
    ///
    /// 不传 <c>/DIR</c>：安装器认得自己上次装到哪儿（UsePreviousAppDir），而调到这里之前
    /// 已经用 <see cref="IsInstalledCopy"/> 确认过当前目录就是它装的那个。
    /// </summary>
    internal static readonly string[] SetupArguments =
        { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS" };

    /// <summary>
    /// 起安装器。装完由安装器自己的 <c>[Run]</c> 条目把新版拉起来（那条去掉了 skipifsilent，
    /// 所以静默安装也照样跑），我们这边只负责起进程然后退出。
    /// 返回 false = 进程没起来，调用方得把原因说给用户。
    /// </summary>
    public static bool RunSetup(string setupPath)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = setupPath, UseShellExecute = false };
            foreach (var arg in SetupArguments) psi.ArgumentList.Add(arg);
            return Process.Start(psi) is not null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>清掉上次留下的安装器和半截下载。启动时顺手做一次，省得堆在 %LOCALAPPDATA% 里。</summary>
    public static void CleanupOldArtifacts() => TryDeleteDir(UpdateDir);

    private static string ReadText(string path) => File.ReadAllText(path);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
