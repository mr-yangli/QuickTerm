namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using QuickTerm.Terminal;

/// <summary>
/// Termora's Fence layout with its visual language: one integrated top bar (app name + sidebar toggle + new tab
/// on the left, the tab strip in the middle, small monochrome line-icon tools and the system caption buttons on
/// the right), a compact host tree with line icons and a solid-blue selection on the left, and one live terminal
/// per tab. No colour emoji and no doubled title bar. Ctrl+Shift+B toggles the sidebar.
/// </summary>
public sealed class MainWindow : Window
{
    private const double DefaultSidebarWidth = 200;
    private const double BarHeight = 32;

    private readonly QuickTermStore _store;
    private readonly List<Session> _sessions = new();
    private readonly Panel _surface = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly TreeView _hostTree = new() { Margin = new Thickness(2, 4, 2, 0) };
    private readonly TextBox _search = new() { PlaceholderText = UiStrings.Get("SearchHosts"), IsVisible = false, Margin = new Thickness(6, 4, 6, 4) };
    private readonly TextBlock _status = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 8, 0),
        Opacity = 0.6,
        FontSize = 12,
    };

    private Grid _rootGrid = null!;
    private Border _scrim = null!;
    private Border _overlay = null!;
    private Control _sidebar = null!;
    private Control _welcome = null!;
    private Session? _active;
    private bool _sidebarVisible = true;
    private double _savedSidebarWidth = DefaultSidebarWidth;

    public MainWindow(QuickTermStore store)
    {
        _store = store;
        Title = UiStrings.Get("AppTitle");
        Width = 1280;
        Height = 800;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // One bar, not two: extend the client area over the decorations so our strip sits flush at the very
        // top (Termora's full-window-content look); BorderOnly keeps a resize border without a system title bar.
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = BarHeight;

        var selection = new SolidColorBrush(Color.FromRgb(0x33, 0x6E, 0xB4));
        Resources["TreeViewItemSelectedBackground"] = selection;
        Resources["TreeViewItemSelectedForeground"] = Brushes.White;
        Resources["TreeViewItemSelectedBorder"] = selection;

        Content = BuildLayout();

        Focusable = true;
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = TerminalView.SharedTextInputClient);
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    private static readonly IBrush Hover = new SolidColorBrush(Color.FromRgb(0xE4, 0xE6, 0xE8));
    private static readonly IBrush CloseHover = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

    private static Button IconButton(string geometry, double size = 20)
    {
        var button = new Button
        {
            Content = TermIcons.Icon(geometry, size),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.PointerEntered += (_, _) => button.Background = Hover;
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    /// <summary>A flat Windows-style caption button: 46 px, hover tint, and a red close hover with a white glyph.</summary>
    private Button CaptionButton(string geometry, double glyphSize, bool isClose, Action onClick)
    {
        var glyph = TermIcons.Glyph(geometry);
        var ink = glyph.Stroke;
        var button = new Button
        {
            Content = new Viewbox
            {
                Width = glyphSize,
                Height = glyphSize,
                Stretch = Stretch.Uniform,
                Child = glyph,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Width = 46,
            Height = BarHeight,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        button.PointerEntered += (_, _) =>
        {
            button.Background = isClose ? CloseHover : Hover;
            if (isClose) glyph.Stroke = Brushes.White;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
            if (isClose) glyph.Stroke = ink;
        };
        return button;
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.T when shift:
                e.Handled = true;
                _ = AddHostAsync();
                break;
            case Key.W when shift:
                if (_active is not null)
                {
                    e.Handled = true;
                    CloseSession(_active);
                }
                break;
            case Key.B when shift:
                e.Handled = true;
                ToggleSidebar();
                break;
            case Key.Tab:
                e.Handled = true;
                CycleTabs(shift ? -1 : 1);
                break;
        }
    }

    private void CycleTabs(int delta)
    {
        if (_sessions.Count < 2)
            return;
        var index = _active is null ? 0 : _sessions.IndexOf(_active);
        var next = (index + delta + _sessions.Count) % _sessions.Count;
        Activate(_sessions[next]);
    }

    private void ToggleSidebar()
    {
        _sidebarVisible = !_sidebarVisible;
        if (_sidebarVisible)
        {
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(_savedSidebarWidth);
            _sidebar.IsVisible = true;
        }
        else
        {
            _savedSidebarWidth = _rootGrid.ColumnDefinitions[0].Width.Value;
            _rootGrid.ColumnDefinitions[0].Width = new GridLength(0);
            _sidebar.IsVisible = false;
        }
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private Control BuildLayout()
    {
        // --- Top bar: app identity + toggle + new tab on the left, tools + caption space on the right ---
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        identity.Children.Add(TermIcons.Icon(TermIcons.Person, 18));
        identity.Children.Add(new TextBlock { Text = UiStrings.Get("AppTitle"), FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var toggle = IconButton(TermIcons.Sidebar);
        toggle.Click += (_, _) => ToggleSidebar();
        var addTab = IconButton(TermIcons.Plus);
        addTab.Click += async (_, _) => await AddHostAsync();

        var leftActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        leftActions.Children.Add(toggle);
        leftActions.Children.Add(addTab);

        // The left cluster is exactly the sidebar width, with its icons pinned to the right edge, so the tab
        // strip begins at the sidebar divider and the whole top bar lines up with the tree below it.
        var left = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Width = DefaultSidebarWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(identity, 0);
        Grid.SetColumn(leftActions, 1);
        left.Children.Add(identity);
        left.Children.Add(leftActions);

        var gear = IconButton(TermIcons.Gear);
        gear.Click += (_, _) => _ = OpenSettingsAsync();
        var search = IconButton(TermIcons.Search);
        search.Click += (_, _) =>
        {
            _search.IsVisible = !_search.IsVisible;
            if (_search.IsVisible) _search.Focus();
        };
        var transfer = IconButton(TermIcons.Folder);
        transfer.Click += (_, _) => OpenSftp();
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        tools.Children.Add(transfer);
        tools.Children.Add(search);
        tools.Children.Add(gear);

        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        captions.Children.Add(CaptionButton(TermIcons.Minimize, 16, false, () => WindowState = WindowState.Minimized));
        captions.Children.Add(CaptionButton(TermIcons.Maximize, 16, false, ToggleMaximize));
        captions.Children.Add(CaptionButton(TermIcons.Close, 16, true, Close));

        var tabsScroll = new ScrollViewer
        {
            Content = _tabs,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var bar = new DockPanel { LastChildFill = true, Height = BarHeight };
        DockPanel.SetDock(captions, Dock.Right);
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(_status, Dock.Right);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(captions);
        bar.Children.Add(tools);
        bar.Children.Add(_status);
        bar.Children.Add(left);
        bar.Children.Add(tabsScroll);

        // The bar IS the title bar now (NoChrome): drag empty space to move, double-click to maximize.
        bar.PointerPressed += (_, e) =>
        {
            if (!e.Handled && e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        bar.DoubleTapped += (_, _) => ToggleMaximize();

        // --- Sidebar: tree only, compact, line icons ---
        var treeScroll = new ScrollViewer
        {
            Content = _hostTree,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var sidebarInner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Top);
        sidebarInner.Children.Add(_search);
        sidebarInner.Children.Add(treeScroll);
        _sidebar = new Border
        {
            Child = sidebarInner,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x94, 0x9B)),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };

        _welcome = BuildWelcome();
        var body = new Panel();
        body.Children.Add(_surface);
        body.Children.Add(_welcome);

        // A one-pixel hairline on the content's left and top edges: the divider from the sidebar and the rule under
// the tab strip, like Termora's framed terminal.
        var bodyFrame = new Border
        {
            Child = body,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x94, 0x9B)),
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        _rootGrid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{DefaultSidebarWidth},*") };
        Grid.SetColumn(_sidebar, 0);
        Grid.SetColumn(bodyFrame, 1);
        _rootGrid.Children.Add(_sidebar);
        _rootGrid.Children.Add(bodyFrame);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_rootGrid);

        // One continuous wallpaper: the image is the window background; a single translucent scrim over the
        // whole window (behind all UI) washes it uniformly so the sidebar and the terminal read as one picture.
        _scrim = new Border { Background = Brushes.Transparent };
        var layered = new Panel();
        layered.Children.Add(_scrim);
        layered.Children.Add(root);

        // In-window modal layer for the Settings and Host editor panels (no separate OS windows).
        _overlay = new Border { IsVisible = false };
        layered.Children.Add(_overlay);
        return layered;
    }

    private async System.Threading.Tasks.Task<bool> ShowOverlayAsync(Control panel, double width, double height)
    {
        var card = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xCC, 0xD0)),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, OffsetY = 8, Color = Color.FromArgb(0x45, 0, 0, 0) }),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        _overlay.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
            Child = card,
        };
        _overlay.IsVisible = true;

        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        void Done(bool ok) => tcs.TrySetResult(ok);
        switch (panel)
        {
            case HostEditorPanel host:
                host.Completed += Done;
                break;
            case SettingsPanel settings:
                settings.Completed += Done;
                break;
        }

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _overlay.IsVisible = false;
            _overlay.Child = null;
        }
    }

    private Control BuildWelcome()
    {
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 420,
            Spacing = 10,
        };
        panel.Children.Add(new TextBlock
        {
            Text = UiStrings.Get("AppTitle"),
            FontSize = 40,
            FontWeight = FontWeight.Light,
            Opacity = 0.3,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiStrings.Get("WelcomeHint"),
            Opacity = 0.5,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return new Panel { Children = { panel } };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshTree();
        if (_store.LoadProblem is { } problem)
            _status.Text = UiStrings.Format("SettingsLoadFailed", problem);
        OpenSession(DefaultProfile());
        ApplyBackground();
    }

    private HostProfile DefaultProfile() => new()
    {
        Name = UiStrings.Get("Local"),
        Kind = SessionKind.Local,
        Shell = _store.Settings.Shell,
        StartDirectory = _store.Settings.StartDirectory,
    };

    private void RefreshTree()
    {
        _hostTree.Items.Clear();
        var filter = _search.Text?.Trim() ?? string.Empty;

        var root = new TreeViewItem
        {
            Header = Header(TermIcons.Person, UiStrings.Get("MyHosts"), semiBold: true),
            IsExpanded = true,
            MinHeight = 24,
            Padding = new Thickness(4, 0),
        };

        var groups = _store.Hosts
            .Where(h => filter.Length == 0
                || h.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || h.Host.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || h.User.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .GroupBy(host => string.IsNullOrWhiteSpace(host.Group) ? string.Empty : host.Group)
            .OrderBy(group => group.Key, StringComparer.CurrentCulture);

        foreach (var group in groups)
        {
            TreeViewItem parent;
            if (group.Key.Length > 0)
            {
                parent = new TreeViewItem
                {
                    Header = Header(TermIcons.FolderIcon(14), group.Key),
                    IsExpanded = true,
                    MinHeight = 24,
                    Padding = new Thickness(4, 0),
                };
                root.Items.Add(parent);
            }
            else
            {
                parent = root;
            }

            foreach (var host in group.OrderBy(h => h.Name, StringComparer.CurrentCulture))
            {
                var item = new TreeViewItem
                {
                    Header = Header(KindGeometry(host.Kind), host.Name),
                    Tag = host,
                    MinHeight = 24,
                    Padding = new Thickness(4, 0),
                };
                item.DoubleTapped += (_, _) => Open(host);
                var edit = new MenuItem { Header = UiStrings.Get("Edit") };
                edit.Click += async (_, _) => await EditHostAsync(host);
                var remove = new MenuItem { Header = UiStrings.Get("Delete") };
                remove.Click += (_, _) =>
                {
                    _store.Hosts.Remove(host);
                    SaveQuietly();
                    RefreshTree();
                };
                item.ContextMenu = new ContextMenu { Items = { edit, remove } };
                parent.Items.Add(item);
            }
        }
        _hostTree.Items.Add(root);
    }

    private static StackPanel Header(string geometry, string text, bool semiBold = false)
        => Header(TermIcons.Icon(geometry, 14), text, semiBold);

    private static StackPanel Header(Control icon, string text, bool semiBold = false)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = semiBold ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return stack;
    }

    private static string KindGeometry(SessionKind kind) => kind switch
    {
        SessionKind.Ssh => TermIcons.Terminal,
        SessionKind.Rdp => TermIcons.Grid,
        SessionKind.Wsl => TermIcons.Terminal,
        SessionKind.Telnet => TermIcons.Prompt,
        _ => TermIcons.Prompt,
    };

    private void OpenSession(HostProfile profile)
    {
        _welcome.IsVisible = false;
        var view = new TerminalView(QuickTermStore.EffectiveTerminal(_store.Settings, profile));
        view.TitleChanged += title =>
        {
            if (ReferenceEquals(_active?.View, view))
                Title = string.IsNullOrEmpty(title) ? profile.Name + " - " + UiStrings.Get("AppTitle") : title;
        };
        var tab = BuildTab(profile);
        var session = new Session { Profile = profile, View = view, Tab = tab };
        tab.Click += (_, _) => Activate(session);
        _sessions.Add(session);
        _surface.Children.Add(view);
        _tabs.Children.Add(tab);
        Activate(session);
        Dispatcher.UIThread.Post(async () =>
        {
            if (!_sessions.Contains(session))
                return;
            if (profile.Kind == SessionKind.Ssh)
                await view.StartSshAsync(profile, profile.Secret is { Length: > 0 } blob ? QuickTermStore.Unprotect(blob) : null, TrustHostKeyAsync, AskPasswordAsync);
            else
                view.Start(profile.Shell, profile.StartDirectory);
        }, DispatcherPriority.Background);
    }

    private Button BuildTab(HostProfile profile)
    {
        var close = IconButton(TermIcons.Close, 12);
        close.MinWidth = 20;
        close.Opacity = 0.6;
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(TermIcons.Icon(KindGeometry(profile.Kind), 14));
        label.Children.Add(new TextBlock { Text = profile.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(close);
        var tab = new Button
        {
            Content = label,
            Padding = new Thickness(12, 0),
            MinWidth = 100,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        close.Click += (_, _) =>
        {
            var session = _sessions.Find(s => ReferenceEquals(s.Tab, tab));
            if (session is not null)
                CloseSession(session);
        };
        return tab;
    }

    private void Activate(Session session)
    {
        _active = session;
        _welcome.IsVisible = false;
        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        foreach (var candidate in _sessions)
        {
            var active = ReferenceEquals(candidate, session);
            candidate.View.IsVisible = active;
            candidate.Tab.BorderBrush = active ? accent : Brushes.Transparent;
            candidate.Tab.Background = active ? new SolidColorBrush(Color.FromArgb(0x18, 0x4C, 0x9A, 0xFF)) : Brushes.Transparent;
        }
        Title = session.Profile.Name + " - " + UiStrings.Get("AppTitle");
        session.View.Focus();
    }

    private void CloseSession(Session session)
    {
        var index = _sessions.IndexOf(session);
        if (index < 0)
            return;
        session.View.Stop();
        _surface.Children.Remove(session.View);
        _tabs.Children.Remove(session.Tab);
        _sessions.RemoveAt(index);
        if (ReferenceEquals(_active, session))
        {
            _active = null;
            if (_sessions.Count > 0)
                Activate(_sessions[Math.Min(index, _sessions.Count - 1)]);
            else
            {
                Title = UiStrings.Get("AppTitle");
                _welcome.IsVisible = true;
            }
        }
    }

    private void Open(HostProfile host)
    {
        if (host.Kind != SessionKind.Rdp)
        {
            OpenSession(host);
            return;
        }
        if (!RdpLauncher.TryLaunch(host, out var problem))
        {
            Notify(problem == "missing-host"
                ? UiStrings.Get("HostMissing")
                : UiStrings.Format("RdpLaunchFailed", problem ?? "unknown"));
        }
    }

    private async System.Threading.Tasks.Task OpenSettingsAsync()
    {
        if (await ShowOverlayAsync(new SettingsPanel(_store.Settings), 640, 520))
        {
            SaveQuietly();
            ApplyBackground();
        }
    }

    /// <summary>Paints the whole-window background: one continuous image (fit/fill/stretch/tile) with a single light
    /// scrim over the whole window, so sidebar and terminal read as one picture and dark chrome text stays legible.</summary>
    private void ApplyBackground()
    {
        var path = _store.Settings.BackgroundImagePath;
        var hasImage = false;
        IBrush background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var mode = _store.Settings.BackgroundMode;
                var brush = new ImageBrush(new Bitmap(path))
                {
                    Stretch = mode switch
                    {
                        "uniform" => Stretch.Uniform,
                        "fill" => Stretch.Fill,
                        "tile" => Stretch.None,
                        _ => Stretch.UniformToFill,
                    },
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                if (mode == "tile")
                    brush.TileMode = TileMode.Tile;
                background = brush;
                hasImage = true;
            }
            catch
            {
                // Unreadable image: keep the plain floor rather than a broken background.
            }
        }
        Background = background;
        var alpha = hasImage ? (byte)(Math.Clamp(_store.Settings.BackgroundOpacity, 0, 100) * 255 / 100) : (byte)0;
        _scrim.Background = alpha == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        foreach (var session in _sessions)
            session.View.SetWallpaperBackground(hasImage, darkInk: true);
    }

    private void OpenSftp()
    {
        if (_active?.View.Ssh is not SshSession session)
        {
            Notify(UiStrings.Get("SftpNeedSsh"));
            return;
        }
        var window = new SftpWindow(session);
        CenterOnOwner(window);
        window.Show();
    }

    private void Notify(string text)
    {
        if (_active is not null)
            _active.View.ShowNotice(text);
        else
            _status.Text = text;
    }

    private void SaveQuietly()
    {
        try
        {
            _store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = UiStrings.Format("SaveFailed", ex.Message);
        }
    }

    private void StorePassword(HostProfile profile, string? password)
    {
        if (password is not { Length: > 0 })
            return;
        if (profile.Kind == SessionKind.Rdp)
        {
            if (!WindowsCredentialStore.TryWrite(WindowsCredentialStore.TargetFor(profile.Host), profile.User, password, out var problem))
                Notify(UiStrings.Format("RdpCredentialFailed", problem ?? "unknown"));
            return;
        }
        profile.Secret = QuickTermStore.Protect(password);
    }

    private async System.Threading.Tasks.Task EditHostAsync(HostProfile host)
    {
        _status.Text = "…";
        try
        {
            var panel = new HostEditorPanel(host, host.Kind, _store.Settings);
            var confirmed = await ShowOverlayAsync(panel, 680, 560);
            _status.Text = string.Empty;
            if (!confirmed)
                return;
            StorePassword(host, panel.Password);
            _store.ForgetUnusableSecrets();
            SaveQuietly();
            RefreshTree();
        }
        catch (Exception ex)
        {
            _status.Text = string.Empty;
            Notify($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task AddHostAsync()
    {
        _status.Text = "…";
        try
        {
            var panel = new HostEditorPanel(settings: _store.Settings);
            var confirmed = await ShowOverlayAsync(panel, 680, 560);
            _status.Text = string.Empty;
            if (!confirmed || panel.Result is not { } profile)
                return;
            StorePassword(profile, panel.Password);
            _store.ForgetUnusableSecrets();
            _store.Hosts.Add(profile);
            if (profile.Kind == SessionKind.Local)
            {
                _store.Settings.Shell = profile.Shell;
                _store.Settings.StartDirectory = profile.StartDirectory;
            }
            SaveQuietly();
            RefreshTree();
            Open(profile);
        }
        catch (Exception ex)
        {
            _status.Text = string.Empty;
            _active?.View.ShowNotice($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Trust-on-first-use: show the fingerprint once and let the user accept; changed keys never reach here.</summary>
    private async System.Threading.Tasks.Task<bool> TrustHostKeyAsync(string host, string fingerprint)
    {
        // Tmds.Ssh raises the host-key callback on its own thread; Avalonia controls must be born on the UI thread.
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => TrustHostKeyAsync(host, fingerprint));

        var trust = new Button { Content = UiStrings.Get("TrustHost"), IsDefault = true };
        var cancel = new Button { Content = UiStrings.Get("Cancel"), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(12) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(trust);

        var dialog = new Window
        {
            Title = UiStrings.Get("SshHostKeyTitle"),
            Width = 480,
            Height = 210,
            CanResize = false,
        };
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock
        {
            Text = UiStrings.Format("SshHostKeyQuestion", host, fingerprint),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Center,
        });
        dialog.Content = root;
        trust.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        CenterOnOwner(dialog);
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Asks for a password when the host has none stored; the value never touches disk or the log.</summary>
    private async System.Threading.Tasks.Task<string?> AskPasswordAsync()
    {
        var box = new TextBox { PasswordChar = '•', Margin = new Thickness(16, 12, 16, 0) };
        var ok = new Button { Content = UiStrings.Get("Confirm"), IsDefault = true };
        var cancel = new Button { Content = UiStrings.Get("Cancel"), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(12) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var dialog = new Window
        {
            Title = UiStrings.Get("SshPasswordTitle"),
            Width = 420,
            Height = 150,
            CanResize = false,
        };
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(box);
        dialog.Content = root;
        string? typed = null;
        ok.Click += (_, _) =>
        {
            typed = box.Text;
            dialog.Close(true);
        };
        cancel.Click += (_, _) => dialog.Close(false);
        CenterOnOwner(dialog);
        await dialog.ShowDialog<bool>(this);
        return typed is { Length: > 0 } ? typed : null;
    }

    private void CenterOnOwner(Window dialog)
    {
        var scaling = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.Scaling ?? 1;
        var ownerWidth = (int)(Width * scaling);
        var ownerHeight = (int)(Height * scaling);
        var width = (int)(dialog.Width * scaling);
        var height = (int)(dialog.Height * scaling);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(
            Position.X + Math.Max(0, (ownerWidth - width) / 2),
            Position.Y + Math.Max(0, (ownerHeight - height) / 2));
    }

    private sealed class Session
    {
        public required HostProfile Profile { get; init; }
        public required TerminalView View { get; init; }
        public required Button Tab { get; init; }
    }
}