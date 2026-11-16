using System.Text.Json;

namespace SoundMaster.Core.Project;

/// <summary>Thông tin một bản tự lưu còn sót lại từ phiên trước.</summary>
/// <param name="ProjectFile">Đường dẫn file .smx của bản tự lưu.</param>
/// <param name="OriginalPath">Project gốc đang mở lúc tự lưu (null nếu chưa lưu lần nào).</param>
/// <param name="SavedAtUtc">Thời điểm tự lưu.</param>
public sealed record AutoSaveInfo(string ProjectFile, string? OriginalPath, DateTime SavedAtUtc);

/// <summary>
/// Kho bản tự lưu để phục hồi khi app bị đóng đột ngột (crash, mất điện, tắt tiến trình).
/// Chỉ giữ một bản mới nhất. Thoát bình thường (hoặc lưu thủ công) thì xóa bản này, nên nếu còn file
/// khi khởi động nghĩa là phiên trước kết thúc bất thường.
/// </summary>
public sealed class AutoSaveStore
{
    private sealed class Meta
    {
        public string? OriginalPath { get; set; }
        public DateTime SavedAtUtc { get; set; }
    }

    public AutoSaveStore(string directory) => Directory = directory;

    /// <summary>Thư mục mặc định: %LOCALAPPDATA%\SoundMaster\Recovery.</summary>
    public static AutoSaveStore CreateDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoundMaster", "Recovery"));

    public string Directory { get; }

    public string ProjectFilePath => Path.Combine(Directory, "autosave" + ProjectDocument.Extension);
    private string MetaPath => Path.Combine(Directory, "autosave.json");

    /// <summary>Ghi bản tự lưu. Đường dẫn file nguồn luôn là đường dẫn đầy đủ (vị trí tự lưu khác vị trí project).</summary>
    public void Write(ProjectDocument document, string? originalPath)
    {
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var track in document.Tracks) track.Source = null;

        // Ghi project trước, meta sau: có meta nghĩa là project đã ghi xong.
        ProjectFile.Save(ProjectFilePath, document);
        string tempMeta = MetaPath + ".tmp";
        File.WriteAllText(tempMeta, JsonSerializer.Serialize(new Meta
        {
            OriginalPath = originalPath,
            SavedAtUtc = DateTime.UtcNow,
        }));
        File.Move(tempMeta, MetaPath, overwrite: true);
    }

    /// <summary>Bản tự lưu còn sót (nếu có và đọc được), ngược lại null.</summary>
    public AutoSaveInfo? TryRead()
    {
        try
        {
            if (!File.Exists(ProjectFilePath) || !File.Exists(MetaPath)) return null;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath));
            if (meta is null) return null;
            ProjectFile.Load(ProjectFilePath); // kiểm tra file đọc được, không thì coi như không có
            return new AutoSaveInfo(ProjectFilePath, meta.OriginalPath, meta.SavedAtUtc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Xóa bản tự lưu (sau khi lưu thủ công, thoát bình thường, hoặc người dùng bỏ phục hồi).</summary>
    public void Clear()
    {
        foreach (var path in new[] { ProjectFilePath, MetaPath, ProjectFilePath + ".tmp", MetaPath + ".tmp" })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // File đang bị khóa: lần sau sẽ xóa.
            }
        }
    }
}
