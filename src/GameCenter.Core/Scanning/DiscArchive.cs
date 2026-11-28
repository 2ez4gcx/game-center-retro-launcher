using System.IO.Compression;
using GameCenter.Core.Localization;

namespace GameCenter.Core.Scanning;

/// <summary>
/// Game đĩa (PS1 / PS2 / PSP) nằm trong file .zip: core không đọc đĩa trong zip được, phải giải nén. Lớp này giải nén
/// giúp người chơi, ra ngay thư mục chứa file zip (tên file giữ nguyên, bỏ thư mục con bên trong zip), để lần quét sau
/// nhận game như bình thường. File zip gốc không bị xóa; khi đã giải nén đủ thì lần quét sau bỏ qua nó.
/// </summary>
public static class DiscArchive
{
    /// <summary>Đuôi ảnh đĩa và file đi kèm cần lấy ra (.bin / .sub / .img là dữ liệu của .cue / .ccd).</summary>
    private static readonly HashSet<string> Wanted = new(StringComparer.OrdinalIgnoreCase)
        { ".cue", ".bin", ".iso", ".cso", ".zso", ".chd", ".pbp", ".m3u", ".ccd", ".img", ".sub", ".mdf", ".mds", ".nrg", ".sbi" };

    private static readonly HashSet<string> Disc = new(StringComparer.OrdinalIgnoreCase)
        { ".cue", ".iso", ".cso", ".zso", ".chd", ".pbp", ".ccd", ".mdf", ".nrg" };

    private static List<ZipArchiveEntry> Entries(ZipArchive zip) =>
        zip.Entries.Where(e => e.Length > 0 && !string.IsNullOrEmpty(e.Name) && Wanted.Contains(Path.GetExtension(e.Name))).ToList();

    /// <summary>File zip (không mật khẩu) có chứa ảnh đĩa.</summary>
    public static bool IsDiscZip(string zipPath)
    {
        try
        {
            if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(zipPath)) return false;
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = Entries(zip);
            return entries.Any(e => Disc.Contains(Path.GetExtension(e.Name))) && !entries.Any(e => e.IsEncrypted);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Mọi file game trong zip đã có sẵn (đúng tên, đúng kích thước) ở thư mục chứa zip.</summary>
    public static bool IsExtracted(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return IsExtracted(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, Entries(zip));
        }
        catch (Exception) { return false; }
    }

    internal static bool IsExtracted(string dir, IReadOnlyCollection<ZipArchiveEntry> entries) =>
        entries.Count > 0 && entries
            .Where(e => e.Length > 0 && Wanted.Contains(Path.GetExtension(e.Name)))
            .All(e => new FileInfo(Path.Combine(dir, e.Name)) is { Exists: true } f && f.Length == e.Length);

    /// <summary>Dung lượng cần thêm (byte) để giải nén các file zip này.</summary>
    public static long RequiredBytes(IEnumerable<string> zipPaths)
    {
        long total = 0;
        foreach (var path in zipPaths)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
                total += Entries(zip).Where(e => !(new FileInfo(Path.Combine(dir, e.Name)) is { Exists: true } f && f.Length == e.Length)).Sum(e => e.Length);
            }
            catch (Exception) { /* file hỏng: Extract sẽ báo */ }
        }
        return total;
    }

    /// <summary>
    /// Giải nén một file zip ra thư mục chứa nó. File đã có sẵn đúng kích thước thì bỏ qua. Ghi ra file tạm ".part"
    /// rồi mới đổi tên, nên bị tắt ngang không để lại ảnh đĩa dở dang mang tên thật.
    /// <paramref name="progress"/>: số byte đã ghi của file zip này.
    /// </summary>
    public static void Extract(string zipPath, Action<long>? progress = null, CancellationToken cancel = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = Entries(zip);
        long need = entries.Sum(e => e.Length);
        long free = FreeBytes(dir);
        if (free >= 0 && free < need + 50L * 1024 * 1024)
            throw new IOException(Loc.T("extract.no_space", Path.GetFileName(zipPath), Mb(need), Mb(free)));

        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var entry in entries)
        {
            var target = Path.Combine(dir, entry.Name);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == entry.Length)
            {
                done += entry.Length;
                progress?.Invoke(done);
                continue;
            }
            var part = target + ".part";
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n);
                        done += n;
                        progress?.Invoke(done);
                    }
                }
                File.Move(part, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(part); } catch (Exception) { /* dọn không được thì thôi */ }
                throw;
            }
        }
    }

    /// <summary>
    /// Cách hỏi dung lượng trống của thư mục (Android đặt bằng StatFs: ở đó DriveInfo chỉ thấy phân vùng gốc "/",
    /// không phải bộ nhớ chứa game). Trả về số âm khi không biết: khi đó không chặn, cứ giải nén.
    /// </summary>
    public static Func<string, long>? FreeSpaceProbe { get; set; }

    public static long FreeBytes(string dir)
    {
        try
        {
            if (FreeSpaceProbe != null) return FreeSpaceProbe(dir);
            return OperatingSystem.IsWindows() ? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace : -1;
        }
        catch (Exception) { return -1; }
    }

    public static string Mb(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
        : $"{Math.Max(1, bytes / (1024 * 1024))} MB";
}
