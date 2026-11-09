namespace QuickTerm.App;

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;

/// <summary>
/// No XAML anywhere in this app on purpose: the terminal surface is self-drawn, the chrome is three
/// controls, and dropping the markup compile keeps the AOT/trim story one layer shorter — the same reason
/// the rendering layer is kept separate from the model.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Set before Avalonia brings the app up, so <see cref="App" /> knows a measurement run does not need a
    /// window: the offscreen bench drives the control directly.
    /// </summary>
    public static bool RunningBench { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // The error mode is inherited by every child we spawn, so a shell that dies during init reports
        // through its exit code onto the grid rather than stealing focus with an OS dialog — which is exactly
        // what a first run looked like before this line existed.
        Native.SetErrorMode(Native.SemFailCriticalErrors | Native.SemNoGpFaultErrorBox);

        // Neither diagnostic mode needs a window, so both run before Avalonia is brought up: the probe in
        // particular must keep the parent's shape (windows-subsystem, no console) without any UI noise.
        if (args.Length > 0 && args[0] == "--pty-probe")
            return PtyProbe.Run(args);

        // The capture/replay pair is how a line that renders wrong gets attributed to the shell, the engine or
        // the paint without a second pair of eyes on a screenshot.
        if (args.Length > 0 && args[0] == "--pty-capture")
            return PtyProbe.Capture(args);

        if (args.Length > 0 && args[0] == "--replay")
            return Replay.Run(args);

        if (args.Length > 0 && args[0] == "--bench")
        {
            RunningBench = true;
            BuildAvaloniaApp().SetupWithoutStarting();
            UiStrings.Apply(UiStrings.DetectSystemTag());
            return RenderBench.Run(args);
        }

        // A WinExe that crashes leaves no console and no dialog, so a silent flash-exit is undiagnosable. Log
        // every unhandled exception (UI thread too, and keep the window alive) to %TEMP%\QuickTerm-crash.log.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog(e.Exception);
            e.SetObserved();
        };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog(e.Exception);
            e.Handled = true;
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void CrashLog(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QuickTerm-crash.log");
            System.IO.File.AppendAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // Logging a crash must never cause another one.
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static class Native
    {
        public const uint SemFailCriticalErrors = 0x0001;
        public const uint SemNoGpFaultErrorBox = 0x0002;

        [DllImport("kernel32.dll")]
        public static extern uint SetErrorMode(uint mode);
    }
}
