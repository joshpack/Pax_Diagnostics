using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PaxDiagnostics.Diagnostics;

public sealed record InfoRow(string Label, string Value);

/// <summary>One device inside a section (a disk, an adapter), or the whole section when Heading is null.</summary>
public sealed class InfoGroup(string? heading = null)
{
    public string? Heading { get; } = heading;
    public List<InfoRow> Rows { get; } = [];

    public InfoGroup Add(string label, object? value)
    {
        var text = value?.ToString()?.Trim();
        if (!string.IsNullOrEmpty(text)) Rows.Add(new InfoRow(label, text));
        return this;
    }
}

public sealed class InfoSection(string title)
{
    public string Title { get; } = title;
    public List<InfoGroup> Groups { get; } = [];
    /// <summary>Why (part of) the section couldn't be read; shown on its card instead of being swallowed.</summary>
    public string? Error { get; set; }
    public bool IsEmpty => Groups.All(g => g.Rows.Count == 0);

    public InfoGroup Group(string? heading = null)
    {
        var g = new InfoGroup(heading);
        Groups.Add(g);
        return g;
    }
}

/// <summary>Reads the PC's hardware and Windows details, from WMI, the registry and .NET. Slow (WMI): run it off the UI thread.</summary>
public static class SystemInfo
{
    public static List<InfoSection> Gather() =>
    [
        Section("Windows", Windows),
        Section("Processor", Processor),
        Section("Memory", Memory),
        Section("Graphics", Graphics),
        Section("Storage", Storage),
        Section("Network", Network),
        Section("Motherboard & BIOS", Motherboard),
    ];

    private static InfoSection Section(string title, Action<InfoSection> fill)
    {
        var s = new InfoSection(title);
        try
        {
            fill(s);
        }
        catch (Exception ex)
        {
            s.Error = ex.Message.Trim();
        }

        if (s.IsEmpty && s.Error is null) s.Error = "Windows returned nothing for this section.";
        return s;
    }

    /// <summary>Runs a WMI query and hands each result over while it's still alive.</summary>
    private static void Each(string query, Action<ManagementBaseObject> use, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var results = searcher.Get();
        foreach (var o in results)
            using (o)
                use(o);
    }

    /// <summary>What board makers leave in fields they never filled in.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To be filled by O.E.M.", "Default string", "System Product Name", "System manufacturer", "System Version", "Not Applicable", "None", "0",
    };

    private static string? Str(ManagementBaseObject o, string prop)
    {
        try
        {
            var s = o[prop]?.ToString()?.Trim();
            return string.IsNullOrEmpty(s) || Placeholders.Contains(s) ? null : s;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static ulong? Num(ManagementBaseObject o, string prop)
    {
        try
        {
            return o[prop] is { } v ? Convert.ToUInt64(v) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes:0} B" : $"{bytes:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min" : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min" : $"{t.Minutes} min";

    private static DateTime? CimDate(string? s)
    {
        if (s is null) return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(s);
        }
        catch
        {
            return null;
        }
    }

    private static object? Reg(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value);
        }
        catch
        {
            return null;
        }
    }

    private static void Windows(InfoSection s)
    {
        var g = s.Group();
        g.Add("Computer name", Environment.MachineName);
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string? caption = null, version = null, build = null;
        DateTime? installed = null;
        Each("SELECT Caption, Version, BuildNumber, InstallDate FROM Win32_OperatingSystem", o =>
        {
            caption = Str(o, "Caption");
            version = Str(o, "Version");
            build = Str(o, "BuildNumber");
            installed = CimDate(Str(o, "InstallDate"));
        });

        g.Add("Edition", caption?.Replace("Microsoft ", "") ?? RuntimeInformation.OSDescription);
        var display = Reg(cv, "DisplayVersion") ?? Reg(cv, "ReleaseId");
        var ubr = Reg(cv, "UBR");
        g.Add("Version", display is null ? version : $"{display} (build {build ?? Environment.OSVersion.Version.Build.ToString()}{(ubr is null ? "" : "." + ubr)})");
        g.Add("Architecture", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
        {
            "x64" => "64-bit (x64)",
            "arm64" => "64-bit (Arm64)",
            "x86" => "32-bit (x86)",
            var a => a,
        });
        g.Add("Installed", installed?.ToString("yyyy-MM-dd"));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        g.Add("Uptime", $"{Duration(up)} (since {DateTime.Now - up:yyyy-MM-dd HH:mm})");
        g.Add("Running as", Platform.Elevation.IsElevated ? "Administrator (elevated)" : "Standard (not elevated)");
    }

    private static void Processor(InfoSection s)
    {
        Each("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, CurrentClockSpeed, MaxClockSpeed, L3CacheSize, SocketDesignation FROM Win32_Processor", o =>
        {
            var g = s.Group();
            g.Add("Name", Str(o, "Name"));
            g.Add("Manufacturer", Str(o, "Manufacturer"));
            g.Add("Cores / threads", $"{Num(o, "NumberOfCores")} cores, {Num(o, "NumberOfLogicalProcessors")} threads");
            if (Num(o, "MaxClockSpeed") is { } max and > 0) g.Add("Base clock", $"{max / 1000.0:0.00} GHz");
            if (Num(o, "CurrentClockSpeed") is { } cur and > 0) g.Add("Current clock", $"{cur / 1000.0:0.00} GHz");
            if (Num(o, "L3CacheSize") is { } l3 and > 0) g.Add("L3 cache", Bytes(l3 * 1024.0));
            g.Add("Socket", Str(o, "SocketDesignation"));
        });
    }

    private static void Memory(InfoSection s)
    {
        var g = s.Group();
        ulong total = 0, free = 0;
        // Win32_ComputerSystem gives bytes; Win32_OperatingSystem gives kilobytes.
        Each("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", o => total = Num(o, "TotalPhysicalMemory") ?? 0);
        Each("SELECT FreePhysicalMemory FROM Win32_OperatingSystem", o => free = (Num(o, "FreePhysicalMemory") ?? 0) * 1024);
        var modules = new List<(ulong Size, ulong Speed, string? Maker, string? Part)>();
        int? slots = null;
        try
        {
            Each("SELECT Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory", o =>
                modules.Add((Num(o, "Capacity") ?? 0, Num(o, "ConfiguredClockSpeed") is { } c and > 0 ? c : Num(o, "Speed") ?? 0, Str(o, "Manufacturer"), Str(o, "PartNumber"))));
            ulong slotCount = 0;
            Each("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", o => slotCount += Num(o, "MemoryDevices") ?? 0);
            if (slotCount > 0) slots = (int)slotCount;
        }
        catch (ManagementException)
        {
            // Some virtual machines have no memory module table; the totals above are still right.
        }

        var installed = modules.Aggregate(0UL, (a, m) => a + m.Size);
        g.Add("Installed", installed > 0 ? Bytes(installed) : null);
        if (total > 0)
        {
            g.Add("Usable", Bytes(total));
            g.Add("In use", $"{Bytes(total - Math.Min(free, total))} ({100.0 * (total - Math.Min(free, total)) / total:0}%)");
            g.Add("Available", Bytes(free));
        }

        if (modules.Count > 0)
        {
            var speed = modules.Max(m => m.Speed);
            g.Add("Modules", string.Join(", ", modules.GroupBy(m => m.Size).Select(x => $"{x.Count()} × {Bytes(x.Key)}")) + (speed > 0 ? $" @ {speed} MT/s" : ""));
            g.Add("Slots used", slots is { } n ? $"{modules.Count} of {n}" : modules.Count.ToString());
            var parts = modules.Select(m => string.Join(" ", new[] { m.Maker, m.Part }.Where(x => x is not null))).Where(x => x.Length > 0).Distinct().ToList();
            if (parts.Count > 0) g.Add("Part", string.Join(", ", parts));
        }
    }

    private static void Graphics(InfoSection s)
    {
        var vram = VideoMemoryFromRegistry();
        Each("SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController", o =>
        {
            var name = Str(o, "Name");
            var g = s.Group(name);
            // AdapterRAM is a 32-bit number and tops out at 4 GB; the driver's own figure in the registry doesn't.
            if (name is not null && vram.TryGetValue(name, out var bytes)) g.Add("Video memory", Bytes(bytes));
            else if (Num(o, "AdapterRAM") is { } ram and > 0) g.Add("Video memory", Bytes(ram) + (ram >= 0xFFF00000 ? "+" : ""));
            g.Add("Driver", Str(o, "DriverVersion"));
            g.Add("Driver date", CimDate(Str(o, "DriverDate"))?.ToString("yyyy-MM-dd"));
            if (Num(o, "CurrentHorizontalResolution") is { } w and > 0 && Num(o, "CurrentVerticalResolution") is { } h and > 0)
                g.Add("Display", $"{w} × {h}{(Num(o, "CurrentRefreshRate") is { } hz and > 1 ? $" @ {hz} Hz" : "")}");
        });
    }

    private static Dictionary<string, double> VideoMemoryFromRegistry()
    {
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return found;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is not string desc) continue;
                    var size = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => (double)l,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b),
                        _ => 0d,
                    };
                    if (size > 0) found[desc] = size;
                }
                catch (System.Security.SecurityException)
                {
                    // Some adapter keys are closed to standard users; WMI's figure is used for those.
                }
            }
        }
        catch
        {
            // Fall back to WMI.
        }

        return found;
    }

    private static void Storage(InfoSection s)
    {
        var disks = 0;
        try
        {
            // The Storage API knows SSD from HDD, NVMe from SATA, and whether Windows thinks the disk is healthy.
            Each("SELECT FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk", o =>
            {
                var g = s.Group(Str(o, "FriendlyName") ?? "Disk");
                var media = Num(o, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
                var bus = Num(o, "BusType") switch
                {
                    1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD card", 17 => "NVMe", 15 => "Virtual", _ => null,
                };
                g.Add("Type", string.Join(" · ", new[] { media, bus }.Where(x => x is not null)));
                if (Num(o, "Size") is { } size and > 0) g.Add("Size", Bytes(size));
                g.Add("Health", Num(o, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => null });
                disks++;
            }, @"root\Microsoft\Windows\Storage");
        }
        catch (ManagementException)
        {
        }

        if (disks == 0)
        {
            Each("SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive", o =>
            {
                var g = s.Group(Str(o, "Model") ?? "Disk");
                g.Add("Interface", Str(o, "InterfaceType"));
                // Size is empty for card readers with no card in them.
                if (Num(o, "Size") is { } size and > 0) g.Add("Size", Bytes(size));
                g.Add("Media", Str(o, "MediaType"));
            });
        }

        var volumes = s.Group("Volumes");
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var used = d.TotalSize - d.TotalFreeSpace;
                volumes.Add(d.Name.TrimEnd('\\') + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : $" {d.VolumeLabel}"),
                    $"{Bytes(d.TotalFreeSpace)} free of {Bytes(d.TotalSize)} ({100.0 * used / d.TotalSize:0}% used, {d.DriveFormat})");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Network(InfoSection s)
    {
        s.Group().Add("Host name", System.Net.Dns.GetHostName());
        var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Each("SELECT GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True", o =>
            {
                if (Str(o, "GUID") is { } id) physical.Add(id);
            });
        }
        catch (ManagementException)
        {
        }

        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            // Physical adapters, plus virtual ones that are actually in use (have an IPv4 address); this leaves out the
            // filter-driver layers and WAN miniports Windows lists as separate "connected" interfaces.
            .Where(n => physical.Contains(n.Id) || (n.OperationalStatus == OperationalStatus.Up && HasIPv4(n)))
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .ThenByDescending(n => physical.Contains(n.Id));
        foreach (var n in nics)
        {
            var g = s.Group(n.Name);
            g.Add("Adapter", n.Description);
            var up = n.OperationalStatus == OperationalStatus.Up;
            g.Add("Status", up ? (n.Speed > 0 ? $"Connected, {Speed(n.Speed)}" : "Connected") : "Disconnected");
            var mac = n.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6) g.Add("MAC", string.Join("-", mac.Select(b => b.ToString("X2"))));
            if (!up) continue;
            var ip = n.GetIPProperties();
            var v4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{a.Address}/{a.PrefixLength}");
            g.Add("IPv4", string.Join(", ", v4));
            var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString());
            g.Add("IPv6", string.Join(", ", v6.Take(2)));
            g.Add("Gateway", string.Join(", ", ip.GatewayAddresses.Select(a => a.Address).Where(a => !a.Equals(System.Net.IPAddress.Any)).Select(a => a.ToString())));
            g.Add("DNS", string.Join(", ", ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString())));
        }
    }

    private static bool HasIPv4(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static string Speed(long bps) => bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbit/s" : $"{bps / 1e6:0} Mbit/s";

    private static void Motherboard(InfoSection s)
    {
        var sys = s.Group();
        Each("SELECT Manufacturer, Model FROM Win32_ComputerSystem", o =>
        {
            sys.Add("System maker", Str(o, "Manufacturer"));
            sys.Add("System model", Str(o, "Model"));
        });
        Each("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard", o =>
        {
            sys.Add("Board maker", Str(o, "Manufacturer"));
            sys.Add("Board", string.Join(" ", new[] { Str(o, "Product"), Str(o, "Version") is { } v && v != "1.0" ? (v.StartsWith("rev", StringComparison.OrdinalIgnoreCase) ? $"({v})" : $"(rev {v})") : null }.Where(x => x is not null)));
            sys.Add("Board serial", Str(o, "SerialNumber"));
        });
        Each("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", o =>
        {
            sys.Add("BIOS", string.Join(" ", new[] { Str(o, "Manufacturer"), Str(o, "SMBIOSBIOSVersion") }.Where(x => x is not null)));
            sys.Add("BIOS date", CimDate(Str(o, "ReleaseDate"))?.ToString("yyyy-MM-dd"));
        });
        sys.Add("Firmware", GetFirmwareType(out var fw) ? fw switch { 2 => "UEFI", 1 => "Legacy BIOS", _ => null } : null);
        sys.Add("Secure Boot", Reg(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") switch { 1 => "On", 0 => "Off", _ => null });
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    /// <summary>Everything as plain text, for "Copy all".</summary>
    public static string ToText(IEnumerable<InfoSection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PAX Diagnostics - system information ({DateTime.Now:yyyy-MM-dd HH:mm})");
        foreach (var s in sections)
        {
            sb.AppendLine().AppendLine(s.Title.ToUpperInvariant());
            foreach (var g in s.Groups.Where(g => g.Rows.Count > 0))
            {
                if (g.Heading is not null) sb.AppendLine("  " + g.Heading);
                var indent = g.Heading is null ? "  " : "    ";
                var width = g.Rows.Max(r => r.Label.Length);
                foreach (var r in g.Rows)
                    sb.AppendLine($"{indent}{r.Label.PadRight(width)}  {r.Value}");
            }

            if (s.Error is not null) sb.AppendLine($"  (couldn't read: {s.Error})");
        }

        return sb.ToString();
    }

    /// <summary>Made-up details for screenshots, so no real machine shows up in them.</summary>
    public static List<InfoSection> Sample()
    {
        var win = new InfoSection("Windows");
        win.Group().Add("Computer name", "WORKSTATION-07").Add("Edition", "Windows 11 Pro").Add("Version", "24H2 (build 26100.4061)")
            .Add("Architecture", "64-bit (x64)").Add("Installed", "2025-03-14").Add("Uptime", "2 d 5 h 41 min (since 2026-11-04 09:12)")
            .Add("Running as", "Standard (not elevated)");
        var cpu = new InfoSection("Processor");
        cpu.Group().Add("Name", "Example 8-Core Processor 4.2 GHz").Add("Manufacturer", "ExampleChip").Add("Cores / threads", "8 cores, 16 threads")
            .Add("Base clock", "4.20 GHz").Add("Current clock", "4.05 GHz").Add("L3 cache", "32 MB").Add("Socket", "SOCKET 0");
        var mem = new InfoSection("Memory");
        mem.Group().Add("Installed", "32 GB").Add("Usable", "31.8 GB").Add("In use", "11.2 GB (35%)").Add("Available", "20.6 GB")
            .Add("Modules", "2 × 16 GB @ 5600 MT/s").Add("Slots used", "2 of 4").Add("Part", "Generic DDR5-5600");
        var gpu = new InfoSection("Graphics");
        gpu.Group("Example Graphics 4070").Add("Video memory", "12 GB").Add("Driver", "32.0.15.6094").Add("Driver date", "2026-09-18")
            .Add("Display", "2560 × 1440 @ 165 Hz");
        var disk = new InfoSection("Storage");
        disk.Group("Generic NVMe SSD 1TB").Add("Type", "SSD · NVMe").Add("Size", "931.5 GB").Add("Health", "Healthy");
        disk.Group("Generic SATA HDD 2TB").Add("Type", "HDD · SATA").Add("Size", "1.8 TB").Add("Health", "Healthy");
        disk.Group("Volumes").Add("C:", "412.3 GB free of 930.6 GB (56% used, NTFS)").Add("D: Data", "1.1 TB free of 1.8 TB (39% used, NTFS)");
        var net = new InfoSection("Network");
        net.Group().Add("Host name", "WORKSTATION-07");
        net.Group("Ethernet").Add("Adapter", "Generic 2.5GbE Controller").Add("Status", "Connected, 2.5 Gbit/s").Add("MAC", "00-11-22-33-44-55")
            .Add("IPv4", "192.168.1.42/24").Add("Gateway", "192.168.1.1").Add("DNS", "192.168.1.1");
        net.Group("Wi-Fi").Add("Adapter", "Generic Wi-Fi 6E Adapter").Add("Status", "Disconnected").Add("MAC", "00-11-22-33-44-66");
        var board = new InfoSection("Motherboard & BIOS");
        board.Group().Add("System maker", "Example Systems").Add("System model", "Tower 7").Add("Board maker", "Example Boards")
            .Add("Board", "X-PRO 600 (rev 1.2)").Add("Board serial", "SAMPLE0000000").Add("BIOS", "Example BIOS 1.40").Add("BIOS date", "2026-06-02")
            .Add("Firmware", "UEFI").Add("Secure Boot", "On");
        return [win, cpu, mem, gpu, disk, net, board];
    }
}
