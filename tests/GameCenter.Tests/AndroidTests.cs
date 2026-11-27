using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Emulation;

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
}
