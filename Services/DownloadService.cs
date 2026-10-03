using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Jicun.Desktop.Models;
using Microsoft.UI.Dispatching;

namespace Jicun.Desktop.Services;

public enum DownloadState { Waiting, Running, Done, Failed, Canceled }

/// <summary>
/// 一个下载任务。属性都在 UI 线程上更新，后台线程只攒原始计数（见 <see cref="Report"/>）。
/// </summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    private readonly DispatcherQueue? _dq = DispatcherQueue.GetForCurrentThread();
    private long _rawReceived;
    private long _lastBytes;
    private DateTime _lastTick = DateTime.UtcNow;

    public DownloadTask(MediaItem item, string folder)
    {
        Item = item;
        Folder = folder;
    }

    public MediaItem Item { get; }
    public string Folder { get; }
    public CancellationTokenSource Cts { get; } = new();

    private DownloadState _state = DownloadState.Waiting;
    private long _received;
    private long _total;
    private double _progress;
    private double _speed;
    private string? _error;
    private string? _savedPath;

    public DownloadState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(StateLabel)); Raise(nameof(CanCancel)); } } }
    public long Received { get => _received; private set { if (Set(ref _received, value)) Raise(nameof(SizeLabel)); } }
    public long Total { get => _total; private set { if (Set(ref _total, value)) Raise(nameof(SizeLabel)); } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public double Speed { get => _speed; private set { if (Set(ref _speed, value)) Raise(nameof(SpeedLabel)); } }
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(StateLabel)); } }
    public string? SavedPath { get => _savedPath; private set { if (Set(ref _savedPath, value)) Raise(nameof(CanOpen)); } }

    public string Title => string.IsNullOrWhiteSpace(Item.FileName) ? Item.Url : Item.FileName;

    public string StateLabel => State switch
    {
        DownloadState.Waiting => "排队中",
        DownloadState.Running => "下载中",
        DownloadState.Done => "已完成",
        DownloadState.Failed => "失败：" + (Error ?? "未知原因"),
        _ => "已取消",
    };

    public string SizeLabel => Total > 0
        ? Format(Received) + " / " + Format(Total)
        : Format(Received);

    public string SpeedLabel => State == DownloadState.Running && Speed > 0 ? Format((long)Speed) + "/s" : "";

    public bool IsRunning => State is DownloadState.Waiting or DownloadState.Running;
    public bool CanCancel => IsRunning;
    public bool CanOpen => SavedPath is not null;

    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB" };

    private static string Format(long bytes)
    {
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < SizeUnits.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : "0.0") + " " + SizeUnits[i];
    }

    // ---- 后台线程调用 ----

    /// <summary>累计新收到的字节。UI 属性最多每 250ms 刷一次，避免每读一块就跨线程投递。</summary>
    public void Report(long delta)
    {
        _rawReceived += delta;
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        if (elapsed < 0.25) return;

        var speed = (_rawReceived - _lastBytes) / elapsed;
        _lastBytes = _rawReceived;
        _lastTick = now;
        var received = _rawReceived;

        Post(() =>
        {
            Received = received;
            Speed = speed;
            if (Total > 0) Progress = Math.Clamp((double)received / Total, 0, 1);
        });
    }

    /// <summary>
    /// 续传开场：把上次已经落盘的字节数灌进来，否则进度条会从 0 跳到一半。
    /// 只由下载线程在开跑前调一次。
    /// </summary>
    public void SeedReceived(long bytes)
    {
        _rawReceived = bytes;
        _lastBytes = bytes;
        _lastTick = DateTime.UtcNow;
        Post(() =>
        {
            Received = bytes;
            if (Total > 0) Progress = Math.Clamp((double)bytes / Total, 0, 1);
        });
    }

    public void SetTotal(long total) => Post(() => Total = total);
    public void SetState(DownloadState state) => Post(() =>
    {
        State = state;
        if (state != DownloadState.Running) Speed = 0;
        FlushReceived();
    });

    public void SetError(string error) => Post(() =>
    {
        Error = error;
        State = DownloadState.Failed;
        Speed = 0;
        FlushReceived();
    });

    public void SetSaved(string path) => Post(() =>
    {
        SavedPath = path;
        State = DownloadState.Done;
        Progress = 1;
        FlushReceived();
    });

    /// <summary>
    /// 把后台攒着的那点尾巴补进界面计数。Report 有 250ms 节流，最后几个包常常还没投递上来就收尾了 ——
    /// 不补的话会出现「已完成 25.9 MB / 27.5 MB」这种自相矛盾的显示。
    /// </summary>
    private void FlushReceived()
    {
        if (State == DownloadState.Done && Total > 0) { Received = Total; return; }
        if (Received < _rawReceived) Received = _rawReceived;
    }

    private void Post(Action action)
    {
        if (_dq is null || _dq.HasThreadAccess) action();
        else _dq.TryEnqueue(() => action());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }
}

/// <summary>
/// 下载器。大文件按 Range 分段并行，段级重试；落盘后嗅探真实格式再定后缀。
/// 分片留在 <c>%LocalAppData%\Jicun\incomplete</c>，进程重启后接着下（见 <see cref="ResumeStore"/>）。
/// </summary>
public sealed class DownloadService
{
    private const long SegmentThreshold = 8L * 1024 * 1024;
    private const int Segments = ResumeStore.Segments;

    // 超时的第二/三层由调用方管（下载走多久算多久 + 每段自己的重试），不在这层设固定上限。
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly SemaphoreSlim _gate = new(3, 3);

    public ObservableCollection<DownloadTask> Tasks { get; } = new();

    public DownloadTask Enqueue(MediaItem item)
    {
        var task = new DownloadTask(item, AppServices.FolderFor(item.Kind));
        Tasks.Insert(0, task);
        _ = RunAsync(task);
        return task;
    }

    /// <summary>启动时把上次没下完的接着下。用户上次既然在下，这次也不问。</summary>
    public void ResumePending()
    {
        foreach (var rec in ResumeStore.Pending())
        {
            var task = new DownloadTask(rec.ToItem(), rec.Folder);
            Tasks.Insert(0, task);
            _ = RunAsync(task);
        }
    }

    /// <summary>
    /// 清掉「合并到一半就被杀」留下的临时文件。那种文件现在落在**用户的下载目录**里
    /// （见 DownloadSegmentedAsync），取消和失败两条路都清了，唯独进程被强杀那种留不下来。
    /// 名字是我们自己定的 <c>.<16 位十六进制 id>.merged.part</c> —— 只认这个形状，不碰用户自己的文件。
    /// </summary>
    public void CleanupStaleMerges()
    {
        foreach (var kind in new[] { MediaKind.Video, MediaKind.Image, MediaKind.Audio })
        {
            try
            {
                var folder = AppServices.FolderFor(kind);
                if (!Directory.Exists(folder)) continue;

                foreach (var file in Directory.EnumerateFiles(folder, ".*.merged.part"))
                    if (IsMergeTemp(Path.GetFileName(file))) TryDelete(file);
            }
            catch
            {
                // 目录读不动就算了，下次启动再清
            }
        }
    }

    /// <summary>合并临时文件的名字形状：<c>.<16 位十六进制>.merged.part</c>。</summary>
    internal static bool IsMergeTemp(string name)
    {
        const string suffix = ".merged.part";
        if (!name.StartsWith('.') || !name.EndsWith(suffix, StringComparison.Ordinal)) return false;

        var id = name[1..^suffix.Length];
        return id.Length == 16 && id.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// 命令行 / 自动化用：不排队、不走 UI 线程，直接把这个任务跑完。
    /// 失败原因收进 <see cref="DownloadTask.Error"/>，不往外抛。
    /// </summary>
    public static async Task RunOnceAsync(DownloadTask task, CancellationToken ct = default)
    {
        try
        {
            await DownloadAsync(task, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadState.Canceled);
        }
        catch (Exception ex)
        {
            task.SetError(ex.Message);
        }
    }

    public void Cancel(DownloadTask task)
    {
        try { task.Cts.Cancel(); } catch { /* 已经结束 */ }
    }

    public void CancelAll()
    {
        foreach (var t in Tasks.Where(t => t.IsRunning)) Cancel(t);
    }

    /// <summary>
    /// 把记录从列表里拿掉。只动列表，磁盘上已经下好的文件不碰 ——
    /// 用户点「删除」是想清界面，不是让我们替他删文件。
    /// 还在跑的先把下载停掉，不然后台会继续往磁盘里写。
    /// </summary>
    public void Remove(IEnumerable<DownloadTask> tasks)
    {
        foreach (var t in tasks.ToList())
        {
            if (t.IsRunning) Cancel(t);
            Tasks.Remove(t);
        }
    }

    private async Task RunAsync(DownloadTask task)
    {
        // 还在排队时被取消，WaitAsync 会直接抛出来 —— 这一句也必须在 try 里，
        // 否则异常没人接，任务会永远停在「排队中」。
        var acquired = false;
        try
        {
            await _gate.WaitAsync(task.Cts.Token).ConfigureAwait(false);
            acquired = true;
            await DownloadAsync(task, task.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            task.SetState(DownloadState.Canceled);
        }
        catch (Exception ex)
        {
            task.SetError(ex.Message);
        }
        finally
        {
            if (acquired) _gate.Release();
        }
    }

    private static async Task DownloadAsync(DownloadTask task, CancellationToken ct)
    {
        task.SetState(DownloadState.Running);

        var folder = task.Folder;
        Directory.CreateDirectory(folder);

        var url = task.Item.Url;
        var rec = ResumeStore.LoadOrCreate(url, task.Item.FileName, folder, task.Item.Kind);

        var (length, supportsRange) = await ProbeAsync(url, ct).ConfigureAwait(false);
        if (length is > 0) task.SetTotal(length.Value);
        rec.Total = length ?? rec.Total;
        rec.SupportsRange = supportsRange;
        ResumeStore.Save(rec);

        // 分段下载的合并结果直接落在**目标目录**（见 DownloadSegmentedAsync）：
        // 同一个卷上改名是瞬时的，而落在 %LocalAppData% 时，只要下载目录在别的盘，
        // 最后那一步 File.Move 就变成把整份文件再拷一遍。
        // mergedTemp 声明在 try 外面：catch 里要拿它把半份合并结果清掉。
        string? mergedTemp = null;

        try
        {
            string source;
            if (supportsRange && length is > SegmentThreshold)
            {
                source = mergedTemp = await DownloadSegmentedAsync(task, rec, length.Value, ct).ConfigureAwait(false);
            }
            else
            {
                await DownloadSingleAsync(task, rec, supportsRange, ct).ConfigureAwait(false);
                source = rec.SinglePath;
            }

            // 字节都下完了、用户在这之前按了取消的：以「已取消」收尾。
            // 少了这一句就会出现「明明按了取消，却报已完成」。
            ct.ThrowIfCancellationRequested();

            // 接口给的后缀经常和实际内容不符（实况图、抽出来的音轨），落盘前按文件头纠正
            var name = Path.GetFileName(task.Item.FileName);
            var sniffed = Sniff(source, task.Item.Kind == MediaKind.Audio);
            if (!string.IsNullOrEmpty(sniffed)) name = Path.GetFileNameWithoutExtension(name) + sniffed;

            var dest = UniquePath(Path.Combine(folder, name));
            File.Move(source, dest, overwrite: true);
            mergedTemp = null; // 已经改名走了，收尾不用再清

            // 音频落盘后补标签。失败不影响下载结论 —— 文件已经在那儿了，标签是加分项。
            await TryWriteAudioTagsAsync(task, dest).ConfigureAwait(false);

            task.SetSaved(dest);
            ResumeStore.Drop(rec.Id);
        }
        catch (OperationCanceledException)
        {
            if (mergedTemp is not null) TryDelete(mergedTemp); // 半份合并结果别留在用户的下载目录里
            ResumeStore.Drop(rec.Id); // 用户自己按的取消，不用留
            throw;
        }
        catch
        {
            if (mergedTemp is not null) TryDelete(mergedTemp);
            // 网络/磁盘出错：分片和记录都留着，下次同一个链接进来从断点接
            throw;
        }
    }

    private static async Task<(long? Length, bool SupportsRange)> ProbeAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        MediaHttp.Apply(req, url);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.PartialContent)
            return (resp.Content.Headers.ContentRange?.Length, true);

        return (resp.Content.Headers.ContentLength, false);
    }

    private static async Task DownloadSingleAsync(DownloadTask task, ResumeRecord rec, bool supportsRange, CancellationToken ct)
    {
        var path = rec.SinglePath;
        var done = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (done > 0) task.SeedReceived(done);

        if (rec.Total > 0 && done >= rec.Total) return; // 早就下完了，只差最后搬一次

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, rec.Url);
                MediaHttp.Apply(req, rec.Url);
                if (done > 0)
                {
                    if (supportsRange) req.Headers.Range = new RangeHeaderValue(done, null);
                    else { File.Delete(path); done = 0; task.SeedReceived(0); } // 不支持 Range 就只能重来
                }

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                if (resp.Content.Headers.ContentLength is { } len) task.SetTotal(done + len);

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(path, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[1 << 16];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    task.Report(read);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < 2)
            {
                await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
                done = File.Exists(path) ? new FileInfo(path).Length : 0; // 重试前把真实进度捡回来
            }
        }
    }

    /// <summary>
    /// 大文件分段并行，再把分片按顺序合成一份。
    /// 合并结果直接落在**目标目录**里（调用方随后在同目录改名），这样下载目录在别的盘时
    /// 不会在最后一步被 File.Move 变成整份再拷一遍。
    /// </summary>
    private static async Task<string> DownloadSegmentedAsync(DownloadTask task, ResumeRecord rec, long total, CancellationToken ct)
    {
        var chunk = total / Segments;

        var already = 0L;
        for (var i = 0; i < Segments; i++)
        {
            var p = rec.PartPath(i);
            if (File.Exists(p)) already += new FileInfo(p).Length;
        }

        if (already > 0) task.SeedReceived(already);

        var work = new Task[Segments];
        for (var i = 0; i < Segments; i++)
        {
            var index = i;
            var from = index * chunk;
            var to = index == Segments - 1 ? total - 1 : from + chunk - 1;
            work[index] = Task.Run(() => DownloadRangeAsync(task, rec, index, from, to, ct), ct);
        }

        await Task.WhenAll(work).ConfigureAwait(false);

        Directory.CreateDirectory(task.Folder);
        var merged = Path.Combine(task.Folder, "." + rec.Id + ".merged.part");
        try
        {
            await using (var output = File.Create(merged))
            {
                for (var i = 0; i < Segments; i++)
                {
                    await using var part = File.OpenRead(rec.PartPath(i));
                    await part.CopyToAsync(output, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(merged); // 半份合并结果不留在用户的下载目录里
            throw;
        }

        return merged;
    }

    private static async Task DownloadRangeAsync(DownloadTask task, ResumeRecord rec, int index, long from, long to, CancellationToken ct)
    {
        var partPath = rec.PartPath(index);
        var done = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        for (var attempt = 0; ; attempt++)
        {
            var start = from + done;
            if (start > to) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, rec.Url);
                MediaHttp.Apply(req, rec.Url);
                req.Headers.Range = new RangeHeaderValue(start, to);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[1 << 16];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    task.Report(read);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < 2)
            {
                await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
                done = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            }
        }
    }

    /// <summary>
    /// 给下载完的音频写标题 / 作者 / 专辑 / 封面。
    ///
    /// 只在 <see cref="MediaKind.Audio"/> 上做，而且格式得是 AudioTags 认的（mp3 / m4a）。
    /// 整段套在 try 里：写标签要整份文件读进内存再原子替换，磁盘紧张或文件太大都可能失败，
    /// 而那时候用户要的文件已经下好了 —— 不能因为标签没写上就把一个成功的下载判成失败。
    /// </summary>
    private static async Task TryWriteAudioTagsAsync(DownloadTask task, string path)
    {
        if (task.Item.Kind != MediaKind.Audio) return;

        try
        {
            if (AudioTags.DetectFormat(path) is null) return;

            var title = task.Item.TagTitle;
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);

            byte[]? cover = null;
            if (!string.IsNullOrWhiteSpace(task.Item.TagCoverUrl))
            {
                // 封面单独打一次请求，给短超时：拿不到就只写文字标签，不拖累整条下载。
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    cover = await Http.GetByteArrayAsync(task.Item.TagCoverUrl!, timeout.Token).ConfigureAwait(false);
                }
                catch
                {
                    cover = null;
                }
            }

            AudioTags.Write(path, title, task.Item.TagArtist, task.Item.TagAlbum, cover);
        }
        catch
        {
            // 标签写不上就算了，文件是好的
        }
    }

    /// <summary>按文件头认真实格式。audio=true 时把 MP4 容器认成音频后缀（汽水音乐的音频就是 ftyp 的 m4a）。</summary>
    private static string? Sniff(string path, bool audio = false)
    {
        var head = new byte[16];
        int n;
        try
        {
            using var fs = File.OpenRead(path);
            n = fs.Read(head, 0, head.Length);
        }
        catch
        {
            return null;
        }

        if (n >= 12 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p') return audio ? ".m4a" : ".mp4";
        if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (n >= 8 && head[0] == 0x89 && head[1] == (byte)'P' && head[2] == (byte)'N' && head[3] == (byte)'G') return ".png";
        if (n >= 6 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F') return ".gif";
        if (n >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return ".webp";
        if (n >= 4 && head[0] == (byte)'O' && head[1] == (byte)'g' && head[2] == (byte)'g' && head[3] == (byte)'S') return ".ogg";
        if (n >= 4 && head[0] == (byte)'f' && head[1] == (byte)'L' && head[2] == (byte)'a' && head[3] == (byte)'C') return ".flac";
        if (n >= 3 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3') return ".mp3";
        if (n >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return ".mp3";
        if (n >= 4 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F') return ".wav";
        if (n >= 7 && head[0] == (byte)'#' && head[1] == (byte)'E' && head[2] == (byte)'X' && head[3] == (byte)'T') return ".m3u8";

        return null;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>删临时文件。删不掉就算了（可能被别的进程占着），别为清理动作再抛一次。</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 占用中，下次再说 */ }
    }
}
