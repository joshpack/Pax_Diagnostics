#Requires -Version 5.1
<#
.SYNOPSIS
    pxd Diagnostics (CLI) - system information, SFC and DISM, and the CBS log, right in the console.

.DESCRIPTION
    The console edition of pxd Diagnostics. Nothing is installed. Windows is only changed when you pick
    a repair tool (SFC or DISM) and confirm it. Run it straight from the web:

        irm pxdtek.com/diag | iex

    (pxdtek.com serves the latest copy of
        https://raw.githubusercontent.com/pxdtek/pxdDiagnostics/master/cli/pxdDiagnostics.ps1)

    It shows what's in the PC at a glance (Windows, processor, memory, graphics, disks, network,
    motherboard and BIOS), runs SFC /scannow and DISM (CheckHealth, ScanHealth, RestoreHealth) with
    live progress and a plain-language verdict, and pulls the errors and repairs out of the CBS log.

    Works as a normal user; SFC and DISM need an elevated prompt (A, or pick a tool and say yes to
    restarting as administrator).

    With parameters through the web:
        & ([scriptblock]::Create((irm pxdtek.com/diag))) -Report

.PARAMETER Report
    Non-interactive: print the system information and the CBS log summary, then return.
.PARAMETER Export
    Write a report to this path (.txt or .json) and return.
.PARAMETER Copy
    Copy the system information to the clipboard and return.
.PARAMETER SfcOnly
    With -Report or -Export: count only SFC's own [SR] lines of the CBS log.
.PARAMETER Tool
    Start this tool straight away (sfc, dism-check, dism-scan or dism-restore); needs an elevated prompt.
    This is what restarting as administrator uses after you picked and confirmed a tool.
#>
[CmdletBinding()]
param(
    [switch]$Report,
    [string]$Export,
    [switch]$Copy,
    [switch]$SfcOnly,
    [ValidateSet('', 'sfc', 'dism-check', 'dism-scan', 'dism-restore')][string]$Tool = ''
)

$script:PdgVersion = '1.2.0'
$script:PdgUrl = 'pxdtek.com/diag'
# In-memory mode (set by the pxd Toolkit console while it runs this script): nothing is saved on the PC unless the
# user asks for a report (R / -Export / S in the CBS view). The CLI keeps no settings, logs, caches or temp files
# anyway; this also carries the mode over to an elevated window (A).
$script:PdgInMemory = $env:PXD_INMEMORY -eq '1'
$script:PdgRuns = New-Object System.Collections.Generic.List[object]   # tools run in this session, for the report

# ======================================================================================
#  Helpers
# ======================================================================================

function Test-PdgAdmin {
    try { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { $false }
}

function Get-PdgBytes([double]$bytes) {
    $units = 'B', 'KB', 'MB', 'GB', 'TB', 'PB'; $i = 0
    while ($bytes -ge 1024 -and $i -lt $units.Count - 1) { $bytes /= 1024; $i++ }
    if ($i -eq 0) { return ('{0:0} B' -f $bytes) }
    return ('{0:0.#} {1}' -f $bytes, $units[$i])
}

function Get-PdgDuration([TimeSpan]$t) {
    if ($t.TotalDays -ge 1) { return ('{0} d {1} h {2} min' -f [int][Math]::Floor($t.TotalDays), $t.Hours, $t.Minutes) }
    if ($t.TotalHours -ge 1) { return ('{0} h {1} min' -f $t.Hours, $t.Minutes) }
    return ('{0} min' -f $t.Minutes)
}

function Get-PdgElapsed([TimeSpan]$t) {
    if ($t.TotalMinutes -ge 1) { return ('{0}m {1:00}s' -f [int][Math]::Floor($t.TotalMinutes), $t.Seconds) }
    return ('{0}s' -f [int]$t.TotalSeconds)
}

function Get-PdgReg([string]$Key, [string]$Value) {
    # [Registry]::GetValue: Get-ItemProperty throws on some binary values in PowerShell 7
    try { [Microsoft.Win32.Registry]::GetValue("HKEY_LOCAL_MACHINE\$Key", $Value, $null) } catch { $null }
}

$script:PdgPlaceholders = @('To be filled by O.E.M.', 'Default string', 'System Product Name', 'System manufacturer', 'System Version', 'Not Applicable', 'None', '0')

function Get-PdgStr($o, [string]$Prop) {
    try { $s = [string]$o.$Prop } catch { return $null }
    if ($null -eq $s) { return $null }
    $s = $s.Trim()
    if (-not $s -or $script:PdgPlaceholders -contains $s) { return $null }
    return $s
}

function Get-PdgNum($o, [string]$Prop) {
    try { $v = $o.$Prop; if ($null -eq $v -or [string]$v -eq '') { return $null }; return [double]$v } catch { return $null }
}

function Get-PdgCim([string]$Class, [string]$Namespace = 'root\cimv2', [string]$Property = '*') {
    @(Get-CimInstance -ClassName $Class -Namespace $Namespace -Property ($Property -split ',') -ErrorAction Stop)
}

# ======================================================================================
#  System information (same sections as the app)
# ======================================================================================

function New-PdgSection([string]$Title) { [pscustomobject]@{ Title = $Title; Groups = New-Object System.Collections.Generic.List[object]; Error = $null } }

function Add-PdgGroup($Section, [string]$Heading = $null) {
    $g = [pscustomobject]@{ Heading = $Heading; Rows = New-Object System.Collections.Generic.List[object] }
    $Section.Groups.Add($g)
    return $g
}

function Add-PdgRow($Group, [string]$Label, $Value) {
    if ($null -eq $Value) { return }
    $text = ([string]$Value).Trim()
    if ($text) { $Group.Rows.Add([pscustomobject]@{ Label = $Label; Value = $text }) }
}

function Get-PdgWindowsSection($s) {
    $g = Add-PdgGroup $s
    Add-PdgRow $g 'Computer name' $env:COMPUTERNAME
    $os = (Get-PdgCim 'Win32_OperatingSystem' -Property 'Caption,Version,BuildNumber,InstallDate,LastBootUpTime')[0]
    $cv = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $caption = Get-PdgStr $os 'Caption'
    Add-PdgRow $g 'Edition' $(if ($caption) { $caption.Replace('Microsoft ', '') } else { [string][Environment]::OSVersion })
    $display = Get-PdgReg $cv 'DisplayVersion'; if (-not $display) { $display = Get-PdgReg $cv 'ReleaseId' }
    $ubr = Get-PdgReg $cv 'UBR'
    $build = Get-PdgStr $os 'BuildNumber'
    Add-PdgRow $g 'Version' $(if ($display) { "$display (build $build$(if ($null -ne $ubr) { ".$ubr" }))" } else { Get-PdgStr $os 'Version' })
    $arch = try { [string][Runtime.InteropServices.RuntimeInformation]::OSArchitecture } catch { $env:PROCESSOR_ARCHITECTURE }
    Add-PdgRow $g 'Architecture' $(switch ($arch.ToLowerInvariant()) { 'x64' { '64-bit (x64)' } 'amd64' { '64-bit (x64)' } 'arm64' { '64-bit (Arm64)' } 'x86' { '32-bit (x86)' } default { $arch } })
    if ($os.InstallDate) { Add-PdgRow $g 'Installed' ([datetime]$os.InstallDate).ToString('yyyy-MM-dd') }
    if ($os.LastBootUpTime) {
        $boot = [datetime]$os.LastBootUpTime
        Add-PdgRow $g 'Uptime' ('{0} (since {1:yyyy-MM-dd HH:mm})' -f (Get-PdgDuration ((Get-Date) - $boot)), $boot)
    }
    Add-PdgRow $g 'Running as' $(if (Test-PdgAdmin) { 'Administrator (elevated)' } else { 'Standard (not elevated)' })
}

function Get-PdgProcessorSection($s) {
    foreach ($o in (Get-PdgCim 'Win32_Processor' -Property 'Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,CurrentClockSpeed,MaxClockSpeed,L3CacheSize,SocketDesignation')) {
        $g = Add-PdgGroup $s
        Add-PdgRow $g 'Name' (Get-PdgStr $o 'Name')
        Add-PdgRow $g 'Manufacturer' (Get-PdgStr $o 'Manufacturer')
        Add-PdgRow $g 'Cores / threads' ('{0} cores, {1} threads' -f $o.NumberOfCores, $o.NumberOfLogicalProcessors)
        $max = Get-PdgNum $o 'MaxClockSpeed'; if ($max -gt 0) { Add-PdgRow $g 'Base clock' ('{0:0.00} GHz' -f ($max / 1000)) }
        $cur = Get-PdgNum $o 'CurrentClockSpeed'; if ($cur -gt 0) { Add-PdgRow $g 'Current clock' ('{0:0.00} GHz' -f ($cur / 1000)) }
        $l3 = Get-PdgNum $o 'L3CacheSize'; if ($l3 -gt 0) { Add-PdgRow $g 'L3 cache' (Get-PdgBytes ($l3 * 1024)) }
        Add-PdgRow $g 'Socket' (Get-PdgStr $o 'SocketDesignation')
    }
}

function Get-PdgMemorySection($s) {
    $g = Add-PdgGroup $s
    # Win32_ComputerSystem gives bytes; Win32_OperatingSystem gives kilobytes
    $total = [double](Get-PdgCim 'Win32_ComputerSystem' -Property 'TotalPhysicalMemory')[0].TotalPhysicalMemory
    $free = [double](Get-PdgCim 'Win32_OperatingSystem' -Property 'FreePhysicalMemory')[0].FreePhysicalMemory * 1024
    $modules = @(); $slots = 0
    try {
        $modules = @(Get-PdgCim 'Win32_PhysicalMemory' -Property 'Capacity,ConfiguredClockSpeed,Speed,Manufacturer,PartNumber')
        foreach ($a in (Get-PdgCim 'Win32_PhysicalMemoryArray' -Property 'MemoryDevices')) { $slots += [int]$a.MemoryDevices }
    } catch {}   # some virtual machines have no module table; the totals are still right
    $installed = 0; foreach ($m in $modules) { $installed += [double]$m.Capacity }
    if ($installed -gt 0) { Add-PdgRow $g 'Installed' (Get-PdgBytes $installed) }
    if ($total -gt 0) {
        $used = $total - [Math]::Min($free, $total)
        Add-PdgRow $g 'Usable' (Get-PdgBytes $total)
        Add-PdgRow $g 'In use' ('{0} ({1:0}%)' -f (Get-PdgBytes $used), (100 * $used / $total))
        Add-PdgRow $g 'Available' (Get-PdgBytes $free)
    }
    if ($modules.Count) {
        $speed = 0
        foreach ($m in $modules) { $sp = Get-PdgNum $m 'ConfiguredClockSpeed'; if (-not ($sp -gt 0)) { $sp = Get-PdgNum $m 'Speed' }; if ($sp -gt $speed) { $speed = $sp } }
        $sizes = ($modules | Group-Object { [double]$_.Capacity } | ForEach-Object { '{0} x {1}' -f $_.Count, (Get-PdgBytes ([double]$_.Name)) }) -join ', '
        Add-PdgRow $g 'Modules' ($sizes + $(if ($speed -gt 0) { " @ $speed MT/s" } else { '' }))
        Add-PdgRow $g 'Slots used' $(if ($slots -gt 0) { "$($modules.Count) of $slots" } else { "$($modules.Count)" })
        $parts = @($modules | ForEach-Object { (@((Get-PdgStr $_ 'Manufacturer'), (Get-PdgStr $_ 'PartNumber')) | Where-Object { $_ }) -join ' ' } | Where-Object { $_ } | Select-Object -Unique)
        if ($parts.Count) { Add-PdgRow $g 'Part' ($parts -join ', ') }
    }
}

function Get-PdgVideoMemory {
    # AdapterRAM is a 32-bit number and tops out at 4 GB; the driver's own figure in the registry doesn't
    $found = @{}
    try {
        $cls = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}')
        if ($cls) {
            foreach ($sub in $cls.GetSubKeyNames()) {
                if ($sub -notmatch '^\d+$') { continue }
                try {
                    $k = $cls.OpenSubKey($sub)
                    if (-not $k) { continue }
                    $desc = $k.GetValue('DriverDesc'); $v = $k.GetValue('HardwareInformation.qwMemorySize'); $k.Close()
                    $size = 0
                    if ($v -is [long] -or $v -is [int]) { $size = [double]$v } elseif ($v -is [byte[]] -and $v.Length -ge 8) { $size = [double][BitConverter]::ToUInt64($v, 0) }
                    if ($desc -and $size -gt 0) { $found[[string]$desc] = $size }
                } catch {}   # some adapter keys are closed to standard users; WMI's figure is used for those
            }
            $cls.Close()
        }
    } catch {}
    return $found
}

function Get-PdgGraphicsSection($s) {
    $vram = Get-PdgVideoMemory
    foreach ($o in (Get-PdgCim 'Win32_VideoController' -Property 'Name,DriverVersion,DriverDate,AdapterRAM,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate')) {
        $name = Get-PdgStr $o 'Name'
        $g = Add-PdgGroup $s $name
        if ($name -and $vram.ContainsKey($name)) { Add-PdgRow $g 'Video memory' (Get-PdgBytes $vram[$name]) }
        else { $ram = Get-PdgNum $o 'AdapterRAM'; if ($ram -gt 0) { Add-PdgRow $g 'Video memory' ((Get-PdgBytes $ram) + $(if ($ram -ge 0xFFF00000) { '+' } else { '' })) } }
        Add-PdgRow $g 'Driver' (Get-PdgStr $o 'DriverVersion')
        if ($o.DriverDate) { try { Add-PdgRow $g 'Driver date' ([datetime]$o.DriverDate).ToString('yyyy-MM-dd') } catch {} }
        $w = Get-PdgNum $o 'CurrentHorizontalResolution'; $h = Get-PdgNum $o 'CurrentVerticalResolution'; $hz = Get-PdgNum $o 'CurrentRefreshRate'
        if ($w -gt 0 -and $h -gt 0) { Add-PdgRow $g 'Display' ('{0} x {1}{2}' -f $w, $h, $(if ($hz -gt 1) { " @ $hz Hz" } else { '' })) }
    }
}

function Get-PdgStorageSection($s) {
    $disks = 0
    try {
        # The Storage API knows SSD from HDD, NVMe from SATA, and whether Windows thinks the disk is healthy
        foreach ($o in (Get-PdgCim 'MSFT_PhysicalDisk' 'root\Microsoft\Windows\Storage' 'FriendlyName,MediaType,BusType,HealthStatus,Size')) {
            $name = Get-PdgStr $o 'FriendlyName'; if (-not $name) { $name = 'Disk' }
            $g = Add-PdgGroup $s $name
            $media = switch ([int](Get-PdgNum $o 'MediaType')) { 3 { 'HDD' } 4 { 'SSD' } 5 { 'SCM' } default { $null } }
            $bus = switch ([int](Get-PdgNum $o 'BusType')) { 1 { 'SCSI' } 3 { 'ATA' } 7 { 'USB' } 8 { 'RAID' } 10 { 'SAS' } 11 { 'SATA' } 12 { 'SD card' } 15 { 'Virtual' } 17 { 'NVMe' } default { $null } }
            Add-PdgRow $g 'Type' ((@($media, $bus) | Where-Object { $_ }) -join ' / ')
            $size = Get-PdgNum $o 'Size'; if ($size -gt 0) { Add-PdgRow $g 'Size' (Get-PdgBytes $size) }
            Add-PdgRow $g 'Health' $(switch ([int](Get-PdgNum $o 'HealthStatus')) { 0 { 'Healthy' } 1 { 'Warning' } 2 { 'Unhealthy' } default { $null } })
            $disks++
        }
    } catch {}
    if ($disks -eq 0) {
        foreach ($o in (Get-PdgCim 'Win32_DiskDrive' -Property 'Model,Size,InterfaceType,MediaType')) {
            $name = Get-PdgStr $o 'Model'; if (-not $name) { $name = 'Disk' }
            $g = Add-PdgGroup $s $name
            Add-PdgRow $g 'Interface' (Get-PdgStr $o 'InterfaceType')
            $size = Get-PdgNum $o 'Size'; if ($size -gt 0) { Add-PdgRow $g 'Size' (Get-PdgBytes $size) }   # empty for card readers with no card
            Add-PdgRow $g 'Media' (Get-PdgStr $o 'MediaType')
        }
    }
    $vol = Add-PdgGroup $s 'Volumes'
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        try {
            if (-not $d.IsReady -or ($d.DriveType -ne 'Fixed' -and $d.DriveType -ne 'Removable')) { continue }
            $used = $d.TotalSize - $d.TotalFreeSpace
            $label = $d.Name.TrimEnd('\') + $(if ($d.VolumeLabel) { " $($d.VolumeLabel)" } else { '' })
            Add-PdgRow $vol $label ('{0} free of {1} ({2:0}% used, {3})' -f (Get-PdgBytes $d.TotalFreeSpace), (Get-PdgBytes $d.TotalSize), (100 * $used / $d.TotalSize), $d.DriveFormat)
        } catch {}
    }
}

function Get-PdgNetworkSection($s) {
    Add-PdgRow (Add-PdgGroup $s) 'Host name' ([Net.Dns]::GetHostName())
    $physical = @{}
    try { foreach ($o in (Get-CimInstance -ClassName Win32_NetworkAdapter -Filter 'PhysicalAdapter = True' -Property GUID -ErrorAction Stop)) { if ($o.GUID) { $physical[[string]$o.GUID] = $true } } } catch {}
    $nics = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {
            $_.NetworkInterfaceType -ne 'Loopback' -and $_.NetworkInterfaceType -ne 'Tunnel' -and
            # physical adapters, plus virtual ones actually in use (an IPv4 address): leaves out filter layers and WAN miniports
            ($physical.ContainsKey($_.Id) -or ($_.OperationalStatus -eq 'Up' -and @($_.GetIPProperties().UnicastAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' }).Count))
        } | Sort-Object @{ E = { $_.OperationalStatus -eq 'Up' }; Descending = $true }, @{ E = { $physical.ContainsKey($_.Id) }; Descending = $true })
    foreach ($n in $nics) {
        $g = Add-PdgGroup $s $n.Name
        Add-PdgRow $g 'Adapter' $n.Description
        $up = $n.OperationalStatus -eq 'Up'
        $speed = if ($n.Speed -ge 1000000000) { '{0:0.#} Gbit/s' -f ($n.Speed / 1e9) } elseif ($n.Speed -gt 0) { '{0:0} Mbit/s' -f ($n.Speed / 1e6) } else { '' }
        Add-PdgRow $g 'Status' $(if ($up) { if ($speed) { "Connected, $speed" } else { 'Connected' } } else { 'Disconnected' })
        $mac = $n.GetPhysicalAddress().GetAddressBytes()
        if ($mac.Length -eq 6) { Add-PdgRow $g 'MAC' (($mac | ForEach-Object { $_.ToString('X2') }) -join '-') }
        if (-not $up) { continue }
        $ip = $n.GetIPProperties()
        Add-PdgRow $g 'IPv4' ((@($ip.UnicastAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' } | ForEach-Object { "$($_.Address)/$($_.PrefixLength)" })) -join ', ')
        Add-PdgRow $g 'IPv6' ((@($ip.UnicastAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetworkV6' -and -not $_.Address.IsIPv6LinkLocal } | Select-Object -First 2 | ForEach-Object { [string]$_.Address })) -join ', ')
        Add-PdgRow $g 'Gateway' ((@($ip.GatewayAddresses | ForEach-Object { [string]$_.Address } | Where-Object { $_ -ne '0.0.0.0' })) -join ', ')
        Add-PdgRow $g 'DNS' ((@($ip.DnsAddresses | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | ForEach-Object { [string]$_ })) -join ', ')
    }
}

function Get-PdgBoardSection($s) {
    $g = Add-PdgGroup $s
    foreach ($o in (Get-PdgCim 'Win32_ComputerSystem' -Property 'Manufacturer,Model')) { Add-PdgRow $g 'System maker' (Get-PdgStr $o 'Manufacturer'); Add-PdgRow $g 'System model' (Get-PdgStr $o 'Model') }
    foreach ($o in (Get-PdgCim 'Win32_BaseBoard' -Property 'Manufacturer,Product,Version,SerialNumber')) {
        Add-PdgRow $g 'Board maker' (Get-PdgStr $o 'Manufacturer')
        $v = Get-PdgStr $o 'Version'
        $rev = if ($v -and $v -ne '1.0') { if ($v -match '^(?i)rev') { "($v)" } else { "(rev $v)" } } else { $null }
        Add-PdgRow $g 'Board' ((@((Get-PdgStr $o 'Product'), $rev) | Where-Object { $_ }) -join ' ')
        Add-PdgRow $g 'Board serial' (Get-PdgStr $o 'SerialNumber')
    }
    foreach ($o in (Get-PdgCim 'Win32_BIOS' -Property 'Manufacturer,SMBIOSBIOSVersion,ReleaseDate')) {
        Add-PdgRow $g 'BIOS' ((@((Get-PdgStr $o 'Manufacturer'), (Get-PdgStr $o 'SMBIOSBIOSVersion')) | Where-Object { $_ }) -join ' ')
        if ($o.ReleaseDate) { try { Add-PdgRow $g 'BIOS date' ([datetime]$o.ReleaseDate).ToString('yyyy-MM-dd') } catch {} }
    }
    # $env:firmware_type is set by Windows ("UEFI" or "Legacy"); no compiled helper needed
    Add-PdgRow $g 'Firmware' $(switch ($env:firmware_type) { 'UEFI' { 'UEFI' } 'Legacy' { 'Legacy BIOS' } default { $null } })
    Add-PdgRow $g 'Secure Boot' $(switch (Get-PdgReg 'SYSTEM\CurrentControlSet\Control\SecureBoot\State' 'UEFISecureBootEnabled') { 1 { 'On' } 0 { 'Off' } default { $null } })
}

function Get-PdgSystemInfo {
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($p in @(@('Windows', 'Get-PdgWindowsSection'), @('Processor', 'Get-PdgProcessorSection'), @('Memory', 'Get-PdgMemorySection'),
            @('Graphics', 'Get-PdgGraphicsSection'), @('Storage', 'Get-PdgStorageSection'), @('Network', 'Get-PdgNetworkSection'), @('Motherboard & BIOS', 'Get-PdgBoardSection'))) {
        $s = New-PdgSection $p[0]
        try { & $p[1] $s } catch { $s.Error = $_.Exception.Message.Trim() }
        if (-not @($s.Groups | Where-Object { $_.Rows.Count }).Count -and -not $s.Error) { $s.Error = 'Windows returned nothing for this section.' }
        $out.Add($s)
    }
    return , $out
}

function Get-PdgInfoValue($Info, [string]$Section, [string]$Label) {
    foreach ($s in $Info) { if ($s.Title -eq $Section) { foreach ($g in $s.Groups) { foreach ($r in $g.Rows) { if ($r.Label -eq $Label) { return $r.Value } } } } }
    return $null
}

function Get-PdgInfoText($Info) {
    # Everything as plain text, for C (copy) and the report
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("pxd Diagnostics - system information ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))")
    foreach ($s in $Info) {
        [void]$sb.AppendLine(); [void]$sb.AppendLine($s.Title.ToUpperInvariant())
        foreach ($g in $s.Groups) {
            if (-not $g.Rows.Count) { continue }
            if ($g.Heading) { [void]$sb.AppendLine('  ' + $g.Heading) }
            $indent = if ($g.Heading) { '    ' } else { '  ' }
            $w = ($g.Rows | ForEach-Object { $_.Label.Length } | Measure-Object -Maximum).Maximum
            foreach ($r in $g.Rows) { [void]$sb.AppendLine($indent + $r.Label.PadRight($w) + '  ' + $r.Value) }
        }
        if ($s.Error) { [void]$sb.AppendLine("  (couldn't read: $($s.Error))") }
    }
    return $sb.ToString()
}

# ======================================================================================
#  SFC and DISM: run with live progress, decode the output, give a verdict (same as the app)
# ======================================================================================

$script:PdgTools = [ordered]@{
    'sfc'          = [pscustomobject]@{ Id = 'sfc'; Key = '1'; Exe = 'sfc.exe'; Arguments = '/scannow'; Title = 'SFC /scannow'; Changes = $true
        About = 'Checks every protected Windows file and replaces damaged ones from the component store. Usually 5 to 15 minutes.' }
    'dism-check'   = [pscustomobject]@{ Id = 'dism-check'; Key = '2'; Exe = 'dism.exe'; Arguments = '/Online /Cleanup-Image /CheckHealth'; Title = 'DISM /CheckHealth'; Changes = $false
        About = 'Quick look at whether the component store has been flagged as damaged. Changes nothing; takes seconds.' }
    'dism-scan'    = [pscustomobject]@{ Id = 'dism-scan'; Key = '3'; Exe = 'dism.exe'; Arguments = '/Online /Cleanup-Image /ScanHealth'; Title = 'DISM /ScanHealth'; Changes = $false
        About = 'Full scan of the component store for damage. Repairs nothing; takes several minutes.' }
    'dism-restore' = [pscustomobject]@{ Id = 'dism-restore'; Key = '4'; Exe = 'dism.exe'; Arguments = '/Online /Cleanup-Image /RestoreHealth'; Title = 'DISM /RestoreHealth'; Changes = $true
        About = 'Repairs the component store, downloading good copies from Windows Update. Needs internet; can take 10 to 30 minutes. Run SFC again afterwards.' }
}

function Get-PdgSystemExe([string]$Name) {
    # The real 64-bit tool: a 32-bit PowerShell asking for System32 is sent to SysWOW64 (no sfc.exe there); Sysnative isn't
    $folder = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { 'Sysnative' } else { 'System32' }
    return (Join-Path (Join-Path $env:SystemRoot $folder) $Name)
}

function Start-PdgToolProcess($T) {
    # Output captured (no console window of its own); the caller reads stdout as bytes for live progress
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Get-PdgSystemExe $T.Exe
    $psi.Arguments = $T.Arguments
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.WorkingDirectory = [Environment]::SystemDirectory
    return [System.Diagnostics.Process]::Start($psi)
}

function Get-PdgPercent([string]$Text) {
    $m = [regex]::Match($Text, '(\d{1,3}(?:[.,]\d+)?)\s*%')
    if (-not $m.Success) { return $null }
    $v = 0.0
    if ([double]::TryParse($m.Groups[1].Value.Replace(',', '.'), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v) -and $v -le 100) { return $v }
    return $null
}

function Get-PdgOemEncoding {
    try {
        try { [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance) } catch {}   # PowerShell 7 only
        return [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)
    } catch { return [Text.Encoding]::UTF8 }
}

# Turns a tool's raw output into lines. SFC writes UTF-16 when its output is redirected; DISM writes the console's OEM
# code page. Progress is redrawn in place with a carriage return, so text ended by CR is a status update and only text
# ended by LF (or the last update before it) becomes a line. Events go into $St.Events as @('line'|'status', text).
function New-PdgDecoder { @{ Decoder = $null; First = (New-Object byte[] 2); Sniffed = 0; Buffer = (New-Object System.Text.StringBuilder); Pending = $null; Events = (New-Object System.Collections.Generic.List[object]) } }

function Add-PdgDecoderBytes($St, [byte[]]$Data, [int]$Count) {
    $start = 0
    if (-not $St.Decoder) {
        while ($St.Sniffed -lt 2 -and $start -lt $Count) { $St.First[$St.Sniffed] = $Data[$start]; $St.Sniffed++; $start++ }
        if ($St.Sniffed -lt 2) { return }
        $f = $St.First
        $St.Decoder = if (($f[0] -eq 0xFF -and $f[1] -eq 0xFE) -or ($f[1] -eq 0 -and $f[0] -ne 0)) { (New-Object System.Text.UnicodeEncoding $false, $false).GetDecoder() } else { (Get-PdgOemEncoding).GetDecoder() }
        Add-PdgDecoderChars $St $f 2
    }
    if ($Count -gt $start) {
        $rest = New-Object byte[] ($Count - $start)
        [Array]::Copy($Data, $start, $rest, 0, $rest.Length)
        Add-PdgDecoderChars $St $rest $rest.Length
    }
}

function Add-PdgDecoderChars($St, [byte[]]$Data, [int]$Count) {
    if ($Count -le 0) { return }
    $chars = New-Object char[] ($St.Decoder.GetCharCount($Data, 0, $Count, $false))
    $n = $St.Decoder.GetChars($Data, 0, $Count, $chars, 0, $false)
    for ($i = 0; $i -lt $n; $i++) {
        $c = $chars[$i]
        if ($c -eq [char]0xFEFF -or $c -eq [char]0) { continue }
        if ($c -eq "`r") {
            if ($St.Buffer.Length -gt 0) {
                $St.Pending = $St.Buffer.ToString(); [void]$St.Buffer.Clear()
                if ($St.Pending.Trim()) { $St.Events.Add(@('status', $St.Pending.Trim())) }
            }
        } elseif ($c -eq "`n") {
            if ($St.Buffer.Length -gt 0) { Add-PdgDecoderLine $St $St.Buffer.ToString() } elseif ($null -ne $St.Pending) { Add-PdgDecoderLine $St $St.Pending }
            [void]$St.Buffer.Clear(); $St.Pending = $null
        } else { [void]$St.Buffer.Append($c) }
    }
}

function Add-PdgDecoderLine($St, [string]$Line) { $Line = $Line.Trim(); if ($Line) { $St.Events.Add(@('line', $Line)) } }

function Complete-PdgDecoder($St) {
    if (-not $St.Decoder -and $St.Sniffed -gt 0) { $St.Decoder = (Get-PdgOemEncoding).GetDecoder(); Add-PdgDecoderChars $St $St.First $St.Sniffed }
    if ($St.Buffer.Length -gt 0) { Add-PdgDecoderLine $St $St.Buffer.ToString() } elseif ($null -ne $St.Pending) { Add-PdgDecoderLine $St $St.Pending }
    [void]$St.Buffer.Clear(); $St.Pending = $null
}

function Get-PdgVerdict($T, [int]$ExitCode, [string[]]$Lines) {
    # A one-line verdict from the tool's final messages; falls back to the last line it printed. @(Verdict, Outcome)
    $all = ($Lines -join "`n")
    $has = { param($s) $all.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
    $last = @($Lines | Where-Object { $_.Trim() -and $null -eq (Get-PdgPercent $_) } | Select-Object -Last 1)
    $last = if ($last.Count) { $last[0] } else { '' }
    if ((& $has 'must be an administrator') -or (& $has 'Elevated permissions are required') -or $ExitCode -eq 740) { return @('Needs administrator rights.', 'Bad') }
    if ((& $has 'repair pending') -or (& $has 'pending.xml')) { return @('A repair is pending: restart Windows, then run it again.', 'Warning') }
    if ($T.Id -eq 'sfc') {
        if (& $has 'did not find any integrity violations') { return @('No integrity violations found.', 'Good') }
        if (& $has 'successfully repaired') { return @('Found corrupt files and repaired them.', 'Repaired') }
        if (& $has 'unable to fix') { return @("Found corrupt files it couldn't fix. Try DISM /RestoreHealth, then SFC again.", 'Bad') }
        if (& $has 'could not perform the requested operation') { return @("SFC couldn't run the scan. Try again in Safe Mode.", 'Bad') }
    } else {
        if (& $has 'No component store corruption detected') { return @('No component store corruption detected.', 'Good') }
        if (& $has 'corruption was repaired') { return @('The component store was repaired.', 'Repaired') }
        if (& $has 'component store is repairable') { return @('The component store is damaged but repairable: run DISM /RestoreHealth.', 'Warning') }
        if (& $has 'source files could not be found') { return @("Repair files couldn't be downloaded. Check the internet connection.", 'Bad') }
    }
    if ($ExitCode -ne 0) { return @(("Finished with exit code $ExitCode. $last").Trim(), 'Bad') }
    if ($last) { return @($last, 'Good') }
    return @('Finished.', 'Good')
}

function Get-PdgOutcomeColor([string]$Outcome) { switch ($Outcome) { 'Good' { 'Green' } 'Repaired' { 'Cyan' } 'Warning' { 'Yellow' } default { 'Red' } } }

function Write-PdgStatus([string]$Text) {
    # Redraws the progress line in place
    $w = 100; try { $w = [Math]::Max(40, $Host.UI.RawUI.WindowSize.Width - 1) } catch {}
    if ($Text.Length -gt $w - 2) { $Text = $Text.Substring(0, $w - 2) }
    Write-Host ("`r  " + $Text.PadRight($w - 2)) -NoNewline -ForegroundColor Cyan
}

function Get-PdgBar([double]$Pct, [int]$Width = 30) {
    $n = [int][Math]::Round($Width * [Math]::Max(0, [Math]::Min(100, $Pct)) / 100)
    return '[' + ('#' * $n) + ('.' * ($Width - $n)) + ']'
}

function Invoke-PdgTool($T) {
    # Runs the tool with its output shown as it comes and a live percentage; returns the result (also kept for the report)
    Write-Host ''
    Write-Host "  Running $($T.Title)..." -ForegroundColor White
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lines = New-Object System.Collections.Generic.List[string]
    $pct = $null; $statusShown = $false
    try { $p = Start-PdgToolProcess $T } catch { Write-Host "  Couldn't start $($T.Exe): $($_.Exception.Message)" -ForegroundColor Red; return $null }
    $errTask = $p.StandardError.ReadToEndAsync()
    $st = New-PdgDecoder
    $out = $p.StandardOutput.BaseStream
    $buf = New-Object byte[] 4096
    $render = {
        foreach ($e in $st.Events) {
            if ($e[0] -eq 'line') {
                if ($statusShown) { Write-PdgStatus ''; Write-Host "`r" -NoNewline; $statusShown = $false }
                $lines.Add($e[1])
                $v = Get-PdgPercent $e[1]
                if ($null -ne $v) { $pct = $v } else { Write-Host "  $($e[1])" -ForegroundColor Gray }
            } else { $v = Get-PdgPercent $e[1]; if ($null -ne $v) { $pct = $v } }
            if ($null -ne $pct -and ($e[0] -eq 'status' -or $null -ne (Get-PdgPercent $e[1]))) {
                Write-PdgStatus ('{0} {1,5:0.0}%  {2}' -f (Get-PdgBar $pct), $pct, (Get-PdgElapsed $clock.Elapsed)); $statusShown = $true
            }
        }
        $st.Events.Clear()
    }
    while (($n = $out.Read($buf, 0, $buf.Length)) -gt 0) { Add-PdgDecoderBytes $st $buf $n; . $render }
    Complete-PdgDecoder $st; . $render
    $err = ''; try { $err = [string]$errTask.Result } catch {}
    foreach ($l in ($err -split "`r?`n")) { if ($l.Trim()) { $lines.Add($l.Trim()); Write-Host "  $($l.Trim())" -ForegroundColor Yellow } }
    $p.WaitForExit()
    $code = $p.ExitCode
    if ($statusShown) { Write-Host '' }
    $clock.Stop()
    $v = Get-PdgVerdict $T $code $lines.ToArray()
    $res = [pscustomobject]@{ Tool = $T.Title; Id = $T.Id; ExitCode = $code; Verdict = $v[0]; Outcome = $v[1]; Seconds = [int]$clock.Elapsed.TotalSeconds; Finished = (Get-Date); Lines = $lines.ToArray() }
    $script:PdgRuns.Add($res)
    Write-Host ''
    Write-Host "  $($T.Title): " -NoNewline -ForegroundColor White
    Write-Host $res.Verdict -NoNewline -ForegroundColor (Get-PdgOutcomeColor $res.Outcome)
    Write-Host "  ($(Get-PdgElapsed $clock.Elapsed), exit code $code)" -ForegroundColor DarkGray
    return $res
}

# ======================================================================================
#  CBS log: the lines that mention errors, corruption or repairs, and what SFC found and fixed
# ======================================================================================

$script:PdgKeywords = @('error', 'corrupt', 'restored', 'repaired')

function Get-PdgCbsPath {
    # CBS.log, or (if Windows has just rolled it over) the newest log in the CBS folder; $null if there's none
    $dir = Join-Path $env:SystemRoot 'Logs\CBS'
    $f = Join-Path $dir 'CBS.log'
    try {
        if (Test-Path -LiteralPath $f) { return $f }
        $n = Get-ChildItem -LiteralPath $dir -Filter '*.log' -File -ErrorAction Stop | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($n) { return $n.FullName }
    } catch {}
    return $null
}

function Read-PdgText([string]$Path) {
    # Read in place (no copy, nothing written): Windows keeps CBS.log open for writing while servicing runs, so it's
    # opened with read-write-delete sharing; a sharing violation is retried a few times.
    for ($attempt = 1; ; $attempt++) {
        try {
            $fs = New-Object IO.FileStream $Path, ([IO.FileMode]::Open), ([IO.FileAccess]::Read), ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete), 65536, ([IO.FileOptions]::SequentialScan)
            try { $r = New-Object IO.StreamReader $fs, ([Text.Encoding]::UTF8), $true; return $r.ReadToEnd() } finally { $fs.Dispose() }
        } catch {
            $ex = $_.Exception; while ($ex.InnerException) { $ex = $ex.InnerException }
            if ($ex -is [IO.IOException] -and (($ex.HResult -band 0xFFFF) -in 32, 33) -and $attempt -lt 4) { Start-Sleep -Milliseconds 1500; continue }
            throw $ex
        }
    }
}

function Read-PdgCbs([string]$Path, [switch]$SfcOnly) {
    $text = Read-PdgText $Path
    $pattern = if ($SfcOnly) { '(?im)^(?=[^\r\n]*\[SR\])[^\r\n]*(?:error|corrupt|restored|repaired)[^\r\n]*' } else { '(?im)^[^\r\n]*(?:error|corrupt|restored|repaired)[^\r\n]*' }
    $matches2 = [regex]::Matches($text, $pattern)
    $lines = New-Object System.Collections.Generic.List[string]
    $counts = @(0, 0, 0, 0)
    foreach ($m in $matches2) {
        $l = $m.Value
        $lines.Add($l)
        for ($i = 0; $i -lt 4; $i++) { if ($l.IndexOf($script:PdgKeywords[$i], [StringComparison]::OrdinalIgnoreCase) -ge 0) { $counts[$i]++ } }
    }
    $read = [regex]::Matches($text, "`n").Count + $(if ($text.Length -and -not $text.EndsWith("`n")) { 1 } else { 0 })
    # What the newest SFC run found and fixed (everything after its last "Beginning Verify and Repair transaction")
    $sfc = [pscustomobject]@{ Started = $null; Repaired = @(); CannotRepair = @(); Done = $false }
    $at = $text.LastIndexOf('[SR] Beginning Verify and Repair transaction')
    if ($at -ge 0) {
        $ls = $text.LastIndexOf("`n", $at) + 1
        $stamp = $text.Substring($ls, [Math]::Min(19, $text.Length - $ls))
        $t = [datetime]::MinValue
        if ([datetime]::TryParseExact($stamp, 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$t)) { $sfc.Started = $t }
        $tail = $text.Substring($ls)
        $sfc.Repaired = @([regex]::Matches($tail, "\[SR\] (?:Repairing corrupted file|Repaired file) (?:\[[^\]]*\])?'?([^'\r\n]+?)'?(?: from store| by copying| from backup|,|\r?$)", 'Multiline') | ForEach-Object { $_.Groups[1].Value.Trim() } | Select-Object -Unique)
        $sfc.CannotRepair = @([regex]::Matches($tail, "\[SR\] Cannot repair member file (?:\[[^\]]*\])?'([^']+)'") | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
        $sfc.Done = $tail.Contains('[SR] Verify complete') -or $tail.Contains('[SR] Repair complete')
    }
    return [pscustomobject]@{ Path = $Path; LinesRead = $read; Matches = $lines.ToArray(); Counts = $counts; SfcOnly = [bool]$SfcOnly; Sfc = $sfc; Error = $null }
}

function Get-PdgCbs([switch]$SfcOnly) {
    $p = Get-PdgCbsPath
    if (-not $p) { return [pscustomobject]@{ Path = $null; Error = 'No CBS log found in C:\Windows\Logs\CBS.'; Matches = @(); Counts = @(0, 0, 0, 0); LinesRead = 0; SfcOnly = [bool]$SfcOnly; Sfc = $null } }
    try { return (Read-PdgCbs $p -SfcOnly:$SfcOnly) }
    catch {
        $msg = $_.Exception.Message
        if ($_.Exception -is [UnauthorizedAccessException]) { $msg = 'Access denied. Run it from an elevated prompt (A) to read the CBS log.' }
        return [pscustomobject]@{ Path = $p; Error = $msg; Matches = @(); Counts = @(0, 0, 0, 0); LinesRead = 0; SfcOnly = [bool]$SfcOnly; Sfc = $null }
    }
}

function Get-PdgCbsSummaryLines($Cbs) {
    # @( @(text, colour) ... )
    $out = New-Object System.Collections.Generic.List[object]
    if ($Cbs.Error) { $out.Add(@("CBS log: $($Cbs.Error)", 'Yellow')); return , $out }
    $out.Add(@(('CBS log: {0}  ({1:N0} lines, {2:N0} matching{3})' -f $Cbs.Path, $Cbs.LinesRead, $Cbs.Matches.Count, $(if ($Cbs.SfcOnly) { ', SFC [SR] lines only' } else { '' })), 'Gray'))
    $out.Add(@((($script:PdgKeywords | ForEach-Object -Begin { $i = 0 } -Process { '{0}: {1:N0}' -f $_, $Cbs.Counts[$i]; $i++ }) -join '   '), 'White'))
    $s = $Cbs.Sfc
    if ($s -and ($s.Started -or $s.Done)) {
        $when = if ($s.Started) { $s.Started.ToString('yyyy-MM-dd HH:mm') } else { 'unknown time' }
        $out.Add(@(('Newest SFC run ({0}): {1} file(s) repaired, {2} it could not fix{3}' -f $when, @($s.Repaired).Count, @($s.CannotRepair).Count, $(if (-not $s.Done) { ' (not finished in this log)' } else { '' })), $(if (@($s.CannotRepair).Count) { 'Red' } elseif (@($s.Repaired).Count) { 'Cyan' } else { 'Green' })))
        foreach ($f in @($s.Repaired | Select-Object -First 10)) { $out.Add(@("  repaired: $f", 'Cyan')) }
        foreach ($f in @($s.CannotRepair | Select-Object -First 10)) { $out.Add(@("  couldn't fix: $f", 'Red')) }
    } else { $out.Add(@('No SFC run in this log.', 'DarkGray')) }
    return , $out
}

function Get-PdgCbsText($Cbs) {
    # The results as a text file: a short header, then every matching line (same as the app's Save results)
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("pxd Diagnostics - CBS log entries ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))")
    [void]$sb.AppendLine("Log: $($Cbs.Path)")
    [void]$sb.AppendLine(('Lines read: {0:N0}, matching: {1:N0}{2}' -f $Cbs.LinesRead, $Cbs.Matches.Count, $(if ($Cbs.SfcOnly) { ' (SFC [SR] lines only)' } else { '' })))
    foreach ($x in (Get-PdgCbsSummaryLines $Cbs | Select-Object -Skip 1)) { [void]$sb.AppendLine($x[0]) }
    [void]$sb.AppendLine()
    foreach ($l in $Cbs.Matches) { [void]$sb.AppendLine($l) }
    return $sb.ToString()
}

# ======================================================================================
#  Report (only when asked: R, -Export)
# ======================================================================================

function Get-PdgReportText($Info, $Cbs) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append((Get-PdgInfoText $Info))
    [void]$sb.AppendLine(); [void]$sb.AppendLine('REPAIR TOOLS (this session)')
    if (-not $script:PdgRuns.Count) { [void]$sb.AppendLine('  none run') }
    foreach ($r in $script:PdgRuns) { [void]$sb.AppendLine(('  {0:yyyy-MM-dd HH:mm}  {1}: {2} (exit code {3}, {4}s)' -f $r.Finished, $r.Tool, $r.Verdict, $r.ExitCode, $r.Seconds)) }
    [void]$sb.AppendLine(); [void]$sb.AppendLine('CBS LOG')
    foreach ($x in (Get-PdgCbsSummaryLines $Cbs)) { [void]$sb.AppendLine('  ' + $x[0]) }
    if ($Cbs.Matches.Count) {
        [void]$sb.AppendLine('  Newest matching lines:')
        foreach ($l in @($Cbs.Matches | Select-Object -Last 50)) { [void]$sb.AppendLine('    ' + $l) }
    }
    [void]$sb.AppendLine(); [void]$sb.AppendLine("pxd Diagnostics CLI $($script:PdgVersion) - $($script:PdgUrl)")
    return $sb.ToString()
}

function Export-PdgReport([string]$Path, $Info, $Cbs) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetExtension($full).ToLowerInvariant() -eq '.json') {
        $o = [ordered]@{
            tool = "pxd Diagnostics CLI $($script:PdgVersion)"; taken = (Get-Date).ToString('o')
            system = @($Info | ForEach-Object { [ordered]@{ title = $_.Title; error = $_.Error; groups = @($_.Groups | Where-Object { $_.Rows.Count } | ForEach-Object { [ordered]@{ heading = $_.Heading; rows = @($_.Rows | ForEach-Object { [ordered]@{ label = $_.Label; value = $_.Value } }) } }) } })
            repairs = @($script:PdgRuns | ForEach-Object { [ordered]@{ tool = $_.Tool; verdict = $_.Verdict; outcome = $_.Outcome; exitCode = $_.ExitCode; seconds = $_.Seconds; finished = $_.Finished.ToString('o') } })
            cbs = [ordered]@{ path = $Cbs.Path; error = $Cbs.Error; linesRead = $Cbs.LinesRead; sfcOnly = $Cbs.SfcOnly; counts = [ordered]@{ error = $Cbs.Counts[0]; corrupt = $Cbs.Counts[1]; restored = $Cbs.Counts[2]; repaired = $Cbs.Counts[3] }
                sfc = $(if ($Cbs.Sfc) { [ordered]@{ started = $(if ($Cbs.Sfc.Started) { $Cbs.Sfc.Started.ToString('o') }); finished = $Cbs.Sfc.Done; repaired = @($Cbs.Sfc.Repaired); cannotRepair = @($Cbs.Sfc.CannotRepair) } }); matches = @($Cbs.Matches) }
        }
        [IO.File]::WriteAllText($full, ($o | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))
    } else { [IO.File]::WriteAllText($full, (Get-PdgReportText $Info $Cbs), (New-Object Text.UTF8Encoding $false)) }
    return $full
}

# ======================================================================================
#  Console
# ======================================================================================

$script:PdgEof = $false
function Read-PdgKey([string]$Prompt) {
    $k = Read-Host $Prompt
    if ($null -eq $k) { $script:PdgEof = $true; return 'q' }   # piped input ran out
    $k.Trim()
}

function Test-PdgYes([string]$Prompt) { (Read-PdgKey $Prompt) -match '^(?i)y(es)?$' }

function Write-PdgHeader([string]$Title) {
    Write-Host ''
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * [Math]::Min(76, $Title.Length + 2))) -ForegroundColor DarkGray
}

function Show-PdgGlance($Info) {
    # The few lines a technician looks at first
    $rows = @(
        @('Computer', (Get-PdgInfoValue $Info 'Windows' 'Computer name')),
        @('Windows', ((@((Get-PdgInfoValue $Info 'Windows' 'Edition'), (Get-PdgInfoValue $Info 'Windows' 'Version')) | Where-Object { $_ }) -join ' ')),
        @('Uptime', (Get-PdgInfoValue $Info 'Windows' 'Uptime')),
        @('Processor', (Get-PdgInfoValue $Info 'Processor' 'Name')),
        @('Memory', ((@((Get-PdgInfoValue $Info 'Memory' 'Installed'), $(if (Get-PdgInfoValue $Info 'Memory' 'In use') { '(in use: ' + (Get-PdgInfoValue $Info 'Memory' 'In use') + ')' })) | Where-Object { $_ }) -join ' ')))
    foreach ($s in $Info) {
        if ($s.Title -eq 'Graphics') { foreach ($g in $s.Groups) { if ($g.Heading) { $rows += , @('Graphics', $g.Heading) } } }
        if ($s.Title -eq 'Storage') { foreach ($g in $s.Groups) { if ($g.Heading -eq 'Volumes') { foreach ($r in $g.Rows) { $rows += , @("Drive $($r.Label)", $r.Value) } } elseif ($g.Heading) { $h = @($g.Rows | Where-Object Label -eq 'Health'); if ($h.Count -and $h[0].Value -ne 'Healthy') { $rows += , @('Disk health', "$($g.Heading): $($h[0].Value)") } } } }
    }
    $rows += , @('Running as', (Get-PdgInfoValue $Info 'Windows' 'Running as'))
    foreach ($r in $rows) {
        if (-not $r[1]) { continue }
        Write-Host ('  {0,-14}' -f $r[0]) -NoNewline -ForegroundColor DarkGray
        Write-Host $r[1] -ForegroundColor $(if ($r[1] -match 'Warning|Unhealthy') { 'Yellow' } else { 'Gray' })
    }
}

function Show-PdgInfo($Info) {
    foreach ($s in $Info) {
        Write-PdgHeader $s.Title
        foreach ($g in $s.Groups) {
            if (-not $g.Rows.Count) { continue }
            if ($g.Heading) { Write-Host "  $($g.Heading)" -ForegroundColor White }
            $indent = if ($g.Heading) { '    ' } else { '  ' }
            foreach ($r in $g.Rows) {
                Write-Host ($indent + ('{0,-16}' -f $r.Label)) -NoNewline -ForegroundColor DarkGray
                Write-Host $r.Value -ForegroundColor $(if ($r.Label -eq 'Health' -and $r.Value -ne 'Healthy') { 'Yellow' } else { 'Gray' })
            }
        }
        if ($s.Error) { Write-Host "  (couldn't read: $($s.Error))" -ForegroundColor DarkGray }
    }
}

function Get-PdgLineColor([string]$Line) {
    if ($Line -match '(?i)corrupt') { return 'Red' }
    if ($Line -match '(?i)error') { return 'Magenta' }
    return 'Green'   # restored / repaired
}

function Show-PdgCbsSummary($Cbs) {
    foreach ($x in (Get-PdgCbsSummaryLines $Cbs)) { Write-Host "  $($x[0])" -ForegroundColor $x[1] }
}

function Save-PdgAsked([string]$Default, [scriptblock]$Write) {
    # Writes a file only when asked, to the path the user gives (Enter takes the default shown)
    $p = Read-PdgKey "  Save as (Enter = $Default)"
    if ($script:PdgEof -or $p -eq 'q') { return }
    if (-not $p) { $p = $Default }
    try { $full = & $Write $p.Trim('"'); Write-Host "  Saved $full" -ForegroundColor Green } catch { Write-Host "  Couldn't save: $($_.Exception.Message)" -ForegroundColor Yellow }
}

function Show-PdgCbs([switch]$SfcOnly) {
    $only = [bool]$SfcOnly
    while (-not $script:PdgEof) {
        Write-Host '  Reading the CBS log...' -ForegroundColor DarkCyan
        $cbs = Get-PdgCbs -SfcOnly:$only
        Write-PdgHeader 'CBS log - errors, corruption and repairs'
        Show-PdgCbsSummary $cbs
        $shown = 0; $more = $true; $reload = $false
        while (-not $script:PdgEof -and -not $reload) {
            $all = $cbs.Matches
            $take = if ($more) { [Math]::Min(25, $all.Count - $shown) } else { 0 }
            $more = $false
            if ($take -gt 0) {
                Write-Host ''
                Write-Host $(if ($shown -eq 0) { '  Newest matching lines:' } else { '  Older:' }) -ForegroundColor DarkGray
                $w = 160; try { $w = [Math]::Max(60, $Host.UI.RawUI.WindowSize.Width - 3) } catch {}
                for ($i = $all.Count - 1 - $shown; $i -ge $all.Count - $shown - $take; $i--) {
                    $l = $all[$i]; if ($l.Length -gt $w - 2) { $l = $l.Substring(0, $w - 3) + '~' }
                    Write-Host "  $l" -ForegroundColor (Get-PdgLineColor $all[$i])
                }
                $shown += $take
            }
            Write-Host ''
            Write-Host ('  {0}F {1}   C copy results   S save results   Enter back' -f $(if ($shown -lt $all.Count) { 'M more   ' } else { '' }), $(if ($only) { 'all lines' } else { 'SFC lines only' })) -ForegroundColor DarkGray
            $k = Read-PdgKey '  Choose'
            switch -Regex ($k.ToLowerInvariant()) {
                '^m$' { $more = $true; break }
                '^f$' { $only = -not $only; $reload = $true; break }
                '^c$' {
                    try { Set-Clipboard -Value (Get-PdgCbsText $cbs); Write-Host '  Copied.' -ForegroundColor Green } catch { Write-Host "  Couldn't use the clipboard here; S saves it as a file instead." -ForegroundColor Yellow }
                    break
                }
                '^s$' {
                    Save-PdgAsked (Join-Path ([Environment]::GetFolderPath('Desktop')) ("pxd-CBS-$env:COMPUTERNAME-$(Get-Date -Format 'yyyyMMdd-HHmm').txt")) {
                        param($p) $full = [IO.Path]::GetFullPath($p); [IO.File]::WriteAllText($full, (Get-PdgCbsText $cbs), (New-Object Text.UTF8Encoding $false)); $full
                    }
                    break
                }
                default { return }
            }
        }
    }
}

function Restart-PdgElevated([string]$ToolId = '') {
    # Opens an elevated window running this script (and, when a tool was picked, starting it there)
    $self = $PSCommandPath
    $exe = (Get-Process -Id $PID).Path
    $toolArg = if ($ToolId) { " -Tool $ToolId" } else { '' }
    $set = if ($script:PdgInMemory) { "`$env:PXD_INMEMORY='1'; " } else { '' }   # the elevated window doesn't inherit this environment
    $arg = if ($self) { "-NoExit -ExecutionPolicy Bypass -Command `"$set& '$self'$toolArg`"" }
    else { "-NoExit -ExecutionPolicy Bypass -Command `"$set& ([scriptblock]::Create((irm $($script:PdgUrl))))$toolArg`"" }
    try { Start-Process -FilePath $exe -ArgumentList $arg -Verb RunAs -ErrorAction Stop; $true } catch { Write-Host '  Not elevated (the prompt was cancelled), so nothing was started.' -ForegroundColor Yellow; $false }
}

function Invoke-PdgToolChoice($T) {
    # A repair tool runs only when picked and confirmed (and needs administrator rights)
    Write-PdgHeader $T.Title
    Write-Host "  $($T.About)" -ForegroundColor Gray
    if ($T.Changes) { Write-Host '  This can change Windows system files.' -ForegroundColor Yellow }
    if (-not (Test-PdgAdmin)) {
        Write-Host "  $($T.Title) needs administrator rights." -ForegroundColor Yellow
        if (Test-PdgYes "  Restart as administrator and run it there? (y/N)") {
            if (Restart-PdgElevated $T.Id) { Write-Host '  Opened an elevated window; it starts the tool there. This one can be closed.' -ForegroundColor Green }
        } else { Write-Host '  Nothing was started.' -ForegroundColor DarkGray }
        return
    }
    if (-not (Test-PdgYes "  Run $($T.Title) now? (y/N)")) { Write-Host '  Nothing was started.' -ForegroundColor DarkGray; return }
    Invoke-PdgToolAndLog $T
}

function Invoke-PdgToolAndLog($T) {
    $r = Invoke-PdgTool $T
    # After SFC or RestoreHealth, the CBS log has the details: read it for them (SFC: its own lines)
    if ($r -and $T.Id -in 'sfc', 'dism-restore') {
        Write-Host ''
        Show-PdgCbsSummary (Get-PdgCbs -SfcOnly:($T.Id -eq 'sfc'))
        Write-Host '  L shows the matching CBS log lines.' -ForegroundColor DarkGray
    }
}

function Show-PdgWindowsTools {
    Write-PdgHeader "Windows' own tools"
    Write-Host '  1 Reliability Monitor   2 Event Viewer   3 Device Manager   4 System Information   Enter back' -ForegroundColor DarkGray
    $k = Read-PdgKey '  Choose'
    try {
        switch ($k) {
            '1' { Start-Process -FilePath 'perfmon.exe' -ArgumentList '/rel' }
            '2' { Start-Process -FilePath 'eventvwr.msc' }
            '3' { Start-Process -FilePath 'devmgmt.msc' }
            '4' { Start-Process -FilePath 'msinfo32.exe' }
        }
    } catch { Write-Host "  Couldn't open it: $($_.Exception.Message)" -ForegroundColor Yellow }
}

# ---- PD header ---------------------------------------------------------------------------------------------------
# The pxd logo beside the tool's name, shown when a pxdtek CLI tool starts. The master copy is cli/PdHeader.ps1 in the
# pxdtek repo; each tool carries a copy (they run through irm | iex, so they can't load it). tools/sync-header.ps1
# copies it into them.
# Ten drawings of the logo, from tools/pdlogos.mjs: solid blocks, braille dots, shades and plain ASCII, tilted like the
# logo or upright, the pxd in white and the ring in cyan. They're stored as gzip + base64 so this stays ASCII and
# irm | iex never trips on encoding. Each time, one is picked at random from those that fit the window (braille only
# in Windows Terminal or VS Code, whose fonts have the dots); a window too narrow for any gets a one-line header.
# Nothing is kept between runs: under irm | iex the script scope is the caller's own session.

# A random logo no wider than $Room (@{ Style; Width; Text = lines; Color = lines }), or $null if none fits.
function Get-PdLogo([int]$Room = [int]::MaxValue) {
    $logos = @(
        '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'
    ) -join ''
    $all = @()
    try {
        $gz = New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(, [Convert]::FromBase64String($logos))), ([IO.Compression.CompressionMode]::Decompress))
        $body = (New-Object IO.StreamReader($gz, [Text.Encoding]::UTF8)).ReadToEnd()
        foreach ($chunk in ($body -split "`n~`n")) {
            $l = $chunk -split "`n"
            $head = $l[0] -split ' '
            $n = [int]$head[2]
            $all += @{ Style = $head[0]; Width = [int]$head[1]; Text = $l[1..$n]; Color = $l[($n + 1)..(2 * $n)] }
        }
    } catch { return $null }
    $dots = [bool]($env:WT_SESSION -or $env:TERM_PROGRAM -eq 'vscode')
    $fit = @($all | Where-Object { ($dots -or $_.Style -ne 'd') -and $_.Width -le $Room })
    if (-not $fit.Count) { return $null }
    $fit[(Get-Random -Maximum $fit.Count)]
}

# Line $Row of a logo in its colours (W the pxd, C the ring, D its faint edge), no newline; -Pad fills it to the
# logo's width. A row past the logo's end writes only the padding.
function Write-PdLogoLine($Logo, [int]$Row, [switch]$Pad) {
    $line = ''; $mask = ''
    if ($Row -ge 0 -and $Row -lt $Logo.Text.Count) { $line = [string]$Logo.Text[$Row]; $mask = [string]$Logo.Color[$Row] }
    $colors = @{ 'W' = 'White'; 'C' = 'Cyan'; 'D' = 'DarkCyan' }
    # one colour run at a time; spaces join whichever run they're in
    $run = ''; $col = 'Cyan'
    for ($k = 0; $k -lt $line.Length; $k++) {
        $c = $col
        if ($line[$k] -ne ' ' -and $k -lt $mask.Length -and $colors.ContainsKey([string]$mask[$k])) { $c = $colors[[string]$mask[$k]] }
        if ($c -ne $col -and $run) { Write-Host $run -NoNewline -ForegroundColor $col; $run = '' }
        $col = $c; $run += $line[$k]
    }
    if ($run) { Write-Host $run -NoNewline -ForegroundColor $col }
    if ($Pad -and $line.Length -lt $Logo.Width) { Write-Host (' ' * ($Logo.Width - $line.Length)) -NoNewline }
}

function Show-PdHeader {
    param([string]$Name, [string]$Version = '', [string[]]$About = @(), [string]$Command = '')
    $text = New-Object System.Collections.Generic.List[object]
    $text.Add(@($Name, 'White'))
    $text.Add(@(((@($Version, 'pxdtek.com') | Where-Object { $_ }) -join '  -  '), 'DarkGray'))
    if ($About) { $text.Add(@('', 'Gray')); foreach ($a in $About) { $text.Add(@($a, 'Gray')) } }
    if ($Command) { $text.Add(@('', 'Gray')); $text.Add(@($Command, 'DarkCyan')) }
    $textWidth = ($text | ForEach-Object { $_[0].Length } | Measure-Object -Maximum).Maximum

    $w = 0
    try { $w = $Host.UI.RawUI.WindowSize.Width } catch {}
    $logo = Get-PdLogo $(if ($w -gt 0) { $w - $textWidth - 7 } else { [int]::MaxValue })

    Write-Host ''
    if (-not $logo) {
        Write-Host '  (pd) ' -NoNewline -ForegroundColor Cyan
        Write-Host $Name -NoNewline -ForegroundColor White
        Write-Host $(if ($Version) { "  $Version" } else { '' }) -ForegroundColor DarkGray
        Write-Host ''
        return
    }
    $rows = $logo.Text.Count
    $top = [Math]::Max(0, [Math]::Floor(($rows - $text.Count) / 2))
    for ($i = 0; $i -lt [Math]::Max($rows, $top + $text.Count); $i++) {
        $j = $i - $top
        $say = $j -ge 0 -and $j -lt $text.Count -and $text[$j][0]
        Write-Host '  ' -NoNewline
        Write-PdLogoLine $logo $i -Pad:$say
        if ($say) { Write-Host ('    ' + $text[$j][0]) -NoNewline -ForegroundColor $text[$j][1] }
        Write-Host ''
    }
    Write-Host ''
}
# ---- end of PD header --------------------------------------------------------------------------------------------

function Start-PdgConsole([string]$StartTool) {
    Show-PdHeader -Name 'pxd Diagnostics' -Version "CLI $($script:PdgVersion)" -Command 'irm pxdtek.com/diag|iex'
    if ($script:PdgInMemory) { Write-Host '  In-memory: nothing is saved on this PC.' -ForegroundColor DarkGray }
    Write-Host '  Reading system information...' -ForegroundColor DarkCyan -NoNewline
    $info = Get-PdgSystemInfo
    Write-Host ("`r" + (' ' * 40) + "`r") -NoNewline
    if ($StartTool) {
        # Started elevated for a tool the user picked and confirmed in the first window
        $t = $script:PdgTools[$StartTool]
        if (Test-PdgAdmin) { Invoke-PdgToolAndLog $t } else { Write-Host "  $($t.Title) needs administrator rights; nothing was started." -ForegroundColor Yellow }
    }
    while (-not $script:PdgEof) {
        $admin = Test-PdgAdmin
        Write-PdgHeader "$($env:COMPUTERNAME) at a glance"
        Show-PdgGlance $info
        Write-Host ''
        Write-Host '  S system info   L CBS log   C copy all info   R save report   W Windows tools   F refresh' -ForegroundColor DarkGray
        Write-Host ('  1 SFC /scannow   2 DISM CheckHealth   3 DISM ScanHealth   4 DISM RestoreHealth{0}' -f $(if (-not $admin) { '   (need admin)' } else { '' })) -ForegroundColor DarkGray
        Write-Host "  $(if (-not $admin) { 'A restart as administrator   ' })Q quit" -ForegroundColor DarkGray
        $k = Read-PdgKey '  Choose'
        switch -Regex ($k.ToLowerInvariant()) {
            '^q$' { return }
            '^s$' { Show-PdgInfo $info; [void](Read-PdgKey '  Enter to go back'); break }
            '^l$' { Show-PdgCbs; break }
            '^[1-4]$' { $t = @($script:PdgTools.Values | Where-Object Key -eq $k)[0]; Invoke-PdgToolChoice $t; break }
            '^f$' { Write-Host '  Reading system information...' -ForegroundColor DarkCyan; $info = Get-PdgSystemInfo; break }
            '^w$' { Show-PdgWindowsTools; break }
            '^c$' {
                try { Set-Clipboard -Value (Get-PdgInfoText $info); Write-Host '  Copied: paste it into the ticket.' -ForegroundColor Green }
                catch { Write-Host "  Couldn't use the clipboard here; R saves it as a file instead." -ForegroundColor Yellow }
                break
            }
            '^r$' {
                $cbs = Get-PdgCbs
                Save-PdgAsked (Join-Path ([Environment]::GetFolderPath('Desktop')) ("pxd-Diagnostics-$env:COMPUTERNAME-$(Get-Date -Format 'yyyyMMdd-HHmm').txt")) { param($p) Export-PdgReport $p $info $cbs }
                break
            }
            '^a$' { if (-not $admin -and (Restart-PdgElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
        }
    }
}

# ======================================================================================
#  Entry
# ======================================================================================

if ($env:OS -ne 'Windows_NT') { Write-Host 'pxd Diagnostics checks Windows, so it runs on Windows only.' -ForegroundColor Yellow; return }

if ($Report -or $Export -or $Copy) {
    if ($script:PdgInMemory -and -not $Export) { Write-Host '  In-memory: nothing is saved on this PC.' -ForegroundColor DarkGray }
    $info = Get-PdgSystemInfo
    if ($Copy) { Set-Clipboard -Value (Get-PdgInfoText $info); Write-Host '  System information copied to the clipboard.' -ForegroundColor Green }
    if ($Report -or $Export) {
        $cbs = Get-PdgCbs -SfcOnly:$SfcOnly
        if ($Report) { Show-PdgInfo $info; Write-Host ''; Write-PdgHeader 'CBS log'; Show-PdgCbsSummary $cbs; Write-Host '' }
        if ($Export) { $full = Export-PdgReport $Export $info $cbs; Write-Host "  Saved $full" -ForegroundColor Green }
    }
    return
}

Start-PdgConsole $Tool
