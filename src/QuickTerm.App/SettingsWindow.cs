namespace QuickTerm.App;

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

/// <summary>
/// Settings, Termora-shaped: a left pane nav and a right form. Only panes with real behaviour are offered
/// (General / Terminal / Background / About); the reference's account/keyboard/transfer/plugin panes have no
/// implementation, so they stay out rather than sitting empty. Background image is the reason this exists now.
/// </summary>
internal sealed class SettingsPanel : UserControl
{
    /// <summary>True on confirm, false on cancel — shown in an in-window overlay by the host.</summary>
    public event Action<bool>? Completed;
    private static readonly IBrush Selected = new SolidColorBrush(Color.FromRgb(0xDC, 0xE8, 0xF7));

    private readonly QuickTermSettings _settings;
    private readonly ComboBox _language = new();
    private readonly NumericUpDown _fontSize = new() { Minimum = 6, Maximum = 72, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _scrollback = new() { Minimum = 0, Maximum = 1000000, Increment = 100, FormatString = "0" };
    private readonly TextBox _bgPath = new();
    private readonly ComboBox _bgMode = new();
    private readonly NumericUpDown _opacity = new() { Minimum = 0, Maximum = 100, Increment = 5, FormatString = "0" };
    private readonly List<Button> _nav = new();
    private readonly Panel _pages = new() { Margin = new Thickness(10, 12, 16, 8) };

    public SettingsPanel(QuickTermSettings settings)
    {
        _settings = settings;

        BuildGeneral();
        BuildTerminal();
        BuildBackground();
        BuildAbout();

        var navStack = new StackPanel { Spacing = 2, Margin = new Thickness(6, 8, 6, 8) };
        navStack.Children.Add(NavTab(0, TermIcons.Sliders, "PaneGeneral"));
        navStack.Children.Add(NavTab(1, TermIcons.Terminal, "PaneTerminal"));
        navStack.Children.Add(NavTab(2, TermIcons.Folder, "PaneBackground"));
        navStack.Children.Add(NavTab(3, TermIcons.Person, "PaneAbout"));

        var ok = new Button { Content = UiStrings.Get("Confirm"), IsDefault = true };
        ok.Click += (_, _) => Confirm();
        var cancel = new Button { Content = UiStrings.Get("Cancel"), IsCancel = true };
        cancel.Click += (_, _) => Completed?.Invoke(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        navStack.Margin = new Thickness(8, 10, 8, 8);
        _pages.Margin = new Thickness(18, 16, 18, 10);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        var navColumn = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE4, 0xE8)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            CornerRadius = new CornerRadius(0, 0, 0, 6),
            Child = navStack,
        };
        Grid.SetColumn(navColumn, 0);
        Grid.SetColumn(_pages, 1);
        body.Children.Add(navColumn);
        body.Children.Add(_pages);

        var close = new Button
        {
            Content = TermIcons.Icon(TermIcons.Close, 14),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Completed?.Invoke(false);
        var header = new DockPanel { Height = 46 };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = UiStrings.Get("Settings"),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        });
        var headerWrap = new Border
        {
            Child = header,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE4, 0xE8)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerWrap, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(headerWrap);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        SelectPane(0);
    }

    private void BuildGeneral()
    {
        var langs = new List<LangOption>
        {
            new(null, UiStrings.Get("FollowSystem")),
            new("en", "English"),
            new("zh-Hans", "简体中文"),
        };
        _language.ItemsSource = langs;
        _language.SelectedItem = langs.Find(o => o.Value == _settings.Language) ?? langs[0];

        var page = Page();
        page.Children.Add(Row("Language", _language));
        _pages.Children.Add(page);
    }

    private void BuildTerminal()
    {
        _fontSize.Value = (decimal)_settings.FontSize;
        _scrollback.Value = _settings.ScrollbackLines;
        var page = Page();
        page.Children.Add(Row("FontSize", _fontSize));
        page.Children.Add(Row("Scrollback", _scrollback));
        _pages.Children.Add(page);
    }

    private void BuildBackground()
    {
        _bgPath.Text = _settings.BackgroundImagePath ?? string.Empty;

        var browse = new Button { Content = UiStrings.Get("Browse") };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider)
                return;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                _bgPath.Text = path;
        };
        var clear = new Button { Content = UiStrings.Get("Clear") };
        clear.Click += (_, _) => _bgPath.Text = string.Empty;

        var pathRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(clear);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_bgPath);

        var modes = new List<ModeOption>
        {
            new("uniform", UiStrings.Get("FitUniform")),
            new("uniformToFill", UiStrings.Get("FitFill")),
            new("fill", UiStrings.Get("FitStretch")),
            new("tile", UiStrings.Get("FitTile")),
        };
        _bgMode.ItemsSource = modes;
        _bgMode.SelectedItem = modes.Find(o => o.Value == _settings.BackgroundMode) ?? modes[1];

        var page = Page();
        page.Children.Add(Row("BackgroundImage", pathRow));
        page.Children.Add(Row("FillMode", _bgMode));
        _opacity.Value = _settings.BackgroundOpacity;
        page.Children.Add(Row("Opacity", _opacity));
        _pages.Children.Add(page);
    }

    private void BuildAbout()
    {
        var page = Page();
        page.Children.Add(new TextBlock { Text = "QuickTerm", FontSize = 18, FontWeight = FontWeight.SemiBold });
        page.Children.Add(new TextBlock
        {
            Text = typeof(SettingsPanel).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Opacity = 0.6,
            Margin = new Thickness(0, 4, 0, 0),
        });
        _pages.Children.Add(page);
    }

    private static StackPanel Page() => new() { Spacing = 10 };

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
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7),
        };
        tab.Click += (_, _) => SelectPane(index);
        _nav.Add(tab);
        return tab;
    }

    private void SelectPane(int index)
    {
        for (var i = 0; i < _nav.Count; i++)
            _nav[i].Background = i == index ? Selected : Brushes.Transparent;
        for (var i = 0; i < _pages.Children.Count; i++)
            _pages.Children[i].IsVisible = i == index;
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
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var label = Label(labelKey);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(field, 1);
        row.Children.Add(label);
        row.Children.Add(field);
        return row;
    }

    private void Confirm()
    {
        _settings.Language = (_language.SelectedItem as LangOption)?.Value;
        _settings.FontSize = (double)(_fontSize.Value ?? 14);
        _settings.ScrollbackLines = (int)(_scrollback.Value ?? 5000);
        _settings.BackgroundImagePath = _bgPath.Text is { Length: > 0 } p ? p : null;
        _settings.BackgroundMode = (_bgMode.SelectedItem as ModeOption)?.Value ?? "uniformToFill";
        _settings.BackgroundOpacity = (int)(_opacity.Value ?? 70);
        Completed?.Invoke(true);
    }

    private sealed record LangOption(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ModeOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }
}