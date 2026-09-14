<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Pavise">

# Pavise

Windows game resource scheduling and guard tool

`C#` · `WinForms` · `Interface: Chinese / English`

[简体中文](README.md) · **English** · [日本語](README.ja.md)

**[Visit the website · pavise.club](https://pavise.club/en/)**

**v2.2.2.3 · [Release notes](https://pavise.club/en/changelog/#latest)**

<br>

<img src="docs/screenshots/en/overview.png" width="100%" alt="Pavise overview">

</div>

## Performance

Pavise does not generate additional performance. It returns the share taken by background processes to the game. Machines with many background programs and noticeable CPU or disk contention benefit the most. A clean system, or a game entirely limited by the GPU, will see little change.

Measure the effect by toggling it on and off in the same game and the same scene.

## Usage

Requires Windows 10 2004 (build 19041) or newer; Windows 11 24H2 is preferred. On earlier builds the efficiency mode and per-process timer isolation that background suppression relies on do not exist, so scheduling would have no effect: Pavise reports this and exits, restoring any system changes left by earlier versions.

Add a game's EXE or shortcut to the library, or use the scan function to import games already installed through Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net, Xbox or Microsoft Store. The scan also reads installed-program uninstall records and desktop and Start Menu shortcuts.

Protection applies for as long as the game runs; switching to the desktop or minimising does not end it. Games started through a launcher (League of Legends, for example) have their real executable remembered after the first confirmation and are recognised directly afterwards. Launchers, updaters, crash reporters and anti-cheat processes are not identified as games.

Session changes are restored from their records when the game exits. After an abnormal exit, the next launch retries recovery and retains records of failures. Application GPU preferences, per-EXE compatibility settings and persistent system settings require separate restoration; input-language changes are not rolled back. Purged caches and manually deleted League add-ons cannot be recovered by restoring settings; the client may download add-ons again during an update or repair.

## Modes

| Mode | Suppression scope |
|---|---|
| Smart | Every background process that clears the protection boundary is isolated outright the moment the match starts, with no heat check and no tier-by-tier escalation. Whatever you are using, and its family, is exempt from suppression. A separate adaptive-escalation switch, off by default, temporarily escalates to the Esports profile under sustained CPU saturation (above 90% for over ten seconds) and steps back down once load falls below 80% and stays calm for two minutes, at most three times per match |
| Esports | Widens suppression to non-game processes that pass the protection boundary, including windowed apps and apps used after alt-tab. The whitelist and built-in protection rules still apply |
| Handheld | Background suppression identical to Esports, with the power side left to vendor tools: no power slider, and pure power-saving items stay enabled on AC. Five items are not offered in this tier and resolve to off even when enabled: disable CPU idle, power plan idle policy, cache warm-up, VRAM residency and exclusive cores. Requires a battery; for handhelds and thin-and-light laptops |
| Custom | Background suppression, cores, memory and power, system environment and graphics, each chosen individually |

The tiers mainly differ in process eligibility and additional policies. Eligible ordinary background processes are isolated directly using Idle CPU priority, very low disk I/O priority, low paging priority, EcoQoS and a timer-resolution cap. GPU yielding also lowers GPU scheduling priority. Windows dynamic priority boosts are preserved so a background process the game is waiting on can finish its work; these boosts are distinct from processor turbo, which background suppression does not directly disable.

Ordinary background suppression always excludes anti-cheat, Windows core services, network accelerators, the input/audio/peripheral chain, hardware tools and other signed-in accounts. Separate, default-off switches on the Anti-Cheat page can suppress selected user-mode anti-cheat processes; they do not control kernel drivers.

Game family exemption is on by default: game platforms, launcher shells, resident processes inside the game folder and child processes spawned by the game are all released as a family. Turning it off releases only the game itself and the whitelist; everything else is suppressed as ordinary background.

There is no Extreme tier from 2.2.2 on. Everything it used to switch on for you in one go now has its own switch, all off by default and left to you, and the System Environment page no longer needs an unlock. Stored configurations pointing at Extreme resolve as Esports — their suppression scope and strength were byte-for-byte identical, so background behaviour does not change. Tiers the machine cannot use are not listed in the mode menu; a desktop, for example, has no Handheld tier.

When the system no longer boots or the program will not open, run `Pavise-Rescue.cmd` from the repository root (it requests administrator rights on its own): it first exports logs, crash records, power and boot configuration to a Pavise-Rescue folder on the desktop, then exits Pavise and restores system changes from its receipts, resets items with no receipt to Windows defaults, deletes the managed power plan and restores all power plans to factory settings, and resets GPU clock locks and power limits. **It also deletes the game library, the whitelist and every setting**, and a restart afterwards is mandatory. HAGS, VBS and the hypervisor are left as they are unless a receipt covers them.

## Per-game configuration

Select a game in the library and open its configuration. The current policy catalogue contains 37 per-game overrides across modes, background suppression, cores, memory and power, environment and graphics, including the mode, family policy and core mask. Unset overrides follow global settings, and changes save immediately. Per-EXE fullscreen-optimization and DPI compatibility settings are separate from these 37 items and are not automatically restored at match exit.

- Most per-game settings are resolved when the match activates and apply to the next one. Pausing nonessential services and disabling CPU idle are handled in the current session and reverted when turned off
- The current core selection can be pinned to one game without affecting others
- A whole override set can be cleared back to global in one action
- The overview page and the tray show the mode actually in effect, labelled with the game its configuration came from
- When repeated driver-write failures turn a switch off automatically, that game's override for it is cleared as well

Library entries can be renamed. This changes the displayed name only and does not affect recognition.

## Features

Session changes are restored from records at match exit. Persistent settings are mainly on the System Environment page, but application GPU preferences and per-EXE compatibility settings also persist and require separate restoration. Restart requirements are stated on each item. File cleanup is a separate operation; restoring settings cannot recover deleted contents.

### Overview and guard

The guard switch controls general game-session scheduling: enabling it starts automatic detection and takeover; disabling it stops takeover and attempts to restore session changes. Standby staging, persistent settings and the League extension follow their own controls. Turning off the guard does not undo all changes.

- The overview shows the current game, a summary of the last session, the current policy, and whether it comes from the global setting or a specific game's configuration
- **Needs attention**: guard off, unsaved game configuration, missing administrator rights, and recent warnings or errors in the log each get a direct button
- **Diagnostic summary**: one click copies the version, game, related settings and recent logs, ready to paste into a report to the author
- **Session report**: play time, the number of suppressed processes and their CPU usage, the share of time spent power- or thermal-limited, and the peak shared video memory the game spilled into system RAM
- **Feature search**: type a keyword at the top to jump straight to a switch instead of remembering which page it lives on
- **Auto-hide**: the window collapses to the tray ten seconds after a game is detected, once per session
- **Interface**: light and dark themes, instant Chinese/English switching, and newly written logs following the selected language. Japanese is a documentation translation; the current app has no Japanese interface

### Library

- Add an EXE, a shortcut or the game's folder manually, or drop them onto the window; when a folder holds several candidate programs they are listed for you to pick. Scanning imports from Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net, Xbox and Microsoft Store, plus games found through installed-program uninstall records and desktop or Start Menu shortcuts; shortcuts are only hints, and a directory still needs game evidence before it is added. When one game yields several entry points you pick them one by one, so select-all never pulls the launcher in too
- **Name search**: full-width and half-width, letter case, spaces and punctuation are ignored, and multiple words only need to match individually
- **Forced takeover**: for emulators, cloud gaming and anything else that cannot be recognised, the match starts as soon as the process does
- **Auto-add**: newly recognised games are collected automatically. A path you removed goes on an ignore list and is never auto-added again until you add it back by hand
- **Suspected malware alert**: a process with no visible window outside the Windows and Program Files directories that uses a quarter or more of the logical CPUs for two minutes straight, or a suppressed process that lifts its own background core restriction, is logged and raised as a tray warning about a possible miner infection. Random-looking names use half the threshold; each name is alerted at most once a day
- **Render observation label**: records GPU 3D activity observed on an EXE. This alone does not identify the main game renderer or establish that related processes can safely be suppressed
- **Family background suppression**: per-game, off by default. While off, game platforms, launcher shells, resident processes in the game folder and child processes spawned by the game are released as a family
- **Per-game configuration**: 37 items in the current policy catalogue; unset overrides follow global settings, and entries can be renamed
- **WeGame shell removal**: games launched through WeGame (CrossFire, Assault Fire, Delta Force and the like) get the same shell-removal strip under their card as League of Legends. With shell launch on, WeGame, Cross and Tencent add-on processes are ended precisely 30 seconds into a match; if the game exits within 20 seconds, automatic removal is disabled for that game on this machine; if the shell keeps respawning, removal pauses for the match. Per-game switch, off by default
- **Controller mapper protection**: controller mappers such as DS4Windows and reWASD are exempt from background suppression by name. The Smart tier's adaptive escalation is a separate switch, off by default

### Processes and cores

- **Game process boost**: the renderer receives high priority and higher disk I/O, memory-page and GPU scheduling priority, restored on exit
- **Candidate thread boost**: unavailable in Handheld or on machines with fewer than 6 physical cores. Elsewhere it is on by default and can be disabled per game. CPU time identifies a busy candidate, not a frame-critical thread, so a wrong choice can slow the game. Under the restored priority rule, machine-wide CPU saturation alone does not withdraw an active candidate boost
- **Smart yield**: ten seconds of sustained CPU saturation returns the game to Normal only when no candidate-thread boost is active. Restricted cores, CPU Sets and unknown CPU domains no longer independently block High priority; original settings are restored on exit
- **In-match self-yield**: Pavise moves off the game cores and lowers its own scheduling weight
- **Background suppression**: directly isolates eligible ordinary background processes through CPU, I/O and paging priority, EcoQoS and timer-resolution policy, while preserving Windows dynamic priority boosts
- **Background GPU priority demotion**: a background process using the GPU also has its GPU scheduling priority lowered
- **Suppressed working-set trim**: off by default, with per-game overrides. Attempts trimming only when available memory is both below 4 GiB and below one eighth of total memory. Subsequent page faults and slower first responses remain possible. Anti-cheat processes are excluded
- **Wider background suppression**: covers eligible non-game background processes even after alt-tab. Forced on in Esports and Handheld; built-in protection and the whitelist still apply
- **Core scheduling**: its own page with one core map and a single Exclusive cores switch. Supports All, No HT, Clear, Invert, CCD shortcuts and Leave system cores; select at least two logical CPUs. Save applies from the next session and original affinity is restored on exit. The exclusive range is derived by aligning the selection to whole physical cores, and at least two complete physical cores must stay outside it; when on, ordinary background processes avoid that range during game boost only, and interrupts may still use the cores
- **Hard-lock background outside the exclusive range**: off by default. When on, background processes get an affinity written that keeps them out of the exclusive range
- **Correct immediately when another process changes affinity**: off by default. By default a change made by another process is kept and no further placement happens this session; when on, the selected range is written back at once
- **Whitelist**: drag in an EXE or shortcut, pick from running programs, or browse for one. Child processes are protected by default, command-line and script hosts protect only themselves, and the context menu switches between "this program only" and "including child processes". Built-in entries required by the system cannot be removed, and the default preset can be restored in one click. The page can expand the automatic-exemption catalogue this build actually uses, including anti-cheat group process names; GPU driver containers and power, boost and fan tools are always exempt and need no manual entry

### Anti-cheat coverage

Nine anti-cheat systems are listed individually, each stating which games it protects and which of its components are kernel drivers that must not be touched: ACE (Tencent), TenProtect, Vanguard (Riot), EasyAntiCheat (Epic), BattlEye, EA Javelin, nProtect GameGuard, FACEIT and NEAC (NetEase). Vanguard is protected but never suppressed: Riot states that it blocks third-party programs that reach low-level system functions while it runs, so the failure mode of suppressing it is a game that will not start.

Chinese arenas are listed separately: Perfect World Arena, 5E and B5. Their anti-cheat lives inside the platform client, so Pavise protects those processes and never suppresses them. NetEase NEAC gains NeacClient, OWNeacClient and NeacProtect, Tencent TP gains TP3Helper and TPHelper, ACE gains ACE-Service64; HoYoverse ships kernel drivers only, which are recognised for log messages.

Seven groups in total are **protection-only**: the three Chinese arenas above and Vanguard, plus PunkBuster, Nexon Game Security (including BlackCipher), and Wellbia XIGNCODE3 / UNCHEATER. Names, name prefixes and dedicated directories exempt their processes from background suppression, including `.aes` components and helpers inside those directories. These groups have no suppression switches and take no part in anti-cheat core pinning. Turning off the anti-cheat master switch does not disable background exemptions.

The **compatibility list** records games that refuse writes, so priority and I/O writes certain to fail are not retried while GPU scheduling priority and the frame thread are still attempted. The list expires when the game updates.

**Anti-cheat suppression** (per-group switches on the Anti-Cheat page, off by default) has three tiers, Isolated by default: Gentle keeps only efficiency-core capping and a disk I/O downgrade; Balanced adds below-normal scheduling priority without core pinning; Isolated adds very low disk I/O (disk scanning is the main source of harm) and confinement to the fewest cores the game does not use (the last physical core on 6- to 8-core machines, efficiency cores on hybrid CPUs, the other L3 block on multi-CCD parts; writes refused by the anti-cheat's own protection make Pavise drop the whole suppression for that run without retrying). No tier drops the scanner to the lowest CPU priority, seals its timer resolution, or lowers its memory page priority — game threads can be suspended by the system during a scan and resume only when the scanner finishes, so starving it of CPU time only stretches a brief stutter into a multi-second freeze.

### Graphics

- **Application GPU preferences**: save the Windows power-saving GPU preference for chosen background apps. Applies at their next launch and never moves a running app
- **Preselect high-performance GPU on standby**: on dual-GPU machines, preselects the high-performance GPU while idle, skips anything already set by hand, and restores on exit
- **GPU power limit**: raised to the vendor-permitted maximum during the match and restored from the snapshot afterwards
- **NVIDIA**: maximum performance power mode (also blocking the CUDA-triggered memory downclock), G-SYNC extended to windowed mode, low latency (on or ultra), Smooth Motion frame generation, unrestricted shader cache, DLSS override (latest or a pinned J/K generation), per-game ReBAR
- **AMD**: Anti-Lag, AFMF frame generation, RSR driver-level upscaling
- **Intel**: global low latency, only on DX9/DX11 paths the driver reports as supporting live changes. Boost and XeSS are never touched; Endurance Gaming is turned off during play so battery mode no longer caps the frame rate to a fraction of the panel refresh
- **VRAM residency**: declares a minimum reservation when video memory runs tight, reducing long frames from textures being evicted and paged back. It neither adds nor locks memory; off by default
- **Auto power-saving GPU**: enrols background apps still running 3D on the game's render GPU to use the power-saving GPU at their next launch, at most 2 per match; off by default

Original values are snapshotted and restored when a switch is turned off. Writes whose ownership cannot be confirmed are never overwritten.

### Input devices

- Turn off **Filter Keys, Sticky Keys and Toggle Keys** and their activation shortcuts to avoid altered keyboard behaviour and accidental activation during play
- **Disable selective suspend for keyboards and mice**, which removes the loose first input after an idle period. Keyboards and mice only, never USB storage or audio
- Repair **input queue length** changed by other tools through System Checkup. There is currently no Enhance Pointer Precision switch
- **Foreground time slice check and repair**: System Checkup decodes the current configuration and repairs fields that meet its criteria. The three-way selector has been removed
- **Switch to English once at game entry**: one English keyboard layout request to the foreground game inside a short entry window. Switching back to Chinese, alt-tabbing and returning are never intervened in

### Device interrupts

Three steps: turn on observation, pick a device, review results and set cores.

- **Match interrupt observation**: kernel ETW collects per-device DPC activity, accounted per match, identifying devices contending with the game's cores. Needs administrator rights
- **Device list**: grouped as input, storage, network, display and audio, and searchable; each device shows its observed peak, currently configured cores and adjustment state. Devices owned by the System environment page are read-only here
- **Candidate cores**: the latest five sessions with the same game, software configuration, game cores, topology, boot and device driver configuration are combined to find cores that stay idle: each needs at least 80% sample coverage, at most 60% average load and at most 20% busy time, excluding the game's physical cores and their hyperthreads. Fewer than three sessions is marked preliminary, three or more as consistent. Shared drivers, storage controllers, multi-queue devices, cross-processor-group devices and devices whose configuration changed get no candidate, and the page says why
- **Manual core selection**: pick the device and the target cores. The cost is spelled out before writing and every device keeps a receipt. Changes need a restart
- **Adjustment verification**: after a restart and one more session, at least three sessions on each side are compared on slow DPCs on game cores, long frames and average target core load, reporting improved, no clear improvement, or insufficient evidence; every excluded session states its reason. When the presentation stream only has a PID identity it is a correlation hint only, and time overlap does not prove causation
- **Adjustment history**: keeps the write time and baseline of every change; a single device can be restored on its own, or everything at once. Restore all only undoes adjustments Pavise saved
- The page flags cases that are not worth touching: pinning a multi-message MSI-X device can reduce parallelism, and StorPort DPCs follow the CPU that issued the I/O, so pinning them usually does nothing

### Memory and power

- **Cache warm-up**: restored, off by default, with per-game overrides. After 90 seconds of stable play, reads asset archives at background priority, up to 2 GiB per session and about 31 MiB/s. Requires AC power, ample memory and an SSD; yields to standby cleanup and stops on exit or memory pressure.
- **Managed power plan**: created at the first match and configured for the processor. Selecting an existing plan only switches plans; explicitly enabling Disable CPU idle can still temporarily change that plan's idle settings. Windows active-plan notifications trigger reapplication if another app switches plans, with no periodic fallback after success. This is not an access-control lock against other writers
- **Storage kept awake in-match**: the managed plan zeroes the NVMe power-state latency tolerances and keeps the AHCI link Active, preventing the occasional hitch of an SSD waking from a low-power state; the NVMe side is relaxed on battery
- **Disable CPU idle**: only during a game; writes both the AC and DC values of the currently active power plan and restores them at match end. On battery it noticeably shortens battery life; not offered on AMD processors
- **Standby memory cleanup**: the entire standby list is purged only when both the list-size and true-free-memory thresholds are crossed. Technical detail below
- **MMCSS multimedia scheduling**: the share reserved for non-multimedia work drops from 20% to 10%, the Games task's scheduling category and file I/O are raised, and the lazy idle-check tier is disabled
- **Low-latency DWM composition**: off by default, with no per-game override. Requests MMCSS scheduling for DWM during play. Benefits depend on the presentation path and workload; this does not guarantee uninterrupted composition
- **Pause Windows Update, Delivery Optimization, nonessential services and automatic maintenance**, restoring owned changes afterwards. Service pausing uses a fixed allowlist. There is no current wireless-scan suppression control
- **Per-game DPI awareness**: with display scaling above 100% the borderless window is sized in physical pixels instead of being stretched by the compositor
- **Turn off Game DVR and Xbox background recording**, and **keep the display awake during a match**
- **Low-latency audio**: off by default, with per-game overrides. Attempts to open a silent stream with the device's minimum supported shared buffer, requesting a shorter engine period; the stream closes on exit
- **Power budget yield**: on laptops, hands shared power budget to the GPU when it is pinned against its limit and the CPU has headroom, reverting automatically when verification fails. Off by default; verification detail below

### System environment

Settings on this page persist and can be restored. Some require a restart; follow the instructions for each item:

- **Hardware-accelerated GPU scheduling (HAGS)**, **AMD Smart Access Memory**, **disable VBS**, **remove speculative-execution mitigations**
- **Game mode guard**, **windowed game optimisation**, **variable refresh rate optimisation** (lets DX11 exclusive-fullscreen games without native VRR use it)
- **Constant timer tick**, **global timer resolution**
- **Disable NIC power saving**, **disable NIC link power saving** (turns off 802.3az low-power idle, Green Ethernet and idle link-speed reduction so the link never sleeps, wakes or renegotiates between 1G and 100M; the adapter drops for a few seconds when written), **NIC interrupt-moderation experiment** (unchanged by default; explicit Off testing only on the single physical wired adapter selected by the public-IPv4 route probe, restored only after both NetCfg GUID and PnP instance identity match)
- **Accessibility key interception**, **disable keyboard and mouse selective suspend**
- **Disable memory compression and page combining**: off by default. Removes the background CPU cost of the compression thread and page-combining scans; offered from 24GB of RAM, fully effective after a restart

### System audit

A read-only check generates findings according to available hardware and samples, rather than a fixed count. It has four sections: write capability, locally measured, persistent system settings, and the findings list. Each finding states its evidence level: measured locally, bench-tested, mechanism clear, or unverified.

The check takes about 4 seconds and only reads; a precise mode spends 30 seconds measuring interrupt distribution and whole-machine load. Manual repairs cover abnormal time-slice settings, multimedia network throttling, input queues, wired-route metrics, FTH shims and explicitly disabled GPU MSI, and every pending repair can be applied at once. GPU power/thermal limits and DPC/ISR sources are also reported.

A local tool: no installed service, no uploaded machine data, no game-process injection and no game-memory edits. General scheduling does not modify game files; the League extension separately offers add-on directory deletion after user confirmation. Writes are read back where possible, and unexpected values are not counted as success.

### Log

- **Structured event stream**: system behaviour, match takeover and recovery are recorded as events, with counts for events, warnings and failures at the top
- Filter down to warnings or failures only, newest first; click to select, double-click to copy the raw line
- **Open log file**, **Refresh now** and **Clear log** (clears `Pavise.log` only; archived logs are untouched)
- **Record runtime log**: can be turned off entirely, after which nothing is written; turning it back on resumes

### Settings

- **Launch at startup**: implemented as an administrator scheduled task, so no UAC prompt appears at boot
- **Collapse the window after a game is detected**: into the tray 10 seconds after detection, once per match
- **Interface language**: Chinese / English, effective immediately for the UI and newly written log messages
- **Window appearance**: light and dark themes, a per-mode accent colour, a background image with adjustable intensity, and a one-click reset
- **Clear shader cache**: for artifacts or stutter after a driver update. Each game recompiles on its next launch, which is slower that once
- **Wipe all configuration**: reverts every persistent change Pavise ever made, including those left by retired features
- **Uninstall Pavise**: done entirely inside the app. It stops the runtime, restores every system change from its receipts, then removes the startup task, the managed power plan, settings, the data folder and leftovers from older versions, so the machine ends up as if Pavise had never been installed. Delete Pavise.exe itself afterward

The About page shows the current build, target platform, runtime state and licence, links to the release notes, and lists the primary and fallback update routes.

### League of Legends enhancement

The current build includes an extension card for recognised League entries:

- **Shell launch / Clean now**: lets the client complete normal login, then exits identified WeGame, Cross and add-on processes after the LCU confirms the session is ready. Repeated respawning stops automatic cleanup for that match
- **Headless match / Restore UI**: closes the lobby UI through the client API and restores it after play. It records a recovery lease and confirms a separate recovery guard before closing the UI
- **Delete add-ons**: after the client exits and checks pass, manual confirmation deletes identified CN-client add-on directories such as AI Coach and iCreate. No backup is retained; Pavise cannot undo deletion, and the client may re-download them during update or repair

## How power budget yield verifies itself

This feature changes the processor energy performance preference, which affects whole-machine power distribution, so it verifies more thoroughly than the others.

At least twenty seconds of observation to decide whether to yield, then at least fifteen more to verify after engaging. Complete evidence confirming no gain or regression reverts immediately and stops automatic attempts until the user re-enables it. Insufficient evidence reverts the change but allows observation in the next match.

Observation no longer expires after 60 seconds. It counts only paired load and power/frequency readings; a 20-second evidence gap restarts baseline collection. Missing evidence can be awaited for up to 60 seconds, and initial verification also has a 60-second deadline. Verification resuming after an evidence gap of at least 15 seconds restores the setting as inconclusive. Missing readings or long sampling gaps during observation or verification also make negative results inconclusive, avoiding persistent disablement based on a scene change. During holding, a 30-second load-data gap restarts rolling statistics.

A passed verification is not a one-way trip. A rolling 30-second window returns the budget when the bottleneck moves back to the processor (GPU load falling or CPU under pressure), and yields again if the GPU saturates, at most three times per match, each with a full re-observation and verification. The hysteresis band is yield at 90, release at 80, which prevents oscillation around the threshold.

Verification has two tiers:

- Machines with readable RAPL wattage verify by package power. Less than 3W freed, or GPU utilization dropping more than 3%, reverts and stops
- Machines without readable power fall back to platform frequency. EPP frees power by lowering frequency, so a relative drop under 3% means EPP has no effect on that machine, and it reverts and stops likewise

The two tiers are accounted separately. If CPU load shifts more than 10 points inside the verification window the result is indeterminate: it reverts without stopping and retries next match. That rule exists so a verification window landing on a cutscene or loading screen, where power falls on its own, is not mistaken for an ineffective change.

On this machine's i7-9750H, EPP across its full range moved neither package power nor actual frequency, so machines like it fail both tiers. That is the intended behavior.

## Memory cleanup

**Currently available: standby memory cleanup, off by default.** Enable it under Optimization Policies → Session Extras. The Parameters button next to it saves three values together: list-size threshold, free-memory threshold and check interval. The dialog offers Conservative, Standard and Aggressive presets that fill the fields for you. Defaults are 1024 MB, 1024 MB and 4000 ms; MB means 1024² bytes, thresholds accept 0–1048576 MB and the interval accepts 250–300000 ms.

The condition is `List ≥ list threshold AND Free < free threshold`. List counts the standby list plus the system working set; Free counts only Free + Zero pages and does not treat standby cache as truly free. Task Manager's Available includes standby cache and cannot stand in for Free. [Microsoft's definitions of these counters](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information).

Polling runs only while a game session is detected. The operation is `MemoryPurgeStandbyList`, which purges standby pages of every priority. It does not trim system or process working sets, flush the modified page list, combine memory pages, or change memory compression, the page file or timer resolution. Polling uses a dedicated thread with no overlapping execution and no catch-up for missed checks. Two consecutive read or purge failures disable the policy, and re-enabling requires confirmation again.

The purge itself stutters the game briefly and the call cannot be interrupted. After a successful purge, cleanup cools down for at least about a minute (no less than eight check intervals). It may also increase disk reads and load times and does not guarantee higher frame rates. Running it alongside another automatic memory cleaner is not recommended.

## Interface

<div align="center">
<img src="docs/screenshots/en/library.png" width="49%" alt="Library">
<img src="docs/screenshots/en/policy.png" width="49%" alt="Optimization policies">
<img src="docs/screenshots/en/graphics.png" width="49%" alt="Graphics">
<img src="docs/screenshots/en/interrupt.png" width="49%" alt="Device interrupts">
<img src="docs/screenshots/en/audit.png" width="49%" alt="System audit">
</div>

A full illustrated walkthrough is in the [online guide](https://pavise.club/en/docs/).

## Running and data locations

Double-click `Pavise.exe` to start it in the tray. The program is unsigned, so SmartScreen may prompt; choose to run it anyway. Adjusting other processes requires administrator rights. Launch at startup is implemented as a scheduled task. Pavise checks the official update source once at startup and uploads no local data.

Data is stored in `%AppData%\Pavise` by default, including target configuration, whitelist and run logs. Interface and feature switches live in the registry under `HKCU\Software\Pavise`. Placing an empty `Pavise.portable` file next to the executable switches storage to the program directory.

The Settings page provides a one-click restore.

## Author and licence

bdth ｜ 2074055628@qq.com ｜ Douyin 44601770838 (bugs, suggestions and usage questions)

The project uses the [Pavise Licence](LICENSE): free to use, free to redistribute unmodified, no reverse engineering, **no selling**.

Taking money in any form for distributing Pavise or a modified version is not allowed. This includes selling copies, activation codes or download access, bundling it into a paid product or subscription, paywalls, paid unlocks and donation gates.

Distributions must keep the licence and author information intact, tell recipients that the software may not be sold, and label the modifier and the modifications when distributing a modified version.

Provided as is, with no guarantee of effect or compatibility. Anti-cheat suppression, VBS and cache cleanup can all have side effects. Use it only on your own machine and understand the risks first.

The latest version is always free in the QQ groups: 1051472054, 1101249532, 383761286. **If you paid for it, you were scammed.** Ask for a refund and get it free from the groups.
