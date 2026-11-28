using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Emulation;
using Xunit;

namespace GameCenter.Tests;

/// <summary>Đọc save trong game của giả lập khác (SaveImporter).</summary>
public class SaveImporterTests
{
    private static byte[] Card(byte fill)
    {
        var card = new byte[SaveImporter.Ps1CardSize];
        Array.Fill(card, fill);
        card[0] = (byte)'M'; card[1] = (byte)'C';
        return card;
    }

    private static string Temp(TestEnv env, string name, byte[] data)
    {
        var path = Path.Combine(env.Root, "other-emulator", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static (GameDatabase Db, GameRecord Game) ScanOne(TestEnv env)
    {
        var db = new GameDatabase(env.Paths.DatabaseFile);
        db.Initialize(env.Catalog);
        db.ApplyScan(env.Scan());
        return (db, Assert.Single(db.GetGames()));
    }

    [Theory]
    [InlineData("card.mcr", 0)]      // ePSXe, thẻ nhớ thô
    [InlineData("card.mcd", 0)]      // DuckStation, PCSX
    [InlineData("card.gme", 3904)]   // DexDrive
    [InlineData("card.vmp", 128)]    // PSP / PS3
    [InlineData("card.mem", 64)]     // Virtual Game Station
    public void Ps1Card_HeaderIsStripped(string name, int header)
    {
        using var env = new TestEnv();
        var card = Card(0x5A);
        var file = Temp(env, name, new byte[header].Concat(card).ToArray());
        Assert.Equal(card, SaveImporter.Convert("PS1", file));
    }

    [Fact]
    public void Ps1Card_WrongFileIsRejected()
    {
        using var env = new TestEnv();
        Assert.Throws<InvalidDataException>(() => SaveImporter.Convert("PS1", Temp(env, "notes.mcr", new byte[1000])));
        Assert.Throws<InvalidDataException>(() => SaveImporter.Convert("PS1", Temp(env, "zero.mcr", new byte[SaveImporter.Ps1CardSize])));
        Assert.Throws<InvalidDataException>(() => SaveImporter.Convert("GBA", Temp(env, "quick.state", new byte[1000])));
        Assert.Throws<InvalidDataException>(() => SaveImporter.Convert("GBA", Temp(env, "empty.sav", Array.Empty<byte>())));
        Assert.Throws<InvalidDataException>(() => SaveImporter.Convert("PS2", Temp(env, "Mcd001.ps2", new byte[8 * 1024])));
    }

    [Fact]
    public void Import_BacksUpCurrentSave_ThenReplacesIt()
    {
        using var env = new TestEnv();
        env.File("GBA/Quest.gba", new byte[512]);
        var (db, game) = ScanOne(env);
        var paths = new AppPaths(env.Paths.AppDir, env.Paths.DataDir, Os.Android);
        var current = Path.Combine(paths.SavesDir, "Quest.srm");
        Directory.CreateDirectory(paths.SavesDir);
        File.WriteAllBytes(current, new byte[] { 1, 1, 1, 1 });

        var foreign = Temp(env, "Quest (My Boy).sav", new byte[] { 9, 8, 7, 6, 5 });
        var target = SaveImporter.Import(paths, env.Catalog, db.GetGames(), game, foreign);

        Assert.Equal(current, target);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, File.ReadAllBytes(current));
        // Save cũ lấy lại được bằng "Khôi phục save cũ"
        var guard = new SaveGuard(paths);
        var backup = Assert.Single(guard.ListBackups(guard.Locate(game, db.GetGames(), env.Catalog)));
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, File.ReadAllBytes(Path.Combine(backup.Dir, Assert.Single(backup.Files))));
    }

    [Fact]
    public void AutoImport_SaveNextToRom_UsedOnlyWhenGameHasNoSave()
    {
        using var env = new TestEnv();
        var rom = env.File("GBA/Quest.gba", new byte[512]);
        File.WriteAllBytes(Path.ChangeExtension(rom, ".sav"), new byte[] { 4, 4, 4 });
        var (db, game) = ScanOne(env);   // file .sav cạnh game không bị tính là một game
        var paths = new AppPaths(env.Paths.AppDir, env.Paths.DataDir, Os.Android);

        Assert.EndsWith("Quest.sav", SaveImporter.AutoImport(paths, env.Catalog, db.GetGames(), game));
        var srm = Path.Combine(paths.SavesDir, "Quest.srm");
        Assert.Equal(new byte[] { 4, 4, 4 }, File.ReadAllBytes(srm));

        // Đã có save rồi (người chơi đã chơi tiếp): không bao giờ tự ghi đè
        File.WriteAllBytes(srm, new byte[] { 7 });
        Assert.Null(SaveImporter.AutoImport(paths, env.Catalog, db.GetGames(), game));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(srm));
    }

    [Fact]
    public void Import_Desktop_GoesToCoreFolderRetroArchReads()
    {
        using var env = new TestEnv();
        env.File("GBA/Quest.gba", new byte[512]);
        var (db, game) = ScanOne(env);
        var paths = new AppPaths(env.Paths.AppDir, env.Paths.DataDir, Os.Windows);
        var target = SaveImporter.Import(paths, env.Catalog, db.GetGames(), game, Temp(env, "Quest.sav", new byte[] { 3 }));
        Assert.Equal(Path.Combine(paths.SavesDir, "mGBA", "Quest.srm"), target);
    }

    [Fact]
    public void PspSaves_CopiedWithoutOverwritingExisting()
    {
        using var env = new TestEnv();
        var source = Path.Combine(env.Root, "PSP", "SAVEDATA");
        var target = Path.Combine(env.Paths.SavesDir, "PSP", "SAVEDATA");
        foreach (var id in new[] { "ULUS10466", "NPJH50000" })
        {
            Directory.CreateDirectory(Path.Combine(source, id));
            File.WriteAllText(Path.Combine(source, id, "DATA.BIN"), "ppsspp " + id);
        }
        Directory.CreateDirectory(Path.Combine(target, "NPJH50000"));
        File.WriteAllText(Path.Combine(target, "NPJH50000", "DATA.BIN"), "game center");

        Assert.Equal(1, SaveImporter.ImportPspSaves(source, target));
        Assert.Equal("ppsspp ULUS10466", File.ReadAllText(Path.Combine(target, "ULUS10466", "DATA.BIN")));
        Assert.Equal("game center", File.ReadAllText(Path.Combine(target, "NPJH50000", "DATA.BIN")));
        Assert.Equal(0, SaveImporter.ImportPspSaves(source, target));
    }
}
