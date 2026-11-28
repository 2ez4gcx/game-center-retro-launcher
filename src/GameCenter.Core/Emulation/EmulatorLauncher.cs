using System.Diagnostics;
using GameCenter.Core.Arcade;
using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Localization;
using GameCenter.Core.Scanning;
using GameCenter.Core.Util;

namespace GameCenter.Core.Emulation;

public enum LaunchProblem
{
    None,
    GameFileMissing,
    BrokenCue,
    UnknownPlatform,
    RetroArchMissing,
    CoreMissing,
    BiosMissing,
    DuckStationMissing,
    /// <summary>Game thùng là bản phụ, thiếu zip bộ gốc.</summary>
    ArcadeParentMissing,
    /// <summary>Game thùng không có trong danh sách FBNeo (tên file bị đổi, game chỉ MAME chạy...).</summary>
    ArcadeNotInList,
}

/// <param name="Warning">Chạy được nhưng nên hỏi người chơi trước (ví dụ bộ ROM game thùng khác phiên bản).</param>
public sealed record LaunchPlan(LaunchProblem Problem, string Message, ProcessStartInfo? StartInfo = null, string? Warning = null);

/// <summary>Kết quả kiểm tra trước khi chơi trên Android (core chạy trong app): core và file game cần mở.</summary>
public sealed record EmbeddedPlan(LaunchProblem Problem, string Message, string? CorePath = null, string? LaunchFile = null, string? Warning = null);

/// <summary>Chọn core, tạo command line và khởi chạy RetroArch (mục 10).</summary>
public sealed class EmulatorLauncher
{
    private readonly AppPaths _paths;
    private readonly PlatformCatalog _catalog;
    private readonly AppSettings _settings;
    private readonly ArcadeDatabase? _arcade;

    /// <param name="arcade">Danh sách game thùng; null: danh sách đi kèm Game Center.</param>
    public EmulatorLauncher(AppPaths paths, PlatformCatalog catalog, AppSettings settings, ArcadeDatabase? arcade = null)
    {
        _paths = paths;
        _catalog = catalog;
        _settings = settings;
        _arcade = arcade;
    }

    public LaunchPlan Plan(GameRecord game)
    {
        if (game.ScanStatus == ScanStatus.BrokenCue)
            return new(LaunchProblem.BrokenCue, Loc.T("launch.broken", game.ScanMessage));
        if (!File.Exists(GameScanner.PhysicalPath(game.LaunchFile)))
            return new(LaunchProblem.GameFileMissing, Loc.T("launch.file_missing"));

        var platform = _catalog.Get(game.Platform);
        if (platform == null)
            return new(LaunchProblem.UnknownPlatform, Loc.T("launch.unknown_platform"));

        // DuckStation do người dùng tự cài (không đóng gói, mục 10.3)
        if (platform.Name == "PS1" && _settings.UseUserDuckStation)
        {
            if (string.IsNullOrWhiteSpace(_settings.DuckStationPath) || !File.Exists(_settings.DuckStationPath))
                return new(LaunchProblem.DuckStationMissing, Loc.T("launch.duck_missing"));
            var dsi = new ProcessStartInfo(_settings.DuckStationPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(_settings.DuckStationPath)! };
            if (_settings.Fullscreen) dsi.ArgumentList.Add("-fullscreen");
            dsi.ArgumentList.Add("--");
            dsi.ArgumentList.Add(game.LaunchFile);
            return new(LaunchProblem.None, "", dsi);
        }

        if (!File.Exists(_paths.RetroArchExe))
            return new(LaunchProblem.RetroArchMissing, Loc.T("launch.retroarch_missing"));

        var core = _paths.CorePath(platform.Core);
        if (!File.Exists(core))
            return new(LaunchProblem.CoreMissing, Loc.T("launch.core_missing", platform.Label, Path.GetFileName(core)));

        try { BiosChecker.Prepare(platform, _paths.BiosDir); }
        catch (Exception ex) { Log.Error($"Không chuẩn bị được BIOS {platform.Name}", ex); }
        if (BiosChecker.Check(platform, _paths.BiosDir) == BiosState.Missing)
            return new(LaunchProblem.BiosMissing, BiosMissingMessage(platform));

        string? warning = null;
        if (platform.Name == ArcadeDatabase.Platform && CheckArcade(game.LaunchFile, out warning) is { } problem)
            return problem;
        // Ổ chứa save sắp đầy: save trong game / chỗ đang chơi có thể không ghi được → hỏi trước
        if (LowDiskWarning(_paths.SavesDir) is { } disk) warning = warning == null ? disk : warning + "\n\n" + disk;
        return new(LaunchProblem.None, "", BuildRetroArchStartInfo(core, game.LaunchFile), warning);
    }

    /// <summary>
    /// Android: core nằm trong APK và chạy ngay trong app, không có RetroArch. Kiểm tra giống <see cref="Plan"/>
    /// (file game, hệ máy, core, BIOS, bộ ROM game thùng) rồi trả về core và file cần mở; app tự mở màn hình chơi.
    /// </summary>
    public EmbeddedPlan PlanEmbedded(GameRecord game)
    {
        if (game.ScanStatus == ScanStatus.BrokenCue)
            return new(LaunchProblem.BrokenCue, Loc.T("launch.broken", game.ScanMessage));
        if (!File.Exists(GameScanner.PhysicalPath(game.LaunchFile)))
            return new(LaunchProblem.GameFileMissing, Loc.T("launch.file_missing"));

        var platform = _catalog.Get(game.Platform);
        if (platform == null)
            return new(LaunchProblem.UnknownPlatform, Loc.T("launch.unknown_platform"));
        if (!platform.Android)
            return new(LaunchProblem.UnknownPlatform, Loc.T("android.unsupported"));

        var core = _paths.CorePath(platform.CoreFor(_paths.Os));
        if (!File.Exists(core))
            return new(LaunchProblem.CoreMissing, Loc.T("launch.core_missing", platform.Label, Path.GetFileName(core)));

        try { BiosChecker.Prepare(platform, _paths.BiosDir); }
        catch (Exception ex) { Log.Error($"Không chuẩn bị được BIOS {platform.Name}", ex); }
        if (platform.BiosRequiredFor(_paths.Os) && BiosChecker.Check(platform, _paths.BiosDir) == BiosState.Missing)
            return new(LaunchProblem.BiosMissing, BiosMissingMessage(platform));

        string? warning = null;
        if (platform.Name == ArcadeDatabase.Platform && CheckArcade(game.LaunchFile, out warning) is { } problem)
            return new(problem.Problem, problem.Message);
        return new(LaunchProblem.None, "", core, game.LaunchFile, warning);
    }

    /// <summary>Dưới mức này thì cảnh báo: chỗ đang chơi của PS2 vài chục MB, thẻ nhớ PS2 8 MB, cộng bản sao lưu.</summary>
    public const long LowDiskBytes = 300L * 1024 * 1024;

    /// <summary>
    /// Câu cảnh báo nếu ổ chứa <paramref name="dir"/> còn trống ít hơn <paramref name="threshold"/>, null nếu đủ hoặc không đọc được.
    /// Ổ được chọn là điểm gắn (mount) dài nhất chứa thư mục (Linux / macOS có nhiều điểm gắn trên cùng "/").
    /// </summary>
    public static string? LowDiskWarning(string dir, long threshold = LowDiskBytes)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, cmp))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive == null || drive.AvailableFreeSpace >= threshold) return null;
            return Loc.T("launch.disk_low", $"{drive.AvailableFreeSpace / (1024 * 1024)} MB", drive.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Game thùng: kiểm tra lại bộ ROM ngay trước khi chạy (người chơi có thể vừa chép thêm BIOS / bộ gốc),
    /// để báo rõ thay vì màn hình lỗi tiếng Anh của FBNeo. Thiếu BIOS / bộ gốc: không chạy.
    /// Bộ ROM khác phiên bản: vẫn cho thử, kèm <paramref name="warning"/>.
    /// </summary>
    private LaunchPlan? CheckArcade(string file, out string? warning)
    {
        warning = null;
        var db = _arcade ?? ArcadeDatabase.Default;
        if (db.Get(Path.GetFileNameWithoutExtension(file)) is not { } set)
        {
            var renamed = ArcadeRomCheck.ReadCrcs(file) is { } crcs ? db.Identify(crcs) : null;
            return new(LaunchProblem.ArcadeNotInList,
                renamed != null ? Loc.T("arcade.renamed", renamed.Title, renamed.Name + ".zip") : Loc.T("scan.arcade"));
        }
        var check = ArcadeRomCheck.Check(db, set, file, _paths.BiosDir);
        switch (check.Issue)
        {
            case ArcadeIssue.MissingBios or ArcadeIssue.BadBios:
                return new(LaunchProblem.BiosMissing, check.Message!);
            case ArcadeIssue.MissingParent:
                return new(LaunchProblem.ArcadeParentMissing, check.Message!);
            case ArcadeIssue.WrongVersion:
                warning = check.Message;
                break;
        }
        return null;
    }

    /// <summary>Hướng dẫn riêng từng hệ máy nếu có (khóa "launch.bios_missing.&lt;hệ máy&gt;"), không thì câu chung.</summary>
    public static string BiosMissingMessage(PlatformDefinition platform)
    {
        var key = "launch.bios_missing." + platform.Name;
        return Loc.T(Loc.Current.Has(key) ? key : "launch.bios_missing");
    }

    /// <summary>Dùng ArgumentList (từng tham số riêng) để an toàn với dấu cách và tiếng Việt.</summary>
    public ProcessStartInfo BuildRetroArchStartInfo(string corePath, string launchFile)
    {
        var psi = new ProcessStartInfo(_paths.RetroArchExe)
        {
            UseShellExecute = false,
            WorkingDirectory = _paths.RetroArchDir,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(_paths.RetroArchCfg);
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add(corePath);
        psi.ArgumentList.Add(launchFile);
        return psi;
    }

    /// <summary>
    /// Thêm file cấu hình phụ chỉ áp dụng cho lần chạy này (nạp chỗ đang chơi, thư mục save riêng...):
    /// "--appendconfig a.cfg|b.cfg" chèn trước "-L" (RetroArch tách nhiều file bằng "|").
    /// </summary>
    public static void AddAppendConfigs(ProcessStartInfo psi, IReadOnlyCollection<string> configFiles)
    {
        if (configFiles.Count == 0) return;
        int existing = psi.ArgumentList.IndexOf("--appendconfig");
        if (existing >= 0)
        {
            psi.ArgumentList[existing + 1] = string.Join("|", configFiles.Prepend(psi.ArgumentList[existing + 1]));
            return;
        }
        int at = Math.Max(0, psi.ArgumentList.IndexOf("-L"));
        psi.ArgumentList.Insert(at, "--appendconfig");
        psi.ArgumentList.Insert(at + 1, string.Join("|", configFiles));
    }

    /// <summary>Chạy game và chờ đến khi thoát.</summary>
    public async Task<int> RunAsync(ProcessStartInfo psi, CancellationToken ct = default)
    {
        Log.Info($"Chạy: {psi.FileName} {string.Join(" ", psi.ArgumentList.Select(a => $"\"{a}\""))}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không khởi chạy được emulator.");
        await p.WaitForExitAsync(ct);
        Log.Info($"Emulator thoát, mã {p.ExitCode}");
        return p.ExitCode;
    }

    /// <summary>Mở RetroArch không kèm game (để cấu hình tay cầm).</summary>
    public ProcessStartInfo? BuildMenuStartInfo()
    {
        if (!File.Exists(_paths.RetroArchExe)) return null;
        var psi = new ProcessStartInfo(_paths.RetroArchExe) { UseShellExecute = false, WorkingDirectory = _paths.RetroArchDir };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(_paths.RetroArchCfg);
        psi.ArgumentList.Add("--menu");
        return psi;
    }
}
