using System.IO.Compression;
using GameCenter.Core.Localization;

namespace GameCenter.Core.Arcade;

public enum ArcadeIssue
{
    None,
    /// <summary>Thiếu file BIOS (neogeo.zip, pgm.zip...).</summary>
    MissingBios,
    /// <summary>Có file BIOS nhưng không phải bản FBNeo cần.</summary>
    BadBios,
    /// <summary>Bản phụ thiếu zip bộ gốc (ví dụ kof97pls.zip cần kof97.zip).</summary>
    MissingParent,
    /// <summary>Thiếu / sai ROM: bộ ROM của phiên bản khác (MAME cũ...), FBNeo có thể báo lỗi.</summary>
    WrongVersion,
}

/// <summary>Kết quả kiểm tra một bộ ROM game thùng. <see cref="NeededFile"/>: file cần thêm (neogeo.zip, kof97.zip...).</summary>
public sealed record ArcadeCheck(ArcadeSet Set, ArcadeIssue Issue, string? NeededFile = null, int Missing = 0, int Total = 0)
{
    /// <summary>Thiếu BIOS / bộ gốc thì chắc chắn không chạy; sai phiên bản thì vẫn cho thử.</summary>
    public bool CanRun => Issue is ArcadeIssue.None or ArcadeIssue.WrongVersion;

    /// <summary>Câu báo cho người chơi, null nếu không có gì cần lưu ý.</summary>
    public string? Message => Issue switch
    {
        ArcadeIssue.MissingBios => Loc.T("arcade.bios_missing", NeededFile),
        ArcadeIssue.BadBios => Loc.T("arcade.bios_bad", NeededFile),
        ArcadeIssue.MissingParent => Loc.T("arcade.parent_missing", NeededFile),
        ArcadeIssue.WrongVersion => Loc.T("arcade.wrong_version", Missing, Total),
        _ => Set.Preliminary ? Loc.T("arcade.preliminary") : null,
    };
}

/// <summary>
/// Kiểm tra bộ ROM game thùng trước khi chạy bằng FinalBurn Neo, để báo rõ thay vì màn hình lỗi của core:
/// - FBNeo nhận game theo tên file (kof98.zip) và tìm từng ROM theo CRC trong zip của game, zip bộ gốc và zip BIOS.
/// - Các zip đó được tìm ở cạnh file game, &lt;system&gt;/fbneo và &lt;system&gt; (thư mục BIOS của Game Center);
///   FBNeo dùng mọi bản tìm thấy (ROM thiếu ở bản này có thể nằm ở bản kia).
/// Chỉ đọc mục lục zip (CRC có sẵn), không giải nén. File .7z không đọc được mục lục: chỉ kiểm tra có file bộ gốc / BIOS.
/// </summary>
public static class ArcadeRomCheck
{
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z" };

    /// <summary>
    /// ROM BIOS FBNeo bắt buộc phải có (xem log "ROM ... is required" của core). Neo Geo: BIOS MVS Asia/Europe ver. 6
    /// (sp-s3.sp1, mặc định khi tùy chọn "Neo-Geo mode" là DIPSWITCH) cùng sm1.sm1, sfix.sfix, 000-lo.lo; bộ neogeo.zip
    /// cũ thường thiếu sp-s3.sp1 nên không chạy. BIOS khác: chỉ kiểm tra có ít nhất một ROM đúng.
    /// </summary>
    private static readonly Dictionary<string, uint[]> RequiredBiosRoms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neogeo"] = new uint[] { 0x91b64be3, 0x94416d67, 0xc2ea0cfd, 0x5a86cff2 },
    };

    /// <summary>Thư mục FBNeo tìm bộ gốc / BIOS, đúng thứ tự core tìm.</summary>
    public static IReadOnlyList<string> SearchDirs(string gameFile, string? biosDir)
    {
        var dirs = new List<string> { Path.GetDirectoryName(Path.GetFullPath(gameFile))! };
        if (biosDir != null)
        {
            dirs.Add(Path.Combine(biosDir, "fbneo"));
            dirs.Add(biosDir);
        }
        return dirs;
    }

    /// <summary>Mọi file zip / 7z của bộ ROM trong các thư mục (theo thứ tự thư mục).</summary>
    public static List<string> FindArchives(string setName, IEnumerable<string> dirs) =>
        dirs.SelectMany(d => ArchiveExtensions.Select(e => Path.Combine(d, setName + e))).Where(File.Exists).ToList();

    /// <summary>CRC các file trong zip (đọc mục lục). null nếu không đọc được (file .7z, zip hỏng...).</summary>
    public static HashSet<uint>? ReadCrcs(string archive)
    {
        if (!Path.GetExtension(archive).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            return zip.Entries.Where(e => e.Length > 0 && !string.IsNullOrEmpty(e.Name)).Select(e => e.Crc32).ToHashSet();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Kiểm tra bộ ROM của file game (đọc zip của game, bộ gốc, BIOS trong các thư mục FBNeo tìm).</summary>
    public static ArcadeCheck Check(ArcadeDatabase db, ArcadeSet set, string gameFile, string? biosDir)
    {
        var dirs = SearchDirs(gameFile, biosDir);
        return Check(db, set, ReadCrcs(gameFile), name => FindArchives(name, dirs));
    }

    /// <summary>
    /// Kiểm tra bộ ROM. <paramref name="crcs"/>: CRC trong zip của game (null nếu không đọc được, ví dụ .7z);
    /// <paramref name="findArchives"/>: các file zip / 7z của một bộ ROM khác (bộ gốc, BIOS).
    /// Thứ tự báo: sai ROM riêng (bộ ROM khác phiên bản: thêm BIOS cũng không chạy) → thiếu bộ gốc → thiếu BIOS.
    /// </summary>
    public static ArcadeCheck Check(ArcadeDatabase db, ArcadeSet set, IReadOnlySet<uint>? crcs, Func<string, IReadOnlyList<string>> findArchives)
    {
        int total = set.OwnCrcs.Length + set.ParentCrcs.Length;

        // ROM lấy từ bộ gốc: trong zip của game (bộ đầy đủ) hoặc các zip bộ gốc
        var parentName = set.CloneOf ?? (set.RomOf != null && db.Get(set.RomOf) is { IsBios: false } ? set.RomOf : null);
        var parent = parentName != null && set.ParentCrcs.Length > 0 ? ReadAll(findArchives(parentName)) : new Contents(0, null);

        if (crcs != null)
        {
            int missingOwn = set.OwnCrcs.Count(c => !crcs.Contains(c));
            int missingParent = set.ParentCrcs.Count(c => !crcs.Contains(c) && !parent.Has(c));
            if (missingOwn > 0) return new(set, ArcadeIssue.WrongVersion, Missing: missingOwn + missingParent, Total: total);
            if (missingParent > 0)
            {
                return parent.Files == 0
                    ? new(set, ArcadeIssue.MissingParent, parentName + ".zip")
                    : new(set, ArcadeIssue.WrongVersion, Missing: missingParent, Total: total);
            }
        }
        // .7z bản phụ không biết có đủ ROM của bộ gốc hay không: không báo thiếu bộ gốc

        if (db.BiosOf(set) is { } bios)
        {
            var files = findArchives(bios.Name);
            var biosContents = ReadAll(files);
            if (files.Count == 0) return new(set, ArcadeIssue.MissingBios, bios.Name + ".zip");
            bool ok = RequiredBiosRoms.TryGetValue(bios.Name, out var required)
                ? required.All(biosContents.Has)
                : bios.OwnCrcs.Any(biosContents.Has);
            if (!ok) return new(set, ArcadeIssue.BadBios, Path.GetFileName(files[0]));
        }
        return new(set, ArcadeIssue.None, Total: total);
    }

    /// <summary>CRC gộp của các file cùng bộ ROM. <see cref="Unreadable"/>: có file .7z (không đọc được) → coi như có đủ ROM.</summary>
    private readonly record struct Contents(int Files, HashSet<uint>? Crcs, bool Unreadable = false)
    {
        public bool Has(uint crc) => Unreadable || Crcs?.Contains(crc) == true;
    }

    private static Contents ReadAll(IReadOnlyList<string> files)
    {
        var all = new HashSet<uint>();
        bool unreadable = false;
        foreach (var f in files)
        {
            if (ReadCrcs(f) is { } c) all.UnionWith(c);
            else unreadable = true;
        }
        return new Contents(files.Count, all, unreadable);
    }
}
