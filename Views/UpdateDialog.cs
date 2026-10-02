using System.Diagnostics;
using System.IO;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jicun.Desktop.Views;

/// <summary>
/// 「检查更新」这件事的编排：谁调都走这里，统一处理「检查中 / 已是最新 / 有新版（弹窗）」。
/// </summary>
internal static class UpdateFlow
{
    private static int _busy;

    /// <summary>
    /// 检查一次，返回设置页要显示的那句话。启动时的自动检查不看返回值。
    /// </summary>
    /// <param name="manual">
    /// 手动检查 = 用户就是想看这一版：**忽略过也照样弹**（他可能改主意了）。
    /// 自动检查则跳过被忽略的版本，只有更新更高的版本才再打扰一次。
    /// </param>
    public static async Task<string> RunAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return "正在检查更新…";

        try
        {
            if (!UpdateService.CanSelfUpdate())
                return "程序目录没有写权限，自动更新用不了，请到 GitHub 手动下载新版";

            var latest = await UpdateService.FetchLatestAsync();
            if (latest is null) return "检查更新失败，请稍后再试";

            var version = UpdateService.ParseVersion(latest.Version)!;
            if (version <= UpdateService.CurrentVersion) return "已是最新版本 " + UpdateService.CurrentVersionText;

            if (!manual && UpdateService.IgnoredVersion == latest.Version)
                return "有新版本 " + latest.Version + "（已忽略）";

            await UpdateDialog.ShowAsync(latest);
            return "有新版本 " + latest.Version;
        }
        catch
        {
            return "检查更新失败，请稍后再试";
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}

/// <summary>
/// 新版公告窗口：上面是更新说明，底下左边「忽略」右边「更新」。
/// 点更新之后按钮锁住、就地变成进度条，校验通过就静默安装 + 重启。
/// </summary>
internal static class UpdateDialog
{
    public static async Task ShowAsync(UpdateManifest manifest)
    {
        var root = await WaitForRootAsync();
        if (root is null) return;

        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(manifest.Notes) ? "（本次发布没有写更新说明）" : manifest.Notes.Trim(),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var scroll = new ScrollViewer
        {
            Content = notes,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        var percent = new TextBlock { FontSize = 12, Visibility = Visibility.Collapsed };
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var head = new TextBlock
        {
            Text = "当前版本 " + UpdateService.CurrentVersionText + "  →  新版本 " + manifest.Version
                   + (manifest.Size > 0 ? "（" + SizeText(manifest.Size) + "）" : ""),
            TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(head);
        panel.Children.Add(scroll);
        panel.Children.Add(bar);
        panel.Children.Add(percent);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "发现新版本 " + manifest.Version,
            Content = panel,
            PrimaryButtonText = "更新",
            SecondaryButtonText = "忽略",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 620d;
        dialog.Resources["ContentDialogMaxHeight"] = 640d;

        // 忽略：只忽略这一个版本。比它高的版本照样会弹，手动检查也照样能再看到它。
        dialog.SecondaryButtonClick += (_, _) => UpdateService.SetIgnored(manifest.Version);

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            // 下载期间别让对话框关掉，进度条就是在它里面动的
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            bar.IsIndeterminate = false;
            bar.Visibility = Visibility.Visible;
            percent.Visibility = Visibility.Visible;
            error.Visibility = Visibility.Collapsed;
            _ = InstallAsync(dialog, manifest, bar, percent, error);
        };

        await dialog.ShowAsync();
    }

    private static async Task InstallAsync(ContentDialog dialog, UpdateManifest manifest,
        ProgressBar bar, TextBlock percent, TextBlock error)
    {
        try
        {
            // 上次没装完的残骸先清掉
            UpdateService.CleanupOldArtifacts();

            var progress = new Progress<double>(value =>
            {
                bar.Value = value * 100;
                percent.Text = "正在下载 " + (value * 100).ToString("0") + "%"
                               + (manifest.Size > 0 ? "（" + SizeText(manifest.Size) + "）" : "");
            });

            percent.Text = "正在下载 0%";
            var zip = await UpdateService.DownloadAsync(manifest, progress);

            percent.Text = "正在校验文件…";
            bar.IsIndeterminate = true;
            if (!await UpdateService.VerifyAsync(zip, manifest.Sha256))
                throw new IOException("文件校验没通过，这次更新已放弃");

            percent.Text = "正在解压…";
            var staging = UpdateService.Extract(zip, manifest.Version);

            percent.Text = "正在安装，马上重启…";
            if (!UpdateService.StartInstaller(staging))
                throw new IOException("安装进程起不来");

            // 交给 --apply-update 那个进程，我们退出它才好覆盖文件
            dialog.Hide();
            AppServices.MainWindow?.Close();
        }
        catch (Exception ex)
        {
            bar.IsIndeterminate = false;
            bar.Visibility = Visibility.Collapsed;
            percent.Visibility = Visibility.Collapsed;
            error.Text = "更新失败：" + ex.Message + "\n可以稍后再试，或者自己到 GitHub 上下载。";
            error.Visibility = Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = true;
            dialog.IsSecondaryButtonEnabled = true;
        }
    }

    /// <summary>窗口刚起来的时候 XamlRoot 还没准备好，等它几秒。</summary>
    private static async Task<XamlRoot?> WaitForRootAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            if (AppServices.MainWindow?.Content?.XamlRoot is { } root) return root;
            await Task.Delay(100);
        }
        return null;
    }

    private static string SizeText(long bytes) => bytes >= 1024L * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0") + " MB"
        : (bytes / 1024d).ToString("0") + " KB";
}
