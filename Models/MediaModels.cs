using System.Text.Json;
using Jicun.Desktop.Services;

namespace Jicun.Desktop.Models;

public enum MediaKind { Video, Image, Audio, Text }

/// <summary>一条可下载/可预览的媒体。解析结果在界面上摊平成这个列表。</summary>
public sealed class MediaItem
{
    public MediaKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string FileName { get; set; } = "";
    public string? QualityLabel { get; set; }

    /// <summary>结果网格第一格的「下载全部」占位项，不是真正的媒体。</summary>
    public bool IsDownloadAll { get; set; }

    public bool IsMediaItem => !IsDownloadAll;

    /// <summary>只有 <see cref="MediaKind.Text"/> 用得到。</summary>
    public string TextContent { get; set; } = "";

    // 只有音频下载完写标签时用得到。解析结果里这几样是帖子级别的（标题/作者/平台），
    // 不在单个媒体项上，所以在摊平的时候顺手带过来 —— 下载器手里只有 MediaItem。
    public string? TagTitle { get; set; }
    public string? TagArtist { get; set; }
    public string? TagAlbum { get; set; }
    public string? TagCoverUrl { get; set; }

    public string KindLabel => Kind switch
    {
        MediaKind.Video => "视频",
        MediaKind.Image => "图片",
        MediaKind.Audio => "音频",
        _ => "文案",
    };

    public string Subtitle => string.IsNullOrWhiteSpace(QualityLabel) ? KindLabel : KindLabel + " · " + QualityLabel;

    /// <summary>文案和图片没有原生播放器,预览按钮要按类型禁用。</summary>
    public bool CanPreview => Kind is MediaKind.Video or MediaKind.Audio or MediaKind.Image;

    /// <summary>缩略图没加载出来时垫在底下的 Segoe MDL2 图标。</summary>
    public string Glyph => Kind switch
    {
        MediaKind.Video => "\uE714",
        MediaKind.Image => "\uE91B",
        MediaKind.Audio => "\uE8D6",
        _ => "\uE8A5",
    };
}

public sealed class VideoVariant
{
    public string Url { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string? Label { get; set; }
    public int? BitRate { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>media-parser 那套应答的模型。字段名对着 Android 版 upstream_mapping.dart。</summary>
public sealed class ParseResult
{
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Platform { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string? VideoUrl { get; set; }
    public string? CoverUrl { get; set; }
    public string? AudioUrl { get; set; }
    public string? Lyrics { get; set; }
    public List<string> ImageUrls { get; } = new();
    public List<VideoVariant> Videos { get; } = new();

    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoUrl) || Videos.Count > 0;
    public bool HasImages => ImageUrls.Count > 0;
    public bool HasAudio => !string.IsNullOrWhiteSpace(AudioUrl);

    public static ParseResult FromJson(JsonElement json)
    {
        var r = new ParseResult
        {
            Title = UpstreamMapping.CleanCopyText(Str(json, "title")),
            Desc = UpstreamMapping.CleanCopyText(Str(json, "desc")),
            Platform = Str(json, "platform"),
            CoverUrl = StrOrNull(json, "cover_url"),
            VideoUrl = StrOrNull(json, "video_url"),
            AudioUrl = UpstreamMapping.Absolute(StrOrNull(json, "audio_url")),
            Lyrics = UpstreamMapping.LyricsOf(json),
        };

        if (json.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
            r.AuthorName = Str(author, "nickname");

        // image_list：字符串，或者 {url, live_photo_url} 对象。带 live_photo_url 的是实况图 ——
        // 主地址只是静态帧（留着当缩略图），真用来下载的是那个 MP4，所以归到视频那一类，
        // **不能混进图集**（混进去会让图集平白多出几张下不动的「图」）。
        var images = new List<string>();
        var livePhotos = new List<VideoVariant>();

        if (json.TryGetProperty("image_list", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in imgs.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.String)
                {
                    if (AsString(it) is { } text) images.Add(text);
                    continue;
                }
                if (it.ValueKind != JsonValueKind.Object) continue;

                var url = StrOrNull(it, "url");
                var live = StrOrNull(it, "live_photo_url");
                if (live is not null)
                    livePhotos.Add(new VideoVariant { Url = live, CoverUrl = url, Label = LivePhotoLabel });
                else if (url is not null)
                    images.Add(url);
            }
        }

        // live_photo_list：单独列一份实况图的情况，字段形态比 image_list 里那套更宽。
        if (json.TryGetProperty("live_photo_list", out var rawLive) && rawLive.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in rawLive.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.String)
                {
                    if (AsString(it) is { } text)
                        livePhotos.Add(new VideoVariant { Url = text, Label = LivePhotoLabel });
                    continue;
                }
                if (it.ValueKind != JsonValueKind.Object) continue;

                var live = StrOrNull(it, "live_photo_url") ?? StrOrNull(it, "video_url") ?? StrOrNull(it, "play_url");
                if (live is null) continue;
                livePhotos.Add(new VideoVariant
                {
                    Url = live,
                    CoverUrl = StrOrNull(it, "url") ?? StrOrNull(it, "cover_url"),
                    Label = LivePhotoLabel,
                });
            }
        }

        // video_list：[{url, cover_url, qualities:[…]}]，也可能直接是地址字符串。
        // 主地址缺失但给了 qualities 的，用最高那档顶上，别把整条丢掉。
        if (json.TryGetProperty("video_list", out var vids) && vids.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vids.EnumerateArray())
            {
                string? url = null;
                string? cover = null;

                if (v.ValueKind == JsonValueKind.String)
                {
                    url = AsString(v);
                }
                else if (v.ValueKind == JsonValueKind.Object)
                {
                    url = StrOrNull(v, "url") ?? StrOrNull(v, "play_url") ?? StrOrNull(v, "video_url");
                    cover = StrOrNull(v, "cover_url") ?? StrOrNull(v, "cover");
                    if (url is null && v.TryGetProperty("qualities", out var fallback)
                        && fallback.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var q in fallback.EnumerateArray())
                            if (StrOrNull(q, "url") is { } first) { url = first; break; }
                    }
                }

                if (url is null) continue;

                if (v.ValueKind == JsonValueKind.Object
                    && v.TryGetProperty("qualities", out var qs)
                    && qs.ValueKind == JsonValueKind.Array
                    && qs.GetArrayLength() > 0)
                {
                    foreach (var q in qs.EnumerateArray())
                    {
                        if (StrOrNull(q, "url") is not { } qualityUrl) continue;
                        r.Videos.Add(new VideoVariant
                        {
                            Url = qualityUrl,
                            CoverUrl = cover,
                            Label = StrOrNull(q, "label"),
                            BitRate = IntOrNull(q, "bit_rate"),
                        });
                    }
                }
                else
                {
                    r.Videos.Add(new VideoVariant
                    {
                        Url = url,
                        CoverUrl = cover,
                        Label = StrOrNull(v, "label"),
                        BitRate = IntOrNull(v, "bit_rate"),
                    });
                }
            }
        }

        // 判「有没有视频」只用 video_url / video_list —— 实况图不算。它自带静态帧，
        // 拿它当视频封面去剔图会把图集里第一张真图平白剔掉（最右那条帖子实测）。
        var hasVideo = !string.IsNullOrWhiteSpace(r.VideoUrl) || r.Videos.Count > 0;

        // 封面往往就是图集第一张，而平台给同一张图的尺寸/签名差别只在 host 或 query 上，
        // 所以按路径判等，并且顺手去重。
        r.ImageUrls.AddRange(UpstreamMapping.CleanImages(images, r.CoverUrl, hasVideo: hasVideo));
        r.Videos.AddRange(livePhotos);

        return r;
    }

    /// <summary>实况图在桌面版里当成视频卡（下载下来就是 MP4），标签用它认出来。</summary>
    public const string LivePhotoLabel = "实况图";

    /// <summary>一个 JSON 值自身的字符串形态（数字也当字符串），空串归一成 null。</summary>
    private static string? AsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
        JsonValueKind.Number => value.ToString(),
        _ => null,
    };

    private static string Str(JsonElement e, string key) => StrOrNull(e, key) ?? "";

    private static string? StrOrNull(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static int? IntOrNull(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var i) ? i : null;
    }
}

public sealed class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
}
