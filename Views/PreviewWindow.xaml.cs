using Jicun.Desktop.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Media.Core;

namespace Jicun.Desktop.Views;

/// <summary>
/// 预览开一个独立窗口。
///
/// 早先用的是 ContentDialog：原生播放器和主窗口糊在一起，标题栏、关闭按钮都跟主窗口的挤在
/// 一块，看着很别扭。播放器本来就该有自己的窗口 —— 能随便挪、能最大化、关掉也不影响主窗口。
/// 同一个地址重复点「预览」不重复开，把已经开着的那个窗口拎到前面来。
/// </summary>
public sealed partial class PreviewWindow : Window
{
    private static readonly Dictionary<string, PreviewWindow> Opened = new(StringComparer.Ordinal);

    private readonly MediaItem _item;

    private PreviewWindow(MediaItem item)
    {
        InitializeComponent();
        _item = item;

        Title = item.KindLabel + "预览";
        TitleText.Text = item.FileName;

        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "jicun.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        // 关掉窗口时得从 Opened 里摘掉自己，所以提前挂上 —— 下面那条提前返回的路也走得到。
        Closed += OnClosed;

        // 相对地址 / 非法地址交给 new Uri 就是抛异常，而预览这条入口没人接异常，进程直接没。
        // 正常的调用方（解析页的预览按钮）已经被 CanPreview 挡住了，这里再兜一层。
        if (AsAbsolute(item.Url) is not { } url)
        {
            TitleText.Text = "地址不可用：" + item.Url;
            Resize(480, 260);
            return;
        }

        if (item.Kind == MediaKind.Image)
        {
            ImageHost.Visibility = Visibility.Visible;
            var bitmap = new BitmapImage(url);
            bitmap.ImageOpened += (_, _) => FitImage(bitmap.PixelWidth, bitmap.PixelHeight);
            Picture.Source = bitmap;
            // ponytail: 滚动容器用无限约束量内容，Image 会按位图像素尺寸铺开，缩放 1.0 也塞不进视口。
            // 用 MaxWidth/MaxHeight 把它压回视口大小；Max 只封顶不放大，比原图小的图仍是原始尺寸。
            ImageHost.SizeChanged += (_, _) => FitPictureToHost();
            FitPictureToHost();
            Resize(900, 660);
        }
        else
        {
            Player.Visibility = Visibility.Visible;
            Player.Source = MediaSource.CreateFromUri(url);

            if (item.Kind == MediaKind.Audio)
            {
                // 播放器条按固定高度摆在下面，剩下的地方留给封面。
                PlayerRow.Height = new GridLength(140);
                // 封面地址同样得是绝对的，不然 new BitmapImage 也抛
                if (AsAbsolute(item.ThumbnailUrl) is { } cover)
                {
                    Backdrop.Source = new BitmapImage(cover);
                    Backdrop.Visibility = Visibility.Visible;
                }
                Resize(560, 460);
            }
            else
            {
                // 视频要铺满整个内容区：Player 在 XAML 里是 Grid.Row=1，而那一行只有音频分支
                // 才撑到 140 —— 光 SetRowSpan 的话它还是待在底下那条 Auto 行里（一条矮条）。
                Grid.SetRow(Player, 0);
                Grid.SetRowSpan(Player, 2);
                Resize(1120, 680);
            }
        }
    }

    /// <summary>打开（或把已经开着的那个拎到前面）。</summary>
    public static void Show(MediaItem item)
    {
        if (Opened.TryGetValue(item.Url, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new PreviewWindow(item);
        Opened[item.Url] = window;
        window.Activate();
    }

    /// <summary>主窗口关掉时把预览窗口一起带走，不然进程退不干净。</summary>
    public static void CloseAll()
    {
        foreach (var window in Opened.Values.ToList())
        {
            try { window.Close(); }
            catch (Exception) { /* 正在关的窗口再关一次会炸，无所谓 */ }
        }
        Opened.Clear();
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Opened.Remove(_item.Url);

        // 先让控件松手再销毁播放器，反过来的顺序控件会去碰一个已经没了的对象。
        var player = Player.MediaPlayer;
        Player.SetMediaPlayer(null);
        if (player is not null)
        {
            try
            {
                player.Pause();
                player.Source = null;
                player.Dispose();
            }
            catch (Exception) { /* 关窗口的路上出错没人能处理，别崩就行 */ }
        }

        Picture.Source = null;
        Backdrop.Source = null;
    }

    /// <summary>挑个刚好的窗口大小，居中，并且不超出工作区。</summary>
    private void Resize(int width, int height)
    {
        try
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var w = Math.Clamp(width, 400, Math.Max(400, work.Width - 60));
            var h = Math.Clamp(height, 320, Math.Max(320, work.Height - 60));
            AppWindow.Resize(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2));
        }
        catch (Exception)
        {
            // 拿不到显示器信息就按系统给的大小来，不耽误看
        }
    }

    /// <summary>把图片的最大尺寸压到预览视口，保证缩放 1.0 时能整张看全。</summary>
    private void FitPictureToHost()
    {
        if (ImageHost.ActualWidth <= 0 || ImageHost.ActualHeight <= 0) return;

        Picture.MaxWidth = ImageHost.ActualWidth;
        Picture.MaxHeight = ImageHost.ActualHeight;
    }

    /// <summary>图片按自己的宽高比挑窗口：小图不撑成大黑框，大图不超出屏幕。</summary>
    private void FitImage(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        double scale;
        try
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            scale = Math.Min(1.0, Math.Min(work.Width * 0.8 / pixelWidth, work.Height * 0.85 / pixelHeight));
        }
        catch (Exception)
        {
            return;
        }

        Resize((int)(pixelWidth * scale), (int)(pixelHeight * scale) + (int)TitleBar.ActualHeight);
    }
    /// <summary>
    /// 只有绝对地址才配交给 <c>new Uri</c>：相对地址（服务端兜底那条路会下发以 / 开头的）会抛
    /// <c>UriFormatException</c>，而调用这条链的都是一碰就崩的 void 事件处理器。
    /// </summary>
    private static Uri? AsAbsolute(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
}
