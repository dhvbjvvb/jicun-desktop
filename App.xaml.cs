using Jicun.Desktop.Services;
using Jicun.Desktop.Views;
using Microsoft.UI.Xaml;

namespace Jicun.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // 先把上次生效的域名读回来，再后台拉一份 /ips.json（不阻塞界面）
        AppServices.Hosts.Boot();

        // 上次合并到一半被强杀，临时文件会留在下载目录里。**必须放在续传之前** ——
        // 续传可能马上又开始合并，同名文件会被这一步误删。
        AppServices.Downloads.CleanupStaleMerges();

        // 上次没下完的接着下
        AppServices.Downloads.ResumePending();

        // 上次更新留下的残骸顺手清掉，再后台查一次有没有新版（查到就弹公告窗口）
        UpdateService.CleanupOldArtifacts();

        // 音频预览缓冲（%LOCALAPPDATA%\Jicun\preview）是临时文件，不是用户数据，启动顺手清掉
        MediaHttp.CleanPreviewCache();
        _ = UpdateFlow.RunAsync(manual: false);
    }
}
