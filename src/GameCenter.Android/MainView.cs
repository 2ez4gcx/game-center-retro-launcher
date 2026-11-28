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
    private readonly Button _storageButton = new()
    {
        FontSize = 20, Padding = new Thickness(20, 14), Margin = new Thickness(28, 0, 28, 28), IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
    };
    private bool _awaitingStorage;
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

        // Nút có chữ, không dùng biểu tượng trần (bánh răng, dấu hỏi): người lớn tuổi đọc chữ dễ hơn đoán hình
        var scan = HeaderButton(Plain(Loc.T("main.scan")));
        scan.Click += (_, _) => _ = RescanAsync();
        var settings = HeaderButton(Loc.T("settings.title"));
        settings.Click += (_, _) => _ = ShowSettings();
        var help = HeaderButton(Loc.T("help.title"));
        help.Click += (_, _) => _ = ShowHelp();

        _search.Watermark = Plain(Loc.T("main.search"));
        _search.FontSize = 20;
        _search.Padding = new Thickness(14, 10);
        _search.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) => ShowGames();

        // Màn hình hẹp (điện thoại cầm dọc, rộng khoảng 360 dp): ba hàng (tên app, ba nút chia đều chiều ngang, ô tìm
        // kiếm); xếp chung một hàng thì ô tìm kiếm bị ép sát và các nút chồng lên nhau, không bấm được.
        // Màn hình rộng (điện thoại xoay ngang, máy tính bảng, TV): một hàng, để dành chiều cao cho danh sách game
        // (điện thoại xoay ngang mà giữ ba hàng thì chỉ còn thấy một game).
        var header = new Grid { Margin = new Thickness(16, 12, 16, 4) };
        var title = new TextBlock { Text = "Game Center", FontSize = 28, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var actions = new UniformGrid { Columns = 3 };
        actions.Children.Add(scan);
        actions.Children.Add(settings);
        actions.Children.Add(help);
        header.Children.Add(title);
        header.Children.Add(actions);
        header.Children.Add(_search);
        bool? wideHeader = null;
        void LayoutHeader(double width)
        {
            bool wide = width >= 700;
            if (wide == wideHeader) return;
            wideHeader = wide;
            header.RowDefinitions = new RowDefinitions(wide ? "Auto" : "Auto,Auto,Auto");
            header.ColumnDefinitions = new ColumnDefinitions(wide ? "Auto,Auto,*" : "*");
            Grid.SetRow(actions, wide ? 0 : 1); Grid.SetColumn(actions, wide ? 1 : 0);
            Grid.SetRow(_search, wide ? 0 : 2); Grid.SetColumn(_search, wide ? 2 : 0);
            actions.Margin = wide ? new Thickness(12, 0, 8, 0) : new Thickness(-4, 10, -4, 10);
        }
        LayoutHeader(0);
        SizeChanged += (_, e) => LayoutHeader(e.NewSize.Width);

        _tabs.Background = Brushes.Transparent;
        _tabs.Margin = new Thickness(8, 0);
        _tabs.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        ScrollViewer.SetHorizontalScrollBarVisibility(_tabs, ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollBarVisibility(_tabs, ScrollBarVisibility.Disabled);
        _tabs.ItemTemplate = new FuncDataTemplate<Tab>((t, _) =>
            new TextBlock { Text = t?.Label, FontSize = 20, LineHeight = 28, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 8) });
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
        _storageButton.Content = new TextBlock { Text = Loc.T("android.storage_button"), FontSize = 20, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        _storageButton.Click += (_, _) => _ = OfferSharedFolder();
        Grid.SetRow(_storageButton, 2); root.Children.Add(_storageButton);
        Grid.SetRow(_status, 3); root.Children.Add(_status);
        Grid.SetRowSpan(_overlay, 4); root.Children.Add(_overlay);
        Content = root;
        // Hộp thoại đang mở thì khóa phần phía sau: nếu không, phím Tab / phím hướng của tay cầm đưa tiêu điểm lọt ra
        // các nút và ô tìm kiếm bên dưới lớp phủ (bàn phím ảo bật lên, bấm nhầm nút không nhìn thấy)
        _overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            foreach (var child in root.Children)
                if (!ReferenceEquals(child, _overlay)) child.IsEnabled = !_overlay.IsVisible;
        };

        _onResumed = () => Dispatcher.UIThread.Post(() => _ = _awaitingStorage ? StorageAccessAnswered() : RescanAsync());
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
        Content = new TextBlock { Text = text, FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis },
        Padding = new Thickness(8, 14), Margin = new Thickness(4, 0), MinWidth = 100,
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
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
        // Game nhiều đĩa ghi rõ số đĩa: người chơi biết cả bộ đã được ghép thành một game
        var sub = PlatformLabel(g) + (g.DiscCount >= 2 ? "  ·  " + Loc.T("game.discs", g.DiscCount) : "")
                  + (IsExperimental(g) ? "  ·  " + Loc.T("android.experimental") : "");
        text.Children.Add(new TextBlock { Text = sub, FontSize = 16, Foreground = Dim, Margin = new Thickness(0, 4, 0, 0) });
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

    /// <summary>
    /// Hệ máy chạy được nhưng chưa đạt trên Android. PS2 (core Play!): đo trên Galaxy Note 9 chỉ được 15-25% tốc độ máy
    /// thật, trên máy ảo x86_64 chạy bằng máy tính cũng chỉ 60-79%; điểm nghẽn nằm trong core.
    /// </summary>
    private static bool IsExperimental(GameRecord g) => g.Platform == "PS2";

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
        _storageButton.IsVisible = _all.Count == 0 && !UsingSharedFolder;
        if (shown.Count == 0)
        {
            _empty.Text = _all.Count == 0
                ? Loc.T("main.empty_title") + "\n\n" + Loc.T("android.empty_text", Friendly(App.Paths.GamesDir))
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
        await OfferExtract(onlyNew: true);
    }

    // ------------------------------------------------------------------ Giải nén game đĩa trong zip

    /// <summary>File zip chứa game đĩa đã hỏi trong lần mở app này (người chơi chọn "Để sau" thì không hỏi lại mãi).</summary>
    private readonly HashSet<string> _extractAsked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Game đĩa (PS1 / PS2 / PSP) còn nằm trong file .zip: đề nghị giải nén giúp, tất cả trong một lần (game nhiều đĩa
    /// thường là mỗi đĩa một file zip). Tự giải nén trên điện thoại là việc khó với người không rành máy.
    /// </summary>
    private async Task OfferExtract(bool onlyNew)
    {
        if (_playing || _scanning || _overlay.IsVisible) return;
        var zips = await Task.Run(() => _all.Where(g => !IsPlayable(g)).Select(g => g.SourceFile).Distinct()
            .Where(f => (!onlyNew || !_extractAsked.Contains(f)) && DiscArchive.IsDiscZip(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList());
        if (zips.Count == 0) return;
        foreach (var z in zips) _extractAsked.Add(z);

        string need = DiscArchive.Mb(await Task.Run(() => DiscArchive.RequiredBytes(zips)));
        string offer = zips.Count == 1 ? Loc.T("extract.offer_one", Path.GetFileName(zips[0]), need) : Loc.T("extract.offer_many", zips.Count, need);
        if (!await Confirm(Loc.T("extract.title"), offer, Loc.T("extract.yes"), Loc.T("extract.no"), true)) return;

        _scanning = true;   // chặn quét và chơi game trong lúc giải nén
        string? error = null;
        MainActivity.Instance?.Window?.AddFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
        try
        {
            for (int i = 0; i < zips.Count; i++)
            {
                string name = Path.GetFileName(zips[i]);
                long total = Math.Max(1, DiscArchive.RequiredBytes(new[] { zips[i] }));
                int index = i, shown = -1;
                ShowBusy(Loc.T("extract.working", index + 1, zips.Count, name, 0));
                try
                {
                    await Task.Run(() => DiscArchive.Extract(zips[index], done =>
                    {
                        int percent = (int)Math.Min(100, done * 100 / total);
                        if (percent == shown) return;
                        shown = percent;
                        Dispatcher.UIThread.Post(() => ShowBusy(Loc.T("extract.working", index + 1, zips.Count, name, percent)));
                    }));
                }
                catch (Exception ex)
                {
                    Log.Error($"Không giải nén được {zips[i]}", ex);
                    error = Loc.T("extract.failed", name, ex.Message);
                    break;
                }
            }
        }
        finally
        {
            // Lệnh ShowBusy gửi từ luồng giải nén có thể còn nằm trong hàng đợi: đóng lớp phủ sau chúng
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            HideBusy();
            MainActivity.Instance?.Window?.ClearFlags(global::Android.Views.WindowManagerFlags.KeepScreenOn);
            _scanning = false;
        }
        await RescanAsync();
        await Info(Loc.T("extract.title"), error ?? Loc.T("extract.done"));
    }

    // ------------------------------------------------------------------ Chơi

    private async void PlaySelected()
    {
        if (_playing || _overlay.IsVisible || _games.SelectedItem is not GameRecord game) return;
        if (MainActivity.Instance is not { } activity) return;
        if (!IsPlayable(game))
        {
            var platform = App.Catalog.Get(game.Platform);
            if (platform == null && DiscArchive.IsDiscZip(game.SourceFile)) { await OfferExtract(onlyNew: false); return; }
            await Info(game.Name, platform is { Android: false } ? Loc.T("android.unsupported") : game.ScanMessage ?? Loc.T("launch.unknown_platform"));
            return;
        }
        _playing = true;
        try
        {
            // Game thùng: lần đầu chơi mỗi game nhắc bỏ xu (máy thùng phải bỏ xu rồi mới bấm START)
            if (game.Platform == ArcadeDatabase.Platform && game.PlayCount == 0)
                await Info(Loc.T("help.arcade_dialog_title"), Loc.T("android.arcade_coin"));
            // PS2: lần đầu chơi mỗi game nói trước là sẽ chậm, để người chơi không tưởng máy hay game bị hỏng
            if (IsExperimental(game) && game.PlayCount == 0
                && !await Confirm(Loc.T("android.ps2_title"), Loc.T("android.ps2_text"), Loc.T("android.ps2_try"), Loc.T("dialog.cancel"), true))
                return;
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
            (Loc.T("more.import_save"), () => ImportSave(game)),
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

    /// <summary>
    /// Dùng save trong game của giả lập khác (<see cref="SaveImporter"/>): file nằm cạnh game được gợi ý trước, không thì
    /// mở trình chọn file của máy. PSP: chép thư mục save của PPSSPP.
    /// </summary>
    private async Task ImportSave(GameRecord game)
    {
        string title = Loc.T("import.title");
        if (game.Platform == "PSP")
        {
            if (!Storage.HasAccess(global::Android.App.Application.Context)) { await Info(title, Loc.T("import.psp_no_access")); return; }
            string source = Path.Combine(global::Android.OS.Environment.ExternalStorageDirectory!.AbsolutePath, "PSP", "SAVEDATA");
            try
            {
                int copied = await Task.Run(() => SaveImporter.ImportPspSaves(source, Path.Combine(App.Paths.SavesDir, "PSP", "SAVEDATA")));
                await Info(title, copied > 0 ? Loc.T("import.psp_done", copied) : Loc.T("import.psp_none", Friendly(source)));
            }
            catch (Exception ex)
            {
                Log.Error("Không chép được save PSP", ex);
                await Info(title, Loc.T("import.failed", ex.Message));
            }
            return;
        }
        if (!SaveImporter.Supports(game.Platform)) { await Info(title, Loc.T("import.unsupported_platform")); return; }

        string? file = null;
        string? shownName = null;
        var sidecars = SaveImporter.FindSidecars(game);
        if (sidecars.Count > 0)
        {
            var labels = sidecars.Select(f => Path.GetFileName(f)!).Append(Loc.T("import.pick_other")).ToList();
            int i = await Choose(title, Loc.T("import.pick", game.Name), labels);
            if (i < 0) return;
            if (i < sidecars.Count) file = sidecars[i];
        }
        string? temp = null;
        try
        {
            if (file == null)
            {
                var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (provider == null) return;
                var picked = await provider.OpenFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerOpenOptions
                    { Title = title, AllowMultiple = false });
                if (picked.Count == 0) return;
                // Trình chọn file của Android trả về luồng dữ liệu, không phải đường dẫn: chép ra thư mục tạm của app
                shownName = picked[0].Name;
                var dir = Path.Combine(global::Android.App.Application.Context.CacheDir!.AbsolutePath, "import");
                Directory.CreateDirectory(dir);
                temp = Path.Combine(dir, shownName);
                await using (var input = await picked[0].OpenReadAsync())
                await using (var output = File.Create(temp))
                    await input.CopyToAsync(output);
                file = temp;
            }
            shownName ??= Path.GetFileName(file);
            try { SaveImporter.Convert(game.Platform, file); }
            catch (InvalidDataException ex) { await Info(title, Loc.T("import.failed", ex.Message)); return; }
            if (!await Confirm(title, Loc.T("import.confirm", shownName, game.Name), Loc.T("import.yes"), Loc.T("dialog.cancel"), true)) return;
            SaveImporter.Import(App.Paths, App.Catalog, App.Db.GetGames(includeMissing: true), game, file);
            await Info(title, Loc.T("import.done"));
        }
        catch (Exception ex)
        {
            Log.Error("Không nhập được save", ex);
            await Info(title, Loc.T("import.failed", ex.Message));
        }
        finally
        {
            if (temp != null) { try { File.Delete(temp); } catch (Exception) { /* file tạm, xóa không được thì thôi */ } }
        }
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
        Add(Loc.T("help.play_title"), Loc.T("android.help_play", Friendly(App.Paths.GamesDir)));
        Add(Loc.T("help.exit_title"), Loc.T("android.help_exit"));
        Add(Loc.T("android.help_touch_title"), Loc.T("android.help_touch"));
        Add(Loc.T("help.pad_title"), Loc.T("android.help_pad"));
        Add(Loc.T("help.disc_title"), Loc.T("android.help_disc"));
        Add(Loc.T("help.arcade_title"), Loc.T("android.help_arcade") + "\n\n" + Loc.T("android.arcade_coin"));
        Add(Loc.T("android.help_ps2_title"), Loc.T("android.ps2_text"));
        Add(Loc.T("help.bios_title"), Loc.T("android.help_bios", Friendly(App.Paths.BiosDir)));
        Add(Loc.T("help.errors_title"), Loc.T("android.help_errors", Friendly(App.Paths.LogFile)));
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
        body.Children.Add(new TextBlock { Text = Loc.T("settings.data_dir", Friendly(App.Paths.DataDir)), FontSize = 17, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        bool moveFolder = false;
        if (!UsingSharedFolder)
        {
            var move = new Button
            {
                Content = new TextBlock { Text = Loc.T("android.storage_button"), FontSize = 20, TextWrapping = TextWrapping.Wrap },
                Padding = new Thickness(20, 12),
            };
            move.Click += (_, _) => { moveFolder = true; _cancelDialog?.Invoke(); };
            body.Children.Add(move);
        }
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
        if (moveFolder) { await OfferSharedFolder(); return; }
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

    // ------------------------------------------------------------------ Thư mục GameCenter ở bộ nhớ trong

    private static bool UsingSharedFolder =>
        string.Equals(App.Paths.DataDir, Storage.SharedDir, StringComparison.Ordinal);

    private static string Friendly(string path) => Storage.Friendly(path, Loc.T("android.internal_storage"));

    /// <summary>
    /// Mời chuyển sang thư mục "GameCenter" ngay ở bộ nhớ trong (dễ chép game hơn thư mục trong Android/data).
    /// Giải thích trước, rồi mới mở màn hình xin quyền của Android; người dùng tự bật quyền ở đó.
    /// </summary>
    private async Task OfferSharedFolder()
    {
        if (_overlay.IsVisible || _playing || UsingSharedFolder || MainActivity.Instance is not { } activity) return;
        if (!await Confirm(Loc.T("android.storage_title"), Loc.T("android.storage_explain", Friendly(Storage.SharedDir)),
                Loc.T("android.storage_go"), Loc.T("dialog.cancel"), true))
            return;
        if (Storage.HasAccess(activity))
        {
            await MoveToSharedFolder();
            return;
        }
        _awaitingStorage = true;
        try { Storage.RequestAccess(activity); }
        catch (Exception ex)
        {
            _awaitingStorage = false;
            Log.Error("Không mở được màn hình xin quyền truy cập file", ex);
            await Info(Loc.T("android.storage_title"), Loc.T("android.storage_denied"));
        }
    }

    /// <summary>App quay lại sau màn hình xin quyền: có quyền thì chuyển dữ liệu, không thì giữ nguyên thư mục cũ.</summary>
    private async Task StorageAccessAnswered()
    {
        _awaitingStorage = false;
        if (MainActivity.Instance is { } activity && Storage.HasAccess(activity)) await MoveToSharedFolder();
        else await Info(Loc.T("android.storage_title"), Loc.T("android.storage_denied"));
    }

    private async Task MoveToSharedFolder()
    {
        while (_scanning) await Task.Delay(100);
        _scanning = true; // chặn quét trong lúc chuyển file
        var from = App.Paths.DataDir;
        var to = Storage.SharedDir;
        ShowBusy(Loc.T("android.storage_moving"));
        int failed = -1;
        try
        {
            failed = await Task.Run(() => Storage.Migrate(from, to));
            Log.Info($"Chuyển thư mục dữ liệu: {from} -> {to}, {failed} file không chuyển được");
        }
        catch (Exception ex) { Log.Error("Chuyển thư mục dữ liệu lỗi", ex); }
        HideBusy();
        _scanning = false;
        if (failed < 0)
        {
            await Info(Loc.T("android.storage_title"), Loc.T("android.storage_failed"));
            return;
        }
        await Info(Loc.T("android.storage_title"),
            Loc.T("android.storage_done", Friendly(Path.Combine(to, "Games"))) + (failed > 0 ? "\n\n" + Loc.T("android.storage_left", failed, Friendly(from)) : ""));
        App.Restart();
    }

    /// <summary>Thông báo đang làm việc lâu (không có nút, không đóng được bằng Back).</summary>
    private void ShowBusy(string text)
    {
        _cancelDialog = null;
        _overlay.Children.Clear();
        _overlay.Children.Add(new Border
        {
            Background = Panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(28), Margin = new Thickness(20), MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 22, TextWrapping = TextWrapping.Wrap },
        });
        _overlay.IsVisible = true;
    }

    private void HideBusy()
    {
        _overlay.IsVisible = false;
        _overlay.Children.Clear();
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

    private static bool CanScroll(ScrollViewer scroll, bool down) =>
        down ? scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 1 : scroll.Offset.Y > 1;

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
            // Đóng hộp thoại thì tiêu điểm mất theo nút vừa bấm: đưa về danh sách game, nếu không phím hướng / nút A
            // của tay cầm không còn tác dụng. Làm sau cùng, khi bên gọi đã kịp mở hộp thoại kế tiếp (nếu có).
            Dispatcher.UIThread.Post(() => { if (!_overlay.IsVisible) FocusList(); }, DispatcherPriority.Background);
        }
        _cancelDialog = cancelIndex is { } c ? () => Close(c) : null;

        // Ba hàng: tiêu đề, phần giữa cuộn được (chiếm phần còn lại), hàng nút. Dùng Grid chứ không dùng StackPanel:
        // StackPanel không giới hạn chiều cao, trên màn hình thấp (điện thoại xoay ngang) hàng nút bị đẩy ra ngoài hộp.
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var middle = new StackPanel { Spacing = 14 };
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
                // Chữ dài (tên file, câu lựa chọn) tự xuống dòng thay vì bị cắt cụt
                Content = new TextBlock { Text = Plain(buttons[i]), FontSize = 20, TextWrapping = vertical ? TextWrapping.Wrap : TextWrapping.NoWrap },
                Padding = new Thickness(22, 14),
                HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                HorizontalContentAlignment = vertical ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            };
            if (!vertical && i == buttons.Count - 1) { b.Background = Accent; b.Foreground = Brushes.White; primary = b; }
            b.Click += (_, _) => Close(index);
            buttonPanel.Children.Add(b);
        }
        if (vertical) middle.Children.Add(buttonPanel);
        // Phần giữa cuộn được khi dài (nhiều lựa chọn, máy màn hình thấp); nút xếp ngang luôn nằm ngoài vùng cuộn
        var scroll = new ScrollViewer { Content = middle };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        if (!vertical) { Grid.SetRow(buttonPanel, 2); body.Children.Add(buttonPanel); }

        bool low = Bounds.Height < 480;
        var box = new Border
        {
            Background = Panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(24, low ? 16 : 24), Margin = new Thickness(20, low ? 8 : 20), MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = body,
        };
        // Phím hướng lên / xuống (tay cầm, điều khiển TV): chuyển sang nút kế tiếp nếu nó đang nằm trong vùng nhìn thấy,
        // không thì cuộn nội dung một đoạn. Nhờ vậy đọc được hết phần chữ dài (Trợ giúp) mà không cần chạm màn hình.
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || e.Source is TextBox) return;
            bool down = e.Key == Key.Down;
            e.Handled = true;
            var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            var next = current == null ? null
                : KeyboardNavigationHandler.GetNext(current, down ? NavigationDirection.Next : NavigationDirection.Previous) as Control;
            if (next != null && current != null && box.IsVisualAncestorOf(next))
            {
                double from = current.TranslatePoint(default, box)?.Y ?? 0;
                double to = next.TranslatePoint(default, box)?.Y ?? 0;
                bool forward = down ? to >= from : to <= from;
                // Nút trong vùng cuộn mà còn khuất: cuộn tới trước đã, lần bấm sau mới chuyển tiêu điểm
                bool visible = true;
                if (scroll.IsVisualAncestorOf(next) && next.TranslatePoint(default, scroll) is { } at)
                    visible = at.Y >= -1 && at.Y + next.Bounds.Height <= scroll.Viewport.Height + 1;
                if (forward && visible) { next.Focus(NavigationMethod.Directional); return; }
                if (forward && !CanScroll(scroll, down)) { next.Focus(NavigationMethod.Directional); next.BringIntoView(); return; }
            }
            if (CanScroll(scroll, down))
                scroll.Offset = scroll.Offset.WithY(Math.Clamp(scroll.Offset.Y + (down ? 1 : -1) * Math.Max(80, scroll.Viewport.Height * 0.6), 0, scroll.Extent.Height - scroll.Viewport.Height));
        }, RoutingStrategies.Tunnel);
        _overlay.Children.Add(box);
        _overlay.IsVisible = true;
        var first = primary ?? buttonPanel.Children.OfType<Button>().FirstOrDefault();
        first?.Focus();
        // Hộp thoại mở ngay sau khi hộp thoại khác đóng: lúc này nút chưa nằm trong cây hiển thị nên Focus() ở trên chưa
        // có tác dụng (bấm A / Enter không chọn được gì). Đặt lại tiêu điểm sau khi đã dựng xong.
        Dispatcher.UIThread.Post(() => { if (_overlay.IsVisible && box.IsVisualAncestorOf(first!) && !box.IsKeyboardFocusWithin) first?.Focus(); }, DispatcherPriority.Loaded);
        return done.Task;
    }
}
