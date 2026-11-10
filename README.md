# pxd Diagnostics

**Windows diagnostics in one window.** See what's in a PC at a glance (Windows version, processor, memory, graphics, disks, network and motherboard), run **SFC** and **DISM** with live progress, and pull the errors and repairs out of the CBS log without scrolling through a hundred thousand lines.

<p align="center">
  <img src="docs/screenshots/system.png" width="720" alt="pxd Diagnostics System tab under the dark pxd title bar: cards for Windows, processor, memory, graphics, storage and network on a made-up workstation, with Refresh and Copy all info buttons">
</p>

## Features

- **System information as cards.** Windows edition, version and build, install date, **uptime**; processor, cores, threads and clocks; memory in use, modules and slots; **graphics card**, video memory, driver and display mode; disks with SSD/HDD, NVMe/SATA and **Windows' health status**, plus free space per drive; network adapters with speed, MAC, IP, gateway and DNS; system and board maker, BIOS version and date, UEFI and Secure Boot.
- **Copy all info** puts the whole lot on the clipboard as plain text, ready for a support ticket or forum post.
- **SFC /scannow** with live progress: the percentage, the elapsed time, everything SFC prints, and a plain-language verdict at the end (nothing found, repaired, couldn't fix, repair pending).
- **DISM CheckHealth and RestoreHealth** next to SFC, with the same live progress and verdict. RestoreHealth is the usual next step when SFC can't fix files.
- **CBS log filter.** *Find CBS.log* opens Windows' servicing log straight away (no need to know where it lives), or *Open log…* a copy from another PC. Only lines mentioning **error**, **corrupt**, **restored** or **repaired** are listed, with those words coloured and counted. *SFC lines only* narrows it to SFC's own `[SR]` entries. **Copy** or **Save results…** to a text file. The log is read while Windows still has it open, and after an SFC or RestoreHealth run it's read for you.
- **Shortcuts to Windows' own tools:** Reliability Monitor, Event Viewer, Device Manager and System Information.
- **Opens as a normal user.** Only SFC and DISM need administrator rights. Their buttons say **Needs admin**; click one and pxd Diagnostics asks Windows for permission, reopens as administrator and starts that tool straight away. Say No to the prompt and nothing happens apart from a note explaining why.
- **Never freezes.** Reading the hardware, running the tools and filtering the log all happen in the background.
- **Remembers the window** (size, position, maximized), and starting it again brings the open window to the front instead of a second copy.
- **Works with [pxd Toolkit](https://github.com/joshpack/pxdToolkit):** same dark title bar as the other pxd apps, installed and kept up to date from there.

| Repair | CBS log |
| --- | --- |
| <img src="docs/screenshots/repair.png" width="420" alt="Repair tab: SFC finished and found corrupt files and repaired them, DISM CheckHealth at 40 percent, SFC's output below, and buttons for Reliability Monitor, Event Viewer, Device Manager and System Information"> | <img src="docs/screenshots/cbs-log.png" width="420" alt="CBS log tab: counts of error, corrupt, restored and repaired lines, and the matching log lines with those words coloured"> |

## Supported systems

| System | Status | Notes |
| --- | --- | --- |
| Windows 11, x64 | **Tested** | |
| Windows 10, x64 | Should work, untested | Uses the same WMI classes, SFC and DISM. |
| Windows on Arm | No download | May build from source with `-r win-arm64`; untested. |
| Linux, macOS | **Not supported** | SFC, DISM, WMI and the CBS log are Windows-only. |

## Install (Windows)

**From pxd Toolkit:** turn on pxd Diagnostics in **[pxd Toolkit](https://github.com/joshpack/pxdToolkit)** and it's downloaded and kept up to date for you.

**By hand:**

1. Install the **[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (x64) if you don't have it. The plain ".NET Runtime" is enough; the ".NET Desktop Runtime" includes it, so that works too.
2. Download **`pxdDiagnostics-1.2.0-win-x64.zip`** from **[the latest release](https://github.com/joshpack/pxdDiagnostics/releases/latest)**.
3. Unzip it somewhere permanent, for example `%USERPROFILE%\Apps\pxdDiagnostics`.
4. Run `pxdDiagnostics.exe`. It opens without administrator rights.

The exe isn't code-signed, so SmartScreen may warn the first time: choose **More info → Run anyway**.

## Run it from PowerShell (no install)

The console edition works on any Windows PC with PowerShell, with nothing to download or install:

```powershell
irm pxdtek.com/diag | iex
```

It shows the PC at a glance (**S** for every detail), runs **SFC /scannow** (**1**), **DISM CheckHealth** (**2**), **ScanHealth** (**3**) and **RestoreHealth** (**4**) with live progress and the same verdicts as the app, and filters the CBS log (**L**: counts, what the newest SFC run repaired or couldn't fix, the matching lines, **F** for SFC's own lines only, copy or save them). **C** copies all the information for a ticket, **R** saves a text or JSON report, **W** opens Reliability Monitor, Event Viewer, Device Manager or System Information. It runs in Windows PowerShell 5.1 and PowerShell 7 as a normal user. SFC and DISM need an elevated prompt: pick one and say yes, and it reopens as administrator and starts that tool (or press **A**). A tool only runs after you confirm it.

Non-interactive, for remote shells and scripts:

```powershell
& ([scriptblock]::Create((irm pxdtek.com/diag))) -Report
& ([scriptblock]::Create((irm pxdtek.com/diag))) -Export C:\Temp\pc.txt      # or .json; add -SfcOnly for SFC's CBS lines
& ([scriptblock]::Create((irm pxdtek.com/diag))) -Copy                       # system information to the clipboard
```

`-Tool sfc|dism-check|dism-scan|dism-restore` starts that tool straight away in an elevated prompt (it's what the restart as administrator uses). The script is [`cli/pxdDiagnostics.ps1`](cli/pxdDiagnostics.ps1); pxdtek.com serves its raw GitHub copy. It saves nothing on the PC unless you ask for a report, and then only where you choose; the CBS log is read in place, never copied. Run from the [pxd Toolkit](https://github.com/joshpack/pxdToolkit) console (`$env:PXD_INMEMORY = '1'`) it says so at the start ("In-memory: nothing is saved on this PC.") and keeps that mode in the elevated window. `cli\Test-Diagnostics.ps1` checks all this without running SFC or DISM.

## Using it

- **System** fills in by itself a second after the window opens. **Refresh** reads everything again; **Copy all info** copies it. A card that couldn't be read says why instead of staying blank.
- **Repair → Run SFC /scannow.** As a standard user, Windows asks for permission first and the app reopens as administrator (the header then says *Administrator*). SFC usually takes 5 to 15 minutes; DISM RestoreHealth can take longer and needs an internet connection. Closing the window while one runs asks you to confirm, because the tool carries on without you seeing the result.
- **CBS log → Find CBS.log** reads `C:\Windows\Logs\CBS\CBS.log`. When a log has more than 5,000 matching lines, the list shows the newest 5,000; Copy and Save always include all of them.

### What the SFC verdicts mean

| Verdict | Next step |
| --- | --- |
| No integrity violations found | Nothing to do. |
| Found corrupt files and repaired them | Restart Windows. |
| Found corrupt files it couldn't fix | Run DISM RestoreHealth, then SFC again. |
| A repair is pending | Restart Windows, then run it again. |
| Couldn't run the scan | Try again after a restart, or from Safe Mode. |

## Command line

| Option | What it does |
| --- | --- |
| `--tool sfc`, `--tool dism-check`, `--tool dism-restore` | Open on the Repair tab and start that tool (only when running as administrator) |
| `--self-test <folder>` | Diagnostics, see below |
| `--self-test <folder> --sample` | The same, with made-up data in the window, for screenshots |
| `--write-icon <file.ico\|file.png>` | Build step: writes the app icon |

## Self-test

```bat
pxdDiagnostics.exe --self-test C:\Temp\pxd-test
```

Writes `report.txt` into that folder: what each system information section returned (and which came back empty), a check of the CBS log filter on a made-up `CBS.log` it writes into the folder (also while that file is held open, and while it's locked), a read-only pass over the real CBS log, decoding of recorded SFC and DISM output, the verdicts, and the elevation check, that the window's size, position and maximized state are saved and restored (a spot no longer on any screen is ignored), and that a second start hands over to the first. **SFC and DISM are never run.** It also saves `system.png`, `repair.png` and `cbs-log.png`, pictures of each tab with the title bar, drawn by Avalonia's headless renderer, so nothing appears on your screen. Window placement goes to a `data` folder inside the test folder unless `PXDDG_DATA` is set. The exit code is 0 when every check passed. Without `--sample` the report and pictures contain your real hardware details.

## Window placement

The window's size, position and maximized state are saved in `%APPDATA%\pxdDiagnostics\windows.json`. Delete it to go back to the default. The folder left by the app under its former name is moved here the first time it starts. The environment variable `PXDDG_DATA` points the app at another folder instead.

## Build from source

Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
git clone https://github.com/joshpack/pxdDiagnostics.git
cd pxdDiagnostics
dotnet run
dotnet publish -c Release -r win-x64 --self-contained false -p:DebugType=none -o publish/win-x64
```

The version comes from `<Version>` in `pxdDiagnostics.csproj`. For a copy that doesn't need the .NET Runtime installed, use `--self-contained true` (a bigger download). After changing the icon drawing, run `pxdDiagnostics.exe --write-icon Assets\pxdDiagnostics.ico` and `--write-icon docs\icon.png`, then build again so the exe picks it up.

## Code layout

| Path | What's there |
| --- | --- |
| `Program.cs` | Start-up and the command-line options |
| `pxdStandard.cs` | The pxd title bar and remembered window placement, shared by the pxd apps |
| `Migration.cs` | Moves the folder kept under the app's former name (first start after the rename) |
| `SingleInstance.cs` | Hands a second start over to the running copy |
| `App.axaml(.cs)` | Theme and opening the window |
| `Views/MainWindow.axaml(.cs)` | The window: System, Repair and CBS log tabs |
| `Diagnostics/SystemInfo.cs` | Reading the hardware and Windows details (WMI, registry, .NET), plus the made-up sample |
| `Diagnostics/RepairTools.cs` | Running SFC and DISM, decoding their output, progress and verdicts |
| `Diagnostics/CbsLog.cs` | Finding, reading, filtering and saving the CBS log |
| `Platform/Elevation.cs` | Checking for administrator rights and restarting elevated |
| `AppIcon.cs` | The app icon, drawn in code |
| `SelfTest.cs` | `--self-test` |
| `cli/pxdDiagnostics.ps1` | The PowerShell console edition (`irm pxdtek.com/diag \| iex`) |
| `cli/Test-Diagnostics.ps1` | Checks for the console edition (SFC and DISM are faked, never run) |
| `docs/screenshots/` | The pictures in this file (made-up data) |

Built with [Avalonia](https://avaloniaui.net/) 12 on .NET 10.
