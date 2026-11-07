using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PaxDiagnostics.Diagnostics;
using PaxDiagnostics.Platform;
using PaxDiagnostics.Views;

namespace PaxDiagnostics;

/// <summary>
/// --self-test DIR [--sample]: reads the system information, checks the CBS log filter on a made-up log written into
/// DIR (the real CBS.log is only read), checks output decoding and verdicts with recorded SFC/DISM output, checks
/// elevation detection, and renders every tab to a PNG off-screen. SFC and DISM are never run. With --sample the window
/// shows invented data instead of this PC's, for screenshots. Exit code 0 when every check passed.
/// </summary>
internal static class SelfTest
{
    public static string? Dir { get; set; }
    public static bool Sample { get; set; }
    public static int ExitCode { get; private set; } = 1;

    private static readonly StringBuilder R = new();
    private static int _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        R.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : ": " + detail)}");
        if (!ok) _failed++;
    }

    public static async Task RunAsync()
    {
        var version = typeof(SelfTest).Assembly.GetName().Version?.ToString(3);
        R.AppendLine($"PAX Diagnostics {version} self-test, {(Sample ? "sample data" : "this PC")}, {DateTime.Now:yyyy-MM-dd HH:mm}");
        R.AppendLine($"Process: {(Environment.Is64BitProcess ? "64" : "32")}-bit on {(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit Windows");
        R.AppendLine();
        try
        {
            var sections = await SystemSections();
            var cbs = await CbsChecks();
            await RealCbs();
            Decoding();
            Verdicts();
            ElevationChecks();
            await Render(sections, cbs);
        }
        catch (Exception ex)
        {
            Check("no crash", false, ex.ToString());
        }

        R.AppendLine();
        R.AppendLine(_failed == 0 ? "RESULT: all checks passed" : $"RESULT: {_failed} check(s) FAILED");
        ExitCode = _failed == 0 ? 0 : 1;
        await File.WriteAllTextAsync(Path.Combine(Dir!, "report.txt"), R.ToString());
    }

    // ---------- System information ----------

    private static async Task<List<InfoSection>> SystemSections()
    {
        R.AppendLine("== System information ==");
        var clock = Stopwatch.StartNew();
        var sections = Sample ? SystemInfo.Sample() : await Task.Run(SystemInfo.Gather);
        R.AppendLine($"Gathered {sections.Count} sections in {clock.ElapsedMilliseconds} ms (off the UI thread)");
        foreach (var s in sections)
        {
            var rows = s.Groups.Sum(g => g.Rows.Count);
            Check($"{s.Title}: {rows} value(s){(s.Groups.Count(g => g.Heading is not null) is var n and > 0 ? $" in {n} group(s)" : "")}", !s.IsEmpty,
                s.IsEmpty ? "EMPTY " + s.Error : s.Error is null ? null : "partly read: " + s.Error);
            foreach (var g in s.Groups.Where(g => g.Rows.Count > 0))
            {
                if (g.Heading is not null) R.AppendLine($"    {g.Heading}");
                foreach (var row in g.Rows)
                    R.AppendLine($"      {row.Label}: {row.Value}");
            }
        }

        var text = SystemInfo.ToText(sections);
        Check("Copy all info text has every section", sections.All(s => text.Contains(s.Title.ToUpperInvariant())), $"{text.Length} chars");
        R.AppendLine();
        return sections;
    }

    // ---------- CBS log ----------

    private static async Task<CbsResult> CbsChecks()
    {
        R.AppendLine("== CBS log filter (synthetic log) ==");
        var path = Path.Combine(Dir!, "CBS.log");
        var sb = new StringBuilder();
        for (var i = 0; i < 20000; i++)
            sb.Append($"2026-11-06 10:{i / 600 % 60:00}:{i / 10 % 60:00}, Info                  CBS    Session: {i:000000} Loaded servicing stack component.\r\n");
        string[] interesting =
        [
            "2026-11-06 10:40:01, Info                  CSI    00000a01 [SR] Verifying 100 components",
            "2026-11-06 10:40:02, Info                  CSI    00000a02 [SR] Cannot repair member file [l:10]'sample.dll' of Sample.Component, file is CORRUPT",
            "2026-11-06 10:40:03, Info                  CSI    00000a03 [SR] Repairing corrupted file \\??\\C:\\Windows\\System32\\sample.dll from store",
            "2026-11-06 10:40:04, Info                  CSI    00000a04 [SR] Repaired file \\SystemRoot\\WinSxS\\Sample\\sample.dll by copying from backup",
            "2026-11-06 10:40:05, Info                  CSI    00000a05 [SR] Restored file sample2.dll",
            "2026-11-06 10:40:06, Error                 CBS    Failed to internally open package. [HRESULT = 0x800f0805 - CBS_E_INVALID_PACKAGE]",
            "2026-11-06 10:40:07, Info                  CBS    Ending TrustedInstaller finalization.",
        ];
        foreach (var l in interesting) sb.Append(l).Append("\r\n");
        await File.WriteAllTextAsync(path, sb.ToString());

        // Windows keeps CBS.log open for writing while servicing runs. Hold it the same way.
        CbsResult all, sr;
        await using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var oldWay = "";
            try
            {
                _ = File.ReadAllLines(path);
                oldWay = "read";
            }
            catch (IOException)
            {
                oldWay = "fails (sharing violation), as the old version did";
            }

            R.AppendLine($"Log held open for writing; File.ReadAllLines {oldWay}");
            var clock = Stopwatch.StartNew();
            all = await CbsLog.FilterAsync(path);
            Check("Reads the log while it is held open for writing", all.LinesRead == 20000 + interesting.Length, $"{all.LinesRead:N0} lines in {clock.ElapsedMilliseconds} ms");
            sr = await CbsLog.FilterAsync(path, sfcOnly: true);
        }

        Check("Matches error/corrupt/restored/repaired lines", all.Matches.Count == 5, $"{all.Matches.Count} matches");
        Check("Keyword counts", all.Count("error") == 1 && all.Count("corrupt") == 2 && all.Count("restored") == 1 && all.Count("repaired") == 1,
            string.Join(", ", CbsLog.Keywords.Select((k, i) => $"{k} {all.Counts[i]}")));
        Check("SFC-only filter keeps [SR] lines", sr.Matches.Count == 4 && sr.Count("error") == 0, $"{sr.Matches.Count} matches");

        var segs = CbsLog.Segments("file is CORRUPT and was Repaired");
        Check("Keyword highlighting splits the line", segs.Count == 4 && segs[1] == ("CORRUPT", 1) && segs[3] == ("Repaired", 3),
            string.Join(" | ", segs.Select(s => $"{s.Text}{(s.Keyword >= 0 ? $"<{CbsLog.Keywords[s.Keyword]}>" : "")}")));

        // An exclusive lock (another program copying it) is waited out instead of failing straight away.
        var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Delay(1000).ContinueWith(_ => locked.Dispose());
        var retried = await CbsLog.FilterAsync(path);
        Check("Retries while the log is locked exclusively", retried.Matches.Count == 5);

        var saved = Path.Combine(Dir!, "CBS_Results.txt");
        await File.WriteAllTextAsync(saved, new string('x', 50000)); // a longer old file must not leave a tail
        await using (var fs = new FileStream(saved, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(0);
            await CbsLog.SaveAsync(all, fs);
        }

        var back = await File.ReadAllLinesAsync(saved);
        Check("Save results writes header and every match", back.Length == 5 + all.Matches.Count && back[0].StartsWith("PAX Diagnostics") && !back.Any(l => l.Contains("xxx")),
            $"{back.Length} lines");
        Check("Copy text has every match", CbsLog.ToText(all).Split(Environment.NewLine).Length == all.Matches.Count);
        Check("Real CBS.log is never written to", !string.Equals(Path.GetFullPath(path), CbsLog.DefaultPath, StringComparison.OrdinalIgnoreCase));
        R.AppendLine();
        return all;
    }

    private static async Task RealCbs()
    {
        R.AppendLine("== Real CBS log (read only) ==");
        var found = CbsLog.Find();
        R.AppendLine($"Auto-find: {found ?? "none"}");
        if (found is null) return;
        try
        {
            var clock = Stopwatch.StartNew();
            var r = await CbsLog.FilterAsync(found);
            R.AppendLine($"Read {r.LinesRead:N0} lines ({new FileInfo(found).Length / 1024.0 / 1024:0.0} MB) in {clock.ElapsedMilliseconds} ms: {r.Matches.Count:N0} matches ("
                         + string.Join(", ", CbsLog.Keywords.Select((k, i) => $"{k} {r.Counts[i]:N0}")) + ")");
        }
        catch (Exception ex)
        {
            R.AppendLine($"Couldn't read it as this user: {ex.GetType().Name}: {ex.Message}");
        }

        R.AppendLine();
    }

    // ---------- SFC / DISM output ----------

    private static (List<string> Lines, List<string> Status) Decode(byte[] bytes, int chunk)
    {
        var lines = new List<string>();
        var status = new List<string>();
        var reader = new ConsoleOutputReader(lines.Add, status.Add);
        for (var i = 0; i < bytes.Length; i += chunk)
            reader.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
        reader.Finish();
        return (lines, status);
    }

    private static void Decoding()
    {
        R.AppendLine("== SFC / DISM output (recorded, nothing is run) ==");
        const string sfc = "\r\nBeginning system scan.  This process will take some time.\r\n\r\nBeginning verification phase of system scan.\r\n" +
                           "Verification 5% complete.\rVerification 47% complete.\rVerification 100% complete.\r\n\r\n" +
                           "Windows Resource Protection did not find any integrity violations.\r\n";
        // SFC writes UTF-16 when redirected; feed it in odd-sized pieces so characters get split.
        var (lines, status) = Decode(Encoding.Unicode.GetBytes(sfc), 7);
        Check("SFC UTF-16 output decodes to lines", lines.Count == 4 && lines[0].StartsWith("Beginning system scan") && lines[2] == "Verification 100% complete.",
            string.Join(" / ", lines));
        Check("SFC progress comes through as status updates", status.Contains("Verification 5% complete.") && status.Contains("Verification 47% complete."),
            string.Join(" / ", status));
        Check("SFC verdict", RepairTools.Judge(RepairTool.Sfc, 0, lines).Outcome == Outcome.Good, RepairTools.Judge(RepairTool.Sfc, 0, lines).Verdict);

        const string dism = "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0.26100.1\r\n\r\nImage Version: 10.0.26100.4061\r\n\r\n" +
                            "[=====                      10.0%                          ]\r[==========================100.0%==========================] \r\n" +
                            "No component store corruption detected.\r\nThe operation completed successfully.\r\n";
        (lines, status) = Decode(Encoding.ASCII.GetBytes(dism), 5);
        Check("DISM single-byte output decodes to lines", lines.Count == 6 && lines[^1] == "The operation completed successfully.", string.Join(" / ", lines));
        Check("DISM progress percentage", status.Any(s => RepairTools.ParsePercent(s) == 10.0) && RepairTools.ParsePercent(lines[3]) == 100.0);
        Check("DISM verdict", RepairTools.Judge(RepairTool.DismCheck, 0, lines).Outcome == Outcome.Good, RepairTools.Judge(RepairTool.DismCheck, 0, lines).Verdict);
        R.AppendLine();
    }

    private static void Verdicts()
    {
        R.AppendLine("== Verdicts ==");
        (RepairTool Tool, int Code, string Line, Outcome Want)[] cases =
        [
            (RepairTool.Sfc, 0, "Windows Resource Protection found corrupt files and successfully repaired them.", Outcome.Repaired),
            (RepairTool.Sfc, 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.", Outcome.Bad),
            (RepairTool.Sfc, 0, "Windows Resource Protection could not perform the requested operation.", Outcome.Bad),
            (RepairTool.Sfc, 0, "You must be an administrator running a console session in order to use the sfc utility.", Outcome.Bad),
            (RepairTool.Sfc, 0, "There is a system repair pending which requires reboot to complete.", Outcome.Warning),
            (RepairTool.DismCheck, 0, "The component store is repairable.", Outcome.Warning),
            (RepairTool.DismRestore, 0, "The restore operation completed successfully. The component store corruption was repaired.", Outcome.Repaired),
            (RepairTool.DismRestore, unchecked((int)0x800f081f), "Error: 0x800f081f The source files could not be found.", Outcome.Bad),
        ];
        foreach (var c in cases)
        {
            var (verdict, outcome) = RepairTools.Judge(c.Tool, c.Code, [c.Line]);
            Check($"{c.Tool.Title}: \"{c.Line[..Math.Min(48, c.Line.Length)]}…\"", outcome == c.Want, $"{outcome}, {verdict}");
        }

        R.AppendLine();
    }

    // ---------- Elevation ----------

    private static void ElevationChecks()
    {
        R.AppendLine("== Elevation (nothing is started) ==");
        R.AppendLine($"Running elevated: {Elevation.IsElevated}");
        var psi = Elevation.RestartInfo("sfc");
        Check("Restart asks Windows for administrator rights", psi.UseShellExecute && psi.Verb == "runas");
        Check("Restart opens straight on the tool", psi.Arguments == "--tool sfc" && RepairTool.FromId("sfc") == RepairTool.Sfc
                                                     && Elevation.Tools.All(t => RepairTool.FromId(t) is not null), psi.Arguments);
        Check("Restart runs this same exe", File.Exists(psi.FileName), Path.GetFileName(psi.FileName));
        foreach (var exe in new[] { "sfc.exe", "dism.exe" })
            Check($"64-bit {exe} found", File.Exists(RepairTools.SystemExe(exe)), RepairTools.SystemExe(exe));
        R.AppendLine();
    }

    // ---------- Window ----------

    private static async Task Render(List<InfoSection> sections, CbsResult cbs)
    {
        R.AppendLine("== Window (rendered off-screen) ==");
        var window = new MainWindow(null, autoLoad: false, elevated: Sample ? false : null)
        {
            // Far off every screen, never activated, not on the taskbar: nothing appears for the user.
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-30000, -30000),
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        window.ShowSystem(sections, Sample ? "Read at 09:41 in 1.2 s" : null);

        if (Sample)
        {
            // A finished SFC run that repaired something, and DISM CheckHealth part-way through.
            string[] output =
            [
                "Beginning system scan.  This process will take some time.",
                "Beginning verification phase of system scan.",
                "Verification 100% complete.",
                "Windows Resource Protection found corrupt files and successfully repaired them.",
                "For online repairs, details are included in the CBS log file located at",
                "windir\\Logs\\CBS\\CBS.log. For example C:\\Windows\\Logs\\CBS\\CBS.log. For offline",
                "repairs, details are included in the log file provided by the /OFFLOGFILE flag.",
            ];
            foreach (var l in output) window.AppendOutput(l);
            var (verdict, outcome) = RepairTools.Judge(RepairTool.Sfc, 0, output);
            window.ShowToolResult(RepairTool.Sfc, new RepairResult(0, output, verdict, outcome), TimeSpan.FromSeconds(8 * 60 + 23));
            window.ShowRunning(RepairTool.DismCheck, "Working: 40% done  ·  0:12", 40, "SFC /scannow, started 09:30");
            window.ShowCbs(cbs with { Path = @"C:\Windows\Logs\CBS\CBS.log" });
        }
        else
        {
            window.ShowCbs(cbs);
        }

        string[] names = ["system", "repair", "cbs-log"];
        for (var tab = 0; tab < names.Length; tab++)
        {
            window.Tabs.SelectedIndex = tab;
            window.Width = 1000;
            window.Height = 760;
            window.UpdateLayout();
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            using var bmp = new RenderTargetBitmap(new PixelSize(1000, 760));
            bmp.Render(window);
            var file = Path.Combine(Dir!, names[tab] + ".png");
            bmp.Save(file, PngBitmapEncoderOptions.Default);
            Check($"Rendered {names[tab]} tab", new FileInfo(file).Length > 10_000, file);
        }

        window.Close();
    }
}
