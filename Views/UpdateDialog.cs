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
            // 版本、说明、附件、哈希都在这一次应答里，不用再单独取说明
            var latest = await UpdateService.FetchLatestAsync();
            if (latest?.Version is not { } version)
            {
                // 国内连不上 GitHub 是常态：手动检查时给「去发布页」这条出路，别只说「稍后再试」
                if (manual) await UpdateDialog.ShowCheckFailedAsync(UpdateService.LastError);
                return "检查更新失败，请稍后再试";
            }

            if (version <= UpdateService.CurrentVersion) return "已是最新版本 " + UpdateService.CurrentVersionText;

            if (!manual && UpdateService.IgnoredVersion == latest.TagName)
                return "有新版本 " + latest.VersionText + "（已忽略）";

            // 绿色版（解压即用）不递「更新」按钮：跑安装器会被装到 %LOCALAPPDATA%\Programs\Jicun，
            // 等于凭空多出一份，而原来那份还在原地。但要弹窗 —— 用户得知道自己落后了。
            var canInstall = UpdateService.IsInstalledCopy();
            var installer = canInstall ? latest.Installer : null;
            var hint = BuildHint(canInstall, installer, latest);

            await UpdateDialog.ShowAsync(latest, installer, hint);
            return "有新版本 " + latest.VersionText;
        }
        catch (Exception ex)
        {
            // 别闷声吞掉：弹窗渲染之类的意外在 Debug 输出里留一条，下次好定位
            Debug.WriteLine("检查更新失败：" + ex);
            return "检查更新失败，请稍后再试";
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 没得自动装时，弹窗里那句解释（有得装就返回 null）。
    /// 三种情况要分开说：绿色版、这个版本没带我们这架构的安装器、这个版本压根没带安装包（纯公告）。
    /// </summary>
    private static string? BuildHint(bool canInstall, ReleaseAsset? installer, UpdateRelease release)
    {
        if (installer is not null) return null;

        if (!canInstall)
            return "这份是绿色版（解压即用）：自动更新只对安装器装的版本生效。点「去下载」到发布页取新版绿色包，解压覆盖一下就行。";

        return release.Assets.Any(a => a.Name.StartsWith("Jicun-Setup-", StringComparison.OrdinalIgnoreCase))
            ? "这个版本没带 " + UpdateService.Rid + " 的安装器附件，只好手动下载。"
            : "这个版本只是一条公告，没有带安装包。";
    }
}

/// <summary>
/// 新版公告窗口：上面是这次改了什么（Release 正文），底下左边「忽略」右边「更新」（绿色版是「去下载」）。
/// 点更新之后按钮锁住、就地变成进度条，校验通过就静默跑安装器并把程序交出去。
/// </summary>
internal static class UpdateDialog
{
    // 弹窗尺寸固定，不跟着说明长短变：说明区吃剩下的高度（超了在里面滚），进度那几行永远可见
    private const double DialogWidth = 620;
    private const double DialogHeight = 520;
    private const double NotesWidth = 560;
    private const double NotesMinHeight = 160;

    /// <param name="installer">
    /// 要装的安装器附件；null = 这次只能手动下载（绿色版，或者这个版本没带安装包）。
    /// </param>
    /// <param name="hint">没得自动装时给用户的一句解释。</param>
    public static async Task ShowAsync(UpdateRelease release, ReleaseAsset? installer, string? hint)
    {
        var root = await WaitForRootAsync();
        if (root is null) return;

        var notes = new ScrollViewer
        {
            Content = Markdown.Build(release.Notes),
            Width = NotesWidth,
            MinHeight = NotesMinHeight,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        var percent = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var head = new TextBlock
        {
            Text = "当前版本 " + UpdateService.CurrentVersionText + "  →  新版本 " + release.VersionText
                   + (installer is { Size: > 0 } ? "（" + SizeText(installer.Size) + "）" : ""),
            TextWrapping = TextWrapping.Wrap,
        };

        var hintText = new TextBlock
        {
            Text = hint ?? "",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible,
        };

        // 进度（条 + 百分比 + 报错）单独一行贴在说明区下面。弹窗高度写死 520，说明区再写死 320，
        // 进度这几行就会被顶到可视区外面 —— 用户点完「更新」看着一片空，也不知道到底动没动。
        // 所以说明区做成 Star 行（剩多少用多少、在里面滚），进度那行是 Auto，永远挤不掉。
        var progressPanel = new StackPanel { Spacing = 6 };
        progressPanel.Children.Add(bar);
        progressPanel.Children.Add(percent);
        progressPanel.Children.Add(error);

        var grid = new Grid { RowSpacing = 8 };
        foreach (var height in new[]
                 {
                     GridLength.Auto, GridLength.Auto,
                     new GridLength(1, GridUnitType.Star), GridLength.Auto,
                 })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });

        Grid.SetRow(head, 0);
        grid.Children.Add(head);
        Grid.SetRow(hintText, 1);
        grid.Children.Add(hintText);
        Grid.SetRow(notes, 2);
        grid.Children.Add(notes);
        Grid.SetRow(progressPanel, 3);
        grid.Children.Add(progressPanel);

        var dialog = new ContentDialog
        {
            Title = "新版本 " + release.VersionText,
            Content = grid,
            PrimaryButtonText = installer is null ? "去下载" : "更新",
            SecondaryButtonText = "忽略",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        // 宽高都写死（Min = Max）：说明长短不该让弹窗忽大忽小，超长就在说明区里滚
        dialog.MinWidth = DialogWidth;
        dialog.MaxWidth = DialogWidth;
        dialog.MinHeight = DialogHeight;
        dialog.MaxHeight = DialogHeight;
        dialog.Resources["ContentDialogMinWidth"] = DialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = DialogWidth;
        dialog.Resources["ContentDialogMinHeight"] = DialogHeight;
        dialog.Resources["ContentDialogMaxHeight"] = DialogHeight;

        // 忽略：只忽略这一个版本。比它高的版本照样会弹，手动检查也照样能再看到它。
        dialog.SecondaryButtonClick += (_, _) => UpdateService.SetIgnored(release.TagName);

        dialog.PrimaryButtonClick += (sender, args) =>
        {
            if (installer is null)
            {
                // 绿色版 / 公告：开这个版本的发布页，让用户自己挑绿色包或安装器
                OpenTag(release);
                return;   // 不 Cancel：点完就把弹窗关掉
            }

            // 下载期间别让对话框关掉，进度条就是在它里面动的
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            bar.IsIndeterminate = false;
            bar.Visibility = Visibility.Visible;
            percent.Visibility = Visibility.Visible;
            error.Visibility = Visibility.Collapsed;
            _ = InstallAsync(dialog, release, installer, bar, percent, error);
        };

        await dialog.ShowAsync();
    }

    private static async Task InstallAsync(ContentDialog dialog, UpdateRelease release, ReleaseAsset installer,
        ProgressBar bar, TextBlock percent, TextBlock error)
    {
        try
        {
            // 上次没装完的残骸先清掉
            UpdateService.CleanupOldArtifacts();

            var progress = new Progress<double>(value => percent.Text = installer.Size > 0
                ? "正在下载 " + (value * 100).ToString("0") + "%（"
                  + SizeText((long)(installer.Size * value)) + " / " + SizeText(installer.Size) + "）"
                : "正在下载 " + (value * 100).ToString("0") + "%");

            // 挑镜像、建连接这一段没有进度可报，先给一句话，别让进度区空着
            percent.Text = "正在连接下载源…";
            var setup = await UpdateService.DownloadAsync(release, installer, progress);

            // sha256 已经在下载那一步逐源对过了（对不上会自己换源重下），这里直接装
            bar.IsIndeterminate = false;
            bar.Value = 100;
            percent.Text = "正在安装，装完会自动重启…";
            if (!UpdateService.RunSetup(setup))
                throw new IOException("安装器起不来，可以稍后再试，或者自己到 GitHub 上下载");

            // 别一转眼就把窗关掉：留一拍，让用户看清「确实装上了」
            percent.Text = "安装器已启动，马上重启…";
            await Task.Delay(700);

            // 剩下的交给安装器：覆盖文件、快捷方式、重开都归它管（重开是 installer.iss 的 [Run] 那条）
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

    /// <summary>
    /// 检查更新失败时的出路。GitHub 在国内经常连不上，而「连不上」不等于「没有新版」——
    /// 所以别让用户停在「请稍后再试」上：给一条发布页的路，自己能下就下。
    /// 只在手动检查时弹（启动时自动检查失败不打扰用户）。
    /// </summary>
    public static async Task ShowCheckFailedAsync(string? reason)
    {
        var root = await WaitForRootAsync();
        if (root is null) return;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "连不上 GitHub（网络或者镜像的问题），这次没查到最新版。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "可以点「去发布页」自己下载安装包，或者稍后再试。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
        if (!string.IsNullOrWhiteSpace(reason))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "原因：" + reason,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        var dialog = new ContentDialog
        {
            Title = "检查更新失败",
            Content = panel,
            PrimaryButtonText = "去发布页",
            SecondaryButtonText = "知道了",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        dialog.PrimaryButtonClick += (_, _) => OpenReleases();
        await dialog.ShowAsync();
    }

    /// <summary>开这个版本的发布页（绿色版 / 公告那条路）。</summary>
    private static void OpenTag(UpdateRelease release) => OpenUrl(release.TagUrl);

    /// <summary>发布页兜底（检查更新失败那条路）。</summary>
    private static void OpenReleases() => OpenUrl(UpdateService.ReleasesUrl);

    /// <summary>开个链接。开不出来只记一条日志，不打扰用户。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("打开链接失败：" + ex);
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
