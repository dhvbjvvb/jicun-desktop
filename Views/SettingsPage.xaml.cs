using System.Diagnostics;
using System.Reflection;
using Jicun.Desktop.Models;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Jicun.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Refresh();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "即存 for Windows " + (version is null ? "dev" : version.ToString(3));
    }

    private void Refresh()
    {
        VideoBox.Text = AppServices.FolderFor(MediaKind.Video);
        ImageBox.Text = AppServices.FolderFor(MediaKind.Image);
        AudioBox.Text = AppServices.FolderFor(MediaKind.Audio);
    }

    private static MediaKind KindOf(object sender) => (sender as FrameworkElement)?.Tag switch
    {
        "image" => MediaKind.Image,
        "audio" => MediaKind.Audio,
        _ => MediaKind.Video,
    };

    private async void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.MainWindow is not { } window) return;

        var kind = KindOf(sender);
        var picker = new FolderPicker
        {
            SuggestedStartLocation = kind switch
            {
                MediaKind.Image => PickerLocationId.PicturesLibrary,
                MediaKind.Audio => PickerLocationId.MusicLibrary,
                _ => PickerLocationId.VideosLibrary,
            },
        };
        picker.FileTypeFilter.Add("*");

        // 非打包应用必须把窗口句柄交给选择器，否则它不知道怎么弹
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        AppServices.Settings.Current.SetFolder(kind, folder.Path);
        AppServices.Settings.Save();
        Refresh();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = AppServices.FolderFor(KindOf(sender));
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了
        }
    }

    /// <summary>手动检查：忽略过的版本也照样弹，用户可能改主意了。</summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            UpdateStatusText.Text = await UpdateFlow.RunAsync(manual: true);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private const string RepoUrl = "https://github.com/dhvbjvvb/jicun-desktop";

    private void OnCopyRepoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton button) return;
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(RepoUrl);
            Clipboard.SetContent(pkg);

            // 复制成功给个瞬间反馈，1.5 秒后还原
            var original = button.Content;
            button.Content = "已复制";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => button.Content = original;
            timer.Start();
        }
        catch
        {
            // 剪贴板被别人占着就算了
        }
    }
}
