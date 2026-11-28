using GameCenter.Core.Config;
using GameCenter.Core.Util;

namespace GameCenter.Core.Emulation;

/// <summary>
/// Tài nguyên core cần có trong thư mục hệ thống của RetroArch (thư mục BIOS của người dùng). PPSSPP (PSP) đòi phông
/// chữ và dữ liệu của nó ở BIOS/PPSSPP; thiếu thì chữ trong game và hộp thoại lưu của PSP không hiện.
/// Bộ cài để sẵn các tài nguyên đó ở &lt;thư mục cài&gt;/Emulators/System (chỉ đọc); lớp này chép sang BIOS/ khi chưa có
/// hoặc khi bản trong bộ cài mới hơn. Không bao giờ xóa hay ghi đè file người dùng tự để trong BIOS/ ngoài các thư mục
/// tài nguyên đó.
/// </summary>
public static class SystemAssets
{
    private const string Marker = ".gamecenter-assets";

    public static string SourceDir(AppPaths paths) => Path.Combine(paths.EmulatorsDir, "System");

    /// <summary>Chép các thư mục tài nguyên còn thiếu / đã cũ. Trả về số thư mục đã chép. Lỗi chỉ ghi log.</summary>
    public static int Install(AppPaths paths)
    {
        int copied = 0;
        try
        {
            var source = SourceDir(paths);
            if (!Directory.Exists(source)) return 0;
            foreach (var dir in Directory.GetDirectories(source))
            {
                var target = Path.Combine(paths.BiosDir, Path.GetFileName(dir));
                var stamp = Stamp(dir);
                var marker = Path.Combine(target, Marker);
                if (File.Exists(marker) && File.ReadAllText(marker) == stamp) continue;
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(target, Path.GetRelativePath(dir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(file, to, overwrite: true);
                }
                File.WriteAllText(marker, stamp);
                copied++;
                Log.Info($"Đã chép tài nguyên hệ thống {Path.GetFileName(dir)} vào thư mục BIOS");
            }
        }
        catch (Exception ex) { Log.Error("Không chép được tài nguyên hệ thống của core", ex); }
        return copied;
    }

    /// <summary>Dấu nhận biết phiên bản tài nguyên: số file và tổng dung lượng (đổi khi bộ cài mới kèm bản khác).</summary>
    private static string Stamp(string dir)
    {
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        return $"{files.Length}:{files.Sum(f => new FileInfo(f).Length)}";
    }
}
