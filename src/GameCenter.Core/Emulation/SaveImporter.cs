using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Localization;
using GameCenter.Core.Util;

namespace GameCenter.Core.Emulation;

/// <summary>
/// Đọc save trong game của giả lập khác rồi đưa vào chỗ lưu của Game Center, để người chơi tiếp tục ván cũ:
/// - PS1: thẻ nhớ .mcr (ePSXe), .mcd (DuckStation, PCSX), .mc, .gme (DexDrive), .vmp (PSP / PS3), .mem / .vgs, .srm.
/// - Máy băng (NES, SNES, Game Boy, GBA, Mega Drive...): pin lưu .sav / .srm của mGBA, VisualBoyAdvance, My Boy,
///   Snes9x, Nestopia...; nội dung giống nhau giữa các giả lập, chỉ khác tên file.
/// - PSP: thư mục SAVEDATA của PPSSPP (<see cref="ImportPspSaves"/>).
/// Không đọc được "chỗ đang chơi" (save state): mỗi giả lập một định dạng riêng. Save hiện có luôn được sao lưu
/// trước khi bị thay (khôi phục lại bằng "Khôi phục save cũ").
/// </summary>
public static class SaveImporter
{
    public const int Ps1CardSize = 128 * 1024;

    /// <summary>Đuôi file save của giả lập khác mà Game Center nhận, khi nằm cạnh file game (cùng tên).</summary>
    public static readonly string[] SidecarExtensions = { ".sav", ".srm", ".mcr", ".mcd", ".mc", ".gme", ".vmp", ".sa1", ".dsv" };

    private static readonly HashSet<string> Cartridge = new(StringComparer.OrdinalIgnoreCase)
        { "NES", "SNES", "GB", "GBC", "GBA", "MegaDrive", "MasterSystem", "GameGear" };

    private static readonly HashSet<string> StateExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".state", ".sgm", ".sst", ".gsv", ".sps", ".zst", ".fc0", ".ss0", ".ss1", ".000", ".frz", ".p2s", ".sstate", ".auto", ".png" };

    public static bool Supports(string? platform) => platform == "PS1" || (platform != null && Cartridge.Contains(platform));

    /// <summary>
    /// Đổi file save của giả lập khác sang dữ liệu Game Center dùng ("&lt;tên&gt;.srm").
    /// Ném <see cref="InvalidDataException"/> kèm lời giải thích cho người chơi khi file không dùng được.
    /// </summary>
    public static byte[] Convert(string? platform, string file)
    {
        var name = Path.GetFileName(file);
        if (!Supports(platform)) throw new InvalidDataException(Loc.T("import.unsupported_platform"));
        if (StateExtensions.Contains(Path.GetExtension(file))) throw new InvalidDataException(Loc.T("import.is_state", name));
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0) throw new InvalidDataException(Loc.T("import.empty", name));
        if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException(Loc.T("import.not_a_save", name));
        var data = File.ReadAllBytes(file);

        if (platform == "PS1")
        {
            // Thẻ nhớ PS1 luôn là 128 KB bắt đầu bằng "MC"; một số định dạng thêm phần đầu riêng phía trước
            int header = data.Length switch
            {
                Ps1CardSize => 0,
                Ps1CardSize + 3904 => 3904,   // .gme (DexDrive)
                Ps1CardSize + 128 => 128,     // .vmp (PSP / PS3)
                Ps1CardSize + 64 => 64,       // .mem / .vgs (Virtual Game Station)
                _ => -1,
            };
            if (header < 0 || data[header] != (byte)'M' || data[header + 1] != (byte)'C')
                throw new InvalidDataException(Loc.T("import.not_ps1_card", name));
            return header == 0 ? data : data[header..];
        }
        return data;
    }

    /// <summary>
    /// File save của giả lập khác nằm ngay cạnh file game, cùng tên ("Game.gba" + "Game.sav"): cách chép save dễ nhất
    /// cho người chơi (chép cả thư mục game cũ sang là xong).
    /// </summary>
    public static IReadOnlyList<string> FindSidecars(GameRecord game)
    {
        var found = new List<string>();
        try
        {
            foreach (var path in new[] { Scanning.GameScanner.PhysicalPath(game.SourceFile), Scanning.GameScanner.PhysicalPath(game.LaunchFile) }.Distinct())
            {
                var dir = Path.GetDirectoryName(path);
                if (dir == null || !Directory.Exists(dir)) continue;
                var stem = Path.GetFileNameWithoutExtension(path);
                foreach (var ext in SidecarExtensions)
                {
                    var candidate = Path.Combine(dir, stem + ext);
                    if (File.Exists(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase)) found.Add(candidate);
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Không tìm được save cạnh file game: {ex.Message}"); }
        return found;
    }

    /// <summary>
    /// Đưa file save của giả lập khác vào làm save của game. Save đang có được sao lưu trước. Trả về file đã ghi.
    /// </summary>
    public static string Import(AppPaths paths, PlatformCatalog catalog, IEnumerable<GameRecord> allGames, GameRecord game, string file)
    {
        var data = Convert(game.Platform, file);
        var guard = new SaveGuard(paths);
        var loc = guard.Locate(game, allGames, catalog);
        guard.Backup(loc, includePs2Cards: false, force: true);

        var existing = guard.FindSaveFiles(loc.ContentName, loc.SavesDir)
            .FirstOrDefault(f => f.EndsWith(".srm", StringComparison.OrdinalIgnoreCase));
        var target = existing ?? Path.Combine(TargetDir(paths, catalog, game, loc), loc.ContentName + ".srm");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, target, overwrite: true);
        Log.Info($"Nhập save từ giả lập khác: \"{file}\" → \"{target}\" ({data.Length} byte)");
        return target;
    }

    /// <summary>
    /// Lần đầu chơi (game chưa có save nào): nếu cạnh file game có save của giả lập khác thì tự nhận. Không bao giờ ghi đè
    /// save đang có. Trả về file nguồn đã nhận (null nếu không có gì để nhận).
    /// </summary>
    public static string? AutoImport(AppPaths paths, PlatformCatalog catalog, IEnumerable<GameRecord> allGames, GameRecord game)
    {
        if (!Supports(game.Platform)) return null;
        try
        {
            var all = allGames as IReadOnlyCollection<GameRecord> ?? allGames.ToList();
            var guard = new SaveGuard(paths);
            var loc = guard.Locate(game, all, catalog);
            if (guard.FindSaveFiles(loc.ContentName, loc.SavesDir).Any(SaveGuard.IsMainSave)) return null;
            foreach (var candidate in FindSidecars(game).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try { Import(paths, catalog, all, game, candidate); return candidate; }
                catch (InvalidDataException ex) { Log.Warn($"Bỏ qua \"{candidate}\": {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Error("Không tự nhận được save cạnh file game", ex); }
        return null;
    }

    /// <summary>
    /// Thư mục RetroArch / màn hình chơi game đọc save. Android: ngay trong Saves/. Máy tính: RetroArch xếp save theo tên
    /// core (sort_savefiles_enable), ví dụ Saves/mGBA/.
    /// </summary>
    private static string TargetDir(AppPaths paths, PlatformCatalog catalog, GameRecord game, SaveLocation loc)
    {
        if (paths.Os == Os.Android) return loc.SavesDir;
        var core = catalog.Get(game.Platform)?.CoreFor(paths.Os);
        var folder = core == null ? null : CoreFolderName(paths, core);
        return folder == null ? loc.SavesDir : Path.Combine(loc.SavesDir, folder);
    }

    private static readonly Dictionary<string, string> KnownCoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mednafen_psx_hw_libretro"] = "Beetle PSX HW",
        ["pcsx_rearmed_libretro"] = "PCSX-ReARMed",
        ["snes9x_libretro"] = "Snes9x",
        ["mgba_libretro"] = "mGBA",
        ["genesis_plus_gx_libretro"] = "Genesis Plus GX",
        ["fceumm_libretro"] = "FCEUmm",
        ["gambatte_libretro"] = "Gambatte",
    };

    /// <summary>Tên thư mục save của core: "corename" trong file .info đi kèm RetroArch; không có thì dùng bảng tên đã biết.</summary>
    internal static string? CoreFolderName(AppPaths paths, string coreFile)
    {
        var id = Path.GetFileNameWithoutExtension(coreFile);
        try
        {
            if (paths.RetroArchResourceDir is { } res && Path.Combine(res, "info", id + ".info") is var info && File.Exists(info))
            {
                foreach (var line in File.ReadLines(info))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Trim() == "corename") return parts[1].Trim().Trim('"');
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Không đọc được {id}.info: {ex.Message}"); }
        return KnownCoreNames.GetValueOrDefault(id);
    }

    /// <summary>
    /// PSP: chép các thư mục save của PPSSPP (PSP/SAVEDATA/&lt;mã game&gt;) vào chỗ lưu của Game Center. Thư mục đã có thì
    /// giữ nguyên, không ghi đè. Trả về số thư mục save đã chép.
    /// </summary>
    public static int ImportPspSaves(string sourceSaveData, string targetSaveData)
    {
        if (!Directory.Exists(sourceSaveData)) return 0;
        if (string.Equals(Path.GetFullPath(sourceSaveData).TrimEnd('/', '\\'), Path.GetFullPath(targetSaveData).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) return 0;
        Directory.CreateDirectory(targetSaveData);
        int copied = 0;
        foreach (var dir in Directory.GetDirectories(sourceSaveData))
        {
            var dest = Path.Combine(targetSaveData, Path.GetFileName(dir));
            if (Directory.Exists(dest)) continue;
            var temp = dest + ".part";
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(temp, Path.GetRelativePath(dir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, overwrite: true);
            }
            Directory.Move(temp, dest);
            copied++;
        }
        if (copied > 0) Log.Info($"Nhập {copied} thư mục save PSP từ \"{sourceSaveData}\"");
        return copied;
    }
}
