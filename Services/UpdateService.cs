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
/// 版本号、更新说明、下载地址、sha256 全在清单里，客户端只认这一份。
/// </remarks>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string? Notes { get; set; }

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

    // 清单很小，10 秒够了；下载单独一个 HttpClient，不然大文件会被超时掐死。
    private static readonly HttpClient ManifestHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
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
                using var resp = await ManifestHttp.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode != HttpStatusCode.OK) continue;

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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

    private static IEnumerable<string> ManifestUrls()
    {
        // 本机调试 / 自建镜像用：直接指定清单地址，跳过所有候选
        var custom = Environment.GetEnvironmentVariable("JICUN_UPDATE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(custom)) yield return custom.Trim();

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
        var zip = Path.Combine(UpdateDir, "Jicun-" + manifest.Version + ".zip");
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
        var staging = Path.Combine(UpdateDir, "staging-" + version);
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
