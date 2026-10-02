using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jicun.Desktop.Services;

/// <summary>
/// 一份「最新版」清单，放在仓库里的 <c>update/&lt;运行时&gt;.json</c>。
/// </summary>
/// <remarks>
/// 为什么不直接问 GitHub Releases API：那个接口国内时通时不通，也没有 CDN 可以垫。
/// 清单是个静态文件，能走 GitHub 镜像 + jsDelivr —— 这就是「检测走 CDN / 镜像」。
/// 版本号、下载地址、sha256 全在清单里，客户端只认这一份。
/// 更新说明**不在**清单里：只从 GitHub Release 正文取（见 FetchNotesAsync）。
/// </remarks>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";

    /// <summary>zip 的地址。GitHub 的 release 附件会被换成镜像地址再试。</summary>
    public string Url { get; set; } = "";

    /// <summary>zip 的 sha256（64 位十六进制）。**必须**有：镜像站是第三方，只能靠它对文件。</summary>
    public string? Sha256 { get; set; }

    public long Size { get; set; }
    public string? PublishedAt { get; set; }

    /// <summary>版本解得出来、地址是 http(s)、哈希是 64 位十六进制，才认这份清单。</summary>
    public bool IsUsable =>
        UpdateService.ParseVersion(Version) is not null &&
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        IsSha256(Sha256);

    internal static bool IsSha256(string? value)
    {
        var s = value ?? "";
        if (s.Length != 64) return false;
        foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}

/// <summary>
/// 检查更新 + 下载新版本。
/// </summary>
/// <remarks>
/// 三段式：<list type="number">
///   <item>拉清单（镜像 → 直连 → CDN，第一个通的就用）；</item>
///   <item>下载 zip（同样走镜像），下完算 sha256 对清单；</item>
///   <item>解压到 %LOCALAPPDATA%\Jicun\update\staging-&lt;版本&gt;，交给新版的 --apply-update 进程去覆盖安装。</item>
/// </list>
/// 「忽略某个版本」存在 %LOCALAPPDATA%\Jicun\update-state.json —— 只有比它更高的版本才会再弹窗。
/// </remarks>
public static class UpdateService
{
    private const string Repo = "dhvbjvvb/jicun-desktop";
    private const string Branch = "main";

    /// <summary>仓库里清单的路径。发版脚本 release.ps1 写的就是这儿。</summary>
    private static string ManifestPath => "update/" + Rid + ".json";

    private static string Rid =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    private static readonly string StateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun");

    private static readonly string StatePath = Path.Combine(StateDir, "update-state.json");

    /// <summary>下载的 zip 和解压出来的新版都放这儿。装完系统自己会清，装不上就留着下次重来。</summary>
    public static string UpdateDir { get; } = Path.Combine(StateDir, "update");

    // 三套超时这里各占一层：清单用**固定超时**（10 秒够，文件很小），下载把上限交给调用处的
    // **整体预算** CTS（20 分钟，见 DownloadAsync）—— 大文件绝不能被一个固定超时掐死。
    private static readonly HttpClient ManifestHttp = CreateManifestHttp();

    /// <summary>
    /// 清单和 Release 说明都走这个客户端。必须带 User-Agent：GitHub 的 API 不给没有 UA 的请求回数据。
    /// </summary>
    private static HttpClient CreateManifestHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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

    /// <summary>把清单里的版本号收敛成「能安全拼进路径」的形状。</summary>
    /// <remarks>
    /// 远端字符串绝不能直接进路径：<c>1.0.1-..\..\..\Temp</c> 这种能过 <see cref="ParseVersion"/>
    /// 的截断（在 - 处切）也能过 <see cref="UpdateManifest.IsUsable"/> 的形状校验，而 staging 路径
    /// 后面跟着的是递归删除 + 解压 + 启动进程。凡是要拼进路径的版本号都先过这里。
    /// </remarks>
    internal static string PathSafeVersion(string version)
    {
        var clean = ParseVersion(version)?.ToString();
        if (string.IsNullOrEmpty(clean)) throw new FormatException("清单里的版本号不对劲：" + version);
        return clean;
    }

    #region 忽略的版本

    private sealed class State
    {
        public string? IgnoredVersion { get; set; }
    }

    private static string? _ignored;
    private static bool _loaded;

    /// <summary>被用户点过「忽略」的版本。比它更高的版本照样弹。</summary>
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

    /// <summary>能不能自己覆盖自己。装在 Program Files 里或者只读盘上就只能手动更新。</summary>
    public static bool CanSelfUpdate()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".jicun-update-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 拉最新版清单。候选按「镜像 → 直连 → CDN」排，第一个通的就是答案。
    /// </summary>
    /// <remarks>
    /// jsDelivr 垫最后：它是真 CDN，但缓存最长 12 小时，刚发的版可能还是旧的；
    /// 而且本机实测 8 秒才回，只适合当前面全挂掉的时候兜底。
    /// </remarks>
    public static async Task<UpdateManifest?> FetchLatestAsync(CancellationToken ct = default)
    {
        foreach (var url in ManifestUrls())
        {
            try
            {
                var body = await ReadMaybeLocalAsync(url, ct).ConfigureAwait(false);
                if (body is null) continue;
                var manifest = JsonSerializer.Deserialize<UpdateManifest>(body.TrimStart('\uFEFF'), Json);
                if (manifest is null || !manifest.IsUsable) continue;

                LastError = null;
                return manifest;
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
    /// 拉某个版本的**发布说明**（GitHub Release 正文的 Markdown 原文）。拉不到返回 null，
    /// 调用方会退回清单里那份 notes。
    /// </summary>
    /// <remarks>
    /// 为什么版本检测不走这个接口、说明却走：检测要的是「稳」（静态清单能走镜像和 CDN），
    /// 而说明是给人看的文字 —— 从 Release 正文拿的好处是**你在 GitHub 网页上改了说明，
    /// 用户下次检查更新就能看到**，不用重新发一版。
    /// 镜像（gh-proxy）也会经手这段文字，但它只是文本、不执行任何东西；里面的链接只放行 http(s)。
    /// </remarks>
    public static async Task<string?> FetchNotesAsync(string version, CancellationToken ct = default)
    {
        foreach (var url in ReleaseApiUrls(version))
        {
            try
            {
                var body = await ReadMaybeLocalAsync(url, ct).ConfigureAwait(false);
                if (body is null) continue;

                using var doc = JsonDocument.Parse(body.TrimStart('\uFEFF'));
                if (!doc.RootElement.TryGetProperty("body", out var value) || value.ValueKind != JsonValueKind.String) continue;

                var notes = value.GetString();
                if (!string.IsNullOrWhiteSpace(notes)) return notes.Trim();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                // 换下一个地址；都拿不到就退回清单里那份说明
            }
        }
        return null;
    }

    /// <summary>
    /// 某版本的 Release 说明在哪儿取。<c>JICUN_RELEASE_API</c> 是排障 / 自检用的（可以指本地文件）。
    /// **不能让 ghfast 上**：实测它对 api 路径回 403，它只代理 raw 和 releases 附件。
    /// </summary>
    private static IEnumerable<string> ReleaseApiUrls(string version)
    {
        var custom = Environment.GetEnvironmentVariable("JICUN_RELEASE_API");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            // 指定了就只走它：排障 / 自检不该再偷偷去撞真地址
            yield return custom.Trim();
            yield break;
        }

        var tag = version.StartsWith('v') || version.StartsWith('V') ? version : "v" + version;
        var api = "https://api.github.com/repos/" + Repo + "/releases/tags/" + tag;
        yield return "https://gh-proxy.com/" + api;   // 国内直连 api.github.com 常被挡，镜像先试
        yield return api;
    }

    /// <summary>
    /// 读一个地址。**如果这个地址其实是本机已存在的文件路径，就直接读文件** ——
    /// 这样排障和自检能在「还没发布任何版本」的情况下，把「弹窗里到底显示什么」完整跑一遍
    /// （JICUN_UPDATE_MANIFEST / JICUN_RELEASE_API 指到本地 JSON 即可）。
    /// </summary>
    private static async Task<string?> ReadMaybeLocalAsync(string url, CancellationToken ct)
    {
        if (File.Exists(url)) return await File.ReadAllTextAsync(url, ct).ConfigureAwait(false);

        using var resp = await ManifestHttp.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK) return null;
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static IEnumerable<string> ManifestUrls()
    {
        // 本机调试 / 自建镜像用：直接指定清单地址，跳过所有候选
        var custom = Environment.GetEnvironmentVariable("JICUN_UPDATE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            yield return custom.Trim();
            yield break;
        }

        var raw = "https://raw.githubusercontent.com/" + Repo + "/" + Branch + "/" + ManifestPath;
        yield return "https://ghfast.top/" + raw;
        yield return "https://gh-proxy.com/" + raw;
        yield return raw;
        yield return "https://cdn.jsdelivr.net/gh/" + Repo + "@" + Branch + "/" + ManifestPath;
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
    /// 下载 zip。镜像站在国内快得多，但它们毕竟是第三方，可能给回来一个坏文件 ——
    /// 所以下完必须过 <see cref="VerifyAsync"/>，那一步才是安全边界。
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UpdateDir);
        var zip = Path.Combine(UpdateDir, "Jicun-" + PathSafeVersion(manifest.Version) + ".zip");
        Exception? last = null;

        foreach (var url in DownloadUrls(manifest.Url))
        {
            var part = zip + ".part";
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

                var total = resp.Content.Headers.ContentLength ?? manifest.Size;
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

                File.Move(part, zip, true);
                progress?.Report(1d);
                LastError = null;
                return zip;
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
        if (!UpdateManifest.IsSha256(expected)) return false;
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

    /// <summary>解压到独立目录。**不**直接往安装目录里解 —— 那时候主程序还在跑，文件都占着。</summary>
    public static string Extract(string zip, string version)
    {
        var staging = Path.Combine(UpdateDir, "staging-" + PathSafeVersion(version));
        TryDeleteDir(staging);
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zip, staging, true);
        return staging;
    }

    /// <summary>
    /// 用**新版自己**的 exe 起一个隐藏进程，它等我们退干净之后再覆盖安装。
    /// 用自己人当安装器省一个额外二进制：Program.cs 那条命令行分支就是干这个的。
    /// </summary>
    public static bool StartInstaller(string staging)
    {
        var exe = Path.Combine(staging, "Jicun.exe");
        if (!File.Exists(exe)) return false;

        var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = staging,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "--apply-update", "--pid", Environment.ProcessId.ToString(), "--from", staging, "--to", target })
            psi.ArgumentList.Add(arg);

        return Process.Start(psi) is not null;
    }

    /// <summary>清掉上次留下的 zip / 解压目录。启动时顺手做一次，省得堆在 %LOCALAPPDATA% 里。</summary>
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
