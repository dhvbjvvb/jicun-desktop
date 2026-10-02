using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jicun.Desktop.Models;

namespace Jicun.Desktop.Services;

public sealed class Settings
{
    /// <summary>
    /// 老版本只有一个下载目录。读进来之后会摊到下面三个新字段里，之后不再往外写
    /// （保留属性只是为了能读懂旧配置文件）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadFolder { get; set; }

    public string VideoFolder { get; set; } = "";
    public string ImageFolder { get; set; } = "";
    public string AudioFolder { get; set; } = "";

    /// <summary>实况图下载下来是 MP4，和普通视频共用同一档。</summary>
    public string FolderFor(MediaKind kind) => kind switch
    {
        MediaKind.Image => ImageFolder,
        MediaKind.Audio => AudioFolder,
        _ => VideoFolder,
    };

    public void SetFolder(MediaKind kind, string path)
    {
        switch (kind)
        {
            case MediaKind.Image: ImageFolder = path; break;
            case MediaKind.Audio: AudioFolder = path; break;
            default: VideoFolder = path; break;
        }
    }
}

public sealed class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    // 大小写不敏感：老版本 / 手改过的配置里 downloadFolder 这种写法也认
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 三档默认目录。视频（含实况图）放「视频」，图片放「图片」，音频放「音乐」，
    /// 都是 Windows 上本来就有的标准文件夹，各自再套一层「即存」。
    /// </summary>
    public static string DefaultFolderFor(MediaKind kind) => Path.Combine(
        Environment.GetFolderPath(kind switch
        {
            MediaKind.Image => Environment.SpecialFolder.MyPictures,
            MediaKind.Audio => Environment.SpecialFolder.MyMusic,
            _ => Environment.SpecialFolder.MyVideos,
        }),
        "即存");

    public Settings Current { get; }

    public SettingsService()
    {
        Current = Load();
    }

    private static Settings Load()
    {
        Settings? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
        }
        catch
        {
            // 配置读不动不值得让 App 起不来，退回默认值
        }

        loaded ??= new Settings();

        // 旧版只有一个 DownloadFolder：三个都填成它，别让已经在用的文件突然换地方。
        var legacy = string.IsNullOrWhiteSpace(loaded.DownloadFolder) ? null : loaded.DownloadFolder;

        if (string.IsNullOrWhiteSpace(loaded.VideoFolder)) loaded.VideoFolder = legacy ?? DefaultFolderFor(MediaKind.Video);
        if (string.IsNullOrWhiteSpace(loaded.ImageFolder)) loaded.ImageFolder = legacy ?? DefaultFolderFor(MediaKind.Image);
        if (string.IsNullOrWhiteSpace(loaded.AudioFolder)) loaded.AudioFolder = legacy ?? DefaultFolderFor(MediaKind.Audio);

        loaded.DownloadFolder = null;
        return loaded;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch
        {
            // 存不上就这次不存，下次启动回默认值
        }
    }
}
