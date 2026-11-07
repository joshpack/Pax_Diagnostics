using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PaxDiagnostics.Platform;

/// <summary>
/// The app opens as a normal user. SFC and DISM need administrator rights, so clicking one of them restarts the app
/// elevated with "--tool &lt;name&gt;", which opens straight on that tool and starts it.
/// </summary>
public static class Elevation
{
    /// <summary>The tools that need administrator rights, as passed after --tool.</summary>
    public static readonly string[] Tools = ["sfc", "dism-check", "dism-restore"];

    public static bool IsElevated { get; } = CheckElevated();

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>How the elevated copy is started (also checked by the self-test, which never starts it).</summary>
    public static ProcessStartInfo RestartInfo(string tool) => new()
    {
        FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PaxDiagnostics.exe"),
        Arguments = $"--tool {tool}",
        UseShellExecute = true,
        Verb = "runas",
        WorkingDirectory = AppContext.BaseDirectory,
    };

    /// <summary>
    /// Starts an elevated copy that runs <paramref name="tool"/>. Returns null when it started (this copy should then
    /// close), otherwise a message for the user. A cancelled UAC prompt is error 1223.
    /// </summary>
    public static string? RestartElevated(string tool)
    {
        try
        {
            using var p = Process.Start(RestartInfo(tool));
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The administrator prompt was cancelled, so nothing was started. This tool needs administrator rights; click it again and choose Yes to run it.";
        }
        catch (Exception ex)
        {
            return $"Couldn't restart as administrator: {ex.Message}";
        }
    }
}
