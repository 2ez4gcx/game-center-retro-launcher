using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GameCenter.Core.Config;
using GameCenter.Core.Data;
using GameCenter.Core.Localization;
using GameCenter.Core.Util;

namespace GameCenter.Droid;

public partial class App : Avalonia.Application
{
    public static AppPaths Paths { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static PlatformCatalog Catalog { get; private set; } = null!;
    public static GameDatabase Db { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            string? error = null;
            try { Start(global::Android.App.Application.Context); }
            catch (Exception ex)
            {
                Log.Error("Lỗi khởi động", ex);
                global::Android.Util.Log.Error("GameCenter", ex.ToString());
                error = ex.Message;
            }
            single.MainView = error == null
                ? new MainView()
                : new TextBlock
                {
                    Text = error, FontSize = 22, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24),
                    VerticalAlignment = VerticalAlignment.Center,
                };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Đổi ngôn ngữ giao diện ngay: lưu cài đặt rồi dựng lại màn hình chính với chuỗi mới.</summary>
    public static void SwitchLanguage(string code)
    {
        if (code == Loc.Language) return;
        Settings.Language = code;
        Loc.Use(code);
        try { Settings.Save(Paths.SettingsJson); }
        catch (Exception ex) { Log.Error("Không lưu được ngôn ngữ", ex); }
        Log.Info($"Đổi ngôn ngữ: {code}");
        if (Current?.ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = new MainView();
    }

    /// <summary>Nạp lại toàn bộ (đường dẫn, cài đặt, database) rồi dựng lại màn hình chính: dùng sau khi đổi thư mục dữ liệu.</summary>
    public static void Restart()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Start(global::Android.App.Application.Context);
        if (Current?.ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = new MainView();
    }

    /// <summary>
    /// Thư mục ứng dụng (chỉ đọc trên máy tính) ở đây là thư mục riêng của app: Defaults/ và Licenses/ được chép ra
    /// từ APK mỗi lần khởi động. Core libretro nằm trong thư mục thư viện native của app.
    /// Thư mục dữ liệu (Games, Saves, BIOS...): xem <see cref="Storage"/>.
    /// </summary>
    private static void Start(global::Android.Content.Context context)
    {
        var appDir = context.FilesDir!.AbsolutePath;
        var dataDir = Storage.ResolveDataDir(context);
        CopyAssets(context, "Defaults", Path.Combine(appDir, "Defaults"));
        CopyAssets(context, "Licenses", Path.Combine(appDir, "Licenses"));

        Paths = new AppPaths(appDir, dataDir, Os.Android) { CoresDirOverride = context.ApplicationInfo!.NativeLibraryDir };
        Paths.EnsureDataFolders();
        Log.Init(Paths.LogFile);
        InstallSystemAssets(context);
        // Dung lượng trống của bộ nhớ chứa game (DriveInfo trên Android chỉ thấy phân vùng gốc)
        GameCenter.Core.Scanning.DiscArchive.FreeSpaceProbe = dir => new global::Android.OS.StatFs(dir).AvailableBytes;
        Log.Info($"Khởi động Android. AppDir={Paths.AppDir} DataDir={Paths.DataDir} Cores={Paths.CoresDir}");

        bool hadSettings = File.Exists(Paths.SettingsJson);
        Settings = AppSettings.Load(Paths.SettingsJson);
        if (!Loc.IsAvailable(Settings.Language))
        {
            Settings.Language = Loc.DetectDefault(Paths.InstallerIni, upgradedFromOldVersion: hadSettings);
            Settings.Save(Paths.SettingsJson);
        }
        Loc.Use(Settings.Language);

        Catalog = PlatformCatalog.Load(Paths.PlatformsJson);
        Db = new GameDatabase(Paths.DatabaseFile);
        Db.Initialize(Catalog);
    }

    /// <summary>
    /// Tài nguyên core cần trong thư mục hệ thống (BIOS/): PPSSPP đòi phông chữ và dữ liệu của nó ở BIOS/PPSSPP.
    /// Đóng trong APK (assets/System), chép ra một lần cho mỗi bản cài (ghi dấu bằng thời điểm cập nhật app).
    /// </summary>
    private static void InstallSystemAssets(global::Android.Content.Context context)
    {
        try
        {
            if ((context.Assets!.List("System") ?? Array.Empty<string>()).Length == 0) return;
            var stamp = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.LastUpdateTime.ToString();
            var marker = Path.Combine(Paths.BiosDir, ".gamecenter-system-assets");
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp) return;
            CopyAssets(context, "System", Paths.BiosDir);
            File.WriteAllText(marker, stamp);
            Log.Info("Đã chép tài nguyên hệ thống của core vào thư mục BIOS");
        }
        catch (Exception ex) { Log.Error("Không chép được tài nguyên hệ thống của core", ex); }
    }

    private static void CopyAssets(global::Android.Content.Context context, string assetDir, string targetDir)
    {
        var assets = context.Assets!;
        var entries = assets.List(assetDir) ?? Array.Empty<string>();
        Directory.CreateDirectory(targetDir);
        foreach (var name in entries)
        {
            var assetPath = assetDir + "/" + name;
            // Thư mục con: List trả về danh sách không rỗng
            if ((assets.List(assetPath) ?? Array.Empty<string>()).Length > 0)
            {
                CopyAssets(context, assetPath, Path.Combine(targetDir, name));
                continue;
            }
            using var src = assets.Open(assetPath);
            using var dst = File.Create(Path.Combine(targetDir, name));
            src.CopyTo(dst);
        }
    }
}
