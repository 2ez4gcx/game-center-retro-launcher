using System.Text;
using System.Text.Json;
using GameCenter.Core.Config;

namespace GameCenter.Core.Data;

/// <summary>
/// Game nhiều đĩa người chơi tự ghép (menu "⋯ → Ghép thêm đĩa"): dùng khi các đĩa khác tên nên không tự ghép được,
/// ví dụ đĩa 1 bản Việt hóa "LOD (VN).cue", đĩa 2–4 bản gốc "Legend of Dragoon (USA) (Disc 2).cue".
/// Mỗi nhóm là danh sách file gốc các đĩa theo thứ tự (đĩa 1 trước). Lưu ở Config/disc-groups.json, đường dẫn tính
/// từ thư mục Games (đổi thư mục dữ liệu vẫn đúng). Không ghi gì vào thư mục game.
/// </summary>
public sealed class DiscGroups
{
    private readonly string _file;
    private readonly string _gamesDir;
    private readonly List<List<string>> _groups;

    private DiscGroups(string file, string gamesDir, List<List<string>> groups)
    {
        _file = file;
        _gamesDir = Path.GetFullPath(gamesDir);
        _groups = groups;
    }

    public static string FileOf(AppPaths paths) => Path.Combine(paths.ConfigDir, "disc-groups.json");

    public static DiscGroups Load(AppPaths paths)
    {
        var file = FileOf(paths);
        var groups = new List<List<string>>();
        try
        {
            if (File.Exists(file))
                groups = JsonSerializer.Deserialize<List<List<string>>>(File.ReadAllText(file)) ?? groups;
        }
        catch (JsonException) { /* file hỏng: coi như chưa ghép tay */ }
        var result = new DiscGroups(file, paths.GamesDir, new List<List<string>>());
        foreach (var g in groups.Where(g => g.Count >= 2))
            result._groups.Add(g.Select(result.ToAbsolute).ToList());
        return result;
    }

    /// <summary>Các nhóm, đường dẫn tuyệt đối.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Groups => _groups;

    /// <summary>Nhóm chứa file gốc này (null nếu không ghép tay).</summary>
    public IReadOnlyList<string>? GroupOf(string sourceFile) =>
        _groups.FirstOrDefault(g => g.Contains(Path.GetFullPath(sourceFile), StringComparer.OrdinalIgnoreCase));

    /// <summary>Thêm <paramref name="next"/> làm đĩa kế tiếp của game có file gốc <paramref name="first"/>.</summary>
    public void Append(string first, string next)
    {
        first = Path.GetFullPath(first);
        next = Path.GetFullPath(next);
        // Đĩa thêm vào thôi là game riêng / thôi thuộc nhóm khác
        foreach (var g in _groups) g.RemoveAll(f => f.Equals(next, StringComparison.OrdinalIgnoreCase));
        var group = _groups.FirstOrDefault(g => g.Count > 0 && g[0].Equals(first, StringComparison.OrdinalIgnoreCase));
        if (group == null) _groups.Add(group = new List<string> { first });
        group.Add(next);
        _groups.RemoveAll(g => g.Count < 2);
    }

    /// <summary>Tách nhóm chứa file gốc này; trả về false nếu nó không thuộc nhóm nào.</summary>
    public bool Split(string sourceFile) =>
        _groups.RemoveAll(g => g.Contains(Path.GetFullPath(sourceFile), StringComparer.OrdinalIgnoreCase)) > 0;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var json = JsonSerializer.Serialize(_groups.Select(g => g.Select(ToStored).ToList()).ToList(),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(_file, json, new UTF8Encoding(false));
    }

    private string ToStored(string full)
    {
        var rel = Path.GetRelativePath(_gamesDir, full);
        return rel.StartsWith("..") || Path.IsPathRooted(rel) ? full : rel.Replace('\\', '/');
    }

    private string ToAbsolute(string stored) =>
        Path.GetFullPath(Path.IsPathRooted(stored) ? stored : Path.Combine(_gamesDir, stored.Replace('/', Path.DirectorySeparatorChar)));
}
