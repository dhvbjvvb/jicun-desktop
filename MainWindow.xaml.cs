using Jicun.Desktop.Services;
using Jicun.Desktop.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jicun.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "即存";
        AppServices.MainWindow = this;

        // Windows 11 给 Mica，10 退到 Acrylic。
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 自绘标题栏 + 无打包运行，窗口图标得自己给：ApplicationIcon 只管 exe 文件
        // 在资源管理器里的样子，不管已经跑起来的窗口和任务栏按钮。
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "jicun.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        ContentFrame.Navigate(typeof(ParsePage));
        Nav.SelectedItem = Nav.MenuItems[0];

        // 预览是独立窗口，主窗口关了得把它们一起带走，否则进程退不干净。
        Closed += (_, _) => PreviewWindow.CloseAll();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        NavigateTo(item.Tag as string);
    }

    /// <summary>侧边栏切换。下载页加入队列后需要把用户带过去看进度。</summary>
    public void NavigateTo(string? tag)
    {
        var page = tag switch
        {
            "downloads" => typeof(DownloadsPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(ParsePage),
        };

        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);

        foreach (var obj in Nav.MenuItems.Concat<object>(Nav.FooterMenuItems))
        {
            if (obj is NavigationViewItem nvi && (nvi.Tag as string) == tag)
            {
                Nav.SelectedItem = nvi;
                break;
            }
        }
    }
}
