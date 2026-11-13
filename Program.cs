using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace pxdDiagnostics;

internal static class Program
{
    public const string AppId = "pxdDiagnostics";

    /// <summary>Where updates come from: this app's releases (asked at pxdtek.com first, GitHub as the fallback) and its Windows zip (the same asset the pxd Toolkit installs).</summary>
    public const string UpdateRepo = "pxdtek/pxdDiagnostics";
    public const string UpdateAsset = @"^pxdDiagnostics-.*-win-x64\.zip$";

    /// <summary>Runs that never update first: the build step, and the elevated copy started for SFC or DISM.</summary>
    public static readonly string[] NoUpdateArgs = ["--write-icon", "--tool"];

    /// <summary>"--tool sfc" and friends: set when the app restarted itself as administrator to run that tool.</summary>
    public static string? StartTool { get; private set; }

    /// <summary>%APPDATA%\pxdDiagnostics (window placement), or the folder in PXDDG_DATA (tests keep the real one untouched).</summary>
    public static string DataFolder =>
        Environment.GetEnvironmentVariable("PXDDG_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pxdDiagnostics");

    [STAThread]
    public static int Main(string[] args)
    {
        // pxd standard rule 6: an installed copy updates itself before it opens, however it was started. A no-op for dev
        // builds (bin\), --self-test, PXD_NO_UPDATE=1, offline or when neither pxdtek.com nor GitHub answers in time; when it updated, the new copy is already starting.
        if (!args.Any(a => NoUpdateArgs.Contains(a, StringComparer.OrdinalIgnoreCase))
            && pxdStandard.pxdUpdate.BeforeStart(UpdateRepo, UpdateAsset, args))
            return 0;

        // Build step: "--write-icon <file.ico|file.png>" renders the app mark and exits, no window.
        var icon = Array.FindIndex(args, a => a.Equals("--write-icon", StringComparison.OrdinalIgnoreCase));
        if (icon >= 0 && icon + 1 < args.Length)
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            AppIcon.Write(args[icon + 1]);
            return 0;
        }

        var test = Array.FindIndex(args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (test >= 0 && test + 1 < args.Length)
        {
            SelfTest.Dir = Path.GetFullPath(args[test + 1]);
            SelfTest.Sample = args.Any(a => a.Equals("--sample", StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(SelfTest.Dir);
            // Keep the real window placement out of it unless the caller chose a folder.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXDDG_DATA")))
                Environment.SetEnvironmentVariable("PXDDG_DATA", Path.Combine(SelfTest.Dir, "data"));
            pxdStandard.pxdChrome.Configure(AppId, DataFolder);

            // Headless platform: the windows are real Avalonia windows, but no OS window exists, so nothing can appear on screen.
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            using var done = new CancellationTokenSource();
            Dispatcher.UIThread.Post(async () =>
            {
                await SelfTest.RunAsync();
                done.Cancel();
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            return SelfTest.ExitCode;
        }

        // The folder kept under the app's former name moves to the new one before anything reads it.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PXDDG_DATA")))
            Migration.Run(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        var tool = Array.FindIndex(args, a => a.Equals("--tool", StringComparison.OrdinalIgnoreCase));
        if (tool >= 0 && tool + 1 < args.Length)
            StartTool = args[tool + 1];
        // One copy at a time: starting it again brings the open window to the front. The elevated copy started for a
        // tool (while this one closes) is left out, so it never hands itself back to the copy that is going away.
        else if (!SingleInstance.Claim(SingleInstance.Name(AppId), args))
            return 0;

        pxdStandard.pxdChrome.Configure(AppId, DataFolder);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Software rendering: a window of cards and text doesn't need the graphics card, and skipping GPU setup keeps
    /// memory use and start-up time low.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();
}
