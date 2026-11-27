using Android.App;
using Android.Content;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace GameCenter.Droid;

[Activity(
    Label = "Game Center",
    Theme = "@style/GameCenterTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity<App>
{
    private const int PlayRequest = 1001;
    private TaskCompletionSource<(Result Result, Intent? Data)>? _playing;

    public static MainActivity? Instance { get; private set; }

    /// <summary>App quay lại màn hình (mở lại, hoặc vừa thoát game): giao diện quét lại thư mục game.</summary>
    public event Action? Resumed;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).LogToTrace();

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        Instance = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Resumed?.Invoke();
    }

    protected override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// <summary>Mở màn hình chơi game và chờ đến khi người chơi thoát.</summary>
    public Task<(Result Result, Intent? Data)> PlayAsync(Intent intent)
    {
        _playing?.TrySetCanceled();
        _playing = new TaskCompletionSource<(Result, Intent?)>();
        StartActivityForResult(intent, PlayRequest);
        return _playing.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PlayRequest) return;
        var playing = _playing;
        _playing = null;
        playing?.TrySetResult((resultCode, data));
    }
}
