namespace QuickTerm.App;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

/// <summary>Application entry: theme in code, one window, and the language applied before any label is read.</summary>
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        // Settings are read before any label is: the language is one of them, and applying the system culture
        // first and then correcting it would flash the wrong language for a frame.
        var store = QuickTermStore.Load();
        UiStrings.Apply(store.Settings.Language ?? UiStrings.DetectSystemTag());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && !Program.RunningBench)
            desktop.MainWindow = new MainWindow(store);

        base.OnFrameworkInitializationCompleted();
    }
}
