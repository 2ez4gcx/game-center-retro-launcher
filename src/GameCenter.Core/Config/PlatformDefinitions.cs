using System.Text.Json;
using System.Text.Json.Serialization;
using GameCenter.Core.Localization;

namespace GameCenter.Core.Config;

public sealed class BiosInfo
{
    /// <summary>Tên file BIOS core tìm trong thư mục BIOS (không phân biệt hoa thường).</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>
    /// Nhận BIOS theo nội dung thay vì tên file: "ps2" = mọi file BIOS PS2 hợp lệ (xem Ps2Bios).
    /// null: dùng danh sách <see cref="Files"/>.
    /// </summary>
    public string? Detect { get; set; }
    /// <summary>true: không có BIOS thì không chạy được. false: core có BIOS thay thế (OpenBIOS/HLE).</summary>
    public bool Required { get; set; }
    /// <summary>
    /// Khác <see cref="Required"/> trên Android (core Android của hệ máy có thể không cần BIOS, ví dụ PS2 dùng Play!).
    /// null: giống <see cref="Required"/>.
    /// </summary>
    public bool? RequiredOnAndroid { get; set; }
    public string? Note { get; set; }
}

public sealed class PlatformDefinition
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public string Core { get; set; } = "";
    /// <summary>
    /// Core dùng trên Android nếu khác bản máy tính (ví dụ PS1: PCSX ReARMed thay Beetle PSX HW, vì app Android
    /// chỉ có OpenGL ES). null: dùng <see cref="Core"/>.
    /// </summary>
    public string? AndroidCore { get; set; }
    /// <summary>false: hệ máy chưa chạy được trên Android (game hiện ở mục chưa hỗ trợ).</summary>
    public bool Android { get; set; } = true;
    public List<string> Extensions { get; set; } = new();
    /// <summary>Game chạy từ đĩa (PS1, PS2): game nằm một mình trong thư mục thì lấy tên thư mục làm tên game.</summary>
    public bool Disc { get; set; }
    public bool Enabled { get; set; } = true;
    public BiosInfo? Bios { get; set; }

    /// <summary>Tên hiện trên giao diện: bản dịch "platform.name.&lt;Name&gt;" nếu có (ví dụ "Máy thùng"), không thì <see cref="DisplayName"/>.</summary>
    /// <summary>Hệ máy có bắt buộc BIOS trên hệ điều hành cho trước không.</summary>
    public bool BiosRequiredFor(Os os) =>
        Bios != null && (os == Os.Android ? Bios.RequiredOnAndroid ?? Bios.Required : Bios.Required);

    /// <summary>Core của hệ máy trên hệ điều hành cho trước.</summary>
    public string CoreFor(Os os) => os == Os.Android && !string.IsNullOrEmpty(AndroidCore) ? AndroidCore : Core;

    [JsonIgnore]
    public string Label => Loc.Current.Has("platform.name." + Name) ? Loc.T("platform.name." + Name) : DisplayName;
}

public sealed class PlatformCatalog
{
    [JsonPropertyName("platforms")]
    public List<PlatformDefinition> Platforms { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PlatformCatalog Load(string path)
    {
        var catalog = JsonSerializer.Deserialize<PlatformCatalog>(File.ReadAllText(path), JsonOptions)
                      ?? throw new InvalidDataException(Loc.T("startup.platforms_unreadable", path));
        return catalog;
    }

    public PlatformDefinition? Get(string name) =>
        Platforms.FirstOrDefault(p => p.Enabled && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tìm platform theo tên thư mục (ví dụ "Games/SNES/...").</summary>
    public PlatformDefinition? FromFolderName(string folder) =>
        Platforms.FirstOrDefault(p => p.Enabled &&
            (p.Name.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
             p.Folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// Platform duy nhất nhận extension này (bỏ qua .zip và .bin vì dùng chung).
    /// </summary>
    public PlatformDefinition? UniqueForExtension(string ext)
    {
        var matches = Platforms.Where(p => p.Enabled && p.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public bool IsKnownExtension(string ext) =>
        Platforms.Any(p => p.Enabled && p.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));

    public bool IsDiscPlatform(string? name) => name != null && Get(name)?.Disc == true;
}
