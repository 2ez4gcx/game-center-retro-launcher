using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Emulation;
using GameCenter.Core.Scanning;

namespace GameCenter.Tests;

/// <summary>Bản Android: core nằm trong APK, chạy trong app, không có RetroArch.</summary>
public class AndroidTests
{
    [Fact]
    public void CoreFileName_Android_HasLibPrefixAndAndroidSuffix()
    {
        Assert.Equal("libfceumm_libretro_android.so", OsPlatform.CoreFileName("fceumm_libretro.dll", Os.Android));
        Assert.Equal("fceumm_libretro.so", OsPlatform.CoreFileName("fceumm_libretro.dll", Os.Linux));
        Assert.Equal("fceumm_libretro.dll", OsPlatform.CoreFileName("fceumm_libretro.dll", Os.Windows));
    }

    [Fact]
    public void Ps1_UsesItsAndroidCore_OtherPlatformsKeepTheirs()
    {
        using var env = new TestEnv();
        var ps1 = env.Catalog.Get("PS1")!;
        Assert.Equal("pcsx_rearmed_libretro.dll", ps1.CoreFor(Os.Android));
        Assert.Equal("mednafen_psx_hw_libretro.dll", ps1.CoreFor(Os.Windows));
        var nes = env.Catalog.Get("NES")!;
        Assert.Equal(nes.Core, nes.CoreFor(Os.Android));
    }

    [Fact]
    public void Ps2_OnAndroid_UsesPlayCore_AndNeedsNoBios()
    {
        // Play! tự giả lập BIOS (mã nguồn mở), nên bản Android chơi PS2 không cần file BIOS của Sony
        using var env = new TestEnv();
        var ps2 = env.Catalog.Get("PS2")!;
        Assert.True(ps2.Android);
        Assert.Equal("play_libretro.dll", ps2.CoreFor(Os.Android));
        Assert.False(ps2.BiosRequiredFor(Os.Android));
        Assert.Equal("pcsx2_libretro.dll", ps2.CoreFor(Os.Windows));
        Assert.True(ps2.BiosRequiredFor(Os.Windows));
    }

    [Fact]
    public void PlanEmbedded_NeedsOnlyTheCoreInTheAppLibraryFolder()
    {
        using var env = new TestEnv();
        var libDir = Path.Combine(Path.GetDirectoryName(env.Root)!, "lib");
        var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(env.Root)!, "app"), env.Root, Os.Android) { CoresDirOverride = libDir };
        Directory.CreateDirectory(libDir);
        var rom = env.File("NES/game.nes");
        var game = new GameRecord { Platform = "NES", LaunchFile = rom, SourceFile = rom };
        var launcher = new EmulatorLauncher(paths, env.Catalog, new AppSettings());

        // Không cần RetroArch; thiếu core thì báo thiếu core
        Assert.Equal(LaunchProblem.CoreMissing, launcher.PlanEmbedded(game).Problem);

        var core = Path.Combine(libDir, "libfceumm_libretro_android.so");
        File.WriteAllText(core, "");
        var plan = launcher.PlanEmbedded(game);
        Assert.Equal(LaunchProblem.None, plan.Problem);
        Assert.Equal(core, plan.CorePath);
        Assert.Equal(rom, plan.LaunchFile);
    }

    [Fact]
    public void PlanEmbedded_PlatformDisabledOnAndroid_IsNotLaunched()
    {
        using var env = new TestEnv();
        var paths = new AppPaths(Path.Combine(Path.GetDirectoryName(env.Root)!, "app"), env.Root, Os.Android) { CoresDirOverride = env.Root };
        env.Catalog.Get("NES")!.Android = false;
        var rom = env.File("NES/game.nes");
        var plan = new EmulatorLauncher(paths, env.Catalog, new AppSettings())
            .PlanEmbedded(new GameRecord { Platform = "NES", LaunchFile = rom, SourceFile = rom });
        Assert.Equal(LaunchProblem.UnknownPlatform, plan.Problem);
    }

    [Fact]
    public void PspDisc_IsPlayableOnAndroid_AndStillUnsupportedOnDesktop()
    {
        using var env = new TestEnv();
        var iso = env.File("a/Patapon.iso", Fake.Disc(null, false, "PSP GAME"));

        var android = new GameScanner(env.Catalog, env.Paths.GamesDir, env.Paths.PlaylistsDir) { Os = Os.Android }.Scan();
        var game = Assert.Single(android);
        Assert.Equal("PSP", game.Platform);
        Assert.Equal(ScanStatus.Ok, game.Status);

        var desktop = new GameScanner(env.Catalog, env.Paths.GamesDir, env.Paths.PlaylistsDir) { Os = Os.Windows }.Scan();
        Assert.Equal(ScanStatus.Unknown, Assert.Single(desktop).Status);

        var psp = env.Catalog.Get("PSP")!;
        Assert.True(psp.AvailableOn(Os.Android));
        Assert.False(psp.AvailableOn(Os.Windows));
        Assert.True(psp.Analog);
        Assert.Equal("libppsspp_libretro_android.so", OsPlatform.CoreFileName(psp.CoreFor(Os.Android), Os.Android));
    }

    [Theory]
    [InlineData("Legend of Dragoon, The (USA)", "The Legend of Dragoon")]
    [InlineData("Legend of Zelda, The - A Link to the Past (USA)", "The Legend of Zelda - A Link to the Past")]
    [InlineData("Bug's Life, A (Europe)", "A Bug's Life")]
    [InlineData("Tekken 6", "Tekken 6")]
    [InlineData("Ready, Steady, Go", "Ready, Steady, Go")]
    public void NameCleaner_MovesTrailingArticleToFront(string file, string expected) =>
        Assert.Equal(expected, GameCenter.Core.Scanning.NameCleaner.Clean(file));

    private static string DiscZip(TestEnv env, string rel, string disc)
    {
        var zip = Path.Combine(env.Paths.GamesDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        using (var w = new StreamWriter(archive.CreateEntry(disc + ".cue").Open()))
        {
            w.WriteLine($"FILE \"{disc}.bin\" BINARY");
            w.WriteLine("  TRACK 01 MODE2/2352");
            w.WriteLine("    INDEX 01 00:00:00");
        }
        using (var b = archive.CreateEntry(disc + ".bin").Open())
            b.Write(new byte[2352 * 20]);
        return zip;
    }

    [Fact]
    public void DiscZip_ExtractedNextToZip_BecomesOneMultiDiscGame_AndZipIsNoLongerReported()
    {
        using var env = new TestEnv();
        var zips = Enumerable.Range(1, 2).Select(n => DiscZip(env, $"E2E Quest (USA) (Disc {n}).zip", $"E2E Quest (USA) (Disc {n})")).ToList();

        // Chưa giải nén: mỗi file zip là một mục "chưa nhận ra", và được nhận là zip chứa game đĩa
        var before = env.Scan();
        Assert.Equal(2, before.Count(g => g.Status == GameCenter.Core.Scanning.ScanStatus.Unknown));
        Assert.All(zips, z => Assert.True(GameCenter.Core.Scanning.DiscArchive.IsDiscZip(z)));
        Assert.False(GameCenter.Core.Scanning.DiscArchive.IsExtracted(zips[0]));
        Assert.True(GameCenter.Core.Scanning.DiscArchive.RequiredBytes(zips) > 2352 * 40);

        long reported = 0;
        foreach (var z in zips) GameCenter.Core.Scanning.DiscArchive.Extract(z, done => reported = done);
        Assert.True(reported > 2352 * 20);
        Assert.All(zips, z => Assert.True(GameCenter.Core.Scanning.DiscArchive.IsExtracted(z)));
        Assert.Empty(Directory.GetFiles(env.Paths.GamesDir, "*.part"));
        Assert.Equal(0, GameCenter.Core.Scanning.DiscArchive.RequiredBytes(zips));

        // Sau khi giải nén: một game 2 đĩa, file zip gốc còn nguyên nhưng không còn bị báo
        var db = new GameDatabase(env.Paths.DatabaseFile);
        db.Initialize(env.Catalog);
        db.ApplyScan(env.Scan());
        var game = Assert.Single(db.GetGames());
        Assert.Equal("PS1", game.Platform);
        Assert.Equal(2, game.DiscCount);
        Assert.All(zips, z => Assert.True(File.Exists(z)));
    }

    [Fact]
    public void DiscZip_RomZipIsNotADiscZip()
    {
        using var env = new TestEnv();
        var zip = Path.Combine(env.Paths.GamesDir, "rom.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = archive.CreateEntry("game.gba").Open()) s.Write(new byte[64]);
        Assert.False(GameCenter.Core.Scanning.DiscArchive.IsDiscZip(zip));
    }
}
