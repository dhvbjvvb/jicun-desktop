using System.Buffers.Binary;
using System.Text;

namespace Jicun.Desktop.Services;

/// <summary>
/// 就地给已下载的音频文件写标签（标题 / 作者 / 专辑 / 封面）。
///
/// 纯字节操作，只用 BCL：MP3 走 ID3v2.4，MP4 / M4A 走 <c>moov.udta.meta.ilst</c>。
/// 只认 <see cref="DetectFormat"/> 认得出来的容器，认不出就一个字节都不改 ——
/// 写坏一个用户刚下好的文件，比没有标签糟得多。
///
/// 和 Dart 版一样**不抛异常**：标签是装饰，下载本身不能因为它失败，出了岔子一律返回 false，
/// 让调用方照常把没标签的文件登记进媒体库。
///
/// 比 Dart 版少两样东西：没有歌词（<c>USLT</c> / <c>©lyr</c>，这个 C# API 不收歌词参数），
/// 也没有 FLAC 的 Vorbis comment —— Dart 参考实现里就没有。FLAC 只由 <see cref="DetectFormat"/>
/// 认出来，写入时不碰它。
/// </summary>
public static class AudioTags
{
    /// <summary>封面超过这个大小就不内嵌。</summary>
    /// <remarks>
    /// 封面是拿来做缩略图的，正常几百 KB 顶天（一张 375x375 的 jpg 只有几十 KB）。
    /// 几 MB 的"封面"要么是选错了文件，要么是把整张海报塞进来了 —— 那会让音频文件白白
    /// 胖一大截，而播放器多半还是不显示。
    /// </remarks>
    private const int MaxCoverBytes = 4 << 20;

    /// <summary>超过这个大小的音频不写标签。</summary>
    /// <remarks>
    /// MP4 那条路要把整个文件读进内存、再拼一份新的出来，峰值是文件大小的两倍。
    /// 歌曲都是几 MB，但上游给的"音频"也可能是整场录音 —— 那种情况下为了几个标签把
    /// 堆吃爆不值当，直接跳过。
    /// 16MB 而不是 64MB：这个上限就是**堆峰值的一半**，而写标签发生在"刚下完、预览
    /// 播放器还活着"的时刻，那时候堆里本来就有下载缓冲和播放器缓冲。
    /// </remarks>
    private const int MaxTagFileBytes = 16 << 20;

    /// <summary>就地把标签写进已下载的音频文件。返回 true 表示改写过，false 表示格式不认识、文件原样未动。</summary>
    public static bool Write(string path, string? title, string? artist, string? album, byte[]? cover)
    {
        // 空字段就是"没有" —— 一个标签都拿不到时别为了写标签去改文件。
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(album)
            && (cover is null || cover.Length == 0))
        {
            return false;
        }

        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            if (new FileInfo(path).Length > MaxTagFileBytes) return false;

            // 认不出的封面（或太大）当作没有，不阻断其余标签。
            var coverBytes = CoverBytes(cover);
            var source = File.ReadAllBytes(path);
            var tagged = DetectFromHead(source) switch
            {
                "mp3" => WriteId3Tag(source, title, artist, album, coverBytes),
                "m4a" => WriteMp4Tag(source, title, artist, album, coverBytes),
                _ => null, // flac 之类这里没实现写入的容器：一个字节都不改
            };
            if (tagged is null) return false;

            // 先写临时文件再改名：中途失败（磁盘满、进程被杀）留下的最坏是一个 .tagging
            // 垃圾文件，已经下好的那份还在原地。flushToDisk 是必须先落盘再改名 —— 否则
            // 改名成功而数据还在系统缓存里时断电，会留下一个长度对、内容空的文件。
            var temp = path + ".tagging";
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(tagged, 0, tagged.Length);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true); // 同卷上是替换式的原子改名
            }
            catch
            {
                TryDelete(temp); // 没搬成就别把临时文件留成垃圾；原文件从头到尾没动过
                throw;
            }
            return true;
        }
        catch
        {
            // 标签是装饰，下载本身不能因为它失败：文件头认不出、磁盘写不动，一律当成
            // "没写成"，原文件保持原样。
            return false;
        }
    }

    /// <summary>按文件头（不是扩展名）判格式："mp3" | "m4a" | "flac" | null。</summary>
    public static string? DetectFormat(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[16];
            var n = fs.Read(head, 0, head.Length);
            return DetectFromHead(head.AsSpan(0, n));
        }
        catch
        {
            return null; // 打不开、读不动就当不认识，调用方会原样跳过
        }
    }

    /// <summary>认文件头。只给 16 字节也够 —— 这几种容器的标识都在开头。</summary>
    private static string? DetectFromHead(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3') return "mp3";
        // 没有 ID3 头的老 MP3：11 位帧同步全 1（前 2 字节 0xFF 0xE?）。
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "mp3";
        if (head.Length >= 4 && head[0] == (byte)'f' && head[1] == (byte)'L' && head[2] == (byte)'a' && head[3] == (byte)'C') return "flac";
        // MP4 系：第 4 字节起是 'ftyp'。m4a / mp4 / mov 在字节层面是同一套结构，
        // 这套 API 只报一个 "m4a"。
        if (head.Length >= 12 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p') return "m4a";
        return null;
    }

    // -----------------------------------------------------------------------
    // 封面
    // -----------------------------------------------------------------------

    private enum ImageKind { None, Jpeg, Png }

    /// <summary>认得出的图片类型。</summary>
    /// <remarks>认不出返回 None —— 认不出就不写 <c>covr</c> / <c>APIC</c>，
    /// 写一个类型标错的封面比不写更糟。</remarks>
    private static ImageKind ImageKindOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ImageKind.Jpeg;
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G') return ImageKind.Png;
        return ImageKind.None;
    }

    /// <summary>挑出能用的封面字节；空、过大、认不出都当没有。</summary>
    private static byte[]? CoverBytes(byte[]? cover) =>
        cover is null || cover.Length == 0 || cover.Length > MaxCoverBytes || ImageKindOf(cover) == ImageKind.None
            ? null
            : cover;

    // -----------------------------------------------------------------------
    // MP4 / M4A
    // -----------------------------------------------------------------------

    // 盒子类型就是 4 个字节，这里按大端读成 uint 来比较。为什么不用字符串：© 系盒子的
    // 首字节是 0xA9，它既不是 ASCII 也不是合法的 UTF-8 单字节，来回转字符串会把它变成
    // 多字节，反而认不出原来的盒子。
    private static readonly uint Moov = Tag("moov");
    private static readonly uint Mdat = Tag("mdat");
    private static readonly uint Udta = Tag("udta");
    private static readonly uint Meta = Tag("meta");
    private static readonly uint Ilst = Tag("ilst");
    private static readonly uint Mvex = Tag("mvex");
    private static readonly uint Data = Tag("data");
    private static readonly uint Hdlr = Tag("hdlr");
    private static readonly uint Trak = Tag("trak");
    private static readonly uint Mdia = Tag("mdia");
    private static readonly uint Minf = Tag("minf");
    private static readonly uint Stbl = Tag("stbl");
    private static readonly uint Stco = Tag("stco");
    private static readonly uint Co64 = Tag("co64");
    private static readonly uint Mdir = Tag("mdir");
    private static readonly uint Nam = Tag("\u00A9nam"); // ©nam
    private static readonly uint Art = Tag("\u00A9ART"); // ©ART
    private static readonly uint Alb = Tag("\u00A9alb"); // ©alb
    private static readonly uint Covr = Tag("covr");

    private static uint Tag(string four) =>
        ((uint)four[0] << 24) | ((uint)four[1] << 16) | ((uint)four[2] << 8) | (uint)four[3];

    /// <summary>往 MP4 / M4A 里写 <c>moov.udta.meta.ilst</c>。写不了返回 null。</summary>
    /// <remarks>
    /// **麻烦在偏移**：moov 通常排在 mdat 前面（faststart —— 实测某条汽水音乐音频是
    /// <c>ftyp@0</c>、<c>moov@28</c>、<c>mdat@22794</c>），而 <c>stbl.stco</c> 里存的是每个
    /// chunk 在文件里的**绝对偏移**。ilst 一长大，mdat 整体后移，这些偏移全部失效 ——
    /// 不补的话文件头还认得、时长也读得出来，声音却是错位的。
    ///
    /// 所以这里做三件事：重建 moov（往 ilst 里追加盒子）、把新 moov 拼回文件、按 moov 长了
    /// 多少去改 stco / co64。mdat 排在 moov 前面时不用改（那种排布下 moov 长大影响不到
    /// mdat 的位置）。
    ///
    /// 分片 MP4（<c>moof</c>）不支持：那种结构下偏移是相对的，得另写一套。认不出原样返回。
    /// </remarks>
    private static byte[]? WriteMp4Tag(byte[] src, string? title, string? artist, string? album, byte[]? cover)
    {
        var top = Boxes(src, 0, src.Length);
        if (!TryFirst(top, Moov, out var moov) || !TryFirst(top, Mdat, out var mdat)) return null;

        var coverKind = cover is null ? ImageKind.None : ImageKindOf(cover);
        var items = new List<byte[]>();
        // 同名的旧项要去掉，不然一个 ilst 里挂着两份标题，播放器读哪份看运气。
        var owned = new HashSet<uint>();
        if (!string.IsNullOrEmpty(title)) { items.Add(TextItem(Nam, title)); owned.Add(Nam); }
        if (!string.IsNullOrEmpty(artist)) { items.Add(TextItem(Art, artist)); owned.Add(Art); }
        if (!string.IsNullOrEmpty(album)) { items.Add(TextItem(Alb, album)); owned.Add(Alb); }
        if (cover is not null && coverKind != ImageKind.None)
        {
            items.Add(CoverItem(cover, coverKind == ImageKind.Jpeg ? 13u : 14u));
            owned.Add(Covr);
        }
        if (items.Count == 0) return null;

        var moovKids = Boxes(src, moov.BodyStart, moov.End);
        // 分片 MP4（mvex，样本装在后面的 moof 里）：那种结构的偏移是相对的，这套
        // "把 moov 撑大再补 stco"的改法不适用。认出来就撒手，别把一个能播的文件改坏。
        if (TryFirst(moovKids, Mvex, out _)) return null;
        var hasUdta = TryFirst(moovKids, Udta, out var udta);

        // 现成的 meta 直接复用：它里面的 hdlr 是原文件带来的，比自造的更可能被这个播放器
        // 认。只有完全没有 meta 时才自己拼一个。
        var metaPrefix = MetaPrefix();
        var ilstBody = new List<byte>();
        if (hasUdta)
        {
            foreach (var meta in Boxes(src, udta.BodyStart, udta.End))
            {
                if (meta.Type != Meta) continue;
                // meta 是 FullBox：前 4 字节 version/flags 不是盒子，扫里面的盒子要跳过。
                if (!TryFirst(Boxes(src, meta.BodyStart + 4, meta.End), Ilst, out var ilst)) continue;
                foreach (var item in Boxes(src, ilst.BodyStart, ilst.End))
                {
                    if (owned.Contains(item.Type)) continue;
                    ilstBody.AddRange(src[item.Start..item.End]);
                }
                metaPrefix = src[meta.BodyStart..ilst.Start];
                break;
            }
        }
        foreach (var item in items) ilstBody.AddRange(item);

        var metaBox = MakeBox(Meta, Cat(metaPrefix, MakeBox(Ilst, ilstBody.ToArray())));

        var udtaBody = new List<byte>();
        var metaPlaced = false;
        if (hasUdta)
        {
            foreach (var kid in Boxes(src, udta.BodyStart, udta.End))
            {
                if (kid.Type == Meta)
                {
                    udtaBody.AddRange(metaBox);
                    metaPlaced = true;
                }
                else
                {
                    udtaBody.AddRange(src[kid.Start..kid.End]);
                }
            }
        }
        if (!metaPlaced) udtaBody.AddRange(metaBox);
        var udtaBox = MakeBox(Udta, udtaBody.ToArray());

        var moovBody = new List<byte>();
        var udtaPlaced = false;
        foreach (var kid in moovKids)
        {
            if (kid.Type == Udta)
            {
                moovBody.AddRange(udtaBox);
                udtaPlaced = true;
            }
            else
            {
                moovBody.AddRange(src[kid.Start..kid.End]);
            }
        }
        if (!udtaPlaced) moovBody.AddRange(udtaBox);
        var newMoov = MakeBox(Moov, moovBody.ToArray());

        var delta = newMoov.Length - moov.Size;
        // moov 在 mdat 前面才要补偏移。补之前先看会不会撑破 stco 的 32 位字段 —— 撑破了
        // 说明这个文件本来就该用 co64，不冒这个险，原样返回。
        var needsShift = delta != 0 && mdat.Start > moov.Start;
        if (needsShift && (long)mdat.Start + 8 + delta > 0xFFFFFFFFL) return null;

        var tagged = new byte[src.Length - moov.Size + newMoov.Length];
        Buffer.BlockCopy(src, 0, tagged, 0, moov.Start);
        Buffer.BlockCopy(newMoov, 0, tagged, moov.Start, newMoov.Length);
        Buffer.BlockCopy(src, moov.End, tagged, moov.Start + newMoov.Length, src.Length - moov.End);
        if (needsShift) ShiftChunkOffsets(tagged, moov.Start, newMoov.Length, delta);
        return tagged;
    }

    /// <summary>把新 moov 里所有 <c>stco</c> / <c>co64</c> 的 chunk 偏移整体加上 <paramref name="delta"/>。</summary>
    /// <remarks>
    /// 按盒子逐层走到 <c>trak.mdia.minf.stbl</c> —— 不能按固定偏移去改：udta 排在 trak
    /// 前面时，重建之后 stco 在 moov 里的相对位置是会变的，而这里是对**新**的字节走的，
    /// 所以顺序和长度怎么变都对得上。
    /// </remarks>
    private static void ShiftChunkOffsets(byte[] bytes, int moovStart, int moovLength, int delta)
    {
        var moovEnd = moovStart + moovLength;
        foreach (var trak in Boxes(bytes, moovStart + 8, moovEnd))
        {
            if (trak.Type != Trak) continue;
            foreach (var mdia in Boxes(bytes, trak.BodyStart, trak.End))
            {
                if (mdia.Type != Mdia) continue;
                foreach (var minf in Boxes(bytes, mdia.BodyStart, mdia.End))
                {
                    if (minf.Type != Minf) continue;
                    foreach (var stbl in Boxes(bytes, minf.BodyStart, minf.End))
                    {
                        if (stbl.Type != Stbl) continue;
                        foreach (var table in Boxes(bytes, stbl.BodyStart, stbl.End))
                        {
                            // stco / co64 都是 FullBox：4 字节 version/flags + 4 字节条数 + 条目。
                            if (table.Type == Stco)
                            {
                                var count = U32(bytes, table.BodyStart + 4);
                                for (var i = 0u; i < count; i++)
                                {
                                    var at = table.BodyStart + 8 + (int)i * 4;
                                    PutU32(bytes, at, U32(bytes, at) + (uint)delta);
                                }
                            }
                            else if (table.Type == Co64)
                            {
                                var count = U32(bytes, table.BodyStart + 4);
                                for (var i = 0u; i < count; i++)
                                {
                                    var at = table.BodyStart + 8 + (int)i * 8;
                                    PutU64(bytes, at, (ulong)((long)U64(bytes, at) + delta));
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>文本项：<c>©nam</c> 里套一个 <c>data</c>，类型 1 = UTF-8。</summary>
    private static byte[] TextItem(uint name, string text) =>
        MakeBox(name, MakeBox(Data, Mp4Data(1, Encoding.UTF8.GetBytes(text))));

    /// <summary>封面项：类型 13 = JPEG，14 = PNG。</summary>
    private static byte[] CoverItem(byte[] image, uint type) =>
        MakeBox(Covr, MakeBox(Data, Mp4Data(type, image)));

    /// <summary><c>data</c> 盒子的内容：4 字节类型（低 24 位有效）+ 4 字节 locale + 净荷。</summary>
    private static byte[] Mp4Data(uint dataType, byte[] payload)
    {
        var body = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), dataType);
        // 后 4 字节是 locale，固定 0。
        payload.CopyTo(body, 8);
        return body;
    }

    /// <summary>从头拼一个 <c>meta</c> 时用的开头：FullBox 的 4 字节 version/flags + 一个 hdlr。</summary>
    /// <remarks>
    /// hdlr 声明这是 iTunes 那套元数据（mdir）。少了它，有的播放器会把整块 ilst 当不认识
    /// 的东西跳过 —— 标签写了等于没写。
    /// </remarks>
    private static byte[] MetaPrefix()
    {
        // hdlr：4 字节 version/flags + 4 字节 pre_defined + 4 字节 handler_type +
        // 12 字节 reserved[3] + 1 字节空名字。除 handler_type 外全为 0。
        var hdlrBody = new byte[25];
        BinaryPrimitives.WriteUInt32BigEndian(hdlrBody.AsSpan(8, 4), Mdir);
        return Cat(new byte[4], MakeBox(Hdlr, hdlrBody));
    }

    // -----------------------------------------------------------------------
    // MP3 / ID3v2.4
    // -----------------------------------------------------------------------

    /// <summary>往 MP3 里写 ID3v2.4 标签。没有可写的内容时返回 null。</summary>
    /// <remarks>
    /// 整块旧标签**直接换掉**：ID3v2.3 和 v2.4 的帧长编码不一样（v2.4 才用 syncsafe），
    /// 把旧帧原样搬进新标签会把长度读错、整块标签报废。标签里那点编码器信息不值这个
    /// 风险 —— 标题、作者、封面都在这儿重新写一遍。
    ///
    /// 顺带把文件末尾的 ID3v1 删掉：那是固定 128 字节的老格式，很多播放器优先读它，
    /// 留着就会显示成旧标题（甚至和 v2.4 里的新标题打架）。
    /// </remarks>
    private static byte[]? WriteId3Tag(byte[] src, string? title, string? artist, string? album, byte[]? cover)
    {
        var frames = new List<byte[]>();
        if (!string.IsNullOrEmpty(title)) frames.Add(Id3Text("TIT2", title));
        if (!string.IsNullOrEmpty(artist)) frames.Add(Id3Text("TPE1", artist));
        if (!string.IsNullOrEmpty(album)) frames.Add(Id3Text("TALB", album));
        if (cover is not null && ImageKindOf(cover) != ImageKind.None) frames.Add(Id3Cover(cover));
        if (frames.Count == 0) return null;

        var audioStart = 0;
        if (HasId3V2(src))
        {
            // 头里的长度是 syncsafe 的，存不下超过 256MB 的标签。长度不合法（截断了、
            // 或者是个假头）就当没有标签 —— 照着它切会把真音频切掉一段。
            var end = 10 + SyncSafeAt(src, 6) + (((src[5] & 0x10) != 0) ? 10 : 0);
            if (end > 10 && end <= src.Length) audioStart = end;
        }
        var audioEnd = src.Length;
        if (audioEnd - audioStart >= 128)
        {
            var at = audioEnd - 128;
            if (src[at] == (byte)'T' && src[at + 1] == (byte)'A' && src[at + 2] == (byte)'G') audioEnd = at;
        }

        var body = new List<byte>();
        foreach (var frame in frames) body.AddRange(frame);

        var tagged = new byte[10 + body.Count + (audioEnd - audioStart)];
        tagged[0] = (byte)'I';
        tagged[1] = (byte)'D';
        tagged[2] = (byte)'3';
        tagged[3] = 0x04; // v2.4.0
        tagged[4] = 0x00;
        tagged[5] = 0x00; // flags：没有扩展头、没有 footer
        SyncSafeBytes(body.Count).CopyTo(tagged, 6);
        body.CopyTo(tagged, 10);
        Buffer.BlockCopy(src, audioStart, tagged, 10 + body.Count, audioEnd - audioStart);
        return tagged;
    }

    private static bool HasId3V2(byte[] src) =>
        src.Length >= 10 && src[0] == (byte)'I' && src[1] == (byte)'D' && src[2] == (byte)'3';

    /// <summary>文本帧：0x03 = UTF-8（v2.4 里是合法的，中文不用绕 UTF-16）。</summary>
    private static byte[] Id3Text(string id, string text) =>
        Id3Frame(id, Cat(new byte[] { 0x03 }, Encoding.UTF8.GetBytes(text)));

    /// <summary>封面。类型 0x03 = 正面封面。</summary>
    private static byte[] Id3Cover(byte[] image)
    {
        var mime = Encoding.ASCII.GetBytes(ImageKindOf(image) == ImageKind.Jpeg ? "image/jpeg" : "image/png");
        return Id3Frame("APIC", Cat(
            new byte[] { 0x03 }, // 编码
            mime,
            new byte[] { 0x00 }, // MIME 以 0 结尾
            new byte[] { 0x03 }, // 图片类型：正面封面
            new byte[] { 0x00 }, // 描述（空）
            image));
    }

    /// <summary>一个帧：4 字节 ID + 4 字节 syncsafe 长度（v2.4 的规定）+ 2 字节 flags + 帧体。</summary>
    private static byte[] Id3Frame(string id, byte[] body)
    {
        var frame = new byte[10 + body.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        SyncSafeBytes(body.Length).CopyTo(frame, 4);
        // frame[8..9] 是 flags，保持 0：标记压缩 / 加密 / 分组 / 只读的位一个都不设。
        body.CopyTo(frame, 10);
        return frame;
    }

    /// <summary>32 位 syncsafe：每字节只用低 7 位，最高位恒为 0。v2.4 的帧长和标签总长都用它。</summary>
    private static byte[] SyncSafeBytes(int value) => new byte[]
    {
        (byte)((value >> 21) & 0x7F),
        (byte)((value >> 14) & 0x7F),
        (byte)((value >> 7) & 0x7F),
        (byte)(value & 0x7F),
    };

    private static int SyncSafeAt(byte[] bytes, int at) =>
        (bytes[at] << 21) | (bytes[at + 1] << 14) | (bytes[at + 2] << 7) | bytes[at + 3];

    // -----------------------------------------------------------------------
    // MP4 盒子
    // -----------------------------------------------------------------------

    /// <summary>一个盒子在文件里的位置。<c>Type</c> 是那 4 个字节按大端读出来的 uint。</summary>
    private readonly struct Box
    {
        public Box(uint type, int start, int size)
        {
            Type = type;
            Start = start;
            Size = size;
        }

        public uint Type { get; }
        public int Start { get; }
        public int Size { get; }

        /// <summary>内容起点（跳过 8 字节的盒子头）。</summary>
        public int BodyStart => Start + 8;

        /// <summary>盒子结尾（不含）。</summary>
        public int End => Start + Size;
    }

    /// <summary>扫一层盒子。碰到长度不合法的就停下，不接着猜 —— 猜错会顺着垃圾数据一路读下去。</summary>
    private static List<Box> Boxes(byte[] bytes, int start, int end)
    {
        var boxes = new List<Box>();
        var pos = start;
        while (end - pos >= 8)
        {
            long size = U32(bytes, pos);
            var type = U32(bytes, pos + 4);
            if (size == 1)
            {
                // size == 1 表示真正的长度放在紧接着的 8 字节里（大盒子）。
                if (end - pos < 16) break;
                size = (long)U64(bytes, pos + 8);
            }
            else if (size == 0)
            {
                // 0 表示"一直到文件结尾"，只有最后一个盒子能这么写。
                size = end - pos;
            }
            if (size < 8 || pos + size > end) break;
            boxes.Add(new Box(type, pos, (int)size));
            pos += (int)size;
        }
        return boxes;
    }

    private static bool TryFirst(List<Box> boxes, uint type, out Box box)
    {
        foreach (var candidate in boxes)
        {
            if (candidate.Type == type)
            {
                box = candidate;
                return true;
            }
        }
        box = default;
        return false;
    }

    /// <summary>一个盒子：4 字节长度 + 4 字节类型 + 内容。</summary>
    private static byte[] MakeBox(uint type, ReadOnlySpan<byte> body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4, 4), type);
        body.CopyTo(box.AsSpan(8));
        return box;
    }

    /// <summary>把几段字节按顺序拼起来，对应 Dart 里的 <c>&lt;int&gt;[...a, ...b]</c>。</summary>
    private static byte[] Cat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts) total += part.Length;
        var joined = new byte[total];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(joined, at);
            at += part.Length;
        }
        return joined;
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));

    private static void PutU32(byte[] bytes, int at, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at, 4), value);

    private static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at, 8));

    private static void PutU64(byte[] bytes, int at, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at, 8), value);

    /// <summary>清理临时文件。删不掉就算了，别让清理动作再抛一次。</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 已经不在，或者被别人占着 */ }
    }
}
