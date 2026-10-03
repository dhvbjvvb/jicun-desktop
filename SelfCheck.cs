using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jicun.Desktop.Models;
using Jicun.Desktop.Services;
using Jicun.Desktop.Views;

namespace Jicun.Desktop;

/// <summary>
/// 命令行自检：把那些「写错了也看不出来、出问题又难查」的纯逻辑挨个断言一遍。
///
///     Jicun.exe --selftest
///
/// 覆盖上游应答映射（跑真实应答 fixture）、清晰度归一化与去重、图集去重、服务端下发的
/// 域名白名单，以及版本号 / 断点 id 这些散在各处的解析。不联网、不开界面；
/// 退出码 0 = 全部断言成立，1 = 有断言没过。
/// </summary>
internal static class SelfCheck
{
    private static int _failed;

    public static int Run()
    {
        QualityLabels();
        QualityDedupe();
        CopyText();
        PlatformDetect();
        ImageDedupe();
        UpstreamFixture();
        MediaParserShape();
        ServerConfigBoundary();
        MiscPaths();
        MergeTempNames();
        TransportMessages();
        MarkdownShape();
        UpdateSources();
        AudioTagsRoundTrip();
        UpdateFlowShape();
        RelativeUrls();
        VersionPath();
        ProbeBudget();

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "自检通过：全部断言成立" : "自检失败：" + _failed + " 项不成立");
        return _failed == 0 ? 0 : 1;
    }

    // ---- 清晰度标签归一化 ----

    private static void QualityLabels()
    {
        Console.WriteLine("清晰度标签归一化");

        CheckEqual("1080p → 1080P", "1080P", UpstreamMapping.NormalizeQualityLabel("1080p"));
        CheckEqual("1920x1080 → 短边 1080P", "1080P", UpstreamMapping.NormalizeQualityLabel("1920x1080"));
        CheckEqual("1080*1920 → 短边 1080P", "1080P", UpstreamMapping.NormalizeQualityLabel("1080*1920"));
        CheckEqual("720P高清 → 720P", "720P", UpstreamMapping.NormalizeQualityLabel("720P高清"));
        CheckEqual("纯名字不动：蓝光", "蓝光", UpstreamMapping.NormalizeQualityLabel("蓝光"));
        CheckEqual("认不出数字就别猜：4K", "4K", UpstreamMapping.NormalizeQualityLabel("4K"));
        CheckEqual("空串还是空串", "", UpstreamMapping.NormalizeQualityLabel(""));
    }

    // ---- 同分辨率多码流去重 + 档位排序 ----

    private static void QualityDedupe()
    {
        Console.WriteLine("清晰度去重 / 排序");

        var qualities = new List<VideoVariant>
        {
            new() { Url = "https://cdn/a-1080-low.mp4", Label = "1080P", BitRate = 100 },
            new() { Url = "https://cdn/a-1080-high.mp4", Label = "1080P", BitRate = 200 },
            new() { Url = "https://cdn/a-720.mp4", Label = "720P" },
            new() { Url = "https://cdn/a-unknown.mp4" },
        };

        var kept = UpstreamMapping.DedupeQualities(qualities);

        CheckEqual("同档位只留一条 + 未知档位留着", 3, kept.Count);
        CheckEqual("排最前的是最高档", "1080P", kept.Count > 0 ? kept[0].Label : null);
        CheckEqual("同档位留码率高的那条", "https://cdn/a-1080-high.mp4", kept.Count > 0 ? kept[0].Url : null);
        CheckEqual("第二档是 720P", "720P", kept.Count > 1 ? kept[1].Label : null);
    }

    // ---- 文案清洗 ----

    private static void CopyText()
    {
        Console.WriteLine("文案清洗");

        CheckEqual("占位文案当没有", "", UpstreamMapping.CleanCopyText("视频加载中"));
        CheckEqual("平台噪声（yn）去掉", "今天的作业", UpstreamMapping.CleanCopyText("今天的作业（yn）"));
        CheckEqual("双重转义换行还原", "a\nb", UpstreamMapping.CleanCopyText(@"a\nb"));
        CheckEqual("普通文案原样保留", "你好，世界", UpstreamMapping.CleanCopyText("你好，世界"));
    }

    // ---- 平台识别 ----

    private static void PlatformDetect()
    {
        Console.WriteLine("平台识别");

        CheckEqual("抖音短链", ParsePlatform.Douyin, UpstreamMapping.DetectPlatform("https://v.douyin.com/abc/"));
        CheckEqual("快手短链", ParsePlatform.Kuaishou, UpstreamMapping.DetectPlatform("https://v.m.chenzhongtech.com/fw/photo/x"));
        CheckEqual("视频号", ParsePlatform.WeChatChannels, UpstreamMapping.DetectPlatform("https://weixin.qq.com/sph/abc"));
        CheckEqual("别的站不认", ParsePlatform.Unknown, UpstreamMapping.DetectPlatform("https://example.com/x"));
        Check("抖音走 /api/dyjx", UpstreamMapping.UpstreamPathOf(ParsePlatform.Douyin) == "/api/dyjx");
        Check("认不出的平台不走上游", UpstreamMapping.UpstreamPathOf(ParsePlatform.Unknown) is null);
    }

    // ---- 图集去重 ----

    private static void ImageDedupe()
    {
        Console.WriteLine("图集去重");

        var images = new List<string>
        {
            "https://cdn/x.jpg?sig=aaa",
            "https://other/x.jpg?sig=bbb",   // 同一个路径、不同 host / 签名 = 同一张
            "https://cdn/y.jpg",
        };

        var cleaned = UpstreamMapping.CleanImages(images, coverUrl: null, hasVideo: false);
        CheckEqual("按路径去重", 2, cleaned.Count);
        CheckEqual("图集不留空档", "https://cdn/x.jpg?sig=aaa", cleaned.Count > 0 ? cleaned[0] : null);
        CheckEqual("第二张还在", "https://cdn/y.jpg", cleaned.Count > 1 ? cleaned[1] : null);

        var withCover = UpstreamMapping.CleanImages(
            new List<string> { "https://cdn/x.jpg?sig=2", "https://cdn/y.jpg" },
            coverUrl: "https://cdn/x.jpg?sig=1",
            hasVideo: true);
        CheckEqual("有视频时封面不再算一张图", 1, withCover.Count);
        CheckEqual("剩下的是那张真图", "https://cdn/y.jpg", withCover.Count > 0 ? withCover[0] : null);
    }

    // ---- 真实上游应答 ----

    private static void UpstreamFixture()
    {
        Console.WriteLine("真实上游应答（test/fixtures/upstream_real.json）");

        var path = FixturePath();
        if (path is null)
        {
            // 这份样本是内部资料（.gitignore 里的 test/fixtures/），别人 clone 出来的仓库里没有。
            // 跳过必须说出来：静默跳过等于这条检查不存在，报失败又会让别人以为自检本身坏了。
            Console.WriteLine("  skip 没找到 test/fixtures/upstream_real.json（样本不进仓库），这一组断言跳过");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var seen = 0;

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var status = entry.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            var body = entry.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            if (status != 200 || string.IsNullOrEmpty(body)) continue;   // 422 那条是上游自己拒了，本该没有结果

            var share = entry.TryGetProperty("shareUrl", out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString() ?? ""
                : "";

            ParseResult result;
            try
            {
                using var bodyDoc = JsonDocument.Parse(body);
                var data = bodyDoc.RootElement.GetProperty("data");
                result = UpstreamMapping.FromUpstream(data, entry.GetProperty("platform").GetString() ?? "");
            }
            catch (Exception ex)
            {
                Check("上游应答能映射：" + share, false, ex.Message);
                seen++;
                continue;
            }

            // 按分享链接认这一条，不按顺序 —— fixture 重新生成时顺序会变，序号断言一改就白报
            if (share.Contains("douyin.com/note/", StringComparison.Ordinal))
            {
                // 抖音：图集 + 空实况图 + music 里的原声
                Check("抖音图集能映射", true);
                CheckEqual("抖音图集 2 张图", 2, result.ImageUrls.Count);
                CheckEqual("抖音图集没有视频（实况图是空的）", 0, result.Videos.Count);
                Check("抖音原声来自 music.url", result.AudioUrl is not null, result.AudioUrl ?? "(null)");
                Check("抖音标题不为空", result.Title.Length > 0);
            }
            else if (share.Contains("weixin.qq.com/sph/AzGrUgqzFv", StringComparison.Ordinal))
            {
                // 视频号：根地址 + 两条同路径的 backup，去重后只剩一档
                Check("视频号视频能映射", true);
                CheckEqual("视频号同路径备份去重成一档", 1, result.Videos.Count);
                CheckEqual("视频号标签取 quality", "1080P", result.Videos.Count > 0 ? result.Videos[0].Label : null);
            }
            else if (share.Contains("weixin.qq.com/sph/APclmPJEZ0", StringComparison.Ordinal))
            {
                // 上游 200 但内容全空的一条：解析层得看得出「空的」，才好回落 media-parser，
                // 不然用户看到的是一张空卡片
                Check("视频号空应答能映射", true);
                Check("视频号空应答里没有视频", !result.HasVideo);
                Check("视频号空应答里没有图", !result.HasImages);
                Check("视频号空应答里没有音频", !result.HasAudio);
            }
            else if (share.Contains("video-sharing", StringComparison.Ordinal))
            {
                // 豆包视频：只有根地址那一档 720p
                Check("豆包视频能映射", true);
                CheckEqual("豆包视频一档", 1, result.Videos.Count);
                CheckEqual("豆包视频标签 720P", "720P", result.Videos.Count > 0 ? result.Videos[0].Label : null);
            }
            else if (share.Contains("doubao.com/thread/", StringComparison.Ordinal))
            {
                // 豆包图集：6 个地址只在 ~tplv 尾巴上不同，不能当同一张合并掉
                Check("豆包图集能映射", true);
                CheckEqual("豆包图集 6 张不被误合并", 6, result.ImageUrls.Count);
            }
            else if (share.Contains("music-sharing", StringComparison.Ordinal))
            {
                // 豆包音乐：只有一档 240p，music 是空对象 → 没有独立音频
                Check("豆包音乐能映射", true);
                CheckEqual("豆包音乐一档视频", 1, result.Videos.Count);
                CheckEqual("豆包音乐标签 240P", "240P", result.Videos.Count > 0 ? result.Videos[0].Label : null);
                Check("music 是空对象就没有音频", result.AudioUrl is null, result.AudioUrl ?? "(null)");
            }
            else
            {
                // fixture 里多了条没见过的新样品：别让它静悄悄过去
                Check("fixture 里这条认得出来", false, share);
            }

            seen++;
        }

        Check("至少跑了一条 200 的应答", seen > 0, "实际 " + seen + " 条");
    }

    /// <summary>fixture 在仓库里，不在编译产物里 —— 从 exe 往上找。</summary>
    private static string? FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test", "fixtures", "upstream_real.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- media-parser 那套应答 ----

    private static void MediaParserShape()
    {
        Console.WriteLine("media-parser 应答映射");

        var json = """
        {
          "title": "测试",
          "cover_url": "https://cdn/cover.jpg",
          "video_list": [
            { "url": "https://cdn/v.mp4", "cover_url": "https://cdn/cover.jpg",
              "qualities": [ { "url": "https://cdn/v1080.mp4", "label": "1080p", "bit_rate": 2000 } ] }
          ],
          "image_list": [ "https://cdn/a.jpg", { "url": "https://cdn/b.jpg", "live_photo_url": "https://cdn/b.mp4" } ]
        }
        """;

        using (var doc = JsonDocument.Parse(json))
        {
            var result = ParseResult.FromJson(doc.RootElement);

            // 这一份应答里有 1 档 qualities + 1 条实况图，都归到视频那一类
            CheckEqual("qualities 摊成一档 + 1 条实况图", 2, result.Videos.Count);
            CheckEqual("用的是 qualities 里的地址", "https://cdn/v1080.mp4", result.Videos.Count > 0 ? result.Videos[0].Url : null);
            CheckEqual("实况图的地址是那个 MP4", "https://cdn/b.mp4", result.Videos.Count > 1 ? result.Videos[1].Url : null);
            CheckEqual("实况图标签", ParseResult.LivePhotoLabel, result.Videos.Count > 1 ? result.Videos[1].Label : null);
            CheckEqual("实况图的静态帧不当图集", 1, result.ImageUrls.Count);
        }

        // 封面就是图集第一张：有视频时那张封面不该再算一张可下载的图（只比路径，签名不算）
        var withCover = """
        { "video_url": "https://cdn/v.mp4", "cover_url": "https://cdn/x.jpg?sig=1",
          "image_list": [ "https://cdn/x.jpg?sig=2", "https://cdn/y.jpg" ] }
        """;

        using (var doc = JsonDocument.Parse(withCover))
        {
            var result = ParseResult.FromJson(doc.RootElement);
            CheckEqual("视频封面从图集里剔掉", 1, result.ImageUrls.Count);
            CheckEqual("图集剩下 y.jpg", "https://cdn/y.jpg", result.ImageUrls.Count > 0 ? result.ImageUrls[0] : null);
        }
    }

    // ---- 服务端下发的域名白名单（信任边界） ----

    private static void ServerConfigBoundary()
    {
        Console.WriteLine("服务端配置解析（信任边界）");

        var config = ServerConfigParser.Parse("""
        {
          "hosts": [ "ok.com", "evil.com:8443", "nodot", "UPPER.COM", "bad_underscore.com" ],
          "ips": [ "1.2.3.4", "nope", "::1" ],
          "supported": [ "a.com", "a.com", "b.com" ]
        }
        """);

        CheckEqual("hosts：带端口 / 单段 / 下划线一律不要", 2, config.Hosts.Count);
        CheckEqual("hosts：第一个（顺序即优先级）", "ok.com", config.Hosts.Count > 0 ? config.Hosts[0] : null);
        CheckEqual("hosts：大写归一成小写", "upper.com", config.Hosts.Count > 1 ? config.Hosts[1] : null);
        CheckEqual("ips：只留解得出来的", 2, config.Ips.Count);
        CheckEqual("supported：同一条只留一份", 2, config.Supported.Count);

        Check("坏 JSON 不抛异常，返回空配置", ServerConfigParser.Parse("{ 不是 json").IsEmpty);

        var capped = ServerConfigParser.Parse(
            "{\"hosts\":[\"a.com\",\"b.com\",\"c.com\",\"d.com\",\"e.com\"]}", maxHosts: 2);
        CheckEqual("hosts 有条数上限", 2, capped.Hosts.Count);
    }

    // ---- 零散解析 ----

    private static void MiscPaths()
    {
        Console.WriteLine("链接 / 版本号 / 断点 id");

        CheckEqual("整段分享文案里挑链接", "https://v.douyin.com/abc/",
            ParseService.ExtractUrl("看看这个 https://v.douyin.com/abc/ 复制打开抖音"));
        Check("没有链接就是没有", ParseService.ExtractUrl("没有链接的文案") is null);

        CheckEqual("v1.2.3", new Version(1, 2, 3), UpdateService.ParseVersion("v1.2.3"));
        CheckEqual("1.2.3-beta.1 取 1.2.3", new Version(1, 2, 3), UpdateService.ParseVersion("1.2.3-beta.1"));
        Check("解不出来的返回 null", UpdateService.ParseVersion("最新版") is null);

        Check("64 位十六进制才算 sha256", UpdateService.IsSha256(new string('a', 64)));
        Check("短的不算", !UpdateService.IsSha256("abc"));
        Check("非十六进制不算", !UpdateService.IsSha256(new string('z', 64)));

        var id = ResumeStore.IdFor("https://cdn/v.mp4", "文件名.mp4");
        Check("同一个链接 + 同一个名字 = 同一份断点", id == ResumeStore.IdFor("https://cdn/v.mp4", "文件名.mp4"));
        Check("换个名字就是另一份", id != ResumeStore.IdFor("https://cdn/v.mp4", "另一个名字.mp4"));

        CheckEqual("竖屏 1080x1920 也算 1080P", "1080P", MediaProbe.Label(1080, 1920));
        CheckEqual("横屏 1920x1080 是 1080P", "1080P", MediaProbe.Label(1920, 1080));
    }

    // ---- 合并临时文件的识别（启动清理只认这个形状，认错会删到用户自己的文件） ----

    private static void MergeTempNames()
    {
        Console.WriteLine("合并临时文件的名字");

        Check("正常形状认得出", DownloadService.IsMergeTemp(".0123456789abcdef.merged.part"));
        Check("大写十六进制也认", DownloadService.IsMergeTemp(".0123456789ABCDEF.merged.part"));
        Check("没有前导点不认", !DownloadService.IsMergeTemp("0123456789abcdef.merged.part"));
        Check("后缀不对不认", !DownloadService.IsMergeTemp(".0123456789abcdef.merged"));
        Check("id 位数不对不认", !DownloadService.IsMergeTemp(".0123456789abcde.merged.part"));
        Check("不是十六进制不认", !DownloadService.IsMergeTemp(".0123456789axyzef.merged.part"));
        Check("普通文件名不认", !DownloadService.IsMergeTemp("视频.mp4"));
    }


    // ---- 音频标签：改的是用户刚下好的文件，必须逐字节验（见 AudioTags） ----

    /// <summary>
    /// AudioTags 是全仓唯一会**改写用户文件**的代码（重建 moov、再按新的 moov 长度去补
    /// stco 里的 chunk 偏移）。写坏了是静默的：文件头还认得、时长也读得出来，声音却是错位的。
    /// 这里用手拼的最小 m4a / mp3 把它走一遍，逐字节对。
    /// </summary>
    private static void AudioTagsRoundTrip()
    {
        Console.WriteLine("音频标签写入（合成文件，逐字节验）");

        var dir = Path.Combine(Path.GetTempPath(), "jicun-selftest-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            Mp4Case(dir);
            Co64Case(dir);
            Mp3Case(dir);
            RefuseCase(dir);
        }
        catch (Exception ex)
        {
            Check("音频标签自检能跑完", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 删不掉就算了，本来就在临时目录里 */ }
        }
    }

    /// <summary>
    /// 最小 m4a：<c>ftyp + moov(trak/mdia/minf/stbl/stco) + mdat</c>，还预置了一条 ©cmt。
    /// stco 里放真实 chunk 偏移 —— 写完标签后它们必须整体后移 moov 长出来的那点。
    /// </summary>
    private static void Mp4Case(string dir)
    {
        var payload = new byte[32];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(0xA0 + i);

        // stco 排在 mdat 前面，所以先拿占位偏移拼一份、量出 mdat 的位置，再回填真值
        var probe = BuildM4a(payload, 0, 0);
        var oldMdat = Find(Boxes(probe, 0, probe.Length), "mdat")
            ?? throw new InvalidOperationException("合成的 m4a 里没有 mdat");
        var chunk1 = oldMdat.Body;
        var chunk2 = oldMdat.Body + 16;

        var file = BuildM4a(payload, chunk1, chunk2);
        var oldStco = StcoEntries(file) ?? throw new InvalidOperationException("合成的 m4a 里没有 stco");
        var path = Path.Combine(dir, "sample.m4a");
        File.WriteAllBytes(path, file);

        CheckEqual("m4a 认得出来", "m4a", AudioTags.DetectFormat(path));
        Check("m4a 写标签成功", AudioTags.Write(path, "新标题", "新作者", "新专辑", Jpeg()));

        var tagged = File.ReadAllBytes(path);
        var newMdat = Find(Boxes(tagged, 0, tagged.Length), "mdat")
            ?? throw new InvalidOperationException("写完标签后 mdat 不见了");
        var delta = newMdat.Start - oldMdat.Start;   // moov 长了这么多，mdat 跟着后移

        // 这条是给下面两条「后移」断言把关的：delta 要是 0，那两条就是空转，测了等于没测
        Check("moov 确实长大了（delta > 0）", delta > 0, "delta=" + delta);
        CheckEqual("文件长度只多了 moov 那点", file.Length + delta, tagged.Length);
        Check("音频载荷逐字节没变", tagged.AsSpan(newMdat.Body, payload.Length).SequenceEqual(payload));

        var entries = StcoEntries(tagged) ?? throw new InvalidOperationException("写完标签后 stco 不见了");
        CheckEqual("stco 条目数没变", 2, entries.Count);
        Check("stco 第 1 项整体后移了 delta", entries.Count > 0 && entries[0] == oldStco[0] + delta,
            "旧 " + (oldStco.Count > 0 ? oldStco[0].ToString() : "?") + " → 新 " +
            (entries.Count > 0 ? entries[0].ToString() : "?") + "，delta " + delta);
        Check("stco 第 2 项整体后移了 delta", entries.Count > 1 && entries[1] == oldStco[1] + delta);

        Check("©nam 写进去了", HasItem(tagged, "©nam"));
        Check("©ART / ©alb / covr 都写进去了",
            HasItem(tagged, "©ART") && HasItem(tagged, "©alb") && HasItem(tagged, "covr"));
        Check("文件里预置的 ©cmt 没被抹掉", HasItem(tagged, "©cmt"));

        // 再写一次（换个标题）：同名的旧项要被换掉，不能挂两份让播放器随便挑一份读
        Check("m4a 再写一次也成功", AudioTags.Write(path, "更新的标题", "新作者", "新专辑", Jpeg()));
        var again = File.ReadAllBytes(path);
        CheckEqual("©nam 只剩一份", 1, CountItems(again, "©nam"));
        CheckEqual("©nam 是新值", "更新的标题", TextItemOf(again, "©nam") ?? "(null)");
    }

    private static void Mp3Case(string dir)
    {
        var audio = new byte[64];
        audio[0] = 0xFF;
        audio[1] = 0xFB;
        for (var i = 2; i < audio.Length; i++) audio[i] = (byte)i;

        var plain = Path.Combine(dir, "plain.mp3");
        File.WriteAllBytes(plain, audio);
        CheckEqual("mp3 认得出来", "mp3", AudioTags.DetectFormat(plain));
        Check("mp3 写标签成功", AudioTags.Write(plain, "歌名", "歌手", "专辑", null));

        var tagged = File.ReadAllBytes(plain);
        Check("mp3 现在是 ID3v2.4 开头",
            tagged.Length > 10 && tagged[0] == 'I' && tagged[1] == 'D' && tagged[2] == '3' && tagged[3] == 0x04);

        var tagLen = SyncSafe(tagged, 6);
        Check("ID3 长度是 syncsafe 的正数", tagLen > 0, "tagLen=" + tagLen);
        Check("音频字节原样跟在标签后面", tagged.AsSpan(10 + tagLen).SequenceEqual(audio));

        // 末尾挂一个 ID3v1：写完标签后它必须被删掉，否则播放器会优先读它、显示旧标题
        var v1 = new byte[128];
        v1[0] = (byte)'T';
        v1[1] = (byte)'A';
        v1[2] = (byte)'G';
        var withV1 = Path.Combine(dir, "withv1.mp3");
        File.WriteAllBytes(withV1, Cat(audio, v1));

        Check("带 ID3v1 的 mp3 也写得成", AudioTags.Write(withV1, "新歌名", null, null, null));
        var stripped = File.ReadAllBytes(withV1);
        var strippedAudio = stripped.AsSpan(10 + SyncSafe(stripped, 6));
        Check("末尾的 ID3v1 被删掉了", strippedAudio.SequenceEqual(audio),
            "音频长度 " + strippedAudio.Length + "，期望 " + audio.Length);
    }

    /// <summary>三种「一个字节都不许改」的情况：认不出的容器、封面过大、文件过大。</summary>
    private static void RefuseCase(string dir)
    {
        var flacBytes = new byte[64];
        flacBytes[0] = (byte)'f';
        flacBytes[1] = (byte)'L';
        flacBytes[2] = (byte)'a';
        flacBytes[3] = (byte)'C';
        var flac = Path.Combine(dir, "sample.flac");
        File.WriteAllBytes(flac, flacBytes);
        Check("FLAC 不写标签（返回 false）", !AudioTags.Write(flac, "歌名", null, null, null));
        Check("FLAC 一个字节都没动", File.ReadAllBytes(flac).AsSpan().SequenceEqual(flacBytes));

        var bigCover = Path.Combine(dir, "bigcover.m4a");
        File.WriteAllBytes(bigCover, BuildM4a(new byte[64], 0, 0));
        var cover = new byte[5 << 20];
        cover[0] = 0xFF;
        cover[1] = 0xD8;
        cover[2] = 0xFF;
        Check("封面超过 4MB 只是跳过封面", AudioTags.Write(bigCover, "标题", null, null, cover));
        var afterBig = File.ReadAllBytes(bigCover);
        Check("文字标签写了、covr 没写", HasItem(afterBig, "©nam") && !HasItem(afterBig, "covr"));

        var huge = Path.Combine(dir, "huge.mp3");
        using (var fs = File.Create(huge)) fs.SetLength(17 << 20);
        Check("超过 16MB 的整份跳过", !AudioTags.Write(huge, "歌名", null, null, null));
    }

    // ---- 上面那些用例用的「自己拼盒子 / 自己找盒子」----
    // 刻意不复用 AudioTags 里那套私有实现：断言要独立算一遍，才验得出它算错了。

    private readonly record struct Box(uint Type, int Start, int Size)
    {
        public int Body => Start + 8;
        public int End => Start + Size;
    }

    private static List<Box> Boxes(byte[] bytes, int start, int end)
    {
        var boxes = new List<Box>();
        var pos = start;
        while (end - pos >= 8)
        {
            var size = (int)ReadU32(bytes, pos);
            if (size < 8 || pos + size > end) break;
            boxes.Add(new Box(ReadU32(bytes, pos + 4), pos, size));
            pos += size;
        }
        return boxes;
    }

    private static Box? Find(List<Box> boxes, string type)
    {
        foreach (var box in boxes) if (box.Type == Tag(type)) return box;
        return null;
    }

    private static byte[] BuildM4a(byte[] payload, int chunkOffset1, int chunkOffset2, bool useCo64 = false)
    {
        // stco 是 32 位偏移、co64 是 64 位：两种表都得造得出来，补偏移那两条分支才都测得到
        var table = useCo64
            ? Cat(U32(0), U32(2), U32(0), U32((uint)chunkOffset1), U32(0), U32((uint)chunkOffset2))
            : Cat(U32(0), U32(2), I32(chunkOffset1), I32(chunkOffset2));
        var stbl = MakeBox("stbl", MakeBox(useCo64 ? "co64" : "stco", table));
        var trak = MakeBox("trak", MakeBox("mdia", MakeBox("minf", stbl)));
        // 预置一条别的标签（©cmt）：写新标签时它必须留下
        var ilst = MakeBox("ilst", MakeBox("©cmt", MakeBox("data", Cat(U32(1), U32(0), Utf8("旧备注")))));
        var meta = MakeBox("meta", Cat(new byte[4], MakeBox("hdlr", new byte[25]), ilst));
        var moov = MakeBox("moov", Cat(trak, MakeBox("udta", meta)));
        var ftyp = MakeBox("ftyp", Cat(Utf8("isom"), U32(0x200), Utf8("isom")));
        return Cat(ftyp, moov, MakeBox("mdat", payload));
    }

    private static List<int>? StcoEntries(byte[] file)
    {
        var stco = Descend(file, "moov", "trak", "mdia", "minf", "stbl", "stco");
        if (stco is null) return null;

        var count = (int)ReadU32(file, stco.Value.Body + 4);
        var entries = new List<int>();
        for (var i = 0; i < count; i++) entries.Add((int)ReadU32(file, stco.Value.Body + 8 + i * 4));
        return entries;
    }

    /// <summary>按名字一层层往里找（每层只认第一个同名的盒子）。</summary>
    private static Box? Descend(byte[] file, params string[] path)
    {
        Box? current = null;
        var start = 0;
        var end = file.Length;

        foreach (var name in path)
        {
            if (current is { } box)
            {
                start = box.Body;
                end = box.End;
                // meta 是 FullBox：里面的盒子要跳过 4 字节 version/flags
                if (box.Type == Tag("meta")) start += 4;
            }

            var found = Find(Boxes(file, start, end), name);
            if (found is null) return null;
            current = found;
        }

        return current;
    }

    /// <summary>ilst 里挂着的那几条标签项。</summary>
    private static List<Box> IlstItems(byte[] file)
    {
        var ilst = Descend(file, "moov", "udta", "meta", "ilst");
        return ilst is null ? new List<Box>() : Boxes(file, ilst.Value.Body, ilst.Value.End);
    }

    private static bool HasItem(byte[] file, string type) => CountItems(file, type) > 0;

    private static int CountItems(byte[] file, string type)
    {
        var count = 0;
        foreach (var item in IlstItems(file)) if (item.Type == Tag(type)) count++;
        return count;
    }

    /// <summary>读一个文本项（©nam）的值：item → data → 类型(4) + locale(4) + UTF-8 文本。</summary>
    private static string? TextItemOf(byte[] file, string type)
    {
        foreach (var item in IlstItems(file))
        {
            if (item.Type != Tag(type)) continue;
            var data = Find(Boxes(file, item.Body, item.End), "data");
            if (data is null) continue;
            return Encoding.UTF8.GetString(file, data.Value.Body + 8, data.Value.Size - 16);
        }
        return null;
    }

    /// <summary>盒子类型那 4 个字节按大端读成 uint —— ©nam 的首字节是 0xA9，不能当 ASCII 处理。</summary>
    private static uint Tag(string four) =>
        ((uint)four[0] << 24) | ((uint)four[1] << 16) | ((uint)four[2] << 8) | four[3];

    private static byte[] MakeBox(string type, byte[] body) =>
        Cat(U32((uint)(8 + body.Length)), U32(Tag(type)), body);

    private static byte[] Cat(params byte[][] parts)
    {
        var joined = new byte[parts.Sum(part => part.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(joined, at);
            at += part.Length;
        }
        return joined;
    }

    private static byte[] U32(uint value) => new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    };

    private static byte[] I32(int value) => U32((uint)value);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static uint ReadU32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));

    /// <summary>ID3v2 的 32 位 syncsafe 长度：每字节只用低 7 位。</summary>
    private static int SyncSafe(byte[] bytes, int at) =>
        (bytes[at] << 21) | (bytes[at + 1] << 14) | (bytes[at + 2] << 7) | bytes[at + 3];

    /// <summary>一张够小的假 JPEG：只看文件头就够，AudioTags 不解析图片内容。</summary>
    private static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    /// <summary>
    /// 大文件那一路：偏移表是 co64（64 位）而不是 stco（32 位）。补偏移在 AudioTags 里是两条
    /// 不同分支，所以得单独走一遍 —— 只测 stco 等于放过一半。
    /// </summary>
    private static void Co64Case(string dir)
    {
        var payload = new byte[32];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(0x10 + i);

        var probe = BuildM4a(payload, 0, 0, useCo64: true);
        var oldMdat = Find(Boxes(probe, 0, probe.Length), "mdat")
            ?? throw new InvalidOperationException("合成的 co64 m4a 里没有 mdat");

        var file = BuildM4a(payload, oldMdat.Body, oldMdat.Body + 16, useCo64: true);
        var oldEntries = Co64Entries(file) ?? throw new InvalidOperationException("合成的 m4a 里没有 co64");
        var path = Path.Combine(dir, "sample64.m4a");
        File.WriteAllBytes(path, file);

        CheckEqual("co64 的 m4a 认得出来", "m4a", AudioTags.DetectFormat(path));
        Check("co64 的 m4a 写标签成功", AudioTags.Write(path, "标题", "作者", "专辑", null));

        var tagged = File.ReadAllBytes(path);
        var newMdat = Find(Boxes(tagged, 0, tagged.Length), "mdat")
            ?? throw new InvalidOperationException("写完标签后 mdat 不见了");
        var delta = newMdat.Start - oldMdat.Start;

        Check("co64 版：moov 也长大了（delta > 0）", delta > 0, "delta=" + delta);
        Check("co64 版：音频载荷逐字节没变",
            tagged.AsSpan(newMdat.Body, payload.Length).SequenceEqual(payload));
        Check("co64 版：还是 co64（没被换成 stco）",
            StcoEntries(tagged) is null && Co64Entries(tagged) is not null);

        var entries = Co64Entries(tagged) ?? new List<long>();
        CheckEqual("co64 版：条目数没变", 2, entries.Count);
        Check("co64 版：第 1 项后移了 delta", entries.Count > 0 && entries[0] == oldEntries[0] + delta,
            "旧 " + (oldEntries.Count > 0 ? oldEntries[0].ToString() : "?") + " → 新 " +
            (entries.Count > 0 ? entries[0].ToString() : "?") + "，delta " + delta);
        Check("co64 版：第 2 项后移了 delta", entries.Count > 1 && entries[1] == oldEntries[1] + delta);
    }

    /// <summary>读 co64 的 64 位偏移表。</summary>
    private static List<long>? Co64Entries(byte[] file)
    {
        var co64 = Descend(file, "moov", "trak", "mdia", "minf", "stbl", "co64");
        if (co64 is null) return null;

        var count = (int)ReadU32(file, co64.Value.Body + 4);
        var entries = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var at = co64.Value.Body + 8 + i * 8;
            entries.Add(((long)ReadU32(file, at) << 32) | ReadU32(file, at + 4));
        }
        return entries;
    }

    // ---- 自更新：校验 + 静默安装参数（只在临时目录里造现场，不碰真实安装目录） ----

    /// <summary>
    /// 更新链路现在是「下安装器 exe → 校验 sha256 → 静默跑它」。不依赖网络就能验的有两处：
    /// sha256 判定，以及静默参数（少了 /SILENT，用户面前会弹一个安装向导；少了
    /// /CLOSEAPPLICATIONS，正在运行的自己被占着，装不进去）。
    /// 「这份是不是安装器装的」要真装一次才知道，不在这测。
    /// </summary>
    private static void UpdateFlowShape()
    {
        Console.WriteLine("自更新（校验 / 静默安装参数）");

        var dir = Path.Combine(Path.GetTempPath(), "jicun-selftest-update-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            VerifyCase(dir);
            SetupShape();
        }
        catch (Exception ex)
        {
            Check("自更新自检能跑完", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录，删不掉就算了 */ }
        }
    }

    private static void VerifyCase(string dir)
    {
        var file = Path.Combine(dir, "payload.bin");
        var bytes = new byte[64];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7);
        File.WriteAllBytes(file, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        Check("哈希对得上就通过", UpdateService.VerifyAsync(file, hash).GetAwaiter().GetResult());
        Check("大写哈希也认", UpdateService.VerifyAsync(file, hash.ToUpperInvariant()).GetAwaiter().GetResult());
        Check("哈希不对就拒绝", !UpdateService.VerifyAsync(file, new string('a', 64)).GetAwaiter().GetResult());
        Check("哈希形状不对就拒绝", !UpdateService.VerifyAsync(file, "不是哈希").GetAwaiter().GetResult());
        Check("文件不在就拒绝", !UpdateService.VerifyAsync(Path.Combine(dir, "nope.bin"), hash).GetAwaiter().GetResult());
    }

    /// <summary>
    /// 静默安装器的参数：必须真静默（/SILENT）、别弹框（/SUPPRESSMSGBOXES）、别重启系统
    /// （/NORESTART），并且让安装器自己去处理文件占用（/CLOSEAPPLICATIONS）—— 少了它，
    /// 正在运行的自己还占着 exe，装不进去。
    /// 另外验一下「目录判等」：那份判断决定绿色版会不会被误判成安装版（跑安装器等于凭空多一份）。
    /// </summary>
    private static void SetupShape()
    {
        var args = UpdateService.SetupArguments;

        Check("/SILENT（静默，不弹向导）", args.Contains("/SILENT"));
        Check("/SUPPRESSMSGBOXES（后台更新别被弹框卡住）", args.Contains("/SUPPRESSMSGBOXES"));
        Check("/NORESTART（不重启系统）", args.Contains("/NORESTART"));
        Check("/CLOSEAPPLICATIONS（自己还占着文件也装得下去）", args.Contains("/CLOSEAPPLICATIONS"));
        Check("不传 /DIR（交给安装器认上次装到哪儿）",
            args.All(a => !a.StartsWith("/DIR", StringComparison.OrdinalIgnoreCase)));

        // 安装器写的 InstallLocation 带尾部反斜杠，大小写也不保证 —— 归一再比
        Check("尾部分隔符不影响判等", UpdateService.IsSameDir(@"C:\a\b\", @"C:\a\b"));
        Check("大小写不影响判等", UpdateService.IsSameDir(@"C:\A\B", @"c:\a\b"));
        Check("不同目录不判等", !UpdateService.IsSameDir(@"C:\a\b", @"C:\a\bc"));
        Check("空串一律不判等", !UpdateService.IsSameDir("", "") && !UpdateService.IsSameDir("", @"C:\a"));
    }

    // ---- 网络失败的中文文案（界面上不该出现 .NET 的英文原文） ----

    private static void TransportMessages()
    {
        Console.WriteLine("网络失败的文案");

        var tls = new HttpRequestException("boom", new AuthenticationException("ssl"));
        var refused = new HttpRequestException("boom", new SocketException(10061));

        Check("TLS 失败 → 中文：安全连接建立失败",
            ParseService.DescribeTransport(tls).StartsWith("安全连接建立失败", StringComparison.Ordinal));
        Check("连不上 → 中文：连不上服务器",
            ParseService.DescribeTransport(refused).StartsWith("连不上服务器", StringComparison.Ordinal));
        Check("读写中断 → 中文：网络读写中断",
            ParseService.DescribeTransport(new IOException("reset")).StartsWith("网络读写中断", StringComparison.Ordinal));
        Check("不认识的异常 → 兜底中文",
            ParseService.DescribeTransport(new InvalidOperationException("x")).StartsWith("网络连接失败", StringComparison.Ordinal));
        Check("挖到内层病因（没把外层那句英文抄出来）",
            !ParseService.DescribeTransport(tls).Contains("boom", StringComparison.Ordinal));

        var wrapped = new ParseException("中文话", ParseFailure.Transport, tls);
        Check("原始异常挂在内层（排障还捞得到）", ReferenceEquals(wrapped.InnerException, tls));
    }

    // ---- 更新说明的迷你 Markdown ----

    private static void MarkdownShape()
    {
        Console.WriteLine("更新说明的 Markdown 解析");

        var heading = Markdown.Parse("# 标题");
        CheckEqual("标题成了一个块", 1, heading.Count);
        CheckEqual("# 是一级", 1, heading.Count > 0 ? heading[0].Heading : -1);
        Check("标题里没有 # 号", heading.Count > 0 && heading[0].Spans.All(s => !s.Text.Contains('#')));

        var styled = Markdown.Parse("**粗体** 普通 *斜* `码` ==亮==").SelectMany(b => b.Spans).ToList();
        Check("加粗切出来了", styled.Any(s => s.Style == MarkdownStyle.Bold && s.Text == "粗体"));
        Check("斜体切出来了", styled.Any(s => s.Style == MarkdownStyle.Italic && s.Text == "斜"));
        Check("代码切出来了", styled.Any(s => s.Style == MarkdownStyle.Code && s.Text == "码"));
        Check("高亮切出来了", styled.Any(s => s.Style == MarkdownStyle.Highlight && s.Text == "亮"));
        Check("普通文字不带样式", styled.Any(s => s.Style == MarkdownStyle.None && s.Text.Contains("普通")));

        var nested = Markdown.Parse("**加粗里有 `代码` 的那种**").SelectMany(b => b.Spans).ToList();
        Check("样式能叠加（加粗 + 代码）",
            nested.Any(s => s.Style == (MarkdownStyle.Bold | MarkdownStyle.Code) && s.Text == "代码"));

        var link = Markdown.Parse("看 [这里](https://example.com) 吧").SelectMany(b => b.Spans).ToList();
        Check("链接认出来了", link.Any(s => s.Link == "https://example.com" && s.Text == "这里"));
        Check("非 http(s) 链接不放行",
            !Markdown.Parse("[坏](file:///c:/x)").SelectMany(b => b.Spans).Any(s => s.Link is not null));

        var lists = Markdown.Parse("- 甲\n- 乙\n1. 丙");
        CheckEqual("无序列表两项", 2, lists.Count(b => b.Bullet));
        Check("有序列表带序号",
            lists.Any(b => b.Number == "1." && b.Spans.Any(s => s.Text == "丙")));

        var rule = Markdown.Parse("上面\n\n---\n\n下面");
        CheckEqual("分隔线算一个块", 1, rule.Count(b => b.Rule));
        Check("分隔线不产生文字块", rule.Count(b => !b.Rule) == 2);

        var oneLine = Markdown.Parse("<div align=\"center\">居中一句</div>");
        Check("同一行开合的居中也认", oneLine.Count == 1 && oneLine[0].Align == MarkdownAlign.Center);

        var block = Markdown.Parse("<div align=\"center\">\n第一行\n第二行\n</div>\n普通一行");
        CheckEqual("居中块里两行都居中", 2, block.Count(b => b.Align == MarkdownAlign.Center));
        Check("居中块结束后回到左对齐",
            block.Any(b => b.Align == MarkdownAlign.Left && b.Spans.Any(s => s.Text.Contains("普通"))));

        var hlOnly = Markdown.HighlightRanges(Markdown.Parse("==亮==")[0]);
        Check("高亮区间：整行就是它", hlOnly.Count == 1 && hlOnly[0] == (0, 1));

        var hlMiddle = Markdown.HighlightRanges(Markdown.Parse("ab ==亮== cd")[0]);
        Check("高亮区间：算上前面的偏移", hlMiddle.Count == 1 && hlMiddle[0] == (3, 1));

        var hlBullet = Markdown.HighlightRanges(Markdown.Parse("- 前 ==亮==")[0]);
        Check("高亮区间：无序符号算进去", hlBullet.Count == 1 && hlBullet[0] == (4, 1));

        var hlNumber = Markdown.HighlightRanges(Markdown.Parse("1. 前 ==亮==")[0]);
        Check("高亮区间：有序序号算进去", hlNumber.Count == 1 && hlNumber[0] == (5, 1));

        var hlTwo = Markdown.HighlightRanges(Markdown.Parse("==甲== 和 ==乙==")[0]);
        Check("高亮区间：两段都标", hlTwo.Count == 2 && hlTwo[0] == (0, 1) && hlTwo[1] == (4, 1));

        Check("高亮区间：没标记就是空的", Markdown.HighlightRanges(Markdown.Parse("没高亮")[0]).Count == 0);

        // ---- GitHub 上常见的那几样 ----

        var underscore = Markdown.Parse("__粗__ 和 _斜_").SelectMany(b => b.Spans).ToList();
        Check("__加粗__ 认", underscore.Any(s => s.Style == MarkdownStyle.Bold && s.Text == "粗"));
        Check("_斜体_ 认", underscore.Any(s => s.Style == MarkdownStyle.Italic && s.Text == "斜"));
        Check("词里的下划线不当斜体（file_name_x）",
            Markdown.Parse("file_name_x").SelectMany(b => b.Spans).All(s => s.Style == MarkdownStyle.None));

        var strike = Markdown.Parse("~~删掉~~").SelectMany(b => b.Spans).ToList();
        Check("~~删除线~~ 认", strike.Any(s => s.Style == MarkdownStyle.Strike && s.Text == "删掉"));

        var bare = Markdown.Parse("见 https://example.com/a, 后面").SelectMany(b => b.Spans).ToList();
        Check("裸链接自动成链接（尾巴逗号不吞）",
            bare.Any(s => s.Link == "https://example.com/a"));
        Check("裸链接后面的普通文字还在", bare.Any(s => s.Link is null && s.Text.Contains("后面")));

        var titled = Markdown.Parse("[文字](https://example.com \"标题\")").SelectMany(b => b.Spans).ToList();
        Check("链接带 \"标题\" 时只取地址", titled.Any(s => s.Link == "https://example.com" && s.Text == "文字"));

        var quoted = Markdown.Parse("> 甲\n> 乙\n\n尾巴");
        var quoteBlock = quoted.FirstOrDefault(b => b.Quote);
        Check("连续引用合成一段", quoted.Count(b => b.Quote) == 1);
        Check("引用里的换行保留", quoteBlock is not null && quoteBlock.Spans.Any(s => s.Text == "甲\n乙"));
        Check("空行结束引用", quoted.Any(b => !b.Quote && b.Spans.Any(s => s.Text.Contains("尾巴"))));

        var fenced = Markdown.Parse("```csharp\nvar a = **1**;\n```");
        Check("围栏代码块成一个块", fenced.Count == 1 && fenced[0].Code);
        Check("代码块里原样保留（** 不解析）", fenced[0].CodeText == "var a = **1**;");
        Check("波浪围栏也认", Markdown.Parse("~~~\nx\n~~~")[0].Code);
        Check("没闭合的代码块吃到末尾", Markdown.Parse("```\nx\ny")[0].CodeText == "x\ny");

        var indented = Markdown.Parse("- 甲\n  - 乙\n    - 丙");
        Check("列表缩进：三级分别是 0/1/2",
            indented.Count == 3 && indented[0].Indent == 0 && indented[1].Indent == 1 && indented[2].Indent == 2);

        var tasks = Markdown.Parse("- [ ] 待办\n- [x] 已办");
        Check("未勾选任务", tasks[0].Task == MarkdownTask.Open && tasks[0].Spans.Any(s => s.Text == "待办"));
        Check("已勾选任务", tasks[1].Task == MarkdownTask.Done && tasks[1].Spans.Any(s => s.Text == "已办"));

        var taskHl = Markdown.HighlightRanges(Markdown.Parse("- [ ] 前 ==亮==")[0]);
        Check("高亮区间：任务符号也算进去", taskHl.Count == 1 && taskHl[0] == (4, 1));

        var table = Markdown.Parse("| 名称 | 值 |\n|---|---|\n| 甲 | 1 |\n| 乙 | 2 |");
        Check("表格成了一个块", table.Count == 1 && table[0].Table.Count == 3);
        Check("表格列数与表头一致", table[0].Table.All(r => r.Length == 2));
        Check("表头与数据都在", table[0].Table[0][0] == "名称" && table[0].Table[2][1] == "2");
        Check("表格后面照常解析", Markdown.Parse("| a |\n|---|\n| 1 |\n尾巴").Any(b => b.Spans.Any(s => s.Text.Contains("尾巴"))));
        Check("表格缺格会补齐", Markdown.Parse("| a | b |\n|---|---|\n| 1 |")[0].Table[1][1] == "");

        Check("标题收尾的 # 不算文字",
            Markdown.Parse("# 标题 #").SelectMany(b => b.Spans).Any(s => s.Text == "标题"));
        Check("<br> 拆成两段", Markdown.Parse("甲<br>乙").Count == 2);
        Check("<br/> 也认", Markdown.Parse("甲<br/>乙").Count == 2);
        Check("其它标签不碰（<b>甲</b> 原样）",
            Markdown.Parse("<b>甲</b>").Count == 1);
        Check("*** 也是分隔线", Markdown.Parse("***").Count == 1 && Markdown.Parse("***")[0].Rule);
        Check("___ 也是分隔线", Markdown.Parse("___")[0].Rule);

        // 引用 / 代码块 / 表格都不该被列表符号或高亮解析带偏
        Check("引用行不会被当成普通块", Markdown.Parse("> 甲").Count == 1 && Markdown.Parse("> 甲")[0].Quote);
        Check("代码块里的 == 不解析", Markdown.Parse("```\n==x==\n```")[0].CodeText == "==x==");
        Check("未闭合的标记当普通文字",
            Markdown.Parse("**没闭合").SelectMany(b => b.Spans).Any(s => s.Text.Contains("**")));
        Check("空说明解析出 0 个块", Markdown.Parse("").Count == 0);
        Check("只有空白的说明也解析出 0 个块", Markdown.Parse("  \n\n  ").Count == 0);
    }

    // ---- 更新来源：就一次 releases/latest 应答（用本地文件跑，不联网） ----

    /// <summary>
    /// 版本、说明、装哪个包、包的 sha256 —— 现在全在那一次应答里，所以这里把「同一份 JSON
    /// 能翻出什么」逐条钉死：tag 当版本号、body 当说明、按架构挑安装器附件、digest 当校验值，
    /// 以及没有安装器附件时（公告）要认得出「没得自动装」。
    /// </summary>
    private static void UpdateSources()
    {
        Console.WriteLine("更新来源（releases/latest 的应答）");

        var dir = Path.Combine(Path.GetTempPath(), "jicun-selftest-update-src-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "release.json");

            const string hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var setupName = UpdateService.SetupAssetName("9.9.9");

            // 一份正常应答：正文（椭圆那块）+ 安装器附件（带 digest）+ 绿色包
            File.WriteAllText(path, $$"""
            {
              "tag_name": "v9.9.9",
              "name": "即存 9.9.9",
              "body": "# 标题\n**加粗**",
              "prerelease": false,
              "draft": false,
              "assets": [
                { "name": "{{setupName}}", "size": 123,
                  "browser_download_url": "https://github.com/dhvbjvvb/jicun-desktop/releases/download/v9.9.9/{{setupName}}",
                  "digest": "sha256:{{hex}}" },
                { "name": "Jicun-win-x64.zip", "size": 456,
                  "browser_download_url": "https://github.com/dhvbjvvb/jicun-desktop/releases/download/v9.9.9/Jicun-win-x64.zip",
                  "digest": "sha256:{{hex}}" }
              ]
            }
            """);

            Environment.SetEnvironmentVariable("JICUN_RELEASE_API", path);
            var release = UpdateService.FetchLatestAsync().GetAwaiter().GetResult();

            CheckEqual("本地文件就当接口应答用（不联网）", "9.9.9", release?.VersionText ?? "(null)");
            Check("tag 解得出 9.9.9", release?.Version == new Version(9, 9, 9));
            CheckEqual("说明取自正文（就是椭圆那块）", "# 标题\n**加粗**", release?.Notes ?? "(null)");
            CheckEqual("挑到当前架构的安装器附件", setupName, release?.Installer?.Name ?? "(null)");
            CheckEqual("sha256 取自附件自带的 digest", hex, release?.Installer?.Sha256 ?? "(null)");
            Check("发布页地址按 tag 拼",
                release?.TagUrl.EndsWith("/releases/tag/v9.9.9", StringComparison.Ordinal) == true);

            // 正文是空白 → Notes 为 null，弹窗显示占位
            File.WriteAllText(path, """{ "tag_name": "v9.9.9", "body": "   ", "assets": [] }""");
            Check("正文空白 → Notes 是 null（弹窗显示占位）",
                UpdateService.FetchLatestAsync().GetAwaiter().GetResult()?.Notes is null);

            // 没有安装器附件 = 公告：版本照样认，但没得自动装
            File.WriteAllText(path, """{ "tag_name": "v9.9.9", "body": "公告", "assets": [] }""");
            var announcement = UpdateService.FetchLatestAsync().GetAwaiter().GetResult();
            Check("公告（没带安装包）也认得出新版本", announcement?.Version == new Version(9, 9, 9));
            Check("公告没有可自动装的安装器", announcement?.Installer is null);

            // 有安装器但 digest 形状不对 → 不认（镜像站是第三方，没哈希就不装）
            File.WriteAllText(path, $$"""
            { "tag_name": "v9.9.9", "body": "x",
              "assets": [ { "name": "{{setupName}}", "size": 1,
                            "browser_download_url": "https://example.com/a.exe", "digest": "sha256:不是哈希" } ] }
            """);
            Check("digest 形状不对 → 不认这个安装器",
                UpdateService.FetchLatestAsync().GetAwaiter().GetResult()?.Installer is null);

            // 别的架构的安装器不算数（跑的是哪个架构，只认那个名字）
            var otherArch = UpdateService.Rid == "win-x64" ? "win-arm64" : "win-x64";
            File.WriteAllText(path, $$"""
            { "tag_name": "v9.9.9", "body": "x",
              "assets": [ { "name": "Jicun-Setup-9.9.9-{{otherArch}}.exe", "size": 1,
                            "browser_download_url": "https://example.com/a.exe", "digest": "sha256:{{hex}}" } ] }
            """);
            Check("只认当前架构的安装器附件",
                UpdateService.FetchLatestAsync().GetAwaiter().GetResult()?.Installer is null);

            // 草稿 / 预发布都不当正式版
            File.WriteAllText(path, """{ "tag_name": "v9.9.9", "draft": true, "body": "x", "assets": [] }""");
            Check("草稿不认", UpdateService.FetchLatestAsync().GetAwaiter().GetResult() is null);

            File.WriteAllText(path, """{ "tag_name": "v9.9.9", "prerelease": true, "body": "x", "assets": [] }""");
            Check("预发布不当正式版", UpdateService.FetchLatestAsync().GetAwaiter().GetResult() is null);

            // 仓库还没有 Release（api 回 404 的应答）、坏 JSON、文件不存在：都返回 null，不抛
            File.WriteAllText(path, """{ "message": "Not Found" }""");
            Check("仓库还没发过 Release（404 那种应答）→ 返回 null，不抛",
                UpdateService.FetchLatestAsync().GetAwaiter().GetResult() is null);

            File.WriteAllText(path, "不是 JSON");
            Check("应答不是 JSON → 返回 null，不抛",
                UpdateService.FetchLatestAsync().GetAwaiter().GetResult() is null);

            Environment.SetEnvironmentVariable("JICUN_RELEASE_API", Path.Combine(dir, "nope.json"));
            Check("指到不存在的文件 → 返回 null，不抛",
                UpdateService.FetchLatestAsync().GetAwaiter().GetResult() is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JICUN_RELEASE_API", null);
            try { Directory.Delete(dir, true); } catch { /* 临时目录 */ }
        }
    }
    // ---- 相对地址归一化（服务端兜底那条路会下发 / 开头的地址） ----

    /// <summary>
    /// 兜底那条路的应答里地址可能是 <c>/xx/a.jpg</c> 这种相对写法。不补域名：缩略图静默空白、
    /// 下载报「地址解析失败」、点预览则抛 <c>UriFormatException</c> 把进程带走 ——
    /// 同一条应答里音频补了、视频没补，差别就在这儿。
    /// </summary>
    private static void RelativeUrls()
    {
        Console.WriteLine("相对地址归一化（兜底那条路的应答）");

        using var doc = JsonDocument.Parse("""
            {
              "title": "标题",
              "cover_url": "/c/cover.jpg",
              "video_url": "/v/main.mp4",
              "audio_url": "/a/music.mp3",
              "image_list": ["/i/1.jpg", { "url": "/i/2.jpg" }],
              "live_photo_list": [{ "live_photo_url": "/l/1.mp4", "url": "/l/1.jpg" }],
              "video_list": [{ "url": "/v/list.mp4", "cover_url": "/v/list.jpg",
                               "qualities": [{ "url": "/v/1080.mp4", "label": "1080P" }] }]
            }
            """);

        var r = ParseResult.FromJson(doc.RootElement);
        var videos = r.Videos.Where(v => v.Label != ParseResult.LivePhotoLabel).ToList();

        Check("封面补齐了域名", IsHttp(r.CoverUrl), r.CoverUrl);
        Check("主视频补齐了域名", IsHttp(r.VideoUrl), r.VideoUrl);
        Check("音频补齐了域名", IsHttp(r.AudioUrl), r.AudioUrl);
        Check("图集两张都补齐了", r.ImageUrls.Count == 2 && r.ImageUrls.All(IsHttp));
        Check("video_list 的地址补齐了", videos.Count > 0 && videos.All(v => IsHttp(v.Url)));
        Check("video_list 的封面补齐了", videos.Count > 0 && videos.All(v => IsHttp(v.CoverUrl)));
        Check("实况图补齐了域名",
            r.Videos.Any(v => v.Label == ParseResult.LivePhotoLabel && IsHttp(v.Url)));

        // Absolute 只补「以 / 开头」的，本来就绝对的不能被改坏
        using var absolute = JsonDocument.Parse("""{ "video_url": "https://cdn.example.com/a.mp4" }""");
        CheckEqual("本来就是绝对地址的原样保留", "https://cdn.example.com/a.mp4",
            ParseResult.FromJson(absolute.RootElement).VideoUrl);

        // 预览按钮：地址不可用就别让它点得动（点了 new Uri 会崩）
        Check("相对地址的媒体不给预览",
            !new MediaItem { Kind = MediaKind.Video, Url = "/v/a.mp4" }.CanPreview);
        Check("空地址不给预览", !new MediaItem { Kind = MediaKind.Image, Url = "" }.CanPreview);
        Check("绝对地址的媒体可以预览",
            new MediaItem { Kind = MediaKind.Video, Url = "https://cdn/a.mp4" }.CanPreview);
    }

    private static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // ---- 清单里的版本号进路径之前必须收敛 ----

    /// <summary>
    /// 清单是远端字符串。staging 目录名直接拿它拼的话，<c>1.0.1-..\..\..\Temp</c> 能同时过
    /// ParseVersion 的截断和 IsUsable 的形状校验，然后把「递归删除 + 解压 + 起进程」指到
    /// %LOCALAPPDATA%\Jicun\update 外面去。
    /// </summary>
    private static void VersionPath()
    {
        Console.WriteLine("版本号进路径（清单来自网络）");

        CheckEqual("正常版本原样", "1.0.1", UpdateService.PathSafeVersion("1.0.1"));
        CheckEqual("带 v 前缀也能收敛", "1.0.1", UpdateService.PathSafeVersion("v1.0.1"));
        CheckEqual("预发布后缀被切掉", "1.0.1", UpdateService.PathSafeVersion("1.0.1-beta.2"));

        var evil = UpdateService.PathSafeVersion(@"1.0.1-..\..\..\Temp");
        CheckEqual("穿越写法收敛成纯版本号", "1.0.1", evil);
        Check("收敛结果里没有分隔符 / 上一级",
            !evil.Contains('\\') && !evil.Contains('/') && !evil.Contains(".."));
        Check("解不出来的版本号直接拒绝", Throws(() => UpdateService.PathSafeVersion(@"..\..\Temp")));
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    // ---- 媒体探测一次最多读多少 ----

    /// <summary>
    /// 上限写死成头部窗口（1MB）时，尾部那次请求只能读回「倒数 2MB 的前一半」，正好跳过压在
    /// 文件尾的 moov —— 而「moov 在文件尾」正是那个类要处理的情况，等于死在常量上。
    /// </summary>
    private static void ProbeBudget()
    {
        Console.WriteLine("媒体探测的读取预算");

        const int head = 1 << 20;
        const int tail = 2 << 20;

        CheckEqual("头部窗口读满", head, MediaProbe.ReadBudget(0, head - 1));
        CheckEqual("尾部窗口也要读满", tail, MediaProbe.ReadBudget(0, tail - 1));
        CheckEqual("服务端忽略 Range 时也不多读", tail, MediaProbe.ReadBudget(0, 300L * 1024 * 1024));
    }

    // ---- 断言 ----

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            Console.WriteLine("  ok   " + name);
            return;
        }

        _failed++;
        Console.WriteLine("  FAIL " + name + (detail is null ? "" : "  —— " + detail));
    }

    private static void CheckEqual<T>(string name, T expected, T actual) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual),
            "期望 " + (Convert.ToString(expected) ?? "(null)") + "，实际 " + (Convert.ToString(actual) ?? "(null)"));
}
