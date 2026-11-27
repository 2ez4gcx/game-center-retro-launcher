using System.IO.Compression;
using System.Text;
using GameCenter.Core.Arcade;
using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Emulation;
using GameCenter.Core.Scanning;
using GameCenter.Core.Util;

namespace GameCenter.Tests;

/// <summary>
/// Game thùng chạy bằng FinalBurn Neo: danh sách đi kèm (Arcade/fbneo.dat.gz), nhận diện zip / 7z, kiểm tra bộ ROM
/// (BIOS, bộ gốc, phiên bản) khi quét và khi bấm Chơi.
/// Phần nhận diện dùng danh sách giả nhỏ với ROM giả (CRC thật của nội dung giả), vì không thể kèm ROM game thật.
/// </summary>
public class ArcadeTests
{
    /// <summary>ROM giả: nội dung khác nhau theo mã → CRC khác nhau.</summary>
    private static (string Name, byte[] Data) Rom(string name, string id) =>
        (name, Encoding.ASCII.GetBytes(("ROM-" + id).PadRight(64, '.')));

    private static string Crc(string id) => Crc32.Compute(new MemoryStream(Rom("", id).Data)).ToString("x8");

    private static string Line(string name, string cloneOf, string romOf, string flags, string title, string[] own, string[]? parent = null) =>
        string.Join('\t', name, cloneOf, romOf, flags, "1994", "Test", title,
            string.Join(' ', own.Select(Crc)), string.Join(' ', (parent ?? Array.Empty<string>()).Select(Crc)));

    /// <summary>
    /// fakebios: BIOS. alpha: cần fakebios. beta: game thường; betaj: bản phụ của beta (ROM X2, X3 lấy từ beta).
    /// gamma: FBNeo chưa giả lập hoàn chỉnh. delta: cần ảnh ổ cứng. contra: trùng tên với game NES.
    /// chips: chip ROM đuôi .bin (dễ bị nhận nhầm là ROM băng).
    /// </summary>
    private static readonly ArcadeDatabase Db = ArcadeDatabase.Parse(new StringReader(string.Join('\n',
        "# FinalBurn Neo test",
        Line("fakebios", "", "", "b", "Fake BIOS", new[] { "B1", "B2" }),
        Line("alpha", "", "fakebios", "", "Alpha Fighters (World 940101)", new[] { "A1", "A2", "A3" }),
        Line("beta", "", "", "", "Beta Game / Beta Gemu (World)", new[] { "X1", "X2", "X3" }),
        Line("betaj", "beta", "beta", "", "Beta Gemu (Japan)", new[] { "J1" }, new[] { "X2", "X3" }),
        Line("gamma", "", "", "p", "Gamma", new[] { "G1" }),
        Line("delta", "", "", "d", "Delta", new[] { "D1" }),
        Line("contra", "", "", "", "Contra (US)", new[] { "C1", "C2" }),
        Line("chips", "", "", "", "Chips", new[] { "P1", "P2", "P3" }))));

    private static void Zip(string path, params (string Name, byte[] Data)[] entries)
    {
        File.Delete(path);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (n, d) in entries)
        {
            using var s = z.CreateEntry(n).Open();
            s.Write(d);
        }
    }

    private static GameScanner Scanner(TestEnv env) =>
        new(env.Catalog, env.Paths.GamesDir, env.Paths.PlaylistsDir, env.Paths.BiosDir, Db);

    [Fact]
    public void EmbeddedList_HasPopularGames_WithBiosAndParents()
    {
        var db = ArcadeDatabase.Default;
        Assert.True(db.Count > 8000, $"chỉ có {db.Count} bộ");
        Assert.Contains("FinalBurn Neo", db.Source);

        var kof98 = db.Get("kof98")!;
        Assert.Equal("The King of Fighters '98 - The Slugfest", kof98.Title);
        Assert.Equal("neogeo", db.BiosOf(kof98)!.Name);
        Assert.True(db.Get("neogeo")!.IsBios);
        Assert.Equal("Cadillacs and Dinosaurs", db.Get("dino")!.Title);
        Assert.Null(db.BiosOf(db.Get("dino")!));
        Assert.Equal("Cadillacs: Kyouryuu Shin Seiki (Japan 930201)", db.Get("dinoj")!.Title);
        Assert.Equal("Knights of Valour", db.Get("kov")!.Title);
        Assert.Equal("pgm", db.BiosOf(db.Get("kov")!)!.Name);
        Assert.Equal("Metal Slug - Super Vehicle-001", db.Get("mslug")!.Title);

        // Bản phụ: ROM lấy từ bộ gốc ghi riêng; ROM BIOS (sp-s3.sp1 = 91b64be3) không tính, chỉ cần có file BIOS
        var pls = db.Get("kof97pls")!;
        Assert.Equal("kof97", pls.CloneOf);
        Assert.NotEmpty(pls.ParentCrcs);
        Assert.DoesNotContain(0x91b64be3u, pls.ParentCrcs);
        Assert.Equal("neogeo", db.BiosOf(pls)!.Name);

        // Nhận ra theo CRC khi tên file bị đổi: zip bộ đầy đủ của bản phụ → bản phụ, không phải bộ gốc
        var dinoj = db.Get("dinoj")!;
        Assert.Same(dinoj, db.Identify(dinoj.OwnCrcs.Concat(dinoj.ParentCrcs).ToHashSet()));
        Assert.Same(db.Get("dino"), db.Identify(db.Get("dino")!.OwnCrcs.ToHashSet()));
        Assert.Null(db.Identify(new HashSet<uint> { 0x12345678 }));
    }

    [Fact]
    public void Scan_RecognizesSets_ChecksBiosAndParent()
    {
        using var env = new TestEnv();
        Zip(env.File("Arcade/beta.zip"), Rom("x1.u1", "X1"), Rom("x2.u2", "X2"), Rom("x3.u3", "X3"), ("readme.txt", Encoding.ASCII.GetBytes("hi")));
        Zip(env.File("Arcade/betaj.zip"), Rom("j1.u1", "J1"));                   // bản phụ dạng tách, bộ gốc nằm cạnh
        Zip(env.File("Khac/betaj.zip"), Rom("j1.u1", "J1"));                     // bản phụ không có bộ gốc
        Zip(env.File("Full/betaj.zip"), Rom("j1.u1", "J1"), Rom("x2.u2", "X2"), Rom("x3.u3", "X3")); // bộ đầy đủ
        Zip(env.File("alpha.zip"), Rom("a1", "A1"), Rom("a2", "A2"), Rom("a3", "A3"));  // cần BIOS, chưa có
        Zip(env.File("chips.zip"), Rom("prog.bin", "P1"), Rom("gfx.bin", "P2"), Rom("snd.bin", "P3"));

        var scanner = Scanner(env);
        var games = scanner.Scan();
        var g = games.ToDictionary(x => Path.GetRelativePath(env.Paths.GamesDir, x.SourceFile).Replace('\\', '/'));
        Assert.Equal(6, games.Count);
        Assert.All(games, x => Assert.Equal("Arcade", x.Platform));
        Assert.All(games, x => Assert.Equal(x.SourceFile, x.LaunchFile));           // chạy nguyên file zip

        Assert.Equal("Beta Game", g["Arcade/beta.zip"].Title);
        Assert.Null(g["Arcade/beta.zip"].Message);
        Assert.Equal("Beta Gemu (Japan)", g["Arcade/betaj.zip"].Title);          // bản phụ giữ phần trong ngoặc
        Assert.Null(g["Arcade/betaj.zip"].Message);
        Assert.Contains("beta.zip", g["Khac/betaj.zip"].Message);
        Assert.Null(g["Full/betaj.zip"].Message);
        Assert.Contains("fakebios.zip", g["alpha.zip"].Message);
        Assert.Null(g["chips.zip"].Message);                                     // chip .bin không bị tách thành ROM băng
        Assert.Empty(scanner.BiosFiles);

        // Chuyển file sang thư mục khác: vẫn là cùng game (mã theo CRC các ROM)
        Assert.NotNull(g["Arcade/betaj.zip"].FileHash);
        Assert.Equal(g["Arcade/betaj.zip"].FileHash, g["Khac/betaj.zip"].FileHash);
    }

    [Fact]
    public void Scan_BiosZip_InGamesOrBiosFolder()
    {
        using var env = new TestEnv();
        Zip(env.File("Neo Geo/alpha.zip"), Rom("a1", "A1"), Rom("a2", "A2"), Rom("a3", "A3"));
        Zip(env.File("BIOS cua toi/fakebios.zip"), Rom("b1.rom", "B1"), Rom("b2.rom", "B2"));

        // BIOS để ở thư mục khác trong Games: không hiện như game, được chép sang thư mục BIOS sau khi quét → không báo thiếu
        var scanner = Scanner(env);
        var game = Assert.Single(scanner.Scan());
        Assert.Null(game.Message);
        var bios = Assert.Single(scanner.BiosFiles);
        Assert.Equal(1, Ps2Bios.ImportFromGames(scanner.BiosFiles, env.Paths.BiosDir));
        Assert.True(File.Exists(Path.Combine(env.Paths.BiosDir, "fakebios.zip")));

        // FBNeo dùng mọi bản BIOS tìm thấy (cạnh game, BIOS/fbneo, BIOS): một bản sai không sao nếu bản khác đúng
        File.Delete(bios);
        Directory.CreateDirectory(Path.Combine(env.Paths.BiosDir, "fbneo"));
        Zip(Path.Combine(env.Paths.BiosDir, "fbneo", "fakebios.zip"), Rom("b1.rom", "KHAC"));
        Assert.Null(Assert.Single(Scanner(env).Scan()).Message);
        // Chỉ còn bản sai: báo BIOS sai
        File.Delete(Path.Combine(env.Paths.BiosDir, "fakebios.zip"));
        Assert.Contains("fakebios.zip", Assert.Single(Scanner(env).Scan()).Message);
        Assert.Equal(ArcadeIssue.BadBios, ArcadeRomCheck.Check(Db, Db.Get("alpha")!, game.SourceFile, env.Paths.BiosDir).Issue);
    }

    [Fact]
    public void Scan_RenamedWrongVersionAndLookalikes()
    {
        using var env = new TestEnv();
        Zip(env.File("x/Beta Game (World).zip"), Rom("x1.u1", "X1"), Rom("x2.u2", "X2"), Rom("x3.u3", "X3"));  // đổi tên
        Zip(env.File("y/beta.zip"), Rom("x1.u1", "X1"), Rom("x2.u2", "KHAC"), Rom("x3.u3", "KHAC2"));        // ROM khác phiên bản
        Zip(env.File("NES/contra.zip"), ("Contra (USA).nes", Fake.NesRom()));                             // trùng tên game thùng
        Zip(env.File("contra.zip"), ("Contra (USA).nes", Fake.NesRom()));
        Zip(env.File("delta.zip"), Rom("d1", "D1"));
        Zip(env.File("Arcade/khongco.zip"), Rom("p1", "Z1"), Rom("p2", "Z2"), Rom("p3", "Z3"));             // không có trong danh sách

        var g = Scanner(env).Scan().ToDictionary(x => Path.GetRelativePath(env.Paths.GamesDir, x.SourceFile).Replace('\\', '/'));
        Assert.Equal(ScanStatus.Unknown, g["x/Beta Game (World).zip"].Status);
        Assert.Contains("beta.zip", g["x/Beta Game (World).zip"].Message);
        Assert.Equal("Arcade", g["y/beta.zip"].Platform);
        Assert.Contains("2/3", g["y/beta.zip"].Message);
        Assert.Equal("NES", g["NES/contra.zip"].Platform);
        Assert.Equal("NES", g["contra.zip"].Platform);
        Assert.Equal(ScanStatus.Unknown, g["delta.zip"].Status);
        Assert.Equal(ScanStatus.Unknown, g["Arcade/khongco.zip"].Status);
        Assert.Contains("MAME", g["Arcade/khongco.zip"].Message);
    }

    [Fact]
    public void Scan_7z_ByName()
    {
        using var env = new TestEnv();
        env.File("Arcade/gamma.7z");
        env.File("beta.7z");
        env.File("NES/beta.7z");                 // thư mục máy băng: file nén thường, phải giải nén
        env.File("Mario.7z");
        env.File("fakebios.7z");

        var scanner = Scanner(env);
        var g = scanner.Scan().ToDictionary(x => Path.GetRelativePath(env.Paths.GamesDir, x.SourceFile).Replace('\\', '/'));
        Assert.Equal(4, g.Count);
        Assert.Equal("Arcade", g["Arcade/gamma.7z"].Platform);
        Assert.Contains("FinalBurn Neo", g["Arcade/gamma.7z"].Message);   // chưa giả lập hoàn chỉnh
        Assert.Equal("Arcade", g["beta.7z"].Platform);
        Assert.Equal(ScanStatus.Unknown, g["NES/beta.7z"].Status);
        Assert.Contains("beta.7z", g["NES/beta.7z"].Message);
        Assert.Equal(ScanStatus.Unknown, g["Mario.7z"].Status);
        Assert.EndsWith("fakebios.7z", Assert.Single(scanner.BiosFiles));
    }

    [Fact]
    public void Launcher_ChecksBiosParentAndVersion_BeforeRunning()
    {
        using var env = new TestEnv();
        var appDir = Path.Combine(Path.GetDirectoryName(env.Root)!, "app");
        var paths = new AppPaths(appDir, env.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RetroArchExe)!);
        File.WriteAllText(paths.RetroArchExe, "");
        Directory.CreateDirectory(paths.CoresDir);
        File.WriteAllText(paths.CorePath("fbneo_libretro.dll"), "");
        var launcher = new EmulatorLauncher(paths, env.Catalog, new AppSettings(), Db);
        GameRecord Game(string file) => new() { Platform = "Arcade", LaunchFile = file, SourceFile = file };

        var alpha = env.File("Arcade/alpha.zip");
        Zip(alpha, Rom("a1", "A1"), Rom("a2", "A2"), Rom("a3", "A3"));
        var plan = launcher.Plan(Game(alpha));
        Assert.Equal(LaunchProblem.BiosMissing, plan.Problem);
        Assert.Contains("fakebios.zip", plan.Message);

        // Vừa chép BIOS vào thư mục BIOS: chơi được ngay, chạy nguyên file zip
        Zip(Path.Combine(paths.BiosDir, "fakebios.zip"), Rom("b1.rom", "B1"), Rom("b2.rom", "B2"));
        plan = launcher.Plan(Game(alpha));
        Assert.Equal(LaunchProblem.None, plan.Problem);
        Assert.Null(plan.Warning);
        Assert.Equal(paths.CorePath("fbneo_libretro.dll"), plan.StartInfo!.ArgumentList[3]);
        Assert.Equal(alpha, plan.StartInfo.ArgumentList[4]);

        var betaj = env.File("Arcade/betaj.zip");
        Zip(betaj, Rom("j1", "J1"));
        Assert.Equal(LaunchProblem.ArcadeParentMissing, launcher.Plan(Game(betaj)).Problem);

        var beta = env.File("Arcade/beta.zip");
        Zip(beta, Rom("x1", "X1"), Rom("x2", "X2"));                   // thiếu một ROM: vẫn cho thử, hỏi trước
        plan = launcher.Plan(Game(beta));
        Assert.Equal(LaunchProblem.None, plan.Problem);
        Assert.Contains("1/3", plan.Warning);

        // Người dùng tự chọn hệ máy "Máy thùng" cho file đã đổi tên: báo đổi lại tên
        var renamed = env.File("Arcade/Alpha Fighters.zip");
        Zip(renamed, Rom("a1", "A1"), Rom("a2", "A2"), Rom("a3", "A3"));
        plan = launcher.Plan(Game(renamed));
        Assert.Equal(LaunchProblem.ArcadeNotInList, plan.Problem);
        Assert.Contains("alpha.zip", plan.Message);
    }

    /// <summary>Nội dung giả có CRC32 cho trước (thêm 4 byte cuối), để giả file BIOS thật theo CRC.</summary>
    private static byte[] WithCrc(string text, uint target)
    {
        var data = Encoding.ASCII.GetBytes(text);
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        uint fwd = ~Crc32.Compute(new MemoryStream(data));
        uint bkd = ~target;
        for (int i = 0; i < 4; i++)
        {
            int j = Array.FindIndex(table, x => x >> 24 == bkd >> 24);
            bkd = ((bkd ^ table[j]) << 8) | (uint)j;
        }
        var result = data.Concat(BitConverter.GetBytes(fwd ^ bkd)).ToArray();
        Assert.Equal(target, Crc32.Compute(new MemoryStream(result)));
        return result;
    }

    [Fact]
    public void NeoGeoBios_OldSetWithoutDefaultBios_IsReportedBad()
    {
        using var env = new TestEnv();
        var db = ArcadeDatabase.Default;
        var kof98 = db.Get("kof98")!;
        var bios = Path.Combine(env.Paths.BiosDir, "neogeo.zip");
        ArcadeCheck Check() => ArcadeRomCheck.Check(db, kof98, kof98.OwnCrcs.ToHashSet(), _ => new[] { bios });

        // neogeo.zip cũ: có BIOS ver. 5 (sp-s2.sp1) nhưng thiếu ver. 6 (sp-s3.sp1) FBNeo dùng mặc định
        Zip(bios, ("sp-s2.sp1", WithCrc("a", 0x9036d879)), ("sm1.sm1", WithCrc("b", 0x94416d67)),
            ("sfix.sfix", WithCrc("c", 0xc2ea0cfd)), ("000-lo.lo", WithCrc("d", 0x5a86cff2)));
        Assert.Equal(ArcadeIssue.BadBios, Check().Issue);

        Zip(bios, ("sp-s3.sp1", WithCrc("a", 0x91b64be3)), ("sm1.sm1", WithCrc("b", 0x94416d67)),
            ("sfix.sfix", WithCrc("c", 0xc2ea0cfd)), ("000-lo.lo", WithCrc("d", 0x5a86cff2)));
        Assert.Equal(ArcadeIssue.None, Check().Issue);
    }

    [Fact]
    public void CoreOptions_DisableServiceMenuHotkey()
    {
        var options = RetroArchConfig.ManagedCoreOptions(new AppSettings(), vulkanAvailable: false);
        Assert.Equal("None", options["fbneo-diagnostic-input"]);
    }
}
