using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundMaster.Core.Project;

/// <summary>
/// Nội dung file project <c>.smx</c> (JSON). Không chứa âm thanh: mỗi track chỉ tham chiếu tới file gốc
/// (đường dẫn tương đối so với file project, kèm đường dẫn đầy đủ dự phòng) hoặc stem demo tổng hợp.
/// </summary>
public sealed class ProjectDocument
{
    public const int CurrentVersion = 1;
    public const string Extension = ".smx";

    public int Version { get; set; } = CurrentVersion;
    public string SongName { get; set; } = "";
    public double Bpm { get; set; } = 120;
    public float MasterGainDb { get; set; }
    public bool LimiterEnabled { get; set; } = true;
    public bool LoopEnabled { get; set; }
    public double Speed { get; set; } = 1;
    public List<ProjectTrack> Tracks { get; set; } = [];
}

public sealed class ProjectTrack
{
    public string Name { get; set; } = "";

    /// <summary>File gốc: tương đối so với thư mục chứa project nếu được, ngược lại là đường dẫn đầy đủ.</summary>
    public string? Source { get; set; }

    /// <summary>Đường dẫn đầy đủ lúc lưu (dự phòng khi project bị di chuyển).</summary>
    public string? SourceFull { get; set; }

    /// <summary>Khóa stem demo tổng hợp (không có file).</summary>
    public string? Demo { get; set; }

    public string ColorHex { get; set; } = "#888888";
    public float GainDb { get; set; }
    public float Pan { get; set; }
    public bool Muted { get; set; }
    public bool Soloed { get; set; }
    public bool Armed { get; set; }
    public bool Locked { get; set; }
    public List<ProjectRegion> Regions { get; set; } = [];
}

public sealed class ProjectRegion
{
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>Tên hiệu ứng: None, FadeIn, FadeOut, Loop, Silence, Reverse.</summary>
    public string Effect { get; set; } = "None";

    /// <summary>Với Loop: tổng số lần nghe đoạn (0 = lặp mãi).</summary>
    public int LoopPlays { get; set; } = 4;
}

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Chuỗi JSON gọn, ổn định: dùng làm "khóa" để so sánh hai trạng thái project.</summary>
    public static string Key(ProjectDocument doc) => JsonSerializer.Serialize(doc, CompactOptions);

    public static string ToJson(ProjectDocument doc) => JsonSerializer.Serialize(doc, Options);

    public static ProjectDocument FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<ProjectDocument>(json, Options)
            ?? throw new InvalidDataException("File project rỗng.");
        if (doc.Version > ProjectDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Project được lưu bằng phiên bản mới hơn (v{doc.Version}); app này chỉ đọc đến v{ProjectDocument.CurrentVersion}.");
        }

        return doc;
    }

    /// <summary>Lưu an toàn: ghi ra file tạm rồi thay thế, để mất điện giữa chừng không làm hỏng file cũ.</summary>
    public static void Save(string path, ProjectDocument doc)
    {
        path = Path.GetFullPath(path);
        string temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(doc));
        File.Move(temp, path, overwrite: true);
    }

    public static ProjectDocument Load(string path)
    {
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"File project không hợp lệ: {ex.Message}", ex);
        }
    }

    /// <summary>Đường dẫn lưu cho một file nguồn: tương đối nếu cùng ổ đĩa, ngược lại đầy đủ.</summary>
    public static string MakeSource(string projectPath, string sourceFull)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
        bool sameRoot = string.Equals(Path.GetPathRoot(dir), Path.GetPathRoot(sourceFull),
            StringComparison.OrdinalIgnoreCase);
        return sameRoot ? Path.GetRelativePath(dir, sourceFull) : sourceFull;
    }

    /// <summary>Tìm file nguồn của một track: thử đường dẫn lưu, rồi đường dẫn đầy đủ; null nếu không còn file.</summary>
    public static string? ResolveSource(ProjectTrack track, string projectPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "";
        foreach (var candidate in new[] { track.Source, track.SourceFull })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full = Path.IsPathRooted(candidate) ? candidate : Path.GetFullPath(Path.Combine(dir, candidate));
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
