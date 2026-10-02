using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Jicun.Desktop.Models;

namespace Jicun.Desktop.Services;

public sealed class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>卡片左边那张封面。旧记录没有这个字段，读出来是空串，界面上就显示占位图标。</summary>
    public string CoverUrl { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;

    public string TimeLabel => Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Headline => string.IsNullOrWhiteSpace(Title) ? Url : Title;
}

public sealed class HistoryService
{
    private const int MaxEntries = 200;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun");

    private static readonly string FilePath = Path.Combine(Dir, "history.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ObservableCollection<HistoryEntry> Items { get; } = new();

    public HistoryService()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    foreach (var e in loaded.Take(MaxEntries)) Items.Add(e);

                    // 老版本会把同一个链接反复记下来，读的时候顺手合并一次
                    if (Collapse()) Persist();
                }
            }
        }
        catch
        {
            // 历史读不动就当空的，别拦启动
        }
    }

    public void Add(string url, ParseResult result)
    {
        var parts = new List<string>();
        if (result.HasVideo) parts.Add("视频");
        if (result.HasImages) parts.Add("图片 " + result.ImageUrls.Count);
        if (result.HasAudio) parts.Add("音频");
        if (!string.IsNullOrWhiteSpace(result.Desc)) parts.Add("文案");

        var entry = new HistoryEntry
        {
            Url = url,
            Title = result.Title,
            Platform = result.Platform,
            Summary = string.Join(" · ", parts),
            CoverUrl = result.CoverUrl ?? "",
        };

        // 同一个链接解析多少次都只留最新那一条：旧的整条丢掉（连它可能没有的封面一起），
        // 新的一条插到最前面 —— 历史里看到的就是「聚合」后的样子。
        var old = Items.FirstOrDefault(e => e.Url.Length > 0 && string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
        if (old is not null) Items.Remove(old);

        Items.Insert(0, entry);
        while (Items.Count > MaxEntries) Items.RemoveAt(Items.Count - 1);
        Persist();
    }

    /// <summary>同一个链接只留最新那一条（列表本来就是新的在最前面）。返回是不是真删掉了东西。</summary>
    private bool Collapse()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dupes = new List<HistoryEntry>();
        foreach (var e in Items)
        {
            // 链接是空的就别动它，那种记录压根没法比
            if (e.Url.Length > 0 && !seen.Add(e.Url)) dupes.Add(e);
        }

        foreach (var d in dupes) Items.Remove(d);
        return dupes.Count > 0;
    }

    public void Clear()
    {
        Items.Clear();
        Persist();
    }

    /// <summary>只从历史里摘掉记录，磁盘上已经下好的文件不碰。</summary>
    public void Remove(IEnumerable<HistoryEntry> entries)
    {
        foreach (var e in entries.ToList()) Items.Remove(e);
        Persist();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList(), Json));
        }
        catch
        {
            // 同上：历史写不进去不该打断用户
        }
    }
}
