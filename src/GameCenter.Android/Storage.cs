using Android.App;
using Android.Content;
using Android.OS;
using GameCenter.Core.Util;
using Microsoft.Data.Sqlite;

namespace GameCenter.Droid;

/// <summary>
/// Nơi để dữ liệu (Games, Saves, BIOS...) trên Android.
/// - Mặc định: phần riêng của app trong bộ nhớ chung (Android/data/&lt;gói&gt;/files). Không cần xin quyền, nhưng nằm
///   sâu và khó tìm, nhất là với người không rành máy.
/// - Tùy chọn: thư mục "GameCenter" ngay ở gốc bộ nhớ trong, dễ chép game hơn nhiều. Cần quyền truy cập file
///   (Android 11+: "Truy cập mọi file"; Android 10 trở xuống: quyền bộ nhớ), người dùng tự bật khi được hỏi.
/// Chỉ dùng thư mục chung khi đã chuyển dữ liệu sang xong (có file đánh dấu) và quyền còn hiệu lực; mất quyền thì
/// quay về thư mục riêng để app vẫn mở được.
/// </summary>
public static class Storage
{
    private const string Marker = ".gamecenter";

    public static string SharedDir =>
        Path.Combine(global::Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath, "GameCenter");

    public static string AppSpecificDir(Context context) =>
        (context.GetExternalFilesDir(null) ?? context.FilesDir)!.AbsolutePath;

    public static bool HasAccess(Context context) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.R
            ? global::Android.OS.Environment.IsExternalStorageManager
            : context.CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) == global::Android.Content.PM.Permission.Granted;

    public static string ResolveDataDir(Context context)
    {
        try
        {
            if (HasAccess(context) && File.Exists(Path.Combine(SharedDir, Marker))) return SharedDir;
        }
        catch (Exception ex) { Log.Error("Không kiểm tra được thư mục GameCenter ở bộ nhớ chung", ex); }
        return AppSpecificDir(context);
    }

    /// <summary>Mở màn hình xin quyền của Android. Kết quả xem bằng <see cref="HasAccess"/> khi app quay lại.</summary>
    public static void RequestAccess(Activity activity)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            try
            {
                activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionManageAppAllFilesAccessPermission,
                    global::Android.Net.Uri.Parse("package:" + activity.PackageName)));
            }
            catch (ActivityNotFoundException)
            {
                activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionManageAllFilesAccessPermission));
            }
        }
        else
        {
            activity.RequestPermissions(new[]
            {
                global::Android.Manifest.Permission.WriteExternalStorage, global::Android.Manifest.Permission.ReadExternalStorage,
            }, 2001);
        }
    }

    /// <summary>
    /// Chuyển toàn bộ dữ liệu từ <paramref name="from"/> sang <paramref name="to"/>: không ghi đè file đã có ở nơi mới,
    /// file nào không chuyển được thì để nguyên ở nơi cũ. Sau đó sửa đường dẫn cũ trong database và danh sách đĩa ghép
    /// để yêu thích, số lần chơi và game nhiều đĩa không bị mất. Trả về số file không chuyển được.
    /// </summary>
    public static int Migrate(string from, string to)
    {
        Directory.CreateDirectory(to);
        int failed = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var name = Path.GetFileName(entry);
            if (name == "Logs") continue;
            failed += MoveEntry(entry, Path.Combine(to, name));
        }
        SqliteConnection.ClearAllPools();
        try { RewritePaths(to, from, to); }
        catch (Exception ex) { Log.Error("Không sửa được đường dẫn sau khi chuyển thư mục dữ liệu", ex); }
        File.WriteAllText(Path.Combine(to, Marker), "");
        return failed;
    }

    private static int MoveEntry(string source, string target)
    {
        if (Directory.Exists(source))
        {
            if (!Directory.Exists(target))
            {
                // Cùng phân vùng thì đổi tên là xong ngay; Android thường không cho đổi tên qua lại Android/data: chuyển từng file
                try { Directory.Move(source, target); return 0; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Directory.CreateDirectory(target);
            int failed = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                failed += MoveEntry(child, Path.Combine(target, Path.GetFileName(child)));
            try { if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source); } catch { }
            return failed;
        }
        if (File.Exists(target)) return 0;
        try { File.Move(source, target); return 0; }
        catch (Exception ex)
        {
            Log.Error($"Không chuyển được {source}", ex);
            return 1;
        }
    }

    private static void RewritePaths(string dataDir, string oldRoot, string newRoot)
    {
        var groups = Path.Combine(dataDir, "Config", "disc-groups.json");
        if (File.Exists(groups)) File.WriteAllText(groups, File.ReadAllText(groups).Replace(oldRoot, newRoot));

        var db = Path.Combine(dataDir, "Database", "games.db");
        if (!File.Exists(db)) return;
        using (var con = new SqliteConnection($"Data Source={db}"))
        {
            con.Open();
            var tables = new List<string>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            foreach (var table in tables)
            {
                var columns = new List<string>();
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var type = r.IsDBNull(2) ? "" : r.GetString(2);
                        if (type.Length == 0 || type.Contains("TEXT", StringComparison.OrdinalIgnoreCase)) columns.Add(r.GetString(1));
                    }
                }
                foreach (var column in columns)
                {
                    using var cmd = con.CreateCommand();
                    // instr(...) = 1: chỉ sửa giá trị bắt đầu bằng thư mục cũ (không dùng LIKE vì đường dẫn có thể chứa "_" và "%")
                    cmd.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = $new || substr(\"{column}\", length($old) + 1) WHERE instr(\"{column}\", $old) = 1";
                    cmd.Parameters.AddWithValue("$old", oldRoot);
                    cmd.Parameters.AddWithValue("$new", newRoot);
                    cmd.ExecuteNonQuery();
                }
            }
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Đường dẫn hiện cho người dùng: "/storage/emulated/0/GameCenter/Games" thành "Bộ nhớ trong > GameCenter > Games",
    /// đúng như cách trình quản lý file và máy tính hiển thị.
    /// </summary>
    public static string Friendly(string path, string internalStorageLabel)
    {
        var root = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        if (string.IsNullOrEmpty(root) || !path.StartsWith(root, StringComparison.Ordinal)) return path;
        var rest = path[root.Length..].Trim('/');
        return rest.Length == 0 ? internalStorageLabel : internalStorageLabel + " > " + rest.Replace("/", " > ");
    }
}
