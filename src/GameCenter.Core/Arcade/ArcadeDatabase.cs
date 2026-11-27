using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using GameCenter.Core.Scanning;

namespace GameCenter.Core.Arcade;

/// <summary>Một bộ ROM game thùng trong danh sách của FinalBurn Neo (tạo bởi tools/update-fbneo-dat.ps1).</summary>
public sealed partial class ArcadeSet
{
    /// <summary>Mã game, cũng là tên file zip FBNeo cần ("kof98" → kof98.zip).</summary>
    public required string Name { get; init; }
    /// <summary>Bộ gốc nếu đây là bản phụ (bản vùng khác, bản sửa...).</summary>
    public string? CloneOf { get; init; }
    /// <summary>Bộ chứa ROM dùng chung: bộ gốc, hoặc BIOS (neogeo, pgm...).</summary>
    public string? RomOf { get; init; }
    public bool IsBios { get; init; }
    /// <summary>FBNeo chưa giả lập hoàn chỉnh (trạng thái driver khác "good").</summary>
    public bool Preliminary { get; init; }
    /// <summary>Cần thêm ảnh đĩa CHD (Game Center chưa hỗ trợ).</summary>
    public bool NeedsDisk { get; init; }
    public string Year { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>CRC các ROM chỉ bộ này có.</summary>
    public uint[] OwnCrcs { get; init; } = Array.Empty<uint>();
    /// <summary>CRC các ROM lấy từ bộ gốc (nằm trong zip bộ gốc, hoặc ngay trong zip này nếu là bộ đầy đủ).</summary>
    public uint[] ParentCrcs { get; init; } = Array.Empty<uint>();

    [GeneratedRegex(@"\s*(\([^()]*\))\s*$")]
    private static partial Regex TrailingTag();

    /// <summary>
    /// Tên hiển thị: phần trước " / " (các tên khác của cùng game). Bản gốc bỏ phần trong ngoặc
    /// ("Cadillacs and Dinosaurs (World 930201)" → "Cadillacs and Dinosaurs"); bản phụ giữ lại để phân biệt các bản.
    /// </summary>
    public string Title
    {
        get
        {
            var text = Description;
            string? tag = null;
            var m = TrailingTag().Match(text);
            if (m.Success)
            {
                tag = m.Groups[1].Value;
                text = text[..m.Index];
            }
            int alt = text.IndexOf(" / ", StringComparison.Ordinal);
            if (alt > 0) text = text[..alt];
            text = NameCleaner.Clean(text);
            if (text.Length == 0) text = Name;
            return CloneOf != null && tag != null ? $"{text} {tag}" : text;
        }
    }
}

/// <summary>
/// Danh sách game thùng FinalBurn Neo chạy được, nhúng trong dll (Arcade/fbneo.dat.gz).
/// Dùng để nhận ra game thùng theo tên file / CRC các ROM bên trong và kiểm tra bộ ROM (xem <see cref="ArcadeRomCheck"/>).
/// </summary>
public sealed class ArcadeDatabase
{
    /// <summary>Tên hệ máy game thùng trong platforms.json.</summary>
    public const string Platform = "Arcade";
    private const string ResourceName = "GameCenter.Core.Arcade.fbneo.dat.gz";
    private static readonly Lazy<ArcadeDatabase> s_default = new(LoadEmbedded);

    private readonly Dictionary<string, ArcadeSet> _sets;
    private Dictionary<uint, List<ArcadeSet>>? _byCrc;

    private ArcadeDatabase(Dictionary<string, ArcadeSet> sets, string source)
    {
        _sets = sets;
        Source = source;
    }

    /// <summary>Danh sách đi kèm Game Center (đọc một lần, lần đầu cần đến).</summary>
    public static ArcadeDatabase Default => s_default.Value;

    /// <summary>Dòng đầu file: phiên bản FBNeo tạo ra danh sách.</summary>
    public string Source { get; }
    public int Count => _sets.Count;
    public IEnumerable<ArcadeSet> Sets => _sets.Values;

    private static ArcadeDatabase LoadEmbedded()
    {
        using var stream = typeof(ArcadeDatabase).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Thiếu tài nguyên {ResourceName}");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return Parse(reader);
    }

    /// <summary>Đọc danh sách dạng văn bản (mỗi dòng một bộ, các cột cách nhau bằng tab, xem tools/update-fbneo-dat.ps1).</summary>
    public static ArcadeDatabase Parse(TextReader reader)
    {
        var sets = new Dictionary<string, ArcadeSet>(StringComparer.OrdinalIgnoreCase);
        var source = "";
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith('#'))
            {
                if (source.Length == 0) source = line.TrimStart('#', ' ');
                continue;
            }
            var c = line.Split('\t');
            if (c.Length < 9 || c[0].Length == 0) continue;
            sets[c[0]] = new ArcadeSet
            {
                Name = c[0],
                CloneOf = NullIfEmpty(c[1]),
                RomOf = NullIfEmpty(c[2]),
                IsBios = c[3].Contains('b'),
                Preliminary = c[3].Contains('p'),
                NeedsDisk = c[3].Contains('d'),
                Year = c[4],
                Manufacturer = c[5],
                Description = c[6],
                OwnCrcs = Crcs(c[7]),
                ParentCrcs = Crcs(c[8]),
            };
        }
        return new ArcadeDatabase(sets, source);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static uint[] Crcs(string list) =>
        list.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => uint.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();

    public ArcadeSet? Get(string name) => _sets.GetValueOrDefault(name);

    /// <summary>BIOS của bộ ROM: đi theo romof (bản phụ → bộ gốc → BIOS) tới bộ là BIOS. null nếu không cần BIOS.</summary>
    public ArcadeSet? BiosOf(ArcadeSet set)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s = set;
        while (s.RomOf != null && seen.Add(s.Name))
        {
            if (Get(s.RomOf) is not { } next) return null;
            if (next.IsBios) return next;
            s = next;
        }
        return null;
    }

    /// <summary>
    /// Nhận ra bộ ROM theo CRC các file trong zip (dùng khi tên file đã bị đổi, ví dụ "King of Fighters 98.zip").
    /// Chọn bộ khớp nhiều nhất; phải khớp ít nhất 70% ROM của bộ (kể cả ROM lấy từ bộ gốc nếu zip là bộ đầy đủ)
    /// và có ít nhất một ROM riêng của bộ. null nếu không bộ nào khớp.
    /// </summary>
    public ArcadeSet? Identify(IReadOnlySet<uint> crcs)
    {
        if (crcs.Count == 0) return null;
        _byCrc ??= BuildCrcIndex();
        var candidates = crcs.SelectMany(c => _byCrc.GetValueOrDefault(c) ?? Enumerable.Empty<ArcadeSet>()).Distinct();
        ArcadeSet? best = null;
        double bestScore = 0;
        foreach (var set in candidates.Where(s => !s.IsBios))
        {
            int own = set.OwnCrcs.Count(crcs.Contains);
            if (own == 0) continue;
            // Bộ tách (split): zip chỉ có ROM riêng; bộ đầy đủ (non-merged): có cả ROM của bộ gốc
            int parent = set.ParentCrcs.Count(crcs.Contains);
            double coverage = parent > 0
                ? (double)(own + parent) / (set.OwnCrcs.Length + set.ParentCrcs.Length)
                : (double)own / set.OwnCrcs.Length;
            // Phần zip giải thích được: tránh chọn bộ gốc cho zip của bản phụ (bản phụ khớp trọn hơn)
            double precision = (double)(own + parent) / crcs.Count;
            double score = coverage * precision;
            if (coverage >= 0.7 && score > bestScore)
            {
                best = set;
                bestScore = score;
            }
        }
        return best;
    }

    private Dictionary<uint, List<ArcadeSet>> BuildCrcIndex()
    {
        var index = new Dictionary<uint, List<ArcadeSet>>();
        foreach (var set in _sets.Values)
        {
            foreach (var crc in set.OwnCrcs.Concat(set.ParentCrcs))
            {
                if (!index.TryGetValue(crc, out var list)) index[crc] = list = new List<ArcadeSet>(1);
                if (list.Count == 0 || list[^1] != set) list.Add(set);
            }
        }
        return index;
    }
}
