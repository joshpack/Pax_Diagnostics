using Avalonia;

namespace PaxDiagnostics;

internal static class Program
{
    /// <summary>"--tool sfc" and friends: set when the app restarted itself as administrator to run that tool.</summary>
    public static string? StartTool { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
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
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return SelfTest.ExitCode;
        }

        var tool = Array.FindIndex(args, a => a.Equals("--tool", StringComparison.OrdinalIgnoreCase));
        if (tool >= 0 && tool + 1 < args.Length) StartTool = args[tool + 1];

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
