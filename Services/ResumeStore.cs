using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jicun.Desktop.Models;

namespace Jicun.Desktop.Services;

/// <summary>一份没下完的下载。进程被杀、断电、重装之后靠它接着下。</summary>
public sealed class ResumeRecord
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Kind { get; set; } = nameof(MediaKind.Video);
    public long Total { get; set; }
    public bool SupportsRange { get; set; }

    [JsonIgnore] public string SinglePath => ResumeStore.PathFor(Id + ".part");

    public string PartPath(int index) => ResumeStore.PathFor(Id + "." + index);

    /// <summary>分片文件加起来已经落了多少字节。别存进 json —— 文件才是真话。</summary>
    [JsonIgnore]
    public long ReceivedOnDisk
    {
        get
        {
            long sum = 0;
            for (var i = 0; i < ResumeStore.Segments; i++)
            {
                var p = PartPath(i);
                if (File.Exists(p)) sum += new FileInfo(p).Length;
            }

            if (File.Exists(SinglePath)) sum += new FileInfo(SinglePath).Length;
            return sum;
        }
    }

    public MediaItem ToItem() => new()
    {
        Kind = Enum.TryParse<MediaKind>(Kind, out var k) ? k : MediaKind.Video,
        Url = Url,
        FileName = FileName,
    };
}

/// <summary>
/// 断点的落盘层。分片文件直接躺在 <c>%LocalAppData%\Jicun\incomplete\</c>，
/// 不删就是断点 —— 不需要另外记「下到哪儿了」，文件长度本身就是进度。
/// </summary>
public static class ResumeStore
{
    public const int Segments = 4;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jicun", "incomplete");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathFor(string name) => Path.Combine(Dir, name);

    /// <summary>同一个链接 + 同一个目标文件名 = 同一份断点。</summary>
    public static string IdFor(string url, string fileName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + fileName));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static ResumeRecord LoadOrCreate(string url, string fileName, string folder, MediaKind kind)
    {
        var id = IdFor(url, fileName);
        try
        {
            var path = PathFor(id + ".json");
            if (File.Exists(path))
            {
                var rec = JsonSerializer.Deserialize<ResumeRecord>(File.ReadAllText(path));
                if (rec is not null && rec.Url == url && rec.FileName == fileName)
                {
                    rec.Folder = folder; // 下载目录可能被改过，以这次为准
                    rec.Kind = kind.ToString();
                    return rec;
                }
            }
        }
        catch
        {
            // 记录读不动就当没有，从头下
        }

        return new ResumeRecord { Id = id, Url = url, FileName = fileName, Folder = folder, Kind = kind.ToString() };
    }

    public static void Save(ResumeRecord rec)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(rec.Id + ".json"), JsonSerializer.Serialize(rec, Json));
        }
        catch
        {
            // 记不上顶多丢断点，下载本身照跑
        }
    }

    public static void Drop(string id)
    {
        TryDelete(PathFor(id + ".json"));
        TryDelete(PathFor(id + ".part"));
        TryDelete(PathFor(id + ".merged")); // 旧版本的合并结果落在这儿，顺手清掉
        for (var i = 0; i < Segments; i++) TryDelete(PathFor(id + "." + i));
    }

    /// <summary>启动时把没下完的翻出来。</summary>
    public static List<ResumeRecord> Pending()
    {
        var list = new List<ResumeRecord>();
        try
        {
            if (!Directory.Exists(Dir)) return list;

            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<ResumeRecord>(File.ReadAllText(file));
                    if (rec is null || rec.Url.Length == 0) continue;

                    // 半片数据都没落下来就别留了，留着只会在列表里挂一个永远 0% 的项
                    if (rec.ReceivedOnDisk <= 0)
                    {
                        Drop(rec.Id);
                        continue;
                    }

                    list.Add(rec);
                }
                catch
                {
                    // 单条记录坏了不该拖垮整轮恢复
                }
            }
        }
        catch
        {
            // 目录读不动就没有待续传的
        }

        return list;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 占用中，下次再说 */ }
    }
}
