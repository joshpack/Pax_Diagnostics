<#
    Checks the console edition (cli/pxdDiagnostics.ps1) without changing anything and without showing anything:
    - it parses, is ASCII-only, and its PD header block matches the master copy (when the pxdtek repo is next to it)
    - SFC and DISM are never run: a fake process plays back recorded output (UTF-16 for SFC, OEM text for DISM)
    - keys are fed to the console; Start-Process (elevation, Windows tools) and the clipboard are fakes
    - in-memory mode (PXD_INMEMORY=1): APPDATA, LOCALAPPDATA, ProgramData and TEMP/TMP point at a temp folder and every
      file under it is compared before and after; only reports the "user" asks for are written, where asked
    Run from Windows PowerShell or pwsh:  powershell -ExecutionPolicy Bypass -File cli\Test-Diagnostics.ps1
#>
param([string]$MasterHeader = 'C:\dev\pxdtek\cli\PdHeader.ps1')

$main = Join-Path $PSScriptRoot 'pxdDiagnostics.ps1'
$fails = 0
function Check([string]$Name, [bool]$Ok) { if ($Ok) { Microsoft.PowerShell.Utility\Write-Host "PASS  $Name" -ForegroundColor Green } else { Microsoft.PowerShell.Utility\Write-Host "FAIL  $Name" -ForegroundColor Red; $script:fails++ } }

# ---- the file itself
$errs = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($main, [ref]$null, [ref]$errs)
Check "parses in PowerShell $($PSVersionTable.PSVersion.Major)" ($errs.Count -eq 0)
$bytes = [IO.File]::ReadAllBytes($main)
Check 'ASCII only (safe for irm | iex)' (@($bytes | Where-Object { $_ -gt 127 }).Count -eq 0)
$src = [IO.File]::ReadAllText($main)
$block = [regex]::Match($src, '(?s)# ---- PD header.*?# ---- end of PD header[^\r\n]*').Value
if (Test-Path -LiteralPath $MasterHeader) {
    $m = [regex]::Match([IO.File]::ReadAllText($MasterHeader), '(?s)# ---- PD header.*?# ---- end of PD header[^\r\n]*').Value
    Check 'PD header block is the master copy, unchanged' ($block.Length -gt 0 -and ($block -replace "`r", '') -eq ($m -replace "`r", ''))
} else { Microsoft.PowerShell.Utility\Write-Host "skip  PD header compare ($MasterHeader not found)" -ForegroundColor DarkGray }

$t = Join-Path ([IO.Path]::GetTempPath()) ('pdg-test-' + [guid]::NewGuid().ToString('N'))
$saved = @{}
foreach ($n in 'APPDATA', 'LOCALAPPDATA', 'ProgramData', 'TEMP', 'TMP', 'PXD_INMEMORY') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$watch = @{ APPDATA = (Join-Path $t 'AppData'); LOCALAPPDATA = (Join-Path $t 'LocalAppData'); ProgramData = (Join-Path $t 'ProgramData'); TEMP = (Join-Path $t 'Temp') }
foreach ($d in $watch.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$picked = Join-Path $t 'Picked'; New-Item -ItemType Directory -Force -Path $picked | Out-Null    # where the "user" saves reports
$logs = Join-Path $t 'Logs'; New-Item -ItemType Directory -Force -Path $logs | Out-Null           # a made-up CBS.log
# Windows' own shell caches are left out (they only land here because the folders are redirected)
function Get-Files { @(Get-ChildItem -LiteralPath @($watch.Values) -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName } | Where-Object { $_ -notmatch '\\Microsoft(\\Windows(\\Caches(\\.*)?)?)?$' }) }
function Same($a, $b) { $x = @($a | Where-Object { $_ }); $y = @($b | Where-Object { $_ }); $d = @(); if ($x.Count -or $y.Count) { $d = @(Compare-Object $(if ($x.Count) { $x } else { @('') }) $(if ($y.Count) { $y } else { @('') })) }; if ($d.Count) { Microsoft.PowerShell.Utility\Write-Host ('      new or gone: ' + (($d | ForEach-Object { $_.SideIndicator + ' ' + $_.InputObject }) -join '; ')) -ForegroundColor DarkGray }; return ($d.Count -eq 0) }

try {
    $env:APPDATA = $watch.APPDATA; $env:LOCALAPPDATA = $watch.LOCALAPPDATA; $env:ProgramData = $watch.ProgramData
    $env:TEMP = $watch.TEMP; $env:TMP = $watch.TEMP
    $env:PXD_INMEMORY = '1'

    # Output captured, keys from a queue, nothing started, clipboard faked
    $script:Out = New-Object System.Text.StringBuilder
    function Write-Host { param([Parameter(Position = 0)]$Object = '', [switch]$NoNewline, $ForegroundColor, $BackgroundColor) [void]$script:Out.Append([string]$Object); if (-not $NoNewline) { [void]$script:Out.AppendLine() } }
    function Clear-Host { }
    $script:Keys = New-Object System.Collections.Queue
    $script:Prompts = New-Object System.Collections.Generic.List[string]
    function Read-Host { param([Parameter(Position = 0)]$Prompt) $script:Prompts.Add([string]$Prompt); if ($script:Keys.Count) { return [string]$script:Keys.Dequeue() } return $null }
    $script:Started = @()
    function Start-Process { param($FilePath, $ArgumentList, $Verb, $ErrorAction) $script:Started += , @([string]$FilePath, [string]$ArgumentList, [string]$Verb) }
    function Set-Clipboard { param($Value) $script:Clip = [string]$Value }
    function Start-Sleep { }
    function Reset-Run([string[]]$Keys) { [void]$script:Out.Clear(); $script:Prompts.Clear(); $script:Keys.Clear(); foreach ($k in $Keys) { $script:Keys.Enqueue($k) }; $script:PdgEof = $false }

    $before = Get-Files

    # ---- 1) the whole script, non-interactive: -Report prints only (reads this PC and its real CBS log)
    $sb = [scriptblock]::Create($src)
    & $sb -Report
    $o = $script:Out.ToString()
    Check '-Report: in-memory line shown' ($o -match 'In-memory: nothing is saved on this PC\.')
    Check '-Report: system sections and CBS summary printed' ($o -match 'Processor' -and $o -match 'Motherboard & BIOS' -and $o -match 'CBS log')
    Check '-Report: nothing written' (Same $before (Get-Files))
    $json = Join-Path $picked 'asked-for.json'
    & $sb -Export $json
    Check '-Export: only the asked-for file is written' ((Test-Path -LiteralPath $json) -and (Same $before (Get-Files)))
    Check '-Export: JSON has system, repairs and cbs' ($(try { $j = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json; $j.system.Count -ge 5 -and $null -ne $j.cbs } catch { $false }))

    # ---- 2) the functions, with fakes for the tools, administrator check and CBS path
    . ([scriptblock]::Create($src.Substring(0, $src.LastIndexOf('#  Entry'))))
    $cbsFile = Join-Path $logs 'CBS.log'
    [IO.File]::WriteAllText($cbsFile, (@(
                '2026-11-09 09:00:00, Info                  CBS    Starting TrustedInstaller initialization.'
                '2026-11-09 09:00:01, Error                 CBS    Failed to load the COMPONENTS hive. [HRESULT = 0x80070002 - ERROR_FILE_NOT_FOUND]'
                '2026-11-09 09:01:00, Info                  CSI    00000001 [SR] Beginning Verify and Repair transaction'
                "2026-11-09 09:05:00, Info                  CSI    00000002 [SR] Cannot repair member file [l:12]'example.dll' of Example-Component, version 10.0.26100.1, arch amd64"
                '2026-11-09 09:05:01, Info                  CSI    00000003 [SR] Repairing corrupted file \??\C:\WINDOWS\System32\other.dll from store'
                '2026-11-09 09:05:02, Info                  CSI    00000004 [SR] Repaired file \SystemRoot\WinSxS\amd64_other\other.dll by copying from backup'
                '2026-11-09 09:05:03, Info                  CSI    00000005 Hashes for file member restored from backup'
                '2026-11-09 09:06:00, Info                  CSI    00000006 [SR] Verify complete'
            ) -join "`r`n") + "`r`n")
    function Get-PdgCbsPath { $script:FakeCbs }
    $script:FakeCbs = $cbsFile
    $script:Admin = $true
    function Test-PdgAdmin { $script:Admin }
    # Recorded tool output played back by a fake process (SFC: UTF-16 with CR progress; DISM: single-byte with a bar)
    $sfcText = "`r`nBeginning system scan.  This process will take some time.`r`n`r`nBeginning verification phase of system scan.`r`nVerification 5% complete.`rVerification 42% complete.`rVerification 100% complete.`r`n`r`nWindows Resource Protection did not find any integrity violations.`r`n"
    $dismText = "`r`nDeployment Image Servicing and Management tool`r`nVersion: 10.0.26100.1`r`n`r`nImage Version: 10.0.26100.4061`r`n`r`n[===                        10.0%                          ] `r[==========================100.0%==========================] `r`nNo component store corruption detected.`r`nThe operation completed successfully.`r`n"
    $script:ToolRuns = New-Object System.Collections.Generic.List[string]
    function Start-PdgToolProcess($T) {
        $script:ToolRuns.Add($T.Id)
        $data = if ($T.Id -eq 'sfc') { (New-Object System.Text.UnicodeEncoding $false, $false).GetBytes($sfcText) } else { [Text.Encoding]::ASCII.GetBytes($dismText) }
        $p = [pscustomobject]@{ StandardOutput = (New-Object IO.StreamReader (New-Object IO.MemoryStream (, $data))); StandardError = (New-Object IO.StreamReader (New-Object IO.MemoryStream)); ExitCode = 0 }
        $p | Add-Member ScriptMethod WaitForExit { }
        return $p
    }
    function Get-PdgSystemInfo {
        $s = New-PdgSection 'Windows'; $g = Add-PdgGroup $s; Add-PdgRow $g 'Computer name' 'TEST-PC'; Add-PdgRow $g 'Running as' 'Administrator (elevated)'
        $l = New-Object System.Collections.Generic.List[object]; $l.Add($s); return , $l
    }

    # decoder and verdicts
    $st = New-PdgDecoder; $sfcBytes = (New-Object System.Text.UnicodeEncoding $false, $false).GetBytes($sfcText)
    foreach ($b in $sfcBytes) { Add-PdgDecoderBytes $st ([byte[]]@($b)) 1 }   # one byte at a time, as a slow pipe would
    Complete-PdgDecoder $st
    $ln = @($st.Events | Where-Object { $_[0] -eq 'line' } | ForEach-Object { $_[1] }); $stat = @($st.Events | Where-Object { $_[0] -eq 'status' } | ForEach-Object { $_[1] })
    Check 'SFC output (UTF-16, fed byte by byte): lines and progress' ($ln -contains 'Windows Resource Protection did not find any integrity violations.' -and $stat -contains 'Verification 42% complete.' -and $ln -contains 'Verification 100% complete.')
    Check 'percent parsing' ((Get-PdgPercent 'Verification 42% complete.') -eq 42 -and (Get-PdgPercent '[====  57,3%  ]') -eq 57.3 -and $null -eq (Get-PdgPercent 'no number'))
    $sfcT = $script:PdgTools['sfc']; $dismT = $script:PdgTools['dism-restore']
    Check 'verdict: SFC clean' ((Get-PdgVerdict $sfcT 0 @('Windows Resource Protection did not find any integrity violations.'))[1] -eq 'Good')
    Check 'verdict: SFC repaired' ((Get-PdgVerdict $sfcT 0 @('Windows Resource Protection found corrupt files and successfully repaired them.'))[1] -eq 'Repaired')
    Check 'verdict: SFC unable to fix' ((Get-PdgVerdict $sfcT 0 @('Windows Resource Protection found corrupt files but was unable to fix some of them.'))[1] -eq 'Bad')
    Check 'verdict: needs admin' ((Get-PdgVerdict $sfcT 0 @('You must be an administrator running a console session in order to use the sfc utility.'))[0] -eq 'Needs administrator rights.')
    Check 'verdict: DISM repairable' ((Get-PdgVerdict $dismT 0 @('The component store is repairable.'))[1] -eq 'Warning')
    Check 'verdict: DISM no source' ((Get-PdgVerdict $dismT 0x800f081f @('Error: 0x800f081f', 'The source files could not be found.'))[1] -eq 'Bad')

    # CBS filter, also while another program holds the log open for writing; nothing copied anywhere
    $holder = New-Object IO.FileStream $cbsFile, ([IO.FileMode]::Open), ([IO.FileAccess]::ReadWrite), ([IO.FileShare]::ReadWrite)
    try { $c = Read-PdgCbs $cbsFile } finally { $holder.Dispose() }
    Check 'CBS: counts (error, corrupt, restored, repaired)' (($c.Counts -join ',') -eq '1,1,1,1' -and $c.Matches.Count -eq 4 -and $c.LinesRead -eq 8)
    Check 'CBS: newest SFC run, repaired and could not fix' ($c.Sfc.Done -and $c.Sfc.Started -eq [datetime]'2026-11-09 09:01:00' -and @($c.Sfc.CannotRepair) -contains 'example.dll' -and @($c.Sfc.Repaired).Count -ge 1)
    $c2 = Read-PdgCbs $cbsFile -SfcOnly
    Check 'CBS: SFC [SR] lines only' ($c2.Matches.Count -eq 2 -and @($c2.Matches | Where-Object { $_ -notmatch '\[SR\]' }).Count -eq 0)
    Check 'CBS: read in place, no copies' ((Same $before (Get-Files)) -and @(Get-ChildItem -LiteralPath $logs).Count -eq 1)

    # ---- 3) the console with keys
    Reset-Run @('1', 'n', 'q'); Start-PdgConsole ''
    Check 'start: in-memory line shown' ($script:Out.ToString() -match 'In-memory: nothing is saved on this PC\.')
    Check 'SFC: confirm asked' (@($script:Prompts) -contains '  Run SFC /scannow now? (y/N)')
    Check 'SFC "n": nothing started' ($script:ToolRuns.Count -eq 0 -and $script:Out.ToString() -match 'Nothing was started')

    Reset-Run @('1', 'y', 'q'); Start-PdgConsole ''
    $o = $script:Out.ToString()
    Check 'SFC "y": runs once with live progress' ($script:ToolRuns.Count -eq 1 -and $o -match '42\.0%' -and $o -match 'Verification')
    Check 'SFC "y": verdict shown' ($o -match 'SFC /scannow: No integrity violations found\.')
    Check 'SFC "y": CBS log read afterwards' ($o -match "1 it could not fix" -and $o -match "couldn't fix: example\.dll")

    Reset-Run @('2', 'y', 'q'); Start-PdgConsole ''
    Check 'DISM CheckHealth: OEM output, bar progress and verdict' ($script:ToolRuns[-1] -eq 'dism-check' -and $script:Out.ToString() -match 'DISM /CheckHealth: No component store corruption detected\.' -and $script:Out.ToString() -match '100\.0%')

    Reset-Run @(); $n = $script:ToolRuns.Count; Start-PdgConsole 'dism-scan'
    Check '-Tool (after the elevated restart): starts the picked tool' ($script:ToolRuns.Count -eq $n + 1 -and $script:ToolRuns[-1] -eq 'dism-scan')

    $script:Admin = $false; $n = $script:ToolRuns.Count
    Reset-Run @('4', 'n', 'q'); Start-PdgConsole ''
    Check 'not admin, "n": nothing started, no elevation' ($script:ToolRuns.Count -eq $n -and $script:Started.Count -eq 0 -and @($script:Prompts) -contains '  Restart as administrator and run it there? (y/N)')
    Reset-Run @('4', 'y', 'q'); Start-PdgConsole ''
    $el = @($script:Started | Where-Object { $_[2] -eq 'RunAs' })
    Check 'not admin, "y": elevated window asked for, with the tool and in-memory mode' ($script:ToolRuns.Count -eq $n -and $el.Count -eq 1 -and $el[0][1] -match '-Tool dism-restore' -and $el[0][1] -match [regex]::Escape("`$env:PXD_INMEMORY='1'"))
    Reset-Run @('a', 'q'); Start-PdgConsole ''
    Check 'A: restart as administrator' (@($script:Started | Where-Object { $_[2] -eq 'RunAs' }).Count -eq 2)
    $script:Admin = $true

    $cbsOut = Join-Path $picked 'cbs.txt'
    Reset-Run @('l', 'm', 'f', 'c', 's', $cbsOut, '', 'q'); Start-PdgConsole ''
    Check 'CBS view: copy and save where asked' ($script:Clip -match 'pxd Diagnostics - CBS log entries' -and (Test-Path -LiteralPath $cbsOut) -and (Get-Content -LiteralPath $cbsOut -Raw) -match 'SFC \[SR\] lines only')

    $rep = Join-Path $picked 'report.txt'
    Reset-Run @('c', 'r', $rep, 'q'); Start-PdgConsole ''
    $r = if (Test-Path -LiteralPath $rep) { Get-Content -LiteralPath $rep -Raw } else { '' }
    Check 'R: report saved where asked, with the tool results' ($r -match 'REPAIR TOOLS' -and $r -match 'SFC /scannow: No integrity violations found' -and $script:Clip -match 'TEST-PC')

    Check 'console: nothing written except the asked-for files' ((Same $before (Get-Files)) -and @(Get-ChildItem -LiteralPath $picked).Count -eq 3)

    # ---- 4) control: the file check does see a write
    Set-Content -LiteralPath (Join-Path $watch.TEMP 'control.txt') -Value 'x'
    Check 'control: a write is detected' (-not (Same $before (Get-Files)))
} catch {
    Check "no unexpected error ($($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber))" $false
} finally {
    foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    if ($t -and (Split-Path $t -Leaf) -like 'pdg-test-*' -and (Test-Path -LiteralPath $t)) { Remove-Item -LiteralPath $t -Recurse -Force }
}
if ($fails) { Microsoft.PowerShell.Utility\Write-Host "$fails check(s) failed" -ForegroundColor Red; exit 1 } else { Microsoft.PowerShell.Utility\Write-Host 'All checks passed' -ForegroundColor Green }
