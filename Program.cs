using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Jicun.Desktop;

/// <summary>
/// 自己接管进程入口：带参数就是命令行模式（不起 UI），不带参数才走 WinUI 的正常启动。
/// XAML 自动生成的 Main 由 DISABLE_XAML_GENERATED_MAIN 关掉，见 Jicun.Desktop.csproj。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 只有 -x / --x / /x 这种开关才当命令行，避免以后加文件关联时误判
        if (args.Length > 0 && (args[0].StartsWith('-') || args[0].StartsWith('/')))
            return Cli.RunAsync(args).GetAwaiter().GetResult();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            new App();
        });
        return 0;
    }
}
