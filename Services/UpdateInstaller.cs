using System.Diagnostics;
using System.IO;

namespace Jicun.Desktop.Services;

/// <summary>
/// 把 staging 里的新版覆盖到安装目录上，再把新版拉起来。
/// </summary>
/// <remarks>
/// 为什么非得另起一个进程：正在跑的 exe 和已经加载的 dll 都是占着的，自己盖自己盖不动。
/// 所以整条链路是「下载 → 校验 → 解压 → 用新版 exe 起 --apply-update 进程 → 主程序退出 →
/// 这个进程等它退干净了再拷文件 → 启动新版」。用户看到的就是闪一下又回来了。
/// </remarks>
internal static class UpdateInstaller
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun", "update.log");

    public static int Apply(int pid, string from, string to)
    {
        Log("start pid=" + pid + " from=" + from + " to=" + to);

        try
        {
            WaitForExit(pid);

            var failed = CopyTree(from, to);
            Log(failed.Count == 0 ? "copy ok" : "copy 失败 " + failed.Count + " 个：" + string.Join(", ", failed));

            var exe = Path.Combine(to, "Jicun.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = to, UseShellExecute = false });
                Log("已重启 " + exe);
            }
            else
            {
                Log("!! 目标目录里没有 Jicun.exe，不敢重启");
            }

            // 自己就在 from 里跑着，删不掉自己；剩下的下次启动由 CleanupOldArtifacts 收
            TryDeleteDir(from);
            return failed.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("!! " + ex);
            return 1;
        }
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(60_000)) Log("等 " + pid + " 退出超时，照样往下走");
            else Log("主程序已退出");
        }
        catch (ArgumentException)
        {
            // 已经退干净了
        }
        catch (Exception ex)
        {
            Log("等进程退出时出错（继续）：" + ex.Message);
        }
    }

    /// <summary>整棵目录树拷过去。返回没拷成功的文件（相对路径 + 异常类型）。</summary>
    private static List<string> CopyTree(string from, string to)
    {
        var failed = new List<string>();

        foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, src);
            var dst = Path.Combine(to, relative);
            try
            {
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                CopyWithRetry(src, dst);
            }
            catch (Exception ex)
            {
                failed.Add(relative + " (" + ex.GetType().Name + ")");
            }
        }

        return failed;
    }

    /// <summary>
    /// 覆盖单个文件，失败就重试 —— 大概率是杀软或者资源管理器正摸着这个文件不放，
    /// 几百毫秒之后就好了。重试完还不行才算失败。
    /// </summary>
    private static void CopyWithRetry(string src, string dst)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(src, dst, true);
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (attempt < 40)
            {
                Thread.Sleep(150);
            }
        }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void Log(string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch
        {
            // 日志而已
        }
    }
}
