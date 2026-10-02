using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jicun.Desktop.Models;

namespace Jicun.Desktop.Services;

/// <summary>
/// 上游聚合那条路认得的平台。**只用来决定走哪个上游**，不是「能不能解析」的白名单 ——
/// media-parser 那边还认一堆别的平台（微博、头条…），它们都归到 <see cref="ParsePlatform.Unknown"/>。
/// </summary>
public enum ParsePlatform { Unknown, Douyin, Kuaishou, Doubao, WeChatChannels }

/// <summary>
/// 上游聚合接口（第三方站点，APP 直连）的应答映射。逐条对着 Android 版
/// jicun/lib/upstream_mapping.dart 搬。
/// </summary>
public static class UpstreamMapping
{
    // ---------------- 平台识别 ----------------

    /// <summary>短链域名 → 平台。</summary>
    private static readonly (string Host, ParsePlatform Platform)[] PlatformHosts =
    {
        // 抖音：主站、短链、以及它的图集/去水印域名
        ("douyin.com", ParsePlatform.Douyin),
        ("iesdouyin.com", ParsePlatform.Douyin),
        ("ixigua.com", ParsePlatform.Douyin),
        // 快手：主站和它那两个短链
        ("kuaishou.com", ParsePlatform.Kuaishou),
        ("gifshow.com", ParsePlatform.Kuaishou),
        ("chenzhongtech.com", ParsePlatform.Kuaishou),
        ("kwai.com", ParsePlatform.Kuaishou),
        // 豆包
        ("doubao.com", ParsePlatform.Doubao),
        ("doubao.cn", ParsePlatform.Doubao),
        // 微信视频号
        ("channels.weixin.qq.com", ParsePlatform.WeChatChannels),
        ("finder.video.qq.com", ParsePlatform.WeChatChannels),
        ("weixin.qq.com", ParsePlatform.WeChatChannels),
    };

    /// <summary>
    /// 认这条链接属于哪个平台。认不出返回 <see cref="ParsePlatform.Unknown"/>。
    ///
    /// 判据只认**域名**，不认整段文本：分享文案里出现「抖音」两个字不代表这条链接是抖音的
    /// （用户转发别人的文案很常见）。大小写、子域名都归一到同一个平台。
    /// </summary>
    public static ParsePlatform DetectPlatform(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return ParsePlatform.Unknown;
        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0) return ParsePlatform.Unknown;

        foreach (var (candidate, platform) in PlatformHosts)
        {
            if (host == candidate || host.EndsWith("." + candidate, StringComparison.Ordinal)) return platform;
        }
        return ParsePlatform.Unknown;
    }

    /// <summary>展示用的中文名。</summary>
    public static string LabelOf(ParsePlatform platform) => platform switch
    {
        ParsePlatform.Douyin => "抖音",
        ParsePlatform.Kuaishou => "快手",
        ParsePlatform.Doubao => "豆包",
        ParsePlatform.WeChatChannels => "微信视频号",
        _ => "",
    };

    /// <summary>
    /// 平台 → 上游那条接口的路径。返回 null 表示这个平台不走上游。
    ///
    /// 上游每个平台一条独立接口，拿错平台的链接去问会回 422「解析参数与该平台不匹配」，
    /// 所以一条路对一个平台。**这张表就是「先走上游」的名单。**
    ///
    /// 注意：这张表和「服务端支持哪些平台」是两回事。微信视频号在服务端是关掉的，
    /// 但它仍然要在这张表里 —— 视频号能不能解析完全取决于上游那条接口，跟 media-parser
    /// 一点关系都没有。把它删掉，这个平台就整个不能用了（Android 版改前这么错过一次）。
    /// </summary>
    public static string? UpstreamPathOf(ParsePlatform platform) => platform switch
    {
        ParsePlatform.Douyin => "/api/dyjx",
        ParsePlatform.Kuaishou => "/api/ksjx",
        ParsePlatform.WeChatChannels => "/api/wxsph",
        ParsePlatform.Doubao => "/api/doubao",
        _ => null,
    };

    // ---------------- 应答映射 ----------------

    /// <summary>
    /// 这是不是上游那套结构。
    ///
    /// 认的字段都是实测确认存在的：type / cover / label / video_backup / live_photo / quality
    /// —— media-parser 那套一个都没有（它用 cover_url、video_url）。url 单独一条不算数：
    /// media-parser 的 video_list 里也有 url，不排除会把 {"url": …} 这种残缺应答也当成上游格式。
    /// </summary>
    public static bool IsUpstreamFormat(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return false;
        foreach (var key in new[] { "type", "cover", "label", "video_backup", "live_photo", "quality" })
            if (data.TryGetProperty(key, out _)) return true;
        return false;
    }

    /// <summary>
    /// 上游应答 → <see cref="ParseResult"/>。认不出是上游那套结构就退回
    /// <see cref="ParseResult.FromJson"/> 的读法：兜的是反代或上游把 media-parser
    /// 那套应答原样透传过来的情况。不兜的话会映射出一个空结果，而空结果会被解析层
    /// 判成「上游没解析出东西」再打一次兜底 —— 用户白等一个来回，还多花一次调用。
    /// </summary>
    public static ParseResult FromUpstream(JsonElement data, string platformLabel)
    {
        if (!IsUpstreamFormat(data)) return ParseResult.FromJson(data);

        var videoUrl = Absolute(StrOrNull(data, "url"));
        var coverUrl = Absolute(StrOrNull(data, "cover"));

        var result = new ParseResult
        {
            Title = CleanCopyText(Str(data, "title")),
            Desc = CleanCopyText(Str(data, "desc")),
            // 上游自己会给一个 platform 字段（实测是 douyin 这种英文名），那个直接给用户看
            // 不合适，所以优先用调用方传进来的中文名，没传才退回上游那个值。
            Platform = platformLabel.Length > 0 ? platformLabel : Str(data, "platform"),
            AuthorName = AuthorOf(data),
            VideoUrl = videoUrl,
            CoverUrl = coverUrl,
            AudioUrl = Absolute(UpstreamAudioUrl(data)),
            Lyrics = LyricsOf(data),
        };

        // 主地址自己也算一档，标签和码率也在根上（label / bit_rate）。
        var qualities = new List<VideoVariant>();
        if (videoUrl is not null)
        {
            qualities.Add(new VideoVariant
            {
                Url = videoUrl,
                CoverUrl = coverUrl,
                Label = RootLabel(data),
                BitRate = IntOrNull(data, "bit_rate") ?? IntOrNull(data, "real_bit_rate"),
                SizeBytes = LongOrNull(data, "size"),
            });
        }

        if (data.TryGetProperty("video_backup", out var backup) && backup.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in backup.EnumerateArray())
                if (QualityFromEntry(entry) is { } quality) qualities.Add(quality);
        }

        // 桌面版没有清晰度选择弹窗，所以把去重排序后的每一档都摊成一张卡 —— 和
        // media-parser 那条路处理 video_list 的方式一致，用户点哪张就下哪档。
        result.Videos.AddRange(DedupeQualities(qualities));

        // 实况图不是图片：下载下来是 MP4，所以归到视频那一类，静态帧留着当缩略图。
        if (data.TryGetProperty("live_photo", out var live) && live.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in live.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var url = Absolute(StrOrNull(item, "video") ?? StrOrNull(item, "url"));
                if (url is null) continue;
                result.Videos.Add(new VideoVariant
                {
                    Url = url,
                    CoverUrl = Absolute(StrOrNull(item, "image") ?? StrOrNull(item, "cover")),
                    Label = "实况图",
                });
            }
        }

        // 图集：上游用 null 占位（实测一条 9 张图的帖子里夹了一个 null），逐个剔掉。
        var images = new List<string>();
        if (data.TryGetProperty("images", out var rawImages) && rawImages.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rawImages.EnumerateArray())
                if (AsString(item) is { } url) images.Add(Absolute(url) ?? url);
        }

        result.ImageUrls.AddRange(CleanImages(images, coverUrl, hasVideo: videoUrl is not null));

        return result;
    }

    // ---------------- 文本 ----------------

    private static readonly HashSet<string> PlaceholderCopies = new(StringComparer.Ordinal)
    {
        "视频加载中", "视频加载中...", "视频加载中…", "分享视频", "网页链接", "暂无文案", "暂无简介",
    };

    /// <summary>
    /// 清掉平台回填在文案里的噪声，并把双重转义的换行还原成真的换行。
    ///
    /// 快手实测的 desc 里有「（yn）」和成串空行，原样显示出来就是文案里夹着噪声。
    /// 有些链接上游是双重转义的 —— 换行是两个字符（反斜杠 + n），那种也要还原。
    /// </summary>
    public static string CleanCopyText(string raw)
    {
        var text = (raw ?? "")
            .Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\t", "\t");

        // 平台自己的标记：快手会在句尾塞一个 (yn)
        text = Regex.Replace(text, @"[（(]\s*yn\s*[)）]", "", RegexOptions.IgnoreCase);

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = string.Join("\n", text.Split('\n').Select(line => line.TrimEnd()));
        text = Regex.Replace(text, @"\n{2,}", "\n").Trim();

        // 平台拿「还没加载出来」的占位文案当正文回填时，这些话不是文案
        return PlaceholderCopies.Contains(text) ? "" : text;
    }

    /// <summary>应答里的歌词原文（LRC 或纯文本）。这里**不洗文本** —— 歌词里的标点、表情都是原文的一部分。</summary>
    public static string? LyricsOf(JsonElement data)
    {
        foreach (var key in new[] { "lyrics", "lyric", "lrc", "lyric_text", "lyricText" })
        {
            if (!data.TryGetProperty(key, out var value)) continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                var lines = new List<string>();
                foreach (var line in value.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.String) continue;
                    var text = line.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) lines.Add(text.Trim());
                }
                if (lines.Count > 0) return string.Join("\n", lines);
            }
        }
        return null;
    }

    private static string AuthorOf(JsonElement data)
    {
        if (data.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
        {
            var name = StrOrNull(author, "name");
            if (name is not null) return name;
        }
        return Str(data, "author_name");
    }

    // ---------------- 音频 ----------------

    /// <summary>
    /// 上游应答里那条**独立音频**的地址。
    ///
    /// 实测上游把音频放在 music 里：快手图集帖是 .m4a、抖音实况帖是 .mp3，两个都是这条帖子的
    /// 原声。字段形态不止一种，所以三道都收；都没有（music 是空对象，豆包 AI 音乐分享实测就是
    /// 这样）就返回 null，音频卡不出现。
    /// </summary>
    private static string? UpstreamAudioUrl(JsonElement data)
    {
        foreach (var key in new[] { "audio_url", "audio", "music_url", "sound_url", "music" })
        {
            if (!data.TryGetProperty(key, out var value)) continue;
            if (AudioUrlOf(value) is { } url) return url;
        }
        return null;
    }

    private static string? AudioUrlOf(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return AsString(value);
        if (value.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in new[] { "url", "play_url", "audio_url" })
        {
            if (StrOrNull(value, key) is { } url) return url;
        }
        return null;
    }

    // ---------------- 清晰度 ----------------

    /// <summary>
    /// 分辨率标签归一化：1080p / 1080P / 超清 1080 / 1920x1080 都变成 1080P。
    ///
    /// 归一化只为**去重**：同一档分辨率上游可能给好几个码流，写法却不一样。认不出来就
    /// 原样返回，不硬猜 —— 猜错会让两档不同的分辨率被并成一档，用户就没得选了。
    /// </summary>
    public static string NormalizeQualityLabel(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";

        // 1920x1080 / 1080*1920：取短边（竖屏视频的高就是短边）
        var cross = Regex.Match(text, @"(\d{3,4})\s*[xX*]\s*(\d{3,4})");
        if (cross.Success)
        {
            var a = int.Parse(cross.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(cross.Groups[2].Value, CultureInfo.InvariantCulture);
            return (a < b ? a : b) + "P";
        }

        // 上游的档位名后面缀着画质词（720P高清、1080P超清），而那些词在每一档上都不一样
        // —— 不削掉的话同一个 720 会出现「720P高清」「720P超清」两个格子。只在数字后面削：
        // 纯名字（蓝光）不能动。
        text = Regex.Replace(text, @"(?<=\d)\s*(高清|超清|蓝光|标清|流畅|原画|高码率|高清版)\s*$", "");

        // 1080P / 1080p60 / 超清1080 / 1080 都归到同一个档位
        var height = Regex.Match(text, @"(\d{3,4})");
        if (height.Success) return height.Groups[1].Value + "P";

        // 认不出数字：蓝光 / 超清 / 原画 这类纯名字，保留原样当标签（仍然能去重）
        return text;
    }

    /// <summary>
    /// 用应答里的真实宽高算分辨率标签（短边 + P）。宽高缺一个就用另一个顶；小于 100 的
    /// 当作占位值不算（有平台会把宽高填 0）。取不到返回 null。
    /// </summary>
    private static string? ResolutionOf(JsonElement map)
    {
        var width = IntOrNull(map, "width") ?? IntOrNull(map, "w") ?? 0;
        var height = IntOrNull(map, "height") ?? IntOrNull(map, "h") ?? 0;
        if (width <= 0 && height <= 0) return null;
        if (width <= 0) width = height;
        if (height <= 0) height = width;
        if (width < 100 || height < 100) return null;
        return Math.Min(width, height) + "P";
    }

    /// <summary>
    /// 根节点那一档的标签。
    ///
    /// 抖音把「原画」放在 label（同时给了 width/height），快手把「原画」放在 quality
    /// 且**不给宽高**，豆包 / 视频号则是 quality=720p / origin。所以顺序是：真实宽高 →
    /// label / quality 里的数字 → 纯名字（留下的「原画」由解析收尾时去读文件头补上）。
    /// </summary>
    private static string RootLabel(JsonElement data)
    {
        if (ResolutionOf(data) is { } resolution) return resolution;

        foreach (var key in new[] { "label", "quality" })
        {
            var label = NormalizeQualityLabel(Str(data, key));
            if (label.Length > 0 && Regex.IsMatch(label, @"\d")) return label;
        }

        var named = NormalizeQualityLabel(Str(data, "label"));
        if (named.Length == 0) named = NormalizeQualityLabel(Str(data, "quality"));

        // 上游给的是英文名（quality:"original" / "origin"），换成中文再往外露。
        return named.Equals("original", StringComparison.OrdinalIgnoreCase)
            || named.Equals("origin", StringComparison.OrdinalIgnoreCase)
            ? "原画"
            : named;
    }

    /// <summary>档位排序用的分量：数字越大越高。</summary>
    private static int QualityRank(string label)
    {
        // K 档先认：4K / 8K / 2K。HeightOf 的正则只抓数字，会把 4K 读成 4 —— 排在 720P 下面。
        var k = Regex.Match(label, @"(\d+(?:\.\d+)?)\s*K", RegexOptions.IgnoreCase);
        if (k.Success && double.TryParse(k.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            return (int)Math.Round(value * 1000, MidpointRounding.AwayFromZero);

        var height = HeightOf(label);
        if (height > 0) return height;
        if (label.Length == 0) return 0;

        // 上游把「原画」这种档位放在首位，排序时也该在首位
        return label switch { "原画" => 3000, "蓝光" => 2000, "超清" => 1500, "高清" => 1000, _ => 1 };
    }

    private static int HeightOf(string label)
    {
        var match = Regex.Match(label, @"\d+");
        return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : 0;
    }

    private static string QualityLabelOfNumber(long value) => value > 0 ? value + "P" : "";

    /// <summary>
    /// 同分辨率的多个码流只留**码率最高**的那一条，最后按档位从高到低排。
    ///
    /// 上游实测会给同一种分辨率列好几档（1080p 两种码率），照单全收就会出现两个一模一样的
    /// 「1080P」。判据：1) 码率大的赢；2) 码率一样时文件大的赢；3) 还一样就保留先出现的。
    /// label 为空的档位不参与去重，原样留着（那多半是「未知画质」的一条备用地址）。
    ///
    /// 最后再按资源去一次重，但**只在同一档位内**算：CDN 会用同一个 host+path、只改 query
    /// 来发不同清晰度，按资源一刀切会把 720P 当成原画吃掉。
    /// </summary>
    public static List<VideoVariant> DedupeQualities(List<VideoVariant> qualities)
    {
        var best = new Dictionary<string, VideoVariant>(StringComparer.Ordinal);
        var unknown = new List<VideoVariant>();

        foreach (var quality in qualities)
        {
            var label = quality.Label ?? "";
            if (label.Length == 0) { unknown.Add(quality); continue; }
            if (!best.TryGetValue(label, out var current) || BetterThan(quality, current)) best[label] = quality;
        }

        var known = best.Values.ToList();
        known.Sort((a, b) =>
        {
            var left = a.Label ?? "";
            var right = b.Label ?? "";
            var byRank = QualityRank(right).CompareTo(QualityRank(left));
            return byRank != 0 ? byRank : string.CompareOrdinal(left, right);
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<VideoVariant>();
        foreach (var quality in known.Concat(unknown))
        {
            // 档位不同的两条即使落在同一个路径上也要各留一条，所以 key 里带上档位
            if (seen.Add((quality.Label ?? "") + (char)1 + ResourceId(quality.Url))) output.Add(quality);
        }
        return output;
    }

    private static bool BetterThan(VideoVariant candidate, VideoVariant current)
    {
        var candidateBitrate = candidate.BitRate ?? 0;
        var currentBitrate = current.BitRate ?? 0;
        if (candidateBitrate != currentBitrate) return candidateBitrate > currentBitrate;
        return (candidate.SizeBytes ?? 0) > (current.SizeBytes ?? 0);
    }

    /// <summary>同一个资源的判据：去掉 query，只比 scheme + host + path。</summary>
    private static string ResourceId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return uri.Scheme + "://" + uri.Host + uri.AbsolutePath;
    }

    /// <summary>
    /// 一个清晰度条目 → <see cref="VideoVariant"/>。
    ///
    /// 上游给的对象实测长这样：
    /// {"label":"720P高清","quality":"720p","url":"…","bit_rate":1678555,"size":377687084,…}
    ///
    /// **HLS 播放列表直接丢掉**：下载器是一条 Range 一条连接地收字节，不做 HLS 分片拼接。
    /// 实测快手一条 85MB 的视频，video_backup 里 4 条全是 .m3u8，只有根上的 url 是真 mp4
    /// —— 把这些列给用户，用户一选「720P」下回来的就是 11KB 的播放列表文本。
    /// </summary>
    private static VideoVariant? QualityFromEntry(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            var text = AsString(entry);
            if (text is null || IsHlsPlaylist(text)) return null;
            return new VideoVariant { Url = text };
        }

        if (entry.ValueKind != JsonValueKind.Object) return null;

        var url = StrOrNull(entry, "url") ?? StrOrNull(entry, "play_url") ?? StrOrNull(entry, "video_url");
        if (url is null || IsHlsPlaylist(url)) return null;

        return new VideoVariant
        {
            Url = url,
            Label = LabelFromMap(entry),
            BitRate = IntOrNull(entry, "bit_rate") ?? IntOrNull(entry, "real_bit_rate") ?? IntOrNull(entry, "bitrate"),
            SizeBytes = LongOrNull(entry, "size") ?? LongOrNull(entry, "file_size"),
        };
    }

    /// <summary>
    /// 从对象里读分辨率标签。
    ///
    /// **数字档位优先**：快手的 video_backup 是 label=高清 + quality=720p，抖音是
    /// label=720P高清 + quality=720p。只认 label 的话，同一份列表在快手上会出现「高清 / 540P」
    /// 这种混搭，而且「高清」在别的平台上可能指的是另一档。所以顺序是：label/quality 里
    /// **带数字的那个**先要；两个都没数字才退回纯名字（原画）；连名字都没有才看 height。
    /// </summary>
    private static string LabelFromMap(JsonElement map)
    {
        // 真实宽高最准，优先用：上游自己的「720p」经常是宣传口径 —— 实测快手那条
        // 1600x704 也叫 720p，抖音 1378x576 却叫 540P。
        if (ResolutionOf(map) is { } resolution) return resolution;

        string? named = null;

        foreach (var key in new[] { "label", "quality", "definition", "gear_name" })
        {
            if (!map.TryGetProperty(key, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                var fromNumber = QualityLabelOfNumber((long)number);
                if (fromNumber.Length > 0) return fromNumber;
            }

            if (value.ValueKind != JsonValueKind.String) continue;

            var label = NormalizeQualityLabel(value.GetString() ?? "");
            if (label.Length == 0 || LooksLikeUrl(label)) continue;
            if (Regex.IsMatch(label, @"\d")) return label;
            named ??= label;
        }

        if (named is not null) return named;

        // 都没有才看高度。上限卡 2160：再大是竖屏/超宽屏的宽边被当成高了。
        var height = IntOrNull(map, "height") ?? IntOrNull(map, "h") ?? 0;
        return height > 0 && height <= 2160 ? height + "P" : "";
    }

    private static bool LooksLikeUrl(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.StartsWith("http://", StringComparison.Ordinal) || lower.StartsWith("https://", StringComparison.Ordinal);
    }

    /// <summary>这是不是一条 HLS 播放列表地址（.m3u8）。只看路径后缀，不看 query。</summary>
    private static bool IsHlsPlaylist(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- 地址 ----------------

    /// <summary>
    /// 服务端下发的相对地址补上域名。只补「以 / 开头」的：绝对地址原样返回。
    /// 不补的话播放器会当成相对本地路径（报 Cannot open file），下载器直接报「地址解析失败」。
    /// </summary>
    public static string? Absolute(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/') return url;
        return ApiHosts.Url(url);
    }

    /// <summary>同一个资源的判据：去掉 scheme / host / query，只比路径。</summary>
    private static string IdentityOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return uri.AbsolutePath.Length == 0 ? url : uri.AbsolutePath;
    }

    /// <summary>
    /// 图集去重，并且视频封面不算一张图。
    ///
    /// 封面往往就是图集第一张，而平台给同一张图的不同尺寸差别只在 host 或 query 上
    /// —— 只比整串会漏掉，于是视频封面被当成一张可下载的图。
    /// </summary>
    public static List<string> CleanImages(List<string> images, string? coverUrl, bool hasVideo)
    {
        var cover = coverUrl is null ? "" : IdentityOf(coverUrl);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>();

        foreach (var url in images)
        {
            var id = IdentityOf(url);
            if (!seen.Add(id)) continue;
            if (hasVideo && id == cover) continue;
            cleaned.Add(url);
        }
        return cleaned;
    }

    // ---------------- JSON 取值 ----------------

    private static string Str(JsonElement element, string key) => StrOrNull(element, key) ?? "";

    private static string? StrOrNull(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value)) return null;
        return AsString(value);
    }

    private static string? AsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => BlankToNull(value.GetString()),
        JsonValueKind.Number => value.ToString(),
        _ => null,
    };

    private static string? BlankToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static int? IntOrNull(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var number) ? number : null;
    }

    private static long? LongOrNull(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt64(out var number) ? number : null;
    }


}
