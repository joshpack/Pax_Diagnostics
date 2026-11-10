using System.Text;

namespace pxdDiagnostics.Diagnostics;

/// <summary>The lines of a CBS.log that mention errors, corruption or repairs, and how often each word came up.</summary>
public sealed record CbsResult(string Path, long LinesRead, IReadOnlyList<string> Matches, int[] Counts, bool SfcOnly)
{
    public int Count(string keyword) => Counts[Array.IndexOf(CbsLog.Keywords, keyword)];
}

/// <summary>Finding, reading and filtering the Component-Based Servicing log that SFC and DISM write to.</summary>
public static class CbsLog
{
    /// <summary>The words a repair entry is found by, in the order they're coloured.</summary>
    public static readonly string[] Keywords = ["error", "corrupt", "restored", "repaired"];

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS");

    public static string DefaultPath => Path.Combine(Folder, "CBS.log");

    /// <summary>CBS.log, or (if Windows has just rolled it over) the newest log in the CBS folder; null if there's none.</summary>
    public static string? Find()
    {
        try
        {
            if (File.Exists(DefaultPath)) return DefaultPath;
            return Directory.Exists(Folder)
                ? new DirectoryInfo(Folder).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the log without blocking the caller. Windows keeps CBS.log open for writing while servicing runs, so it is
    /// opened with read-write sharing, which works while it's in use; a sharing violation (another program holding it
    /// exclusively) is retried a few times before giving up.
    /// </summary>
    public static Task<CbsResult> FilterAsync(string path, bool sfcOnly = false, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return Filter(path, sfcOnly, ct);
                }
                catch (IOException ex) when (IsSharingViolation(ex) && attempt < 4)
                {
                    await Task.Delay(1500, ct);
                }
            }
        }, ct);

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private static CbsResult Filter(string path, bool sfcOnly, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var matches = new List<string>();
        var counts = new int[Keywords.Length];
        long read = 0;
        while (reader.ReadLine() is { } line)
        {
            read++;
            if ((read & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            if (sfcOnly && !line.Contains("[SR]", StringComparison.Ordinal)) continue;
            var hit = false;
            for (var i = 0; i < Keywords.Length; i++)
            {
                if (!line.Contains(Keywords[i], StringComparison.OrdinalIgnoreCase)) continue;
                counts[i]++;
                hit = true;
            }

            if (hit) matches.Add(line);
        }

        return new CbsResult(path, read, matches, counts, sfcOnly);
    }

    /// <summary>A line cut into plain text and keyword pieces (Keyword is the index in <see cref="Keywords"/>, or -1).</summary>
    public static List<(string Text, int Keyword)> Segments(string line)
    {
        var parts = new List<(string, int)>();
        var pos = 0;
        while (pos < line.Length)
        {
            int best = -1, bestAt = line.Length;
            for (var i = 0; i < Keywords.Length; i++)
            {
                var at = line.IndexOf(Keywords[i], pos, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && at < bestAt) (best, bestAt) = (i, at);
            }

            if (best < 0)
            {
                parts.Add((line[pos..], -1));
                break;
            }

            if (bestAt > pos) parts.Add((line[pos..bestAt], -1));
            parts.Add((line.Substring(bestAt, Keywords[best].Length), best));
            pos = bestAt + Keywords[best].Length;
        }

        return parts;
    }

    public static string ToText(CbsResult r) => string.Join(Environment.NewLine, r.Matches);

    /// <summary>The results as a text file: a short header, then every matching line.</summary>
    public static async Task SaveAsync(CbsResult r, Stream destination)
    {
        await using var w = new StreamWriter(destination, new UTF8Encoding(false));
        await w.WriteLineAsync($"pxd Diagnostics - CBS log entries ({DateTime.Now:yyyy-MM-dd HH:mm})");
        await w.WriteLineAsync($"Log: {r.Path}");
        await w.WriteLineAsync($"Lines read: {r.LinesRead:N0}, matching: {r.Matches.Count:N0}{(r.SfcOnly ? " (SFC [SR] lines only)" : "")}");
        await w.WriteLineAsync(string.Join(", ", Keywords.Select((k, i) => $"{k}: {r.Counts[i]:N0}")));
        await w.WriteLineAsync();
        foreach (var line in r.Matches)
            await w.WriteLineAsync(line);
    }
}
