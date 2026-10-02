using Jicun.Desktop.Models;

namespace Jicun.Desktop.Services;

/// <summary>
/// 进程内服务定位器。App 很小，不值得引一个 DI 容器 —— 页面直接拿这几个单例。
/// </summary>
public static class AppServices
{
    public static MainWindow? MainWindow { get; set; }

    public static SettingsService Settings { get; } = new();
    public static ParseService Parser { get; } = new();
    public static DownloadService Downloads { get; } = new();
    public static HistoryService History { get; } = new();

    /// <summary>域名池 / 服务端配置热更。见 <see cref="ApiHosts"/>。</summary>
    public static HostUpdater Hosts { get; } = new();

    /// <summary>
    /// 按媒体类型取保存目录：视频（含实况图）/ 图片 / 音频各自一个位置，用户可以在设置页分别改。
    /// </summary>
    public static string FolderFor(MediaKind kind) => Settings.Current.FolderFor(kind);

    /// <summary>历史页点「重新解析」时把链接塞这儿，解析页导航过去自己取走。</summary>
    public static string? PendingInput { get; set; }

    public static void Navigate(string tag) => MainWindow?.NavigateTo(tag);
}
