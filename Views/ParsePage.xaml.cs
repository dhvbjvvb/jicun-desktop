using System.Collections.ObjectModel;
using System.IO;
using Jicun.Desktop.Models;
using Jicun.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace Jicun.Desktop.Views;

public sealed partial class ParsePage : Page
{
    public ObservableCollection<MediaItem> Items { get; } = new();

    private bool _busy;
    private ParseResult? _last;

    // 正在跑的那一次解析：跑的时候解析按钮变「取消」—— 兜底那条路最坏要等十几秒，
    // 不给个出口用户只能干等。_parsing 是「先掐掉上一次、再起新的一次」用的把手。
    private CancellationTokenSource? _parse;
    private Task? _parsing;

    // 音频单独一张卡：播放器按需创建，换页或重新解析时拆掉。
    private MediaItem? _audio;
    private MediaPlayer? _audioPlayer;
    private bool _syncSeek;

    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    private int TotalCount => Items.Count(i => !i.IsDownloadAll) + (_audio is null ? 0 : 1);

    public ParsePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (AppServices.PendingInput is not { Length: > 0 } pending) return;
        AppServices.PendingInput = null;
        InputBox.Text = pending;
        _ = StartParseAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        DisposeAudio();
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            InputBox.Text = await content.GetTextAsync();
        }
        catch
        {
            // 剪贴板被别的进程占着，忽略
        }
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = StartParseAsync();
    }

    /// <summary>跑着的时候这个按钮是「取消」，否则起一次新的解析。</summary>
    private void OnParseClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CancelParse();
            return;
        }

        _ = StartParseAsync();
    }

    /// <summary>
    /// 起一次解析。已经有一次在跑（历史页带新链接回来、用户连点）就先掐掉、等它把界面状态
    /// 交还，再起新的 —— 直接 return 会让「重新解析」点了没反应。
    /// </summary>
    private async Task StartParseAsync()
    {
        if (_parsing is { } previous)
        {
            CancelParse();
            try { await previous; } catch { /* 收尾在它自己的 finally 里 */ }
        }

        var input = InputBox.Text?.Trim() ?? "";
        if (input.Length == 0)
        {
            ShowBar(InfoBarSeverity.Warning, "先粘贴一条分享链接");
            return;
        }

        // 从这一行到 RunParseAsync 之间不能有 await：_busy 是先同步置位的重入闸，
        // 消息循环进不来，界面状态不会被第二次调用抢走。
        var cts = new CancellationTokenSource();
        _parse = cts;
        _busy = true;
        ParseButton.IsEnabled = true;      // 留着当「取消」用
        ParseButton.Content = "取消";
        InputBox.IsEnabled = false;
        Ring.IsActive = true;
        Bar.IsOpen = false;

        var run = RunParseAsync(input, cts.Token);
        _parsing = run;

        try
        {
            await run;
        }
        finally
        {
            if (ReferenceEquals(_parsing, run))
            {
                _parsing = null;
                _parse = null;
                _busy = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>掐掉正在跑的那次解析。</summary>
    private void CancelParse()
    {
        try { _parse?.Cancel(); } catch { /* 已经收尾了 */ }
    }

    /// <summary>真正跑一次解析，界面状态的收尾也在这里。</summary>
    private async Task RunParseAsync(string input, CancellationToken ct)
    {
        try
        {
            var result = await AppServices.Parser.ParseAsync(input, ct);
            _last = result;
            Render(result);
            AppServices.History.Add(ParseService.ExtractUrl(input) ?? input, result);
            ShowBar(InfoBarSeverity.Success, "解析完成，共 " + TotalCount + " 项可下载");
        }
        catch (OperationCanceledException)
        {
            ShowBar(InfoBarSeverity.Informational, "已取消");
        }
        catch (ParseException ex)
        {
            ShowBar(InfoBarSeverity.Error, ex.Message);
        }
        catch (Exception ex)
        {
            ShowBar(InfoBarSeverity.Error, "出了点问题：" + ex.Message);
        }
        finally
        {
            ParseButton.Content = "解析";
            ParseButton.IsEnabled = true;
            InputBox.IsEnabled = true;
            Ring.IsActive = false;
        }
    }

    private void Render(ParseResult r)
    {
        ResultHead.Visibility = Visibility.Visible;
        TitleText.Text = string.IsNullOrWhiteSpace(r.Title) ? "（没有标题）" : r.Title;

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Platform)) meta.Add(r.Platform);
        if (!string.IsNullOrWhiteSpace(r.AuthorName)) meta.Add("@" + r.AuthorName);
        MetaText.Text = string.Join(" · ", meta);

        DescCard.Visibility = string.IsNullOrWhiteSpace(r.Desc) ? Visibility.Collapsed : Visibility.Visible;
        DescText.Text = r.Desc ?? "";

        var stem = Sanitize(string.IsNullOrWhiteSpace(r.Title)
            ? (string.IsNullOrWhiteSpace(r.Platform) ? "即存" : r.Platform) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm")
            : r.Title);

        Items.Clear();

        if (r.Videos.Count > 0)
        {
            for (var i = 0; i < r.Videos.Count; i++)
            {
                var v = r.Videos[i];
                Items.Add(new MediaItem
                {
                    Kind = MediaKind.Video,
                    Url = v.Url,
                    ThumbnailUrl = v.CoverUrl ?? r.CoverUrl,
                    FileName = stem + (i == 0 ? "" : "_" + (i + 1)) + ".mp4",
                    QualityLabel = v.Label,
                });
            }
        }
        else if (!string.IsNullOrWhiteSpace(r.VideoUrl))
        {
            Items.Add(new MediaItem
            {
                Kind = MediaKind.Video,
                Url = r.VideoUrl!,
                ThumbnailUrl = r.CoverUrl,
                FileName = stem + ".mp4",
            });
        }

        for (var i = 0; i < r.ImageUrls.Count; i++)
        {
            Items.Add(new MediaItem
            {
                Kind = MediaKind.Image,
                Url = r.ImageUrls[i],
                ThumbnailUrl = r.ImageUrls[i],
                FileName = stem + "_" + (i + 1) + ".jpg",
            });
        }

        DisposeAudio();
        _audio = string.IsNullOrWhiteSpace(r.AudioUrl)
            ? null
            : new MediaItem
            {
                Kind = MediaKind.Audio,
                Url = r.AudioUrl!,
                ThumbnailUrl = string.IsNullOrWhiteSpace(r.CoverUrl) ? null : r.CoverUrl,
                FileName = stem + ".mp3",

                // 音频下载完要写 ID3 / MP4 标签，那几样是帖子级别的信息，
                // 从解析结果里带过去（下载器手里只有 MediaItem）。
                TagTitle = string.IsNullOrWhiteSpace(r.Title) ? stem : r.Title,
                TagArtist = r.AuthorName,
                TagAlbum = string.IsNullOrWhiteSpace(r.Platform) ? "即存" : r.Platform,
                TagCoverUrl = r.CoverUrl,
            };

        AudioCard.Visibility = _audio is null ? Visibility.Collapsed : Visibility.Visible;
        AudioPlayIcon.Glyph = PlayGlyph;
        _syncSeek = true;
        AudioSeek.Value = 0;
        AudioSeek.Maximum = 1;
        _syncSeek = false;
        AudioTime.Text = "--:-- / --:--";

        // 音频卡一出现就把时长读出来（播放器只加载不播），省得用户不点播放就看不到长度
        EnsureAudioPlayer();

        // 「下载全部」占位卡就放在结果网格的第一个格子；有内容才插
        if (Items.Count > 0 || _audio is not null)
            Items.Insert(0, new MediaItem { Kind = MediaKind.Text, IsDownloadAll = true, FileName = "下载全部" });

        CountText.Text = TotalCount + " 项";
        EmptyHint.Visibility = TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaItem item) PreviewWindow.Show(item);
    }

    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MediaItem item) return;
        AppServices.Downloads.Enqueue(item);
        ShowBar(InfoBarSeverity.Informational, "已加入下载队列：" + item.FileName);
    }

    private void OnDownloadAllClick(object sender, RoutedEventArgs e)
    {
        var all = Items.Where(i => !i.IsDownloadAll).ToList();
        if (_audio is not null) all.Add(_audio);
        if (all.Count == 0) return;
        foreach (var item in all) AppServices.Downloads.Enqueue(item);
        AppServices.Navigate("downloads");
    }

    private void OnCopyDescClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(DescText.Text ?? "");
            Clipboard.SetContent(pkg);
            ShowBar(InfoBarSeverity.Informational, "文案已复制");
        }
        catch
        {
            // 同上
        }
    }

    private void OnAudioPlayClick(object sender, RoutedEventArgs e)
    {
        EnsureAudioPlayer();
        if (_audioPlayer is null) return;

        if (_audioPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _audioPlayer.Pause();
        else _audioPlayer.Play();
    }

    /// <summary>播放器按需建；建好就会读时长，所以渲染时也预建一次。</summary>
    private void EnsureAudioPlayer()
    {
        if (_audioPlayer is not null || _audio is null) return;
        var player = new MediaPlayer();
        player.MediaOpened += OnAudioMediaOpened;
        player.MediaEnded += OnAudioMediaEnded;
        player.PlaybackSession.PlaybackStateChanged += OnAudioStateChanged;
        player.PlaybackSession.PositionChanged += OnAudioPositionChanged;
        _audioPlayer = player;
        _ = LoadAudioAsync(player, _audio.Url);
    }

    /// <summary>
    /// 给播放器装音频源。B 站那条音频直链要求 Referer，而系统媒体栈只会带自己的 UA、
    /// 加不了 Referer，直接 <c>CreateFromUri</c> 拿到的就是 403（「音频预览不了」就是这么来的）——
    /// 这类地址先由我们自己的请求缓冲成本地文件再播；别的平台照旧直接播，不白等一次下载。
    /// </summary>
    private async Task LoadAudioAsync(MediaPlayer player, string url)
    {
        if (!MediaHttp.NeedsBiliReferer(url))
        {
            player.Source = MediaSource.CreateFromUri(new Uri(url));
            return;
        }

        try
        {
            ShowBar(InfoBarSeverity.Informational, "正在缓冲音频…");
            var path = await MediaHttp.BufferToTempAsync(url, ".m4a", CancellationToken.None);
            if (_audioPlayer != player) return;   // 用户已经换了结果，这份别再塞进去
            player.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch (Exception ex)
        {
            ShowBar(InfoBarSeverity.Error, "音频加载失败：" + ex.Message);
        }
    }

    private void OnAudioDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_audio is null) return;
        AppServices.Downloads.Enqueue(_audio);
        ShowBar(InfoBarSeverity.Informational, "已加入下载队列：" + _audio.FileName);
    }

    private void OnAudioSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncSeek || _audioPlayer is null) return;
        try
        {
            _audioPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        }
        catch
        {
            // 还没加载完就想跳，忽略
        }
    }

    private void OnAudioMediaOpened(MediaPlayer sender, object args)
    {
        var seconds = sender.PlaybackSession.NaturalDuration.TotalSeconds;
        DispatcherQueue.TryEnqueue(() =>
        {
            _syncSeek = true;
            AudioSeek.Maximum = seconds > 0 ? seconds : 1;
            _syncSeek = false;
            AudioTime.Text = seconds > 0 ? "00:00 / " + FormatTime(seconds) : "--:-- / --:--";
        });
    }

    private void OnAudioMediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AudioPlayIcon.Glyph = PlayGlyph;
            _syncSeek = true;
            AudioSeek.Value = 0;
            _syncSeek = false;
            AudioTime.Text = "00:00 / " + FormatTime(AudioSeek.Maximum);
        });
    }

    private void OnAudioStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AudioPlayIcon.Glyph = sender.PlaybackState == MediaPlaybackState.Playing ? PauseGlyph : PlayGlyph;
        });
    }

    private void OnAudioPositionChanged(MediaPlaybackSession sender, object args)
    {
        var position = sender.Position.TotalSeconds;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_syncSeek) return;
            _syncSeek = true;
            AudioSeek.Value = Math.Min(position, AudioSeek.Maximum);
            _syncSeek = false;
            var total = sender.NaturalDuration.TotalSeconds;
            if (total > 0) AudioTime.Text = FormatTime(position) + " / " + FormatTime(total);
        });
    }

    /// <summary>播放器只在用得到时才建，离开页面或重新解析时立刻拆干净。</summary>
    private void DisposeAudio()
    {
        if (_audioPlayer is null) return;
        var player = _audioPlayer;
        _audioPlayer = null;
        try
        {
            player.MediaOpened -= OnAudioMediaOpened;
            player.MediaEnded -= OnAudioMediaEnded;
            player.PlaybackSession.PlaybackStateChanged -= OnAudioStateChanged;
            player.PlaybackSession.PositionChanged -= OnAudioPositionChanged;
            player.Pause();
            player.Source = null;
        }
        catch
        {
            // 媒体管线可能已经被系统拆掉，忽略
        }
        player.Dispose();
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        // 自定义 TimeSpan 格式串里的 ':' 必须转义，否则 .NET (Core) 会抛 FormatException（.NET Framework 不抛）。
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private void ShowBar(InfoBarSeverity severity, string message)
    {
        Bar.Severity = severity;
        Bar.Message = message;
        Bar.IsOpen = true;
    }

    /// <summary>标题会直接进文件名，先把路径非法字符摘掉。</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "即存";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}
