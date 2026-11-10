using pxdStandard;

namespace pxdDiagnostics;

/// <summary>
/// One-time move from the app's name before the pxd rename: the folder %APPDATA%\&lt;former name&gt; (window placement)
/// becomes %APPDATA%\pxdDiagnostics. The former name comes from pxdMigrate.FormerName. Safe to repeat, never throws.
/// The app has no autostart, file associations or other registrations to move.
/// </summary>
internal static class Migration
{
    /// <summary>Moves the former data folder under <paramref name="appDataRoot"/> to the new one. True if it moved.</summary>
    public static bool Run(string appDataRoot) =>
        pxdMigrate.Folder(Path.Combine(appDataRoot, pxdMigrate.FormerName(Program.AppId)), Path.Combine(appDataRoot, Program.AppId));
}
