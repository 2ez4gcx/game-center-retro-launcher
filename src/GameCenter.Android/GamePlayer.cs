using System.IO.Compression;
using Android.App;
using Android.Content;
using GameCenter.Core.Arcade;
using GameCenter.Core.Data;
using GameCenter.Core.Emulation;
using GameCenter.Core.Localization;
using GameCenter.Core.Scanning;
using GameCenter.Core.Util;

namespace GameCenter.Droid;

/// <summary>
/// Chạy một game trên Android: kiểm tra như bản máy tính (<see cref="EmulatorLauncher.PlanEmbedded"/>), chuẩn bị save
/// (<see cref="SaveGuard"/>), rồi mở màn hình chơi game viết bằng Kotlin (android/player, GameActivity) và chờ thoát.
/// Tên file save giống bản máy tính: "&lt;tên file game&gt;.srm" trong Saves/, "&lt;tên&gt;.state.auto" trong States/.
/// </summary>
public sealed class GamePlayer
{
    private const string PlayerActivity = "com.khuongdoan.gamecenter.player.GameActivity";

    /// <summary>Hỏi người chơi (có / không); do màn hình chính cung cấp.</summary>
    public required Func<string, string, string, string, bool, Task<bool>> Confirm { get; init; }
    public required Func<string, string, Task> Info { get; init; }

    public async Task PlayAsync(MainActivity activity, GameRecord game)
    {
        var paths = App.Paths;
        var launcher = new EmulatorLauncher(paths, App.Catalog, App.Settings);
        var plan = launcher.PlanEmbedded(game);
        if (plan.Problem != LaunchProblem.None)
        {
            await Info(plan.Problem == LaunchProblem.BiosMissing ? Loc.T("play.bios_missing_title") : Loc.T("play.error_title"), plan.Message);
            return;
        }
        if (plan.Warning != null && !await Confirm(game.Name, plan.Warning, Loc.T("play.anyway"), Loc.T("dialog.cancel"), true))
            return;

        var guard = new SaveGuard(paths);
        var saves = guard.Locate(game, App.Db.GetGames(includeMissing: true), App.Catalog);
        guard.PrepareForLaunch(App.Db, game, saves);

        // Chỗ đang chơi (save state): hỏi chơi tiếp hay chơi từ đầu, như bản máy tính
        // Play! (PS2) tự ghi thẻ nhớ vào thư mục Saves, không có .srm; và nó chụp "chỗ đang chơi" trong lúc luồng giả lập
        // vẫn chạy nên nạp lại có thể treo game. Với core này chỉ dùng save trong game (thẻ nhớ).
        bool ownSaves = IsPlay(plan.CorePath!);
        bool useStates = App.Settings.AskSaveStateOnExit && !ownSaves;
        var states = new SaveStateService(saves.StatesDir);
        var stateFile = states.FindAutoState(game.LaunchFile)
                        ?? Path.Combine(saves.StatesDir, saves.ContentName + SaveStateService.AutoSuffix);
        bool resume = false;
        if (useStates && File.Exists(stateFile))
            resume = await Confirm(game.Name, Loc.T("play.resume_text"), Loc.T("play.resume_yes"), Loc.T("play.resume_no"), true);

        var sramFile = ownSaves ? null
            : guard.FindSaveFiles(saves.ContentName, saves.SavesDir)
                  .FirstOrDefault(f => f.EndsWith(".srm", StringComparison.OrdinalIgnoreCase))
              ?? Path.Combine(saves.SavesDir, saves.ContentName + ".srm");

        var intent = new Intent();
        intent.SetClassName(activity, PlayerActivity);
        intent.PutExtra("core", plan.CorePath);
        intent.PutExtra("game", PrepareContent(game, plan.LaunchFile!));
        intent.PutExtra("systemDir", paths.BiosDir);
        intent.PutExtra("savesDir", saves.SavesDir);
        if (sramFile != null) intent.PutExtra("sramFile", sramFile);
        if (useStates) intent.PutExtra("stateFile", stateFile);
        intent.PutExtra("loadState", resume);
        // Game nhiều đĩa: nhớ đĩa đang dùng, cạnh thẻ nhớ (bản máy tính: RetroArch tự ghi file .ldci cùng chỗ)
        if (game.DiscCount > 1) intent.PutExtra("discFile", Path.Combine(saves.SavesDir, saves.ContentName + ".disc"));
        intent.PutExtra("saveStateOnExit", useStates);
        var options = CoreOptions(plan.CorePath!, paths);
        intent.PutExtra("variables", options.Where(o => !o.StartsWith("shader=", StringComparison.Ordinal)).ToArray());
        intent.PutExtra("shader", options.LastOrDefault(o => o.StartsWith("shader=", StringComparison.Ordinal))?["shader=".Length..] ?? "");
        intent.PutExtra("env", CoreEnvironment(plan.CorePath!, paths.SavesDir));
        intent.PutExtra("analog", App.Catalog.Get(game.Platform)?.Analog == true);
        intent.PutExtra("psSymbols", game.Platform is "PS1" or "PS2" or "PSP");
        intent.PutExtra("padLayout", PadLayout(game.Platform));
        intent.PutExtra("textExitAgain", Loc.T("android.exit_again"));
        intent.PutExtra("textLoading", Loc.T("android.loading"));
        intent.PutExtra("title", game.Name);
        intent.PutExtra("textMenuContinue", Loc.T("android.menu_continue"));
        intent.PutExtra("textMenuDisc", Loc.T("android.menu_disc"));
        intent.PutExtra("textMenuExit", Loc.T("android.menu_exit"));
        intent.PutExtra("textDisc", Loc.T("android.disc_n", "{0}"));

        var startedUtc = DateTime.UtcNow;
        App.Db.MarkPlayed(game.Id);
        var previous = useStates ? states.BackupBeforePlay(game.LaunchFile) : null;
        Log.Info($"Chạy: {plan.CorePath} \"{plan.LaunchFile}\" (chơi tiếp: {resume})");

        var (result, data) = await activity.PlayAsync(intent);
        Log.Info($"Thoát game, kết quả {result}");
        if (result != Result.Ok)
        {
            // Core hoặc game không mở được: giữ nguyên chỗ đang chơi cũ
            if (previous != null) SaveStateService.Keep(previous);
            if (result != Result.Canceled)
            {
                Log.Error($"Màn hình chơi game báo lỗi {data?.GetIntExtra("error", -1)}", null);
                await Info(Loc.T("play.error_title"), Loc.T("play.start_failed"));
            }
            return;
        }
        if (!useStates) return;

        var state = states.NewStateSince(game.LaunchFile, startedUtc);
        if (state == null)
        {
            if (previous != null) SaveStateService.Keep(previous);
            return;
        }
        bool keep = await Confirm(Loc.T("play.keep_title"), Loc.T("play.keep_text", game.Name), Loc.T("play.keep_yes"), Loc.T("play.keep_no"), false);
        try
        {
            if (keep) SaveStateService.Keep(state);
            else SaveStateService.Discard(state);
        }
        catch (Exception ex) { Log.Error("Lỗi xử lý save state", ex); }
    }

    /// <summary>
    /// Biến môi trường riêng cho từng core. Play! (PS2) ghi thẻ nhớ và cấu hình vào "$EXTERNAL_STORAGE/Play Data Files",
    /// mặc định là gốc bộ nhớ chung (app không có quyền ghi, core sẽ sập): trỏ về thư mục Saves của Game Center.
    /// </summary>
    private static string[] CoreEnvironment(string corePath, string savesDir) =>
        IsPlay(corePath) ? new[] { "EXTERNAL_STORAGE=" + savesDir } : Array.Empty<string>();

    /// <summary>Bộ nút cảm ứng theo hệ máy (TouchPadView): chỉ hiện những nút máy đó có, đúng tên nút của máy.</summary>
    private static string PadLayout(string? platform) => platform switch
    {
        "NES" or "GB" or "GBC" => "two",
        "MasterSystem" or "GameGear" => "sms",
        "SNES" => "snes",
        "GBA" => "gba",
        "MegaDrive" => "md",
        "PSP" => "psp",
        ArcadeDatabase.Platform => "arcade",
        _ => "",
    };

    private static bool IsPlay(string corePath) =>
        Path.GetFileName(corePath).Contains("play_libretro", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tùy chọn core do Game Center đặt sẵn cho từng hệ máy (tốc độ, hình ảnh), dạng "khóa=giá trị"; riêng "shader=..."
    /// là cách phóng hình lên màn hình (sharp / lcd / crt / default). Người rành máy có thể ghi đè bằng file
    /// Config/core-options.txt trong thư mục dữ liệu, mỗi dòng một "khóa=giá trị" (dòng bắt đầu bằng # là ghi chú).
    /// </summary>
    internal static List<string> CoreOptions(string corePath, GameCenter.Core.Config.AppPaths paths)
    {
        var options = new List<string>(DefaultCoreOptions(Path.GetFileName(corePath)));
        try
        {
            var file = Path.Combine(paths.ConfigDir, "core-options.txt");
            if (File.Exists(file))
                options.AddRange(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#' && l.Contains('=')));
        }
        catch (Exception ex) { Log.Error("Không đọc được core-options.txt", ex); }
        // Dòng sau ghi đè dòng trước cùng khóa
        return options.GroupBy(o => o[..o.IndexOf('=')]).Select(g => g.Last()).ToList();
    }

    /// <summary>
    /// Thiết lập tốc độ / hình ảnh cho từng core, đã đo trên máy thật (Galaxy Note 9, 60 khung hình/giây):
    /// - Máy băng và máy thùng (hình điểm ảnh): phóng hình kiểu "sharp" cho nét, không nhòe như làm mịn mặc định.
    /// - PS1: dựng hình 3D ở độ phân giải gấp đôi. PSP: độ phân giải gấp đôi (960x544). Hai mục này chỉ bật trên máy đủ
    ///   mạnh (<see cref="IsStrongDevice"/>); máy yếu giữ độ phân giải gốc để không tụt tốc độ.
    /// - PS2 (Play!): không có tùy chọn nào giúp nhanh hơn; để nguyên độ phân giải gốc.
    /// </summary>
    private static string[] DefaultCoreOptions(string core)
    {
        bool Is(string name) => core.Contains(name, StringComparison.OrdinalIgnoreCase);
        // Game thùng: không để giữ START lâu là vào menu kỹ thuật của máy thùng
        if (Is("fbneo")) return new[] { "fbneo-diagnostic-input=None", "shader=sharp" };
        if (Is("fceumm") || Is("snes9x") || Is("gambatte") || Is("mgba") || Is("genesis_plus_gx")) return new[] { "shader=sharp" };
        if (Is("pcsx_rearmed") && IsStrongDevice)
            return new[] { "pcsx_rearmed_neon_enhancement_enable=enabled", "pcsx_rearmed_neon_enhancement_no_main=enabled" };
        if (Is("ppsspp") && IsStrongDevice) return new[] { "ppsspp_internal_resolution=960x544" };
        return Array.Empty<string>();
    }

    /// <summary>Máy đủ mạnh để tăng độ phân giải: từ 8 nhân xử lý và 4 GB RAM trở lên (ngưỡng ước lượng, chưa đo trên máy yếu).</summary>
    private static bool IsStrongDevice
    {
        get
        {
            try
            {
                var info = new ActivityManager.MemoryInfo();
                (Application.Context.GetSystemService(Context.ActivityService) as ActivityManager)?.GetMemoryInfo(info);
                return Environment.ProcessorCount >= 8 && info.TotalMem >= 3_500_000_000L;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>
    /// File core mở được. RetroArch tự đọc game băng trong zip ("a.zip" có một ROM, hoặc "a.zip#rom.nes"), ở đây phải
    /// giải nén ROM ra thư mục tạm của app. Tên save vẫn theo tên file chạy nên không đổi. Game thùng chạy nguyên file zip.
    /// </summary>
    private static string PrepareContent(GameRecord game, string launchFile)
    {
        var zipPath = GameScanner.PhysicalPath(launchFile);
        if (game.Platform == ArcadeDatabase.Platform || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return launchFile;

        int hash = launchFile.IndexOf(".zip#", StringComparison.OrdinalIgnoreCase);
        var entryName = hash < 0 ? null : launchFile[(hash + 5)..].Replace('\\', '/');
        var extensions = App.Catalog.Get(game.Platform)?.Extensions ?? new List<string>();

        using var zip = ZipFile.OpenRead(zipPath);
        var files = zip.Entries.Where(e => e.Length > 0).ToList();
        var entry = entryName != null
            ? files.FirstOrDefault(e => e.FullName.Replace('\\', '/') == entryName)
            : files.FirstOrDefault(e => extensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
        if (entry == null) throw new FileNotFoundException(entryName ?? zipPath);

        var dir = Path.Combine(Application.Context.CacheDir!.AbsolutePath, "content");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, entry.Name);
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }
}
