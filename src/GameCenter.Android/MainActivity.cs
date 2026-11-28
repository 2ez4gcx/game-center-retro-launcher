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

    /// <summary>Nút tay cầm dành cho màn hình chính (Y, X, LB, RB, START, SELECT); báo một lần mỗi lần bấm.</summary>
    public event Action<Android.Views.Keycode>? GamepadButton;

    /// <summary>
    /// Tay cầm trên màn hình chính. Phím hướng tới giao diện như phím mũi tên sẵn rồi; ở đây đổi A thành Enter
    /// (chơi, bấm nút đang chọn), B thành Back (đóng hộp thoại), các nút còn lại báo cho màn hình chính.
    /// </summary>
    public override bool DispatchKeyEvent(Android.Views.KeyEvent? e)
    {
        if (e == null) return base.DispatchKeyEvent(e);
        switch (e.KeyCode)
        {
            case Android.Views.Keycode.ButtonA:
                return base.DispatchKeyEvent(new Android.Views.KeyEvent(e.Action, Android.Views.Keycode.Enter));
            case Android.Views.Keycode.ButtonB:
                return base.DispatchKeyEvent(new Android.Views.KeyEvent(e.Action, Android.Views.Keycode.Back));
            case Android.Views.Keycode.ButtonY:
            case Android.Views.Keycode.ButtonX:
            case Android.Views.Keycode.ButtonL1:
            case Android.Views.Keycode.ButtonR1:
            case Android.Views.Keycode.ButtonStart:
            case Android.Views.Keycode.ButtonSelect:
                if (e.Action == Android.Views.KeyEventActions.Down && e.RepeatCount == 0) GamepadButton?.Invoke(e.KeyCode);
                return true;
        }
        return base.DispatchKeyEvent(e);
    }

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
