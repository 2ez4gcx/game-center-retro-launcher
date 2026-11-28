using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameCenter.Core.Config;

public enum Os { Windows, MacOS, Linux, Android }

/// <summary>
/// Khác biệt giữa Windows / macOS / Linux: vị trí RetroArch, đuôi file core,
/// driver hình / tay cầm mặc định, cách mở thư mục.
/// </summary>
public static class OsPlatform
{
    public static Os Current =>
        OperatingSystem.IsWindows() ? Os.Windows :
        OperatingSystem.IsMacOS() ? Os.MacOS :
        OperatingSystem.IsAndroid() ? Os.Android : Os.Linux;

    /// <summary>
    /// Android: không có RetroArch đi kèm. Core nằm sẵn trong APK và chạy ngay trong app (LibretroDroid),
    /// xem <see cref="GameCenter.Core.Emulation.EmulatorLauncher.PlanEmbedded"/>.
    /// </summary>
    public static bool UsesEmbeddedCores(Os os) => os == Os.Android;

    /// <summary>File chạy RetroArch, tính từ thư mục Emulators.</summary>
    public static string RetroArchExe(string emulatorsDir, Os os) => os switch
    {
        Os.Windows => Path.Combine(emulatorsDir, "RetroArch", "retroarch.exe"),
        // Không sửa bên trong RetroArch.app (sẽ hỏng chữ ký); core để ngoài bundle
        Os.MacOS => Path.Combine(emulatorsDir, "RetroArch.app", "Contents", "MacOS", "RetroArch"),
        // Bản Linux: file chạy hoặc AppImage của RetroArch, đặt tên "retroarch"
        _ => Path.Combine(emulatorsDir, "RetroArch", "retroarch"),
    };

    /// <summary>Thư mục core: Windows/Linux trong RetroArch/cores, macOS trong Emulators/cores.</summary>
    public static string CoresDir(string emulatorsDir, Os os) =>
        os == Os.MacOS ? Path.Combine(emulatorsDir, "cores") : Path.Combine(emulatorsDir, "RetroArch", "cores");

    /// <summary>Thư mục phụ của RetroArch (info, assets, autoconfig): chỉ Windows/Linux có sẵn cạnh file chạy.</summary>
    public static string? RetroArchResourceDir(string emulatorsDir, Os os) =>
        os == Os.MacOS ? null : Path.Combine(emulatorsDir, "RetroArch");

    public static string CoreExtension(Os os) => os switch
    {
        Os.Windows => ".dll",
        Os.MacOS => ".dylib",
        _ => ".so",
    };

    /// <summary>
    /// Tên core trong platforms.json ghi dạng Windows ("x_libretro.dll"); đổi sang tên file của hệ hiện tại.
    /// Android: bản buildbot tên "x_libretro_android.so"; đóng trong APK phải có tiền tố "lib" thì Android mới giải nén.
    /// </summary>
    public static string CoreFileName(string core, Os os) =>
        os == Os.Android
            ? "lib" + Path.GetFileNameWithoutExtension(core) + "_android.so"
            : Path.GetFileNameWithoutExtension(core) + CoreExtension(os);

    /// <summary>Driver tay cầm cho RetroArch (null: để RetroArch tự chọn).</summary>
    public static string? JoypadDriver(Os os) => os switch
    {
        Os.Windows => "xinput",
        Os.Linux => "udev",
        _ => null,
    };

    /// <summary>
    /// Máy dùng được Vulkan không: có driver Vulkan thật, không chỉ có file bộ nạp (xem <see cref="VulkanProbe"/>).
    /// macOS: không có Vulkan gốc → dùng renderer phần mềm.
    /// </summary>
    public static bool IsVulkanAvailable(Os os) => VulkanProbe.IsUsable(os);

    /// <summary>
    /// Driver hình khi không dùng Vulkan. Windows: Direct3D 11, có trên mọi máy Windows 10/11 kể cả máy không có card
    /// đồ họa (Windows tự vẽ bằng CPU); OpenGL thì máy ảo / Remote Desktop / driver mặc định của Windows không có đủ.
    /// </summary>
    public static string FallbackVideoDriver(Os os) => os switch
    {
        Os.Windows => "d3d11",
        Os.MacOS => "glcore",
        _ => "gl",
    };

    /// <summary>Mở thư mục bằng trình quản lý file của hệ điều hành.</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        var psi = Current switch
        {
            Os.Windows => new ProcessStartInfo("explorer.exe"),
            Os.MacOS => new ProcessStartInfo("open"),
            _ => new ProcessStartInfo("xdg-open"),
        };
        psi.UseShellExecute = false;
        psi.ArgumentList.Add(path);
        Process.Start(psi);
    }

    /// <summary>Mở file văn bản (giấy phép) bằng trình mặc định.</summary>
    public static void OpenFile(string path)
    {
        if (Current == Os.Windows)
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(Current == Os.MacOS ? "open" : "xdg-open") { ArgumentList = { path }, UseShellExecute = false });
    }

    public static bool IsArm => RuntimeInformation.OSArchitecture == Architecture.Arm64;
}
