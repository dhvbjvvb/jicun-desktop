using System.Runtime.InteropServices;
using System.Text;
using Jicun.Desktop.Models;
using Jicun.Desktop.Services;

namespace Jicun.Desktop;

/// <summary>
/// 无界面模式。给批处理、排障、以及「下载器到底能不能下」这种验证用。
///
///     Jicun.exe --download &lt;url&gt; [--name 文件名] [--dir 文件夹]
///     Jicun.exe --parse &lt;链接或整段分享文案&gt;
///
/// ponytail: 出口是 WinExe，双击时不弹黑框；从终端跑的时候靠 AttachConsole 挂回父进程的控制台。
/// </summary>
internal static class Cli
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public static async Task<int> RunAsync(string[] args)
    {
        EnsureConsole();

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--download" or "-d":
                    return await DownloadAsync(args).ConfigureAwait(false);
                case "--parse" or "-p":
                    return await ParseAsync(args).ConfigureAwait(false);
                case "--hosts":
                    return await HostsAsync().ConfigureAwait(false);
                case "--secrets":
                    return SecretsCommand();
                case "--selftest":
                    return SelfCheck.Run();
                case "--help" or "-h" or "--?":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine("未知参数：" + args[0]);
                    PrintHelp();
                    return 2;
            }
        }
        catch (ParseException ex)
        {
            Console.Error.WriteLine("解析失败：" + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("出错：" + ex.Message);
            return 1;
        }
    }

    private static async Task<int> DownloadAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：Jicun.exe --download <url> [--name 文件名] [--dir 文件夹] [--kind video|image|audio]");
            return 2;
        }

        string? name = null;
        string? dir = null;
        string? title = null;
        string? artist = null;
        string? album = null;
        string? cover = null;
        string? kindArg = null;
        var audio = false;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--name": name = Next(args, ref i); break;
                case "--dir": dir = Next(args, ref i); break;
                case "--audio": audio = true; break;
                case "--kind": kindArg = Next(args, ref i); break;
                case "--title": title = Next(args, ref i); break;
                case "--artist": artist = Next(args, ref i); break;
                case "--album": album = Next(args, ref i); break;
                case "--cover": cover = Next(args, ref i); break;
            }
        }

        var kind = audio
            ? MediaKind.Audio
            : (kindArg?.ToLowerInvariant() switch
            {
                "image" or "img" => MediaKind.Image,
                "audio" => MediaKind.Audio,
                _ => MediaKind.Video,
            });

        // 没给 --dir 时按类型走设置页那三个目录
        var folder = dir ?? AppServices.FolderFor(kind);
        var item = new MediaItem
        {
            Kind = kind,
            Url = args[1],
            FileName = name ?? GuessName(args[1]),
            TagTitle = title,
            TagArtist = artist,
            TagAlbum = album,
            TagCoverUrl = cover,
        };

        Console.WriteLine("链接  " + item.Url);
        Console.WriteLine("文件名 " + item.FileName);
        Console.WriteLine("保存到 " + folder);

        var task = new DownloadTask(item, folder);
        var running = DownloadService.RunOnceAsync(task);

        while (!running.IsCompleted)
        {
            await Task.Delay(300).ConfigureAwait(false);
            var line = "  " + task.SizeLabel;
            if (task.SpeedLabel.Length > 0) line += "  " + task.SpeedLabel;
            Console.Write("\r" + line.PadRight(48));
        }

        await running.ConfigureAwait(false);
        Console.WriteLine();

        if (task.State == DownloadState.Done)
        {
            Console.WriteLine("完成  " + task.SavedPath);
            return 0;
        }

        Console.Error.WriteLine("失败：" + (task.Error ?? task.StateLabel));
        return 1;
    }

    private static string? Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("参数 " + args[i] + " 后面缺一个值");
            return null;
        }
        return args[++i];
    }

    /// <summary>看上游直连的密钥配没配、从哪里读的（排障用，不打印密钥本身）。</summary>
    private static int SecretsCommand()
    {
        Console.WriteLine("配置文件 " + Secrets.FilePath +
                          (File.Exists(Secrets.FilePath) ? "（存在）" : "（不存在）"));
        Console.WriteLine("密钥     " + (Secrets.ApiKey.Length == 0
            ? "(未配置)"
            : "已配置，" + Secrets.ApiKey.Length + " 字符，来源 " + Secrets.KeySource));
        Console.WriteLine("上游基址 " + (Secrets.BaseUrl.Length == 0
            ? "(未配置)"
            : Secrets.BaseUrl + "（来源 " + Secrets.BaseSource + "）"));
        Console.WriteLine("可用     " + (Secrets.IsConfigured ? "是，抖音/快手/豆包/微信视频号会先走上游" : "否，全部走 media-parser"));

        if (Secrets.ApiKey.Length > 0) return 0;

        Console.WriteLine();
        Console.WriteLine("还没配上游密钥。配法，前者优先：");
        Console.WriteLine("  1. 环境变量  JICUN_UPSTREAM_KEY / JICUN_UPSTREAM_BASE");
        Console.WriteLine("  2. 写进上面的配置文件：{\"upstreamKey\": \"…\", \"upstreamBase\": \"https://…\"}");
        Console.WriteLine("  3. 或复制 Services\\LocalDefaults.cs.example 成 LocalDefaults.cs，填上再重新构建");
        return 1;
    }

    private static async Task<int> ParseAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法：Jicun.exe --parse <分享链接或整段分享文案>");
            return 2;
        }

        var input = string.Join(' ', args.Skip(1));
        var r = await AppServices.Parser.ParseAsync(input).ConfigureAwait(false);

        Console.WriteLine("标题   " + r.Title);
        Console.WriteLine("平台   " + r.Platform + (r.AuthorName.Length > 0 ? "  @" + r.AuthorName : ""));
        if (r.Desc.Length > 0) Console.WriteLine("文案   " + (r.Desc.Length > 80 ? r.Desc[..80] + "…" : r.Desc));

        for (var i = 0; i < r.Videos.Count; i++)
            Console.WriteLine($"视频{i + 1} [{r.Videos[i].Label ?? "default"}] {r.Videos[i].Url}");

        if (r.VideoUrl is not null) Console.WriteLine("视频   " + r.VideoUrl);
        if (r.AudioUrl is not null) Console.WriteLine("音频   " + r.AudioUrl);
        for (var i = 0; i < r.ImageUrls.Count; i++)
            Console.WriteLine($"图片{i + 1}  {r.ImageUrls[i]}");

        Console.WriteLine("路由   " + (AppServices.Parser.LastRoute ?? "?"));
        return 0;
    }

    /// <summary>拉一次 /ips.json，把域名热更这条路真跑一遍（排障 + 验证用）。</summary>
    private static async Task<int> HostsAsync()
    {
        ServerConfigStore.Load();
        Console.WriteLine("存档  " + ServerConfigStore.FilePath);
        Console.WriteLine("起始  " + ApiHosts.Current);

        var config = await AppServices.Hosts.RefreshAsync().ConfigureAwait(false);
        if (config is null)
        {
            Console.Error.WriteLine("没拉到 /ips.json，继续用内置兜底：" + string.Join(", ", ApiHosts.BuiltIn));
            return 1;
        }

        Console.WriteLine("生效  " + ApiHosts.Current);
        Console.WriteLine("域名  " + string.Join(", ", config.Hosts));
        Console.WriteLine("IP    " + (config.Ips.Count == 0 ? "(无)" : string.Join(", ", config.Ips)));
        Console.WriteLine("白名单 " + config.Supported.Count + " 条" +
                          (config.Supported.Count == 0 ? "" : "  例：" + string.Join(", ", config.Supported.Take(5))));
        Console.WriteLine("候选  " + string.Join(", ", ApiHosts.Candidates()));
        return 0;
    }

    private static string GuessName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var last = path.TrimEnd('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last)) return Uri.UnescapeDataString(last);
        }
        catch
        {
            // 不是合法 URL 就退回默认名，反正落盘前还有一道文件头嗅探
        }

        return "download.bin";
    }

    private static void PrintHelp()
    {
        Console.WriteLine("即存 · 命令行模式");
        Console.WriteLine();
        Console.WriteLine("  Jicun.exe                                            打开界面");
        Console.WriteLine("  Jicun.exe --download <url> [--name 文件名] [--dir 文件夹] [--kind 类型]  下载一个文件");
        Console.WriteLine("  Jicun.exe --parse <链接或分享文案>                      只看解析结果");
        Console.WriteLine("  Jicun.exe --hosts                                    拉一次 /ips.json，看域名热更结果");
        Console.WriteLine("  Jicun.exe --secrets                                  看上游直连密钥配没配");
        Console.WriteLine("  Jicun.exe --selftest                                 跑一遍纯逻辑自检（不联网、不开界面）");
        Console.WriteLine();
        Console.WriteLine("  --download 追加：--kind video|image|audio 按类型落到设置页对应的目录（默认 video）");
        Console.WriteLine("                    --audio 当音频下（写完会补 ID3/MP4 标签）");
        Console.WriteLine("                    --dir 直接指定目录，优先于 --kind");
        Console.WriteLine("                    --title / --artist / --album / --cover <封面地址>");
        Console.WriteLine();
    }

    /// <summary>从终端调用时挂回父进程的控制台，否则 WinExe 的输出哪儿都去不了。</summary>
    private static void EnsureConsole()
    {
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero) AttachConsole(AttachParentProcess);
        }
        catch
        {
            // 没有 kernel32 之外的指望，继续，重定向到文件时本来就是好的
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch
        {
            // 句柄无效就放弃输出，不影响下载本身
        }
    }
}
