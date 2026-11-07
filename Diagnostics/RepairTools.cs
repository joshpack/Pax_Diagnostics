using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PaxDiagnostics.Diagnostics;

/// <summary>Which repair tool, its command line, and what it is called on screen.</summary>
public sealed record RepairTool(string Id, string Exe, string Arguments, string Title)
{
    public static readonly RepairTool Sfc = new("sfc", "sfc.exe", "/scannow", "SFC /scannow");
    public static readonly RepairTool DismCheck = new("dism-check", "dism.exe", "/Online /Cleanup-Image /CheckHealth", "DISM /CheckHealth");
    public static readonly RepairTool DismRestore = new("dism-restore", "dism.exe", "/Online /Cleanup-Image /RestoreHealth", "DISM /RestoreHealth");

    public static RepairTool? FromId(string? id) =>
        new[] { Sfc, DismCheck, DismRestore }.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed record RepairResult(int ExitCode, IReadOnlyList<string> Lines, string Verdict, Outcome Outcome);

public enum Outcome { Good, Repaired, Warning, Bad }

/// <summary>Runs SFC and DISM with their output captured, reporting each line and the percentage as they come.</summary>
public static partial class RepairTools
{
    /// <summary>
    /// The real 64-bit tool. A 32-bit process asking for System32 is silently sent to SysWOW64, where there is no
    /// sfc.exe (and a 32-bit DISM that can't service a 64-bit Windows); Sysnative reaches the real System32.
    /// </summary>
    public static string SystemExe(string name)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var folder = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        return Path.Combine(windows, folder, name);
    }

    public static async Task<RepairResult> RunAsync(RepairTool tool, Action<string> onLine, Action<string> onStatus,
        Action<double> onPercent, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(SystemExe(tool.Exe), tool.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{tool.Exe} didn't start.");
        var lines = new List<string>();
        var reader = new ConsoleOutputReader(
            line => { lock (lines) lines.Add(line); onLine(line); if (ParsePercent(line) is { } p) onPercent(p); },
            status => { onStatus(status); if (ParsePercent(status) is { } p) onPercent(p); });

        var errors = Task.Run(async () =>
        {
            var errReader = new ConsoleOutputReader(l => { lock (lines) lines.Add(l); onLine(l); }, _ => { });
            await errReader.PumpAsync(process.StandardError.BaseStream, ct);
        }, ct);
        await reader.PumpAsync(process.StandardOutput.BaseStream, ct);
        await errors;
        await process.WaitForExitAsync(ct);

        // The callbacks above run on background threads; the caller hands them to the UI thread.
        var (verdict, outcome) = Judge(tool, process.ExitCode, lines);
        return new RepairResult(process.ExitCode, lines, verdict, outcome);
    }

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    public static double? ParsePercent(string text)
    {
        var m = PercentRegex().Match(text);
        if (!m.Success) return null;
        return double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v <= 100
            ? v
            : null;
    }

    /// <summary>A one-line verdict from the tool's final messages. Falls back to the last line it printed.</summary>
    public static (string Verdict, Outcome Outcome) Judge(RepairTool tool, int exitCode, IReadOnlyList<string> lines)
    {
        bool Has(string s) => lines.Any(l => l.Contains(s, StringComparison.OrdinalIgnoreCase));
        var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && ParsePercent(l) is null) ?? "";

        if (Has("must be an administrator") || Has("Elevated permissions are required") || exitCode == 740)
            return ("Needs administrator rights.", Outcome.Bad);
        if (Has("repair pending") || Has("pending.xml"))
            return ("A repair is pending: restart Windows, then run it again.", Outcome.Warning);

        if (tool.Id == "sfc")
        {
            if (Has("did not find any integrity violations")) return ("No integrity violations found.", Outcome.Good);
            if (Has("successfully repaired")) return ("Found corrupt files and repaired them.", Outcome.Repaired);
            if (Has("unable to fix")) return ("Found corrupt files it couldn't fix. Try DISM /RestoreHealth, then SFC again.", Outcome.Bad);
            if (Has("could not perform the requested operation")) return ("SFC couldn't run the scan. Try again in Safe Mode.", Outcome.Bad);
        }
        else
        {
            if (Has("No component store corruption detected")) return ("No component store corruption detected.", Outcome.Good);
            if (Has("corruption was repaired")) return ("The component store was repaired.", Outcome.Repaired);
            if (Has("component store is repairable")) return ("The component store is damaged but repairable: run DISM /RestoreHealth.", Outcome.Warning);
            if (Has("source files could not be found")) return ("Repair files couldn't be downloaded. Check the internet connection.", Outcome.Bad);
        }

        if (exitCode != 0)
            return ($"Finished with exit code {exitCode}. {last}".Trim(), Outcome.Bad);
        return (last.Length > 0 ? last : "Finished.", Outcome.Good);
    }
}

/// <summary>
/// Turns a console tool's raw output into lines. SFC writes UTF-16 when its output is redirected; DISM writes the
/// console's OEM code page. Progress is redrawn in place with a carriage return, so text ended by '\r' is a status
/// update and only text ended by '\n' (or the last update before it) becomes a line.
/// </summary>
public sealed class ConsoleOutputReader(Action<string> onLine, Action<string> onStatus)
{
    private Decoder? _decoder;
    private readonly StringBuilder _buffer = new();
    private string? _pending;
    private int _sniffed;
    private readonly byte[] _first = new byte[2];

    static ConsoleOutputReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task PumpAsync(Stream stream, CancellationToken ct = default)
    {
        var bytes = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(bytes, ct)) > 0)
            Feed(bytes.AsSpan(0, n));
        Finish();
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_decoder is null)
        {
            // Wait for two bytes to tell UTF-16 ("V\0e\0") from single-byte text.
            while (_sniffed < 2 && data.Length > 0)
            {
                _first[_sniffed++] = data[0];
                data = data[1..];
            }

            if (_sniffed < 2) return;
            _decoder = PickEncoding(_first).GetDecoder();
            Decode(_first);
        }

        Decode(data);
    }

    public void Finish()
    {
        if (_decoder is null && _sniffed > 0)
        {
            _decoder = OemEncoding().GetDecoder();
            Decode(_first.AsSpan(0, _sniffed));
        }

        if (_buffer.Length > 0) Commit(_buffer.ToString());
        else if (_pending is not null) Commit(_pending);
        _buffer.Clear();
        _pending = null;
    }

    private static Encoding PickEncoding(ReadOnlySpan<byte> first)
    {
        if (first[0] == 0xFF && first[1] == 0xFE) return new UnicodeEncoding(false, false);
        if (first[1] == 0 && first[0] != 0) return new UnicodeEncoding(false, false);
        return OemEncoding();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding OemEncoding()
    {
        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private void Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var chars = new char[_decoder!.GetCharCount(data, flush: false)];
        var count = _decoder.GetChars(data, chars, flush: false);
        foreach (var c in chars.AsSpan(0, count))
        {
            switch (c)
            {
                case '﻿' or '\0':
                    break;
                case '\r':
                    if (_buffer.Length > 0)
                    {
                        _pending = _buffer.ToString();
                        _buffer.Clear();
                        if (!string.IsNullOrWhiteSpace(_pending)) onStatus(_pending.Trim());
                    }
                    break;
                case '\n':
                    if (_buffer.Length > 0) Commit(_buffer.ToString());
                    else if (_pending is not null) Commit(_pending);
                    _buffer.Clear();
                    _pending = null;
                    break;
                default:
                    _buffer.Append(c);
                    break;
            }
        }
    }

    private void Commit(string line)
    {
        line = line.Trim();
        if (line.Length > 0) onLine(line);
    }
}
