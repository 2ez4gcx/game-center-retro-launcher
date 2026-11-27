using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GameCenter.Core.Arcade;
using GameCenter.Core.Data;
using GameCenter.Core.Emulation;
using GameCenter.Core.Localization;
using GameCenter.Core.Scanning;
using GameCenter.Core.Util;

namespace GameCenter.Droid;

/// <summary>
/// Màn hình chính trên Android: một danh sách game chữ lớn, chạm vào game là chơi. Cùng các mục và câu chữ với
/// bản máy tính (src/GameCenter.Desktop): tìm kiếm không dấu, yêu thích, chơi gần đây, đổi tên, chọn hệ máy,
/// khôi phục save, cài đặt. Android không có cửa sổ con nên mọi hộp thoại là lớp phủ trong cùng màn hình.
/// Chưa có: ảnh bìa.
/// </summary>
public sealed class MainView : UserControl
{
    private static readonly IBrush Background0 = new SolidColorBrush(Color.Parse("#101418"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1B222B"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2E8BFF"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#9AA7B4"));
    private static readonly IBrush Gold = new SolidColorBrush(Color.Parse("#FFC83D"));

    private const string TabAll = "";
    private const string TabFav = "*fav";
    private const string TabRecent = "*recent";
    private const string TabUnknown = "*unknown";

    private readonly ListBox _tabs = new();
    private readonly ListBox _games = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _status = new() { FontSize = 16, Foreground = Dim, Margin = new Thickness(16, 6) };
    private readonly TextBlock _empty = new()
    {
        FontSize = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28), IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Grid _overlay = new() { IsVisible = false, Background = new SolidColorBrush(Color.Parse("#C0000000")) };
    private Action? _cancelDialog;

    private List<GameRecord> _all = new();
    private string _tab = TabAll;
    private bool _scanning;
    private bool _playing;
    private readonly Action _onResumed;

    private sealed record Tab(string Key, string Label);

    public MainView()
    {
        Background = Background0;

        var scan = HeaderButton(Plain(Loc.T("main.scan")));
        scan.Click += (_, _) => _ = RescanAsync();
        var settings = HeaderButton("⚙");
        settings.Click += (_, _) => _ = ShowSettings();
        var help = HeaderButton("?");
        help.Click += (_, _) => _ = ShowHelp();

        _search.Watermark = Plain(Loc.T("main.search"));
        _search.FontSize = 20;
        _search.Padding = new Thickness(14, 10);
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.Margin = new Thickness(0, 10, 0, 0);
        _search.TextChanged += (_, _) => ShowGames();

        // Hai hàng: tên app và các nút ở trên, ô tìm kiếm rộng hết chiều ngang ở dưới. Xếp chung một hàng thì màn hình
        // điện thoại (rộng khoảng 360 dp) không đủ chỗ: ô tìm kiếm bị ép sát và các nút chồng lên nhau, không bấm được.
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(16, 12, 16, 4),
        };
        header.Children.Add(new TextBlock
        {
            Text = "Game Center", FontSize = 28, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(scan, 1); header.Children.Add(scan);
        Grid.SetColumn(settings, 2); header.Children.Add(settings);
        Grid.SetColumn(help, 3); header.Children.Add(help);
        Grid.SetRow(_search, 1); Grid.SetColumnSpan(_search, 4); header.Children.Add(_search);

        _tabs.Background = Brushes.Transparent;
        _tabs.Margin = new Thickness(8, 0);
        _tabs.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        ScrollViewer.SetHorizontalScrollBarVisibility(_tabs, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollBarVisibility(_tabs, ScrollBarVisibility.Disabled);
        _tabs.ItemTemplate = new FuncDataTemplate<Tab>((t, _) =>
            new TextBlock { Text = t?.Label, FontSize = 20, Margin = new Thickness(10, 8) });
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedItem is Tab t && t.Key != _tab) { _tab = t.Key; ShowGames(); }
        };

        _games.Background = Brushes.Transparent;
        _games.ItemTemplate = new FuncDataTemplate<GameRecord>((g, _) => GameRow(g));
        _games.Tapped += (_, e) =>
        {
            // Chạm vào nút "⋯" trong dòng không phải là bấm chơi
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
            PlaySelected();
        };
        _games.KeyDown += (_, e) =>
        {
            // Enter, hoặc nút A của tay cầm / nút giữa của điều khiển TV
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; PlaySelected(); }
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        root.Children.Add(header);
        Grid.SetRow(_tabs, 1); root.Children.Add(_tabs);
        Grid.SetRow(_games, 2); root.Children.Add(_games);
        Grid.SetRow(_empty, 2); root.Children.Add(_empty);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Grid.SetRowSpan(_overlay, 4); root.Children.Add(_overlay);
        Content = root;

        _onResumed = () => Dispatcher.UIThread.Post(() => _ = RescanAsync());
        AttachedToVisualTree += (_, _) =>
        {
            if (MainActivity.Instance is { } activity)
            {
                activity.Resumed += _onResumed;
                activity.GamepadButton += OnGamepadButton;
            }
            if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested += OnBackRequested;
            FocusList();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (MainActivity.Instance is { } activity)
            {
                activity.Resumed -= _onResumed;
                activity.GamepadButton -= OnGamepadButton;
            }
            if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested -= OnBackRequested;
        };

        Reload();
        _ = RescanAsync();
    }

    /// <summary>
    /// Nhãn của bản máy tính mở đầu bằng biểu tượng ("✏  Đổi tên", "💾  Lưu lại"). Trên Android biểu tượng màu bị đo sai
    /// độ rộng nên chữ phía sau bị cắt mất: bỏ phần biểu tượng, chỉ giữ chữ.
    /// </summary>
    private static string Plain(string label)
    {
        int i = 0;
        while (i < label.Length && !char.IsLetterOrDigit(label[i])) i++;
        return i < label.Length ? label[i..] : label;
    }

    private static Button HeaderButton(string text) => new()
    {
        Content = text, FontSize = 20, Padding = new Thickness(18, 12), Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Nút tay cầm trên màn hình chính, như bản máy tính: Y yêu thích, X thêm tùy chọn, LB / RB đổi mục,
    /// START cài đặt, SELECT trợ giúp. (A = chơi và B = quay lại do MainActivity đổi thành Enter / Back.)
    /// </summary>
    private void OnGamepadButton(global::Android.Views.Keycode key)
    {
        if (_overlay.IsVisible || _playing) return;
        var game = _games.SelectedItem as GameRecord;
        switch (key)
        {
            case global::Android.Views.Keycode.ButtonY when game != null: ToggleFavorite(game); break;
            case global::Android.Views.Keycode.ButtonX when game != null: _ = ShowMore(game); break;
            case global::Android.Views.Keycode.ButtonL1: MoveTab(-1); break;
            case global::Android.Views.Keycode.ButtonR1: MoveTab(+1); break;
            case global::Android.Views.Keycode.ButtonStart: _ = ShowSettings(); break;
            case global::Android.Views.Keycode.ButtonSelect: _ = ShowHelp(); break;
        }
    }

    /// <summary>
    /// Đưa tiêu điểm phím về game đang chọn, để tay cầm / điều khiển TV / bàn phím bấm Lên, Xuống, A là dùng được ngay
    /// mà không phải bấm Tab qua các nút phía trên. Gọi sau mỗi lần nạp lại danh sách (nạp lại làm mất tiêu điểm),
    /// trừ khi đang gõ tìm kiếm hoặc đang có hộp thoại. Chạy sau khi bố cục xong vì dòng game phải được tạo ra trước.
    /// </summary>
    private void FocusList() => Dispatcher.UIThread.Post(() =>
    {
        if (_overlay.IsVisible || _search.IsFocused) return;
        var selected = _games.SelectedItem;
        ((selected != null ? _games.ContainerFromItem(selected) as Control : null) ?? _games).Focus();
    }, DispatcherPriority.Background);

    private void MoveTab(int step)
    {
        if (_tabs.ItemsSource is not List<Tab> tabs || tabs.Count == 0) return;
        int i = (tabs.FindIndex(t => t.Key == _tab) + step + tabs.Count) % tabs.Count;
        _tabs.SelectedItem = tabs[i];
        _tabs.ScrollIntoView(tabs[i]);
        _games.Focus();
    }

    /// <summary>Nút Back của Android: đang có hộp thoại thì đóng hộp thoại, không thì để hệ thống xử lý (thoát app).</summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (!_overlay.IsVisible) return;
        e.Handled = true;
        _cancelDialog?.Invoke();
    }

    private Control GameRow(GameRecord? g)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(6, 10) };
        if (g == null) return grid;

        // DockPanel (không phải StackPanel ngang): tên game được giới hạn bề rộng nên tự xuống dòng,
        // không tràn ra đè lên nút "⋯" trên màn hình hẹp
        var title = new DockPanel();
        if (g.IsFavorite)
        {
            var star = new TextBlock { Text = "★", FontSize = 22, Foreground = Gold, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(star, Dock.Left);
            title.Children.Add(star);
        }
        title.Children.Add(new TextBlock { Text = g.Name, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = PlatformLabel(g), FontSize = 16, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(text);

        var more = new Button
        {
            Content = "⋯", FontSize = 24, Padding = new Thickness(18, 10), Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        more.Click += (_, _) => { _games.SelectedItem = g; _ = ShowMore(g); };
        Grid.SetColumn(more, 1);
        grid.Children.Add(more);

        if (IsPlayable(g))
        {
            var play = new Border
            {
                Background = Accent, CornerRadius = new CornerRadius(10), Padding = new Thickness(20, 12), Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "▶", FontSize = 24, Foreground = Brushes.White },
            };
            Grid.SetColumn(play, 2);
            grid.Children.Add(play);
        }
        return grid;
    }

    private static bool IsPlayable(GameRecord g) =>
        g.ScanStatus is ScanStatus.Ok or ScanStatus.BrokenCue && App.Catalog.Get(g.Platform) is { Android: true };

    private static string PlatformLabel(GameRecord g) =>
        App.Catalog.Get(g.Platform)?.Label ?? Loc.T("tab.unknown");

    // ------------------------------------------------------------------ Danh sách

    private void Reload(long? selectId = null)
    {
        selectId ??= (_games.SelectedItem as GameRecord)?.Id;
        _all = App.Db.GetGames().OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var tabs = new List<Tab> { new(TabAll, Loc.T("tab.all")) };
        tabs.AddRange(App.Catalog.Platforms
            .Where(p => p.Enabled && _all.Any(g => IsPlayable(g) && g.Platform == p.Name))
            .Select(p => new Tab(p.Name, p.Label)));
        if (_all.Any(g => g.IsFavorite)) tabs.Add(new Tab(TabFav, "★ " + Loc.T("tab.favorites")));
        if (_all.Any(g => g.LastPlayedAt != null)) tabs.Add(new Tab(TabRecent, Loc.T("tab.recent")));
        if (_all.Any(g => !IsPlayable(g))) tabs.Add(new Tab(TabUnknown, Loc.T("tab.unknown")));
        if (tabs.All(t => t.Key != _tab)) _tab = TabAll;
        _tabs.ItemsSource = tabs;
        _tabs.SelectedItem = tabs.First(t => t.Key == _tab);
        ShowGames(selectId);
    }

    private void ShowGames(long? selectId = null)
    {
        selectId ??= (_games.SelectedItem as GameRecord)?.Id;
        IEnumerable<GameRecord> q = _tab switch
        {
            TabAll => _all.Where(IsPlayable),
            TabFav => _all.Where(g => g.IsFavorite),
            TabRecent => _all.Where(g => g.LastPlayedAt != null).OrderByDescending(g => g.LastPlayedAt),
            TabUnknown => _all.Where(g => !IsPlayable(g)),
            _ => _all.Where(g => IsPlayable(g) && g.Platform == _tab),
        };
        var search = (_search.Text ?? "").Trim();
        if (search.Length > 0)
        {
            var s = RemoveDiacritics(search);
            q = q.Where(g => RemoveDiacritics(g.Name).Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        var shown = q.ToList();
        _games.ItemsSource = shown;
        // Luôn có một game được chọn (game đầu tiên nếu chưa chọn gì): tay cầm / điều khiển TV bấm A là chơi ngay
        _games.SelectedItem = shown.FirstOrDefault(g => g.Id == selectId) ?? shown.FirstOrDefault();

        _games.IsVisible = shown.Count > 0;
        _empty.IsVisible = shown.Count == 0;
        if (shown.Count == 0)
        {
            _empty.Text = _all.Count == 0
                ? Loc.T("main.empty_title") + "\n\n" + Loc.T("android.empty_text", App.Paths.GamesDir)
                : Loc.T("main.empty_here_title") + "\n\n" + (search.Length > 0 ? Loc.T("main.empty_search", search) : Loc.T("main.empty_tab"));
        }
        _status.Text = _scanning ? Loc.T("main.scanning")
            : shown.Count == _all.Count ? Loc.Plural("main.status_count", _all.Count)
            : Loc.T("main.status_filtered", shown.Count, _all.Count);
        FocusList();
    }

    /// <summary>Tìm kiếm không cần gõ dấu ("dai chien" tìm ra "Đại chiến"), như bản máy tính.</summary>
    private static string RemoveDiacritics(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var chars = norm.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D');
    }

    private async Task RescanAsync()
    {
        if (_scanning || _playing) return;
        _scanning = true;
        _status.Text = Loc.T("main.scanning");
        try
        {
            var scanner = new GameScanner(App.Catalog, App.Paths.GamesDir, App.Paths.PlaylistsDir, App.Paths.BiosDir)
            {
                ManualDiscGroups = DiscGroups.Load(App.Paths).Groups,
            };
            var summary = await Task.Run(() => App.Db.ApplyScan(scanner.Scan()));
            Log.Info($"Quét xong: {summary}");
            if (scanner.BiosFiles.Count > 0)
            {
                try { Ps2Bios.ImportFromGames(scanner.BiosFiles, App.Paths.BiosDir); }
                catch (Exception ex) { Log.Error("Không chép được BIOS từ thư mục Games", ex); }
            }
        }
        catch (Exception ex) { Log.Error("Lỗi khi quét", ex); }
        finally
        {
            _scanning = false;
            Reload();
        }
    }

    // ------------------------------------------------------------------ Chơi

    private async void PlaySelected()
    {
        if (_playing || _overlay.IsVisible || _games.SelectedItem is not GameRecord game) return;
        if (MainActivity.Instance is not { } activity) return;
        if (!IsPlayable(game))
        {
            var platform = App.Catalog.Get(game.Platform);
            await Info(game.Name, platform is { Android: false } ? Loc.T("android.unsupported") : game.ScanMessage ?? Loc.T("launch.unknown_platform"));
            return;
        }
        _playing = true;
        try
        {
            // Game thùng: lần đầu chơi mỗi game nhắc bỏ xu (máy thùng phải bỏ xu rồi mới bấm START)
            if (game.Platform == ArcadeDatabase.Platform && game.PlayCount == 0)
                await Info(Loc.T("help.arcade_dialog_title"), Loc.T("android.arcade_coin"));
            await new GamePlayer { Confirm = Confirm, Info = Info }.PlayAsync(activity, game);
        }
        catch (Exception ex)
        {
            Log.Error("Lỗi khi chạy game", ex);
            await Info(Loc.T("play.error_title"), Loc.T("play.start_failed"));
        }
        finally
        {
            _playing = false;
            Reload(game.Id);
            _games.Focus();
        }
    }

    // ------------------------------------------------------------------ Tùy chọn của từng game

    private async Task ShowMore(GameRecord game)
    {
        if (_overlay.IsVisible || _playing) return;
        var options = new List<(string Label, Func<Task> Run)>
        {
            (Loc.T(game.IsFavorite ? "more.unfavorite" : "more.favorite"), () => { ToggleFavorite(game); return Task.CompletedTask; }),
            (Loc.T("more.rename"), () => Rename(game)),
            (Loc.T("more.choose_platform"), () => ChoosePlatform(game)),
            (Loc.T("more.restore_save"), () => RestoreSave(game)),
        };
        if (App.Catalog.IsDiscPlatform(game.Platform))
        {
            options.Add((Loc.T("more.merge_discs"), () => MergeDiscs(game)));
            if (DiscGroups.Load(App.Paths).GroupOf(game.SourceFile) != null)
                options.Add((Loc.T("more.split_discs"), () => SplitDiscs(game)));
        }
        int i = await Choose(game.Name, null, options.Select(o => o.Label).ToList());
        if (i >= 0) await options[i].Run();
    }

    private void ToggleFavorite(GameRecord game)
    {
        App.Db.SetFavorite(game.Id, !game.IsFavorite);
        Reload(game.Id);
    }

    private async Task Rename(GameRecord game)
    {
        var name = await AskText(Loc.T("rename.title"), Loc.T("rename.text"), game.Name);
        if (name == null) return;
        App.Db.SetDisplayTitle(game.Id, name == game.Title ? null : name);
        Reload(game.Id);
    }

    private async Task ChoosePlatform(GameRecord game)
    {
        var platforms = App.Catalog.Platforms.Where(p => p.Enabled && p.Android).ToList();
        int i = await Choose(Loc.T("platform.title"), Loc.T("platform.text", game.Name), platforms.Select(p => p.Label).ToList());
        if (i < 0) return;
        App.Db.SetPlatform(game.Id, platforms[i].Name);
        Reload(game.Id);
    }

    /// <summary>Khôi phục save từ các bản sao lưu tự động (tạo mỗi lần trước khi chơi).</summary>
    private async Task RestoreSave(GameRecord game)
    {
        var guard = new SaveGuard(App.Paths);
        var saves = guard.Locate(game, App.Db.GetGames(includeMissing: true), App.Catalog);
        var backups = guard.ListBackups(saves);
        if (backups.Count == 0)
        {
            await Info(Loc.T("restore.title"), Loc.T("restore.none"));
            return;
        }
        string When(SaveBackup b) => $"{Loc.Date(b.Time)}  {b.Time:HH:mm:ss}";
        int i = await Choose(Loc.T("restore.title"), Loc.T("restore.text"), backups.Select(When).ToList());
        if (i < 0) return;
        if (!await Confirm(Loc.T("restore.title"), Loc.T("restore.confirm", game.Name, When(backups[i])), Loc.T("restore.yes"), Loc.T("dialog.cancel"), true))
            return;
        try
        {
            guard.Restore(saves, backups[i]);
            await Info(Loc.T("restore.title"), Loc.T("restore.done"));
        }
        catch (Exception ex)
        {
            Log.Error("Khôi phục save lỗi", ex);
            await Info(Loc.T("settings.save_error_title"), ex.Message);
        }
    }

    /// <summary>
    /// Ghép thêm đĩa khi các đĩa khác tên nên không tự ghép được (đĩa 1 bản Việt hóa, đĩa sau bản gốc...).
    /// Chọn game kế tiếp (là bộ nhiều đĩa thì góp hết các đĩa), quét lại để thành một game nhiều đĩa.
    /// </summary>
    private async Task MergeDiscs(GameRecord game)
    {
        var candidates = _all.Where(g => g.Id != game.Id && g.Platform == game.Platform).ToList();
        if (candidates.Count == 0)
        {
            await Info(Loc.T("merge.title"), Loc.T("merge.none", PlatformLabel(game)));
            return;
        }
        string Label(GameRecord g) => $"{g.Name}  ·  {Path.GetFileName(g.SourceFile)}" +
                                      (g.DiscCount >= 2 ? $"  ·  {Loc.T("game.discs", g.DiscCount)}" : "");
        int i = await Choose(Loc.T("merge.title"), Loc.T("merge.text", game.Name, Math.Max(1, game.DiscCount) + 1),
            candidates.Select(Label).ToList());
        if (i < 0) return;
        var groups = DiscGroups.Load(App.Paths);
        groups.Append(game.SourceFile, candidates[i].SourceFile);
        groups.Save();
        Log.Info($"Ghép đĩa: {game.SourceFile} + {candidates[i].SourceFile}");
        await RescanAsync();
    }

    private async Task SplitDiscs(GameRecord game)
    {
        var groups = DiscGroups.Load(App.Paths);
        if (!groups.Split(game.SourceFile)) return;
        groups.Save();
        Log.Info($"Tách đĩa đã ghép: {game.SourceFile}");
        await RescanAsync();
    }

    // ------------------------------------------------------------------ Trợ giúp

    private const string Author = "Khuong Doan";
    private const string Contact = "https://khuongdoan.com/";

    private static string AppVersion()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            return context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.VersionName ?? "";
        }
        catch { return ""; }
    }

    private async Task ShowHelp()
    {
        if (_overlay.IsVisible || _playing) return;
        var body = new StackPanel { Spacing = 10 };
        void Add(string title, string text)
        {
            body.Children.Add(SectionLabel(title));
            body.Children.Add(new TextBlock { Text = text, FontSize = 19, TextWrapping = TextWrapping.Wrap });
        }
        Add(Loc.T("help.play_title"), Loc.T("android.help_play", App.Paths.GamesDir));
        Add(Loc.T("help.exit_title"), Loc.T("android.help_exit"));
        Add(Loc.T("help.pad_title"), Loc.T("android.help_pad"));
        Add(Loc.T("help.disc_title"), Loc.T("android.help_disc"));
        Add(Loc.T("help.arcade_title"), Loc.T("android.help_arcade") + "\n\n" + Loc.T("android.arcade_coin"));
        Add(Loc.T("help.bios_title"), Loc.T("android.help_bios", App.Paths.BiosDir));
        Add(Loc.T("help.errors_title"), Loc.T("android.help_errors", App.Paths.LogFile));
        Add(Loc.T("help.about_title"), Loc.T("help.about_text", AppVersion(), Author, Contact));
        Add(Loc.T("help.license_title"), Loc.T("android.help_license"));

        bool licenses = false;
        var licenseButton = new Button { Content = Loc.T("help.license_title"), FontSize = 20, Padding = new Thickness(20, 12) };
        licenseButton.Click += (_, _) => { licenses = true; _cancelDialog?.Invoke(); };
        body.Children.Add(licenseButton);

        await ShowDialog(Loc.T("help.title"), null, body, new[] { Loc.T("dialog.close") }, vertical: false, cancelIndex: 0);
        if (licenses) await ShowLicenses();
    }

    /// <summary>Giấy phép của Game Center và từng thành phần nguồn mở (các file .txt trong Licenses/, đóng trong APK).</summary>
    private async Task ShowLicenses()
    {
        var files = Directory.Exists(App.Paths.LicensesDir)
            ? Directory.EnumerateFiles(App.Paths.LicensesDir, "*.txt", SearchOption.AllDirectories).OrderBy(f => f).ToList()
            : new List<string>();
        if (files.Count == 0) return;
        while (true)
        {
            int i = await Choose(Loc.T("help.license_title"), null, files.Select(f => Path.GetFileNameWithoutExtension(f)!).ToList());
            if (i < 0) return;
            string text;
            try { text = File.ReadAllText(files[i]); }
            catch (Exception ex) { text = ex.Message; }
            await Info(Path.GetFileNameWithoutExtension(files[i]), text);
        }
    }

    // ------------------------------------------------------------------ Cài đặt

    private async Task ShowSettings()
    {
        if (_overlay.IsVisible || _playing) return;
        var body = new StackPanel { Spacing = 14 };

        body.Children.Add(SectionLabel(Loc.T("settings.language")));
        var languages = new WrapPanel();
        string? newLanguage = null;
        foreach (var (code, name) in Loc.Available)
        {
            var b = new Button { Content = name, FontSize = 20, Padding = new Thickness(20, 12), Margin = new Thickness(0, 0, 10, 8) };
            // Ngôn ngữ đang dùng: nút màu xanh. Không gán null cho nút còn lại (sẽ mất nền và chữ của giao diện mặc định).
            if (code == Loc.Language) { b.Background = Accent; b.Foreground = Brushes.White; }
            b.Click += (_, _) => { newLanguage = code; _cancelDialog?.Invoke(); };
            languages.Children.Add(b);
        }
        body.Children.Add(languages);

        body.Children.Add(SectionLabel(Loc.T("settings.saves")));
        var askState = new CheckBox
        {
            IsChecked = App.Settings.AskSaveStateOnExit, FontSize = 20,
            Content = new TextBlock { Text = Loc.T("settings.ask_state"), TextWrapping = TextWrapping.Wrap, FontSize = 20 },
        };
        body.Children.Add(askState);
        body.Children.Add(new TextBlock { Text = Loc.T("settings.saves_text"), FontSize = 17, Foreground = Dim, TextWrapping = TextWrapping.Wrap });

        body.Children.Add(SectionLabel(Loc.T("settings.data")));
        body.Children.Add(new TextBlock { Text = Loc.T("settings.data_dir", App.Paths.DataDir), FontSize = 17, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        bool backup = false;
        var backupButton = new Button { Content = Plain(Loc.T("settings.backup")), FontSize = 20, Padding = new Thickness(20, 12) };
        backupButton.Click += (_, _) => { backup = true; _cancelDialog?.Invoke(); };
        body.Children.Add(backupButton);

        await ShowDialog(Loc.T("settings.title"), null, body, new[] { Loc.T("dialog.close") }, vertical: false, cancelIndex: 0);

        if (askState.IsChecked != App.Settings.AskSaveStateOnExit)
        {
            App.Settings.AskSaveStateOnExit = askState.IsChecked == true;
            try { App.Settings.Save(App.Paths.SettingsJson); }
            catch (Exception ex) { Log.Error("Không lưu được cài đặt", ex); }
        }
        if (backup) await Backup();
        if (newLanguage != null) App.SwitchLanguage(newLanguage);
    }

    private static TextBlock SectionLabel(string text) =>
        new() { Text = text, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Dim, Margin = new Thickness(0, 8, 0, 0) };

    private async Task Backup()
    {
        try
        {
            var zip = await Task.Run(() => BackupService.CreateBackup(App.Paths));
            await Info(Loc.T("backup.done_title"), Loc.T("backup.done_text", zip));
        }
        catch (Exception ex)
        {
            Log.Error("Sao lưu lỗi", ex);
            await Info(Loc.T("backup.error_title"), ex.Message);
        }
    }

    // ------------------------------------------------------------------ Hộp thoại (lớp phủ)

    private Task Info(string title, string text) =>
        ShowDialog(title, text, null, new[] { Loc.T("dialog.ok") }, vertical: false, cancelIndex: 0);

    /// <param name="cancellable">Nút Back của Android được tính là trả lời "không".</param>
    private async Task<bool> Confirm(string title, string text, string yes, string no, bool cancellable) =>
        await ShowDialog(title, text, null, new[] { no, yes }, vertical: false, cancelIndex: cancellable ? 0 : null) == 1;

    /// <summary>Chọn một trong nhiều dòng (mỗi dòng một nút lớn). Trả về -1 nếu hủy.</summary>
    private async Task<int> Choose(string title, string? text, IReadOnlyList<string> options)
    {
        var buttons = options.Append(Loc.T("dialog.cancel")).ToList();
        int i = await ShowDialog(title, text, null, buttons, vertical: true, cancelIndex: buttons.Count - 1);
        return i == buttons.Count - 1 ? -1 : i;
    }

    private async Task<string?> AskText(string title, string text, string initial)
    {
        var box = new TextBox { Text = initial, FontSize = 20, Padding = new Thickness(14, 10) };
        int i = await ShowDialog(title, text, box, new[] { Loc.T("dialog.cancel"), Loc.T("dialog.save") }, vertical: false, cancelIndex: 0);
        var value = (box.Text ?? "").Trim();
        return i == 1 && value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Hộp thoại chung. Nút cuối là nút chính (màu xanh) khi xếp ngang; xếp dọc thì mọi nút như nhau.
    /// <paramref name="cancelIndex"/>: nút được coi là đã bấm khi người dùng bấm Back (null: bắt buộc phải chọn).
    /// </summary>
    private Task<int> ShowDialog(string title, string? text, Control? content, IReadOnlyList<string> buttons, bool vertical, int? cancelIndex)
    {
        var done = new TaskCompletionSource<int>();
        void Close(int answer)
        {
            _cancelDialog = null;
            _overlay.IsVisible = false;
            _overlay.Children.Clear();
            done.TrySetResult(answer);
        }
        _cancelDialog = cancelIndex is { } c ? () => Close(c) : null;

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        var middle = new StackPanel { Spacing = 14, Margin = new Thickness(0, 14, 0, 0) };
        if (!string.IsNullOrEmpty(text)) middle.Children.Add(new TextBlock { Text = text, FontSize = 20, TextWrapping = TextWrapping.Wrap });
        if (content != null) middle.Children.Add(content);

        Button? primary = null;
        var buttonPanel = new StackPanel
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = vertical ? 10 : 14,
            HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            Margin = new Thickness(0, vertical ? 0 : 20, 0, 0),
        };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = Plain(buttons[i]), FontSize = 20, Padding = new Thickness(22, 14),
                HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            };
            if (!vertical && i == buttons.Count - 1) { b.Background = Accent; b.Foreground = Brushes.White; primary = b; }
            b.Click += (_, _) => Close(index);
            buttonPanel.Children.Add(b);
        }
        if (vertical) middle.Children.Add(buttonPanel);
        // Phần giữa cuộn được khi dài (nhiều lựa chọn, máy màn hình thấp); nút xếp ngang luôn nằm ngoài vùng cuộn
        body.Children.Add(new ScrollViewer { MaxHeight = Math.Max(240, Bounds.Height - 260), Content = middle });
        if (!vertical) body.Children.Add(buttonPanel);

        _overlay.Children.Add(new Border
        {
            Background = Panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(24), Margin = new Thickness(20), MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = body,
        });
        _overlay.IsVisible = true;
        (primary ?? buttonPanel.Children.OfType<Button>().FirstOrDefault())?.Focus();
        return done.Task;
    }
}
