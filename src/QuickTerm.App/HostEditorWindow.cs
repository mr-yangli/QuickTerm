namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

/// <summary>
/// The host editor in Termora's shape: a row of protocol icon-tabs across the top, a vertical pane nav on the
/// left (Properties / Terminal), and a right-aligned-label form on the right. Host and port share one row, the
/// password carries a reveal eye, and the remark is a tall multiline box. Only panes and protocols that actually
/// have behaviour are offered — the SSH-only proxy/tunnel/jump/transfer panes stay out until a transport lands.
/// </summary>
internal sealed class HostEditorPanel : UserControl
{
    /// <summary>True on confirm, false on cancel — the host shows this panel in an in-window overlay.</summary>
    public event Action<bool>? Completed;

    private static readonly IBrush Selected = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

    private readonly TextBox _name = new();
    private readonly TextBox _note = new() { AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _shell = new();
    private readonly TextBox _directory = new();
    private readonly TextBox _host = new();
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0" };
    private readonly ComboBox _authKind = new();
    private readonly TextBox _keyPath = new();
    private readonly NumericUpDown _fontSize = new() { Minimum = 6, Maximum = 72, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _scrollback = new() { Minimum = 0, Maximum = 1000000, Increment = 100, FormatString = "0" };
    private readonly TextBlock _problem = new()
    {
        Foreground = Brushes.IndianRed,
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Dictionary<SessionKind, Button> _protocolTabs = new();
    private readonly List<Button> _navTabs = new();
    private readonly StackPanel _propsPage = new() { Spacing = 10 };
    private readonly StackPanel _termPage = new() { Spacing = 10 };

    private Control? _hostPortRow;
    private Control? _userRow;
    private Control? _authRow;
    private Control? _passwordRow;
    private Control? _keyRow;
    private Control? _shellRow;
    private Control? _dirRow;

    private SessionKind _kind;
    private readonly double _inheritedFontSize;
    private readonly int _inheritedScrollback;

    public HostEditorPanel(HostProfile? existing = null, SessionKind preferred = SessionKind.Local, QuickTermSettings? settings = null)
    {
        // The Terminal page seeds from what the host would actually inherit — its own override, else the
        // global settings — so a value left untouched on save can be written back as null (inherit) instead
        // of pinning today's global into the host.
        _inheritedFontSize = existing?.Terminal?.FontSize ?? settings?.FontSize ?? 14;
        _inheritedScrollback = existing?.Terminal?.ScrollbackLines ?? settings?.ScrollbackLines ?? 5000;
        _name.Text = existing?.Name ?? string.Empty;
        _note.Text = existing?.Note ?? string.Empty;
        _shell.Text = existing?.Shell ?? string.Empty;
        _shell.PlaceholderText = UiStrings.Get("SystemDefaultShell");
        _directory.Text = existing?.StartDirectory ?? string.Empty;
        _host.Text = existing?.Host ?? string.Empty;
        _user.Text = existing?.User ?? string.Empty;
        _keyPath.Text = existing?.KeyPath ?? string.Empty;
        _port.Value = existing is { Port: > 0 } ? existing.Port
            : (existing?.Kind ?? preferred) == SessionKind.Rdp ? RdpLauncher.DefaultPort : 22;
        _fontSize.Value = (decimal)_inheritedFontSize;
        _scrollback.Value = _inheritedScrollback;

        var authOptions = new List<AuthOption>
        {
            new(SshAuthKind.Password, UiStrings.Get("AuthPassword")),
            new(SshAuthKind.PrivateKey, UiStrings.Get("AuthPrivateKey")),
            new(SshAuthKind.Agent, UiStrings.Get("AuthAgent")),
        };
        _authKind.ItemsSource = authOptions;
        _authKind.SelectedItem = authOptions.Find(o => o.Kind == (existing?.AuthKind ?? SshAuthKind.Password)) ?? authOptions[0];
        _authKind.SelectionChanged += (_, _) => ShowAuthFields();

        var shells = new ComboBox { ItemsSource = DiscoverShells(), Width = 170 };
        shells.SelectionChanged += (_, _) =>
        {
            if (shells.SelectedItem is string chosen)
                _shell.Text = chosen;
        };

        BuildPropsPage(shells);
        BuildTermPage();

        var cancel = new Button { Content = UiStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Completed?.Invoke(false);
        var confirm = new Button { Content = UiStrings.Get("Confirm"), IsDefault = true };
        confirm.Click += (_, _) => Accept(existing);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(6, 8, 6, 8) };
        nav.Children.Add(NavTab(0, TermIcons.Sliders, "PaneProperties"));
        nav.Children.Add(NavTab(1, TermIcons.Terminal, "PaneTerminal"));
        var pages = new Panel { Margin = new Thickness(8, 12, 16, 8) };
        pages.Children.Add(_propsPage);
        pages.Children.Add(_termPage);
        Grid.SetColumn(nav, 0);
        Grid.SetColumn(pages, 1);
        body.Children.Add(nav);
        body.Children.Add(pages);

        var root = new DockPanel { LastChildFill = true };
        var protocols = BuildProtocolRow();
        DockPanel.SetDock(protocols, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(protocols);
        root.Children.Add(buttons);
        root.Children.Add(body);

        Content = root;
        SelectKind(existing?.Kind ?? preferred);
        SelectPane(0);
        _name.Focus();
    }

    public HostProfile? Result { get; private set; }

    public string? Password { get; private set; }

    private Control BuildProtocolRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 10),
        };
        AddProtocol(row, SessionKind.Ssh, TermIcons.Terminal, "Ssh");
        AddProtocol(row, SessionKind.Local, TermIcons.Prompt, "Local");
        AddProtocol(row, SessionKind.Rdp, TermIcons.Grid, "Rdp");
        return row;
    }

    private void AddProtocol(StackPanel row, SessionKind kind, string geometry, string labelKey)
    {
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(TermIcons.Icon(geometry, 24));
        stack.Children.Add(new TextBlock { Text = UiStrings.Get(labelKey), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var tab = new Button
        {
            Content = stack,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6),
        };
        tab.Click += (_, _) => SelectKind(kind);
        _protocolTabs[kind] = tab;
        row.Children.Add(tab);
    }

    private Button NavTab(int index, string geometry, string labelKey)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stack.Children.Add(TermIcons.Icon(geometry, 14));
        stack.Children.Add(new TextBlock { Text = UiStrings.Get(labelKey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var tab = new Button
        {
            Content = stack,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6),
        };
        tab.Click += (_, _) => SelectPane(index);
        _navTabs.Add(tab);
        return tab;
    }

    private void BuildPropsPage(ComboBox shells)
    {
        _propsPage.Children.Add(Row("SessionName", _name));

        var hostPort = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,46,84") };
        var hostLabel = Label("Host");
        var portLabel = Label("Port");
        Grid.SetColumn(hostLabel, 0);
        Grid.SetColumn(_host, 1);
        Grid.SetColumn(portLabel, 2);
        Grid.SetColumn(_port, 3);
        hostPort.Children.Add(hostLabel);
        hostPort.Children.Add(_host);
        hostPort.Children.Add(portLabel);
        hostPort.Children.Add(_port);
        _hostPortRow = hostPort;
        _propsPage.Children.Add(hostPort);

        _userRow = Row("User", _user);
        _propsPage.Children.Add(_userRow);
        _authRow = Row("AuthMethod", _authKind);
        _propsPage.Children.Add(_authRow);

        var eye = new Button
        {
            Content = TermIcons.Icon(TermIcons.Eye, 14),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0),
        };
        eye.Click += (_, _) => _password.PasswordChar = _password.PasswordChar == '•' ? (char)0 : '•';
        var passwordBox = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(eye, Dock.Right);
        passwordBox.Children.Add(eye);
        passwordBox.Children.Add(_password);
        _passwordRow = Row("Password", passwordBox);
        _propsPage.Children.Add(_passwordRow);

        var keyBrowse = new Button { Content = UiStrings.Get("Browse") };
        keyBrowse.Click += async (_, _) => await BrowseKeyAsync();
        var keyBox = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(keyBrowse, Dock.Right);
        keyBox.Children.Add(keyBrowse);
        keyBox.Children.Add(_keyPath);
        _keyRow = Row("KeyPath", keyBox);
        _propsPage.Children.Add(_keyRow);

        var shellBox = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(shells, Dock.Right);
        shellBox.Children.Add(shells);
        shellBox.Children.Add(_shell);
        _shellRow = Row("Shell", shellBox);
        _propsPage.Children.Add(_shellRow);

        var browse = new Button { Content = UiStrings.Get("Browse") };
        browse.Click += async (_, _) => await BrowseAsync();
        var dirBox = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        dirBox.Children.Add(browse);
        dirBox.Children.Add(_directory);
        _dirRow = Row("StartDirectory", dirBox);
        _propsPage.Children.Add(_dirRow);

        _propsPage.Children.Add(Row("Note", _note));
        _propsPage.Children.Add(_problem);
    }

    private void BuildTermPage()
    {
        _termPage.Children.Add(Row("FontSize", _fontSize));
        _termPage.Children.Add(Row("Scrollback", _scrollback));
    }

    private void SelectKind(SessionKind kind)
    {
        _kind = kind;
        foreach (var pair in _protocolTabs)
            pair.Value.Background = pair.Key == kind ? Selected : Brushes.Transparent;

        var remote = kind is SessionKind.Rdp or SessionKind.Ssh;
        var ssh = kind == SessionKind.Ssh;
        var local = kind == SessionKind.Local;
        if (_hostPortRow is not null) _hostPortRow.IsVisible = remote;
        if (_userRow is not null) _userRow.IsVisible = remote;
        if (_authRow is not null) _authRow.IsVisible = ssh;
        if (_shellRow is not null) _shellRow.IsVisible = local;
        if (_dirRow is not null) _dirRow.IsVisible = local;

        if (ssh && _port.Value is 3389) _port.Value = 22;
        else if (kind == SessionKind.Rdp && _port.Value is 22) _port.Value = RdpLauncher.DefaultPort;

        ShowAuthFields();
    }

    private void SelectPane(int index)
    {
        for (var i = 0; i < _navTabs.Count; i++)
            _navTabs[i].Background = i == index ? Selected : Brushes.Transparent;
        _propsPage.IsVisible = index == 0;
        _termPage.IsVisible = index == 1;
    }

    private void ShowAuthFields()
    {
        if (_keyRow is null || _passwordRow is null)
            return;
        var auth = (_authKind.SelectedItem as AuthOption)?.Kind ?? SshAuthKind.Password;
        _keyRow.IsVisible = _kind == SessionKind.Ssh && auth == SshAuthKind.PrivateKey;
        _passwordRow.IsVisible = _kind != SessionKind.Ssh || auth != SshAuthKind.Agent;
    }

    private static TextBlock Label(string key) => new()
    {
        Text = UiStrings.Get(key),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
    };

    private static Control Row(string labelKey, Control field)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*") };
        var label = Label(labelKey);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(field, 1);
        row.Children.Add(label);
        row.Children.Add(field);
        return row;
    }

    private void Accept(HostProfile? existing)
    {
        var kind = _kind;
        var host = _host.Text?.Trim() ?? string.Empty;
        var directory = _directory.Text?.Trim() ?? string.Empty;

        if (kind is SessionKind.Rdp or SessionKind.Ssh && host.Length == 0)
        {
            Problem(UiStrings.Get("HostMissing"));
            return;
        }
        if (kind == SessionKind.Local && directory.Length > 0 && !Directory.Exists(directory))
        {
            Problem(UiStrings.Get("DirectoryMissing"));
            return;
        }
        var auth = (_authKind.SelectedItem as AuthOption)?.Kind ?? SshAuthKind.Password;
        var keyPath = _keyPath.Text?.Trim() ?? string.Empty;
        if (kind == SessionKind.Ssh && auth == SshAuthKind.PrivateKey && keyPath.Length > 0 && !File.Exists(keyPath))
        {
            Problem(UiStrings.Get("KeyFileMissing"));
            return;
        }

        var shell = _shell.Text?.Trim() ?? string.Empty;
        var name = _name.Text?.Trim() ?? string.Empty;

        Result = existing ?? new HostProfile();
        Result.Kind = kind;
        Result.Name = name.Length > 0 ? name : SuggestName(kind, shell, host);
        Result.Note = _note.Text?.Trim() ?? string.Empty;
        Result.Shell = shell.Length > 0 ? shell : null;
        Result.StartDirectory = directory.Length > 0 ? directory : null;
        Result.Host = kind is SessionKind.Rdp or SessionKind.Ssh ? host : Result.Host;
        Result.User = _user.Text?.Trim() ?? string.Empty;
        Result.Port = (int)(_port.Value ?? (kind == SessionKind.Rdp ? RdpLauncher.DefaultPort : 22));
        Result.AuthKind = auth;
        Result.KeyPath = kind == SessionKind.Ssh && auth == SshAuthKind.PrivateKey && keyPath.Length > 0 ? keyPath : null;
        // Null means inherit — the store's rule for this page. A field left at its inherited value writes
        // null rather than pinning today's global into the host, so a later global change still flows; a
        // page the user never touched serialises as nothing at all. FontFamily has no field in this dialog,
        // so a hand-edited one survives untouched.
        var fontSize = (double?)_fontSize.Value;
        var scrollback = (int?)_scrollback.Value;
        Result.Terminal = fontSize != _inheritedFontSize || scrollback != _inheritedScrollback || existing?.Terminal?.FontFamily is not null
            ? new TerminalOverrides
            {
                FontFamily = existing?.Terminal?.FontFamily,
                FontSize = fontSize == _inheritedFontSize ? null : fontSize,
                ScrollbackLines = scrollback == _inheritedScrollback ? null : scrollback,
            }
            : null;

        Password = _password.Text is { Length: > 0 } typed ? typed : null;
        Completed?.Invoke(true);
    }

    private void Problem(string text)
    {
        _problem.Text = text;
        _problem.IsVisible = true;
    }

    private async System.Threading.Tasks.Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
            return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiStrings.Get("StartDirectory"),
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _directory.Text = path;
    }

    private async System.Threading.Tasks.Task BrowseKeyAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
            return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiStrings.Get("KeyPath"),
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            _keyPath.Text = path;
    }

    private static string SuggestName(SessionKind kind, string shell, string host)
    {
        if (kind is SessionKind.Rdp or SessionKind.Ssh)
            return host.Length > 0 ? host : UiStrings.Get(kind == SessionKind.Rdp ? "Rdp" : "Ssh");
        if (shell.Length == 0)
            return UiStrings.Get("Local");
        var file = Path.GetFileNameWithoutExtension(shell);
        return file.Length > 0 ? file : UiStrings.Get("Local");
    }

    internal static List<string> DiscoverShells()
    {
        var choices = new List<string>();
        void Offer(string? command)
        {
            if (string.IsNullOrWhiteSpace(command) || !File.Exists(command))
                return;
            if (!choices.Contains(command))
                choices.Add(command);
        }
        Offer(Environment.GetEnvironmentVariable("COMSPEC"));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Offer(Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Offer(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        return choices;
    }

    private sealed record AuthOption(SshAuthKind Kind, string Label)
    {
        public override string ToString() => Label;
    }
}