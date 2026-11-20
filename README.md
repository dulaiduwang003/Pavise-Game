<div align="center">

<a href="https://pavise.club/en/">
  <img src="docs/icon.png" width="88" height="88" alt="Pavise logo">
</a>

# PAVISE

**English** · [简体中文](README.zh-CN.md)

### Keep resources for your game.

**A free, open-source runtime resource orchestrator for Windows games.**<br>
Intelligent background suppression during play helps your existing hardware devote more of its performance to the current game.

**Source version 3.3.1.3 · Intelligent background suppression · Per-game configuration · Action and diagnostic logs**

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game)

**[Website](https://pavise.club/en/) · [Download and updates](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [User guide](https://pavise.club/en/docs/v3/) · [Report an issue](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> This repository provides **Pavise source code, build instructions, and runnable regression tests**. The current source version is 3.3.1.3. Available downloads and published release notes are listed on the [releases page](https://github.com/dulaiduwang003/Pavise-Game/releases/latest).

## Why runtime resource orchestration?

Pavise is a free, open-source **runtime resource orchestrator for Windows games**. Built around intelligent background suppression during play, it coordinates CPU placement, critical threads, performance priorities, and power policies for the current game.

**Pavise focuses on how resources are allocated and used while a game is running.** Game-thread placement, CPU selection, power states, and competing workloads all shape the gaming experience. Per-game configuration, execution checks, and restoration after play give players control over runtime scheduling and help them get more from the hardware they already own.

Our goal is to make **professional Windows gaming resource management freely available to every player, for a smoother and more consistent gaming experience.** Players should not need expensive optimization software to make good use of their own hardware. That is why Pavise remains free and open source: its implementation can be read, its behavior can be checked, and its tools are available to everyone.

## At its core: intelligent background suppression

**Pavise reduces competition from background activity during play, leaving more of your hardware's existing performance available to the game.** Browsers, launchers, updaters, and other background applications share CPU, disk, and GPU resources with your game. Pavise manages eligible background workloads according to the selected mode and current load.

Once the guard is enabled and a game is detected, Pavise can, according to your settings:

- **Make background work yield:** lower eligible background CPU, disk I/O, and memory-page priorities and request efficiency mode. CPU placement can also move manageable background work away from game cores.
- **Adjust to the situation:** Smart mode adapts to foreground status and sustained load; Competitive mode keeps background governance active throughout the game session, including when you switch away.
- **Protect applications you need:** the game, essential dependencies, and allowlisted applications remain protected. Add voice chat, streaming tools, and other applications that need their resources to the whitelist.
- **Restore changes and show what happened:** recorded temporary session changes are restored after play. Overview counters, action logs, and diagnostic logs show suppression and restoration results.

CPU scheduling, thread coordination, power, and graphics settings can work alongside background suppression. Choose each module according to your PC and the needs of each game.

## Before you start

**Most features apply only during a game session and restore their changes afterward. Features that do not restore on game exit are marked separately.** Persistent settings have their own disable or restore actions, and some require a restart. Deleted files and cleared caches are not reversible setting changes.

**Avoid running Pavise alongside other tools that manage process priorities or CPU affinity**, as they may repeatedly overwrite each other's settings.

**Security software and allowlisting:** depending on the features you select, Pavise changes process scheduling, services, power settings, and registry values; some actions use PowerShell. Security software may detect, block, or flag these operations. Download from an official source and verify its origin before adding Pavise to your security software's allowlist. **If you have concerns about these operations, review the public source code or build it yourself before deciding whether to run it with administrator privileges.**

Results depend on the game, hardware, drivers, and background load. Compare smoothness, stutter, and responsiveness in a repeatable scene.

## Download and get started

Requirements: **Windows 10 version 2004 (build 19041) or later, .NET Framework 4.7.2 or later, and administrator privileges.** Features are available according to the capabilities of your PC.

| Download option | Link |
| --- | --- |
| Website | [Download and updates](https://pavise.club/en/changelog/#latest) |
| GitHub | [Releases and downloads](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) |
| Quark cloud drive | [Alternative download; check the version](https://pan.quark.cn/s/3c8c986b3ea4) |

1. Download to a permanent folder. If using the complete ZIP, extract all accompanying files together.
2. Run as administrator, add the actual game EXE, and review the whitelist and per-game settings.
3. Select the features you want, choose Smart or Competitive mode and the background governance level, then enable the guard and start your game.
4. After leaving the game, check the restoration status. Export the session logs if troubleshooting is needed.

Before upgrading, exit the game and close the old version normally. In-app updates are under **About → Check for updates** and preserve your personal data.

## Build from source

Use the .NET Framework C# compiler and Windows PowerShell included with Windows, plus `link.exe`, `lib.exe` and `rc.exe` from Visual Studio Build Tools (MSVC x64 tools and a Windows SDK). The build script finds these tools with vswhere, or you can point `PAVISE_MSVC_ENV` at your own environment script. The ETW and CPUID object files are precompiled with Zig and stored in `src/Interop/NativeEtwImage.cs` and `src/Interop/NativeCpuImage.cs`. The standard build links them into `Pavise.exe` without requiring Zig. The [source guide](docs/SOURCE-GUIDE.md) maps the current directories and build entry points.

```bat
build.cmd -b prod
```

The output is `build\Pavise.exe`. It imports only KERNEL32 and mscoree and needs no VC runtime; the build checks the implementation and version identity. `build.cmd -b dev <output> --il-only` builds a pure IL twin from the same sources for tests and release checks that load the program as an assembly; it is not for release.

## Tests and benchmarks

This repository includes runnable regression tests for the current source, covering intelligent background governance, configuration reads, session and configuration identity, bounded retries, restoration ownership, timers, and security software detection. Run the published suites with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Run-PublicTests.ps1
```

Tests use in-memory settings, controlled interfaces, or their own processes. Test windows stay hidden, and the Pavise application entry point is not started. The independent CPU and memory benchmark also provides capability probes, measurement checks, and controlled background CPU-placement experiments. See the [test documentation](tests/README.md) for requirements, isolation boundaries, and measurement limits.

Development experiments on Microsoft background governance, Job limits, recovery, CPU topology, runtime overhead, and independent benchmarks are collected in [Experiments and validation](https://github.com/dulaiduwang003/Pavise-Game/blob/pavise3x/docs/experiments/README.md). Each topic includes methods, data, and test conditions. The linked records cover measurements from November 7–10, 2026 on the 3.3.0.8 development snapshot.

## Complete feature list

The following lists the features and sub-capabilities in the current 3.3.1.3 source. Availability depends on Windows, hardware, drivers, and the game. See the [user guide](https://pavise.club/en/docs/v3/) for instructions.

<details>
<summary>View the complete feature list</summary>

| Group | Features |
| --- | --- |
| **Guard and overview** | Guard switch, Smart and Competitive modes, game and guard status, hardware and utilization monitoring, live CPU, GPU, and memory readings, and session counters for suppressed processes and paused services. |
| **Intelligent background suppression (core)** | Background governance; Off, Normal, and Aggressive levels; dynamic scope expansion under load; whole-core background limits; background CPU, disk I/O, and memory-page priority reduction; efficiency mode (EcoQoS); limits on dynamic background thread boosts; Delivery Optimization download yielding; automatic maintenance limits during play; game and essential dependency protection; releasing or retaining governance when switching away; restoration after play. |
| **Game library and detection** | Game executable detection; adding an EXE or a running game; selecting minimized or tray games; Steam and WeGame scanning; automatic library additions; ignore list and undo; search; renaming games; replacing or restoring covers; removing games. |
| **Per-game tuning** | Per-game configuration; forced optimization trigger; Suppress family processes; disabling fullscreen optimizations; NVIDIA ReBAR; advanced tuning grouped into CPU and Other with saved values shown inline; game core range and isolation; a separate render-thread affinity switch with Automatic, Single render core, and Manual modes and optional whole-core avoidance; per-game opt-out of Performance priority and Game thread boost. |
| **CPU placement and isolation** | All-core, P-core, E-core, CCD, and cache-domain selection; manual logical processor selection; keeping one thread per core or including SMT siblings; whole-core background avoidance; draft CPU selections with Save and Reload saved; CPU-range restoration. |
| **Performance priority, thread boost, and render thread affinity** | Two per-game switches in advanced tuning, on by default: Performance priority (game process at High, raised disk and GPU priority, locked high-performance QoS) and Game thread boost (frame-submitting thread at the highest level with its partner slightly higher, or the busiest thread when it cannot be identified). Per-game render thread affinity only places cores: Automatic gives one to three frame-critical threads their own physical cores by measurement, Single render core pins the render thread to the best core, and Manual pins it to chosen CPUs. Changes apply next session and owned changes are restored when it is turned off, the game ends, or the app exits. Game memory priority adjustment and its legacy recovery path have been removed. |
| **In-game power** | No-switch, managed, or existing power plans; managed power and dynamic parameters; separate AC and DC settings; CPU-topology matching; core parking, EPP, and boost coordination; plan and parameter verification; an in-game power-plan guard; original-plan restoration; laptop vendor performance modes; Lenovo SmartFan and ASUS G-Helper integration; original vendor-mode restoration; handheld compatibility; keeping the system awake during play. |
| **Timers** | Global timer resolution; high-resolution timer requests during play; timer readback and post-restart verification; timer-state restoration. |
| **Memory and caches** | Turn Standby memory cleanup on or off in Environment. Open Parameters to choose Default, Conservative, Aggressive, or Custom and adjust the standby-list size threshold, available-memory threshold, and polling interval. Other features include manual deep cleanup, reclaiming Pavise's own memory during play, and a display of changes in available physical memory. |
| **Graphics and presentation** | NVIDIA maximum-performance preference and shader-cache size; disabling background game recording. |
| **System performance** | System enhancement groups VBS and memory integrity, configurable CPU software mitigations, and AC audio power management under one switch. Turning it on uses the extended CPU policy on supported systems and the basic policy where applicable. Upgrading preserves the previous scope. Enabling it reduces protection and can affect Hyper-V and WSL2. Restart Windows after applying or restoring these settings. Windows Update can be paused separately. |
| **Audio** | Keeping audio awake on AC power; original audio setting restoration. |
| **Performance laboratory** | EPP power yielding, maximum GPU clock requests (raise the clock floor on supported AMD GPUs or request a clock lock on NVIDIA), maximum power limits on supported AMD and NVIDIA discrete GPUs, CPU idle policy (original policy or higher idle threshold), and laboratory-state checks and restoration. |
| **Anti-cheat suppression and protection** | Per-game Anti-cheat suppression switch in the Library, off by default and turned on per game: installed ACE, EasyAntiCheat and BattlEye services are suppressed only during that game's matches and restored when the match ends; components bundled in game folders and Vanguard are only protected, other anti-cheat software is protected or recorded only; anti-cheat is never governed as ordinary background; game and launch-chain dependency protection. |
| **Whitelist and dependency protection** | Adding running processes, services, EXEs, or files by drag and drop; path and family protection; name search; scope inspection; removal; restoring platform and preset lists; protection for Windows system processes, gaming network accelerators, GPU drivers, hardware power and cooling tools, anti-cheat and game-protection components (marked protection only or record only), input/audio/peripheral driver chains, and streaming tools; viewing and clearing the process access restriction history. |
| **League of Legends and WeGame** | Dedicated LoL enhancements; auxiliary-process cleanup and restoration; immediate cleanup or restore; headless LoL client; lobby restoration; LoL add-on inspection and removal; overlay-cleanup notices; login and update dependency protection. |
| **System checkup** | Sampling; cancellation and rerun; findings, evidence levels, and action links; restoration records; one-click repair. |
| **Checkup: platform and CPU** | Platform capabilities; GPU inventory; NVIDIA interfaces; CPU Sets partitioning; EcoQoS; Windows version; 3D V-Cache driver; Windows Game Mode; CPU and background load; CPU topology; throttling events from the last seven days; top three background consumers; resident RGB software detection. |
| **Checkup: memory** | Capacity, usage, and remaining memory; page file; memory speed; module count and channel layout; memory compression; page combining. |
| **Checkup: display and graphics** | Primary and multiple-display refresh rates; NVIDIA throttle flags; ReBAR; HAGS; MPO; Game DVR. |
| **Checkup: power and system** | Current power plan; energy-saving mode; AC or battery status; core parking; input-idle demotion; Windows-version guidance for Ryzen branch-prediction improvements; foreground time-slice inspection and restoring records left by Pavise. |
| **Checkup: input chain** | Keyboard and mouse connection types, including Bluetooth; Filter Keys; accessibility hotkey interception; third-party ThreadDpcEnable changes. |
| **Checkup: storage and network** | DirectStorage and BypassIO; reasons passthrough is blocked; network links; default route; network throttling settings. |
| **Checkup: GPU interrupts and drivers** | GPU interrupt inspection; ISR/DPC placement and P99 sampling; interrupt placement; post-restart verification; rollback on regression; original GPU interrupt configuration restoration; separate GPU MSI repair and restoration; driver latency diagnosis; maximum duration, P99, and device clues. |
| **Checkup: repair and restoration** | Memory compression; network throttling; wired-network preference; Fault Tolerant Heap (FTH); separate repair and restore actions. |
| **General and maintenance** | Startup at sign-in; automatic hiding when a game begins; exit grace period (0, 2, 5, 8, 15, or 30 seconds); shader-cache cleanup; settings reset; restoration-record protection; automatic restoration on the next start after an abnormal exit. |
| **Window and appearance** | Adjust UI scale at the top of Appearance. Choose the language, light or dark theme, separate Smart and Competitive colors, and background intensity. Drag or minimize the window, or open, hide, or exit from the tray. |
| **5 interface animations** | Choose a static thumbnail for each mode in Appearance to apply it immediately, or restore the default. Choices include Original Smart, Original Esports, Original Extreme, Astral Whale, and Event Horizon. |
| **Logs and AI tuning** | Log-writing switch; runtime logs; action and diagnostic views; refresh and clear; copying selections; separate action and diagnostic exports; output folder and filename selection; scheduling role identification, actual CPU placement, priority verification, and restoration results; AI tuning prompt summarizing this PC's hardware, every feature and the current settings, with a goal field, preview and copy. |
| **Updates, about, and support** | Version information and release notes; website and source links; voluntary donations; in-app update checks; signature verification; download, verification, and installation progress; cancelling checks or downloads; rollback on failure; manual updates; GitHub and Quark alternatives; preserving personal data during upgrades; restore and uninstall. |

</details>

**Performance priority** and **Game thread boost** are two per-game switches in Library → Advanced tuning, on by default for every game; there is no global switch. Turn either off for a game that does not need it; the change applies next session.

- Performance priority: during matches the game process runs at High (an existing Realtime category is kept), disk I/O and GPU scheduling priority are raised, and high-performance QoS is locked so the game is not moved to efficiency cores when unfocused or covered. Original values are recorded and restored when it is turned off or the game ends; later changes by other programs are preserved.
- Game thread boost: once the render process is confirmed it samples for about 3.2 seconds to identify the frame-submitting thread and sets it to TIME_CRITICAL; a stable partner thread gets +1. It does not pin cores or wait for the foreground. After at most 5 unsuccessful captures per phase, a fallback raises the busiest thread two levels (not in Handheld). Boosted threads are checked every 5 seconds and returned if the game changes them back or they exit. Other apps such as voice, streaming, or recording may slow down when the CPU is saturated.

**Render thread affinity** in Library → Advanced tuning is a per-game switch, off by default, with a warning that results vary by game and hardware. It only decides which cores threads run on and never changes thread or process priority. The dialog offers three modes:

| Mode | What it does |
| --- | --- |
| **Automatic (recommended)** | Measures the game twice to find the serial threads that gate frame time (the render thread and the main or RHI threads it waits on) and points each at a preferred physical core (ideal processor only, not locked); other threads are not restricted and background apps stay off those cores. A second thread is reserved only when the two together need more than one core; a third only when the three need more than two cores on a CPU with at least ten physical cores. Each reservation must leave the remaining cores 10% headroom for the measured load of all other threads. If conditions are not met it reserves fewer or none. |
| **Single render core** | Pins the identified render thread to the most suitable physical core, with optional whole-core avoidance. It does not depend on load, so it also pins in GPU-bound games. |
| **Manual** | Choose one to four logical CPUs in the core grid; the render thread runs only on them, with optional whole-core avoidance. Choose again after changing the processor or the game's CPU range. |

Core choice prefers performance cores (hybrid CPUs) and the 3D V-Cache CCD (X3D), and avoids CPU 0, where system interrupts and DPCs tend to land. Whole-core avoidance only constrains covered game threads and leaves at least two physical cores for them; system tasks can still use those cores, so it is not system-wide exclusivity. New game threads are checked every 10 seconds, with a rescan 2 seconds later (at most twice in a row) when coverage is incomplete.

Strategies that only adjusted priority (Game priority, Render priority, Dependency coordination) are now handled by the two per-game switches and their saved rules run as off; old Dedicated rendering runs as Single render core, Multicore coordination as Automatic, and Enhanced dedicated rendering as Single render core with whole-core avoidance. Changes apply next session; turning it off revokes write eligibility at once and restores owned changes. Real frame-time gains must be judged by comparing 1% lows in a fixed scene, not inferred from setting readback.

Discovery budgets are per managed game session and rule phase: Single render core and Manual use at most 2 captures, Automatic at most 3, about 3.2 seconds each, and failed or exhausted captures are not refilled; consecutive matches in the same game process do not add captures. When a critical role exits, the original plan is restored first (including avoidance limits on other threads); once recovery settles and the phase still has capture budget, one replacement role may be reviewed.

## Background suppression: mode and intensity

**Usage mode and suppression intensity are separate settings.** Smart and Competitive determine when and how broadly background work is managed. The Background governance setting controls suppression intensity. Games, manually allowlisted applications, and essential dependencies stay protected.

| Mode | Behavior |
| --- | --- |
| **Smart** | Adjusts governance scope according to load while the game is in the foreground, expands it under sustained high load, and contracts it as load falls. Switching away releases background governance while retaining the session's power plan and affinity. |
| **Competitive** | Keeps governance and affinity active throughout the game session, including when you switch away. Bringing an ordinary application to the foreground does not automatically exempt it. |

| Background governance level | Behavior |
| --- | --- |
| **Off** | Disables background governance. |
| **Normal** | Lowers background CPU, disk I/O and memory priorities, enables efficiency mode and blocks temporary boosts. CPU usage per app is capped at the equivalent of two cores, up to a quarter of the machine. The disk limit is 50 MiB/s per app when Windows accepts it. |
| **Aggressive** | Caps each background app at 0.01% of total CPU capacity and confines it to a few cores. The disk limit is 1 MiB/s per app. Both levels retain the CPU cap, priority lock and very low I/O priority if Windows rejects the disk limit. |

Background governance leaves system services and system components in the Windows folder alone: session 0 processes that host services such as diagnostics and search indexing are not governed, and wide scope only adds apps with visible windows, never the Windows folder.

Anti-cheat software is never governed as ordinary background. Anti-cheat suppression in Library advanced tuning is off by default and turned on per game. Only during that game's matches, the ACE, EasyAntiCheat and BattlEye services it uses are suppressed, and only service processes installed in their standard locations and registered with Windows services qualify (ACE under Program Files\AntiCheatExpert, EasyAntiCheat under Program Files (x86)\EasyAntiCheat or EasyAntiCheat_EOS, BattlEye under Program Files (x86)\Common Files\BattlEye). Suppression uses the strongest level: lowest CPU priority, lowest disk I/O and memory priority, efficiency mode and no priority boost, moved to at most two background physical cores during matches and verified every second; no CPU caps, disk limits or freezing. Original settings are restored when the match ends, the game exits or the switch is turned off, and on the next start after an abnormal exit. Components bundled in game folders, unreadable paths and Vanguard are only protected. The WeGame main process is governed as ordinary background and is not exempt as the League of Legends launcher.

Clear core reclaims a normal game thread's record only after verifying its exit and the updated restoration ledger. New threads can use the released capacity without refilling failure budgets. Live threads, unknown identities, and records with pending restoration are retained. Capacity limits and scan intervals can still leave coverage gaps.

Game CPU selections and isolation remain drafts until Save is clicked. Their separate enable switch is in Advanced tuning. Leaving the editing page or switching games does not save the draft. Refreshes, theme changes, and scaling preserve it; a failed save keeps it, and Reload saved loads the stored configuration. Selecting all logical CPUs without isolation adds no extra game CPU-range constraint, avoiding the corresponding Job, guardian, and periodic audit. Existing game ranges and pending restoration obligations remain respected, including checks for render placement and clear core.

Automatic render placement and clear core stay within the game's allowed CPU range and avoid physical cores reserved by other features. With game core range disabled, Pavise adds no selection limit but still respects the game's existing process range. Ordinary strategy saves and CPU-range changes apply in the next session; explicit enable follows the discovery-phase rules above. Disabling immediately stops correction and restores owned changes. Pinning a thread is not whole-core exclusivity; system interrupts and protected processes remain under Windows control.

## Power

Smart and Competitive share power parameters. With managed power, laptops retain the previous policy and desktops use CPU-matched presets. If a high-performance template is missing, the original plan can be checked and copied as a candidate. Supported handhelds leave power management to Windows and vendor software.

While a game session owns a power plan, the guard keeps verifying the active plan and managed parameters and writes back external changes. Repeated external edits do not change the user's choice to No switching. Checks and writes are serial; repeated conflicts slow the response interval to at most 15 seconds, with rate-limited logs. Initial acquisition, plan creation, write failures, monitor-registration failures, and restoration retain finite retry budgets. Restoration preserves later external changes. Reselect the power plan if an older version changed it to No switching.

## 3.3.1.3 release notes

- Added an optional per-game setting for ACE, EasyAntiCheat and BattlEye suppression. It is off by default.
- Background limits now exclude system services and components in the Windows folder. Removed the service whitelist.
- Background limits now spare the 64-bit Steam overlay, Edge Game Assist, the NetEase UU launcher and Oopz.
- Peripheral protection now covers Flydigi and other brands and can identify drivers by device connection or custom keywords.
- Aggressive mode keeps the preferred core available for games and favors its CCD on dual-CCD processors.
- Background limits restore previously lowered GPU priorities and retain CPU caps and Idle priority if disk limits fail.
- Fixed helper processes in apps such as WeChat staying at low priority after a match.
- Pavise now restores settings on the next launch after a crash, without a separate guard process.
- Global timer resolution is now selected by testing the available settings on the PC, including on Windows 10.
- Removed one-click setup, cache warm-up and several graphics and system tuning options. Their existing settings are preserved.
- Old timer-tick and NTFS tweaks are restored automatically. Checkup can restore foreground scheduling changes made by Pavise.
- Match tracing and CPU core-type detection are now built into Pavise.exe.
- Renamed Tuning to Environment in the main navigation.
- Environment now groups system enhancements under one switch and includes standby memory cleanup and its settings.
- UI scale moved to the top of Appearance. Removed 12 animations and replaced live previews with thumbnails.

## 3.3.1.2 release notes

- New per-game switches in advanced tuning: Performance priority and Game thread boost, on by default.
- Render thread scheduling is now Render thread affinity: core placement only, with Automatic, Single render core and Manual modes.
- The separate anti-cheat engine is gone; ACE is governed as ordinary background and other anti-cheat software is only protected.
- Microsoft background services and the WeGame main process join ordinary background governance.
- The whitelist adds built-in voice chat apps and a service whitelist.
- The AI tuning prompt is back, and Multimedia scheduling is a separate switch again.
- Changes left by 1.x/2.x builds and by the old anti-cheat and Microsoft background governance are no longer restored.
- Fixed a case where exiting a game reported restoration as incomplete and the next game ignored the manual core range.
- Critical-thread measurement pre-filters events in a built-in native component and uses less CPU.
- Critical-thread identification and Game thread boost start after the game has run for two minutes; games unsuitable for reserved cores are not measured again for 7 days or until they update.
- Fixed the ACE main process (SGuard64) not being moved to background cores.
- Per-game family governance now keeps only the game itself; its launch chain, such as the launcher, is governed as ordinary background.
- Games already confirmed are taken over as soon as they start, without waiting for the loading screen.
- The core shows a "Motion paused" hint while its animation is paused during a match.
- Fixed the render thread affinity dialog flickering when clicking cores in Manual mode.
- Fixed the Automatic mode recommended badge not showing in the English UI at high scaling.
- Lower runtime overhead and better UI scaling.

## 3.3.1.1 release notes

- Fixed anti-cheat per-thread checks raising Pavise's own CPU use during matches: thread enumeration for checks and thread admission now uses a single system query, falls back to the previous method when the query is unavailable, and keeps the same decisions.
- A Microsoft background target that exits on its own after a scan is no longer logged as a failure or warning and uses no retry attempt; read failures keep their bounded retries.
- Clarified logs: thread-placement refusals no longer read "Unknown reason"; newly admitted anti-cheat threads are no longer logged as drift; an unused game CPU range states that it is off; sessions without any boost no longer read "Game priorities in effect".
- Identical renderer-probe results are no longer repeated line by line and are logged at once when they change; present-probe event loss now logs diagnostic readings in the warning, and the sample is still treated as invalid.
- The main navigation item Performance is renamed Tuning.
- Limit maintenance triggers during matches now runs automatically with background governance and stays off when governance is off; Multimedia scheduling runs automatically while protection is on. Neither has its own switch any more; how they apply and restore is unchanged.
- The Anti-cheat page has been removed from the bottom navigation. Anti-cheat suppression is now enabled per game, with its strength, in Library advanced tuning, supports only games with ACE anti-cheat, and applies only during that game's matches, restoring on exit. EasyAntiCheat and BattlEye are now protected and never suppressed. The anti-cheat protection scope and the process access restriction history moved to the Whitelist page. A previously enabled global ACE setting remains the default for games without their own setting.
- Microsoft background governance is now a one-time operation: limits are applied and read back once at match start, are no longer re-verified every cycle, and no longer start a separate guardian process per target (previously 5 to 7 per match at about 25 MB each, staying resident after the match). Pavise itself restores them after the match. If Pavise exits abnormally, the limits remain until the affected service or the PC restarts, and the next start logs a notice.
- Desktop compositor scheduling has been removed. Measurements showed it raised the compositor threads to real-time priority most of the time but dropped them below game threads for part of every period, which can delay composition under full CPU load; no benefit was established. The old switch is cleared automatically, and the request only lived as long as the Pavise process, so nothing is left in the system.

## 3.3.1.0 full release notes

These notes cover the complete update from the published 3.3.0.8 release to 3.3.1.0.

I never expected so many players to take an interest in Pavise and use it. Pavise is a game runtime resource orchestrator. Its core job is to coordinate resource use between games and background tasks during play; it isn't a traditional one-click optimizer. I started this project because I didn't want players who had already paid for their hardware to have to buy performance optimization software just to make better use of their own PCs. Those who can contribute code or share their experience can help improve it, and everyone else can use those contributions for free. Thank you for using Pavise, sharing it and sending feedback. I'll keep maintaining Pavise, keep it open source and free, and work on compatibility, stability and the gaming experience.

### New game-thread scheduling

- Introduced a complete per-game render-thread scheduling system with six strategies: Game priority, Render priority, Dedicated rendering, Multicore coordination, Dependency coordination, and Enhanced dedicated rendering. It is off by default for each game, with a notice that performance may drop or stutter and results need your own comparison. Game priority is the default without a saved valid strategy; existing selections are kept.
- Game priority raises game-process CPU priority and pins and prioritizes the identified render thread. Whole-process boosting is specific to this strategy and independent of the GPU/disk resource switch; an existing Realtime process class is preserved.
- Clear core can be enabled separately for the five strategies other than Dependency coordination, moving covered game threads away from critical physical cores. Placement respects the game's valid CPU range; incomplete coverage is not reported as exclusivity.
- Multicore coordination uses total thread load, observed capacity by core type, and task relationships to place critical roles and workers. Eligible threads in High-priority processes can be boosted, with each setting verified separately.
- Acquired thread settings are continually verified and corrected. Temporary read failures back off, while unknown identity or ranges pause writes. Critical-role exits restore the old plan before bounded re-evaluation using the remaining budget. Without reserved main/secondary cores, one ordinary boosted member exiting leaves the others active.

### Per-game settings and CPU ranges

- Game CPU ranges now require Save after selecting cores and isolation, with an option to restore the saved selection. Deep tuning retains a separate enable switch. Selecting all CPUs without isolation avoids extra constraints and audits while retaining existing ranges and restoration ownership.
- GPU and disk I/O resource priority move into the render-scheduling dialog and are saved with the strategy for the next session. Saving does not change the enable switch; disabling immediately restores owned changes. Game memory-page priority and high-performance GPU preselection are removed; existing Windows GPU preferences remain editable in system Graphics settings.
- Deep tuning is reorganized into Scheduling and priority / Other, with inline CPU-range, isolation, and strategy summaries. Help buttons are no longer covered by the row-wide toggle.
- Author and Extreme presets no longer change scheduling, core selection, or resource priority in game cards. Bulk presets and per-game choices are maintained separately.

### Background governance and anti-cheat

- Added built-in governance for verified Microsoft diagnostic services, search indexing, Widgets, and their WebView2 processes. Normal gives each built-in target a 0.01% Job CPU quota and at most two background physical cores. Aggressive uses the same quota, at most one core, and lower CPU/I/O priority. Manual allowlists and family protection remain effective.
- Simplified ordinary background CPU-range handling to reduce per-process guardian overhead. Background placement avoids verified critical game cores; when spare cores run out, owned affinity limits are released while resource governance remains. Suppression counts and restoration status are corrected.
- Protected verified AMD Noise Suppression components and restored earlier background restrictions from their records.
- Updated ACE suppression and ongoing correction of acquired settings for ACE, EAC, and BattlEye. Failures back off and logs are rate-limited; initial acquisition, new threads, and restoration retain bounded budgets.
- Fixed SGuard64 placement when Job assignment is refused by using direct affinity with recorded restoration ownership. Anti-cheat placement prefers cores outside the game range, including parked cores when needed; render placement leaves nonconflicting anti-cheat ranges unchanged.

### Game detection and capture

- Automatic session entry now requires stable rendering evidence. Adding a game to the library or bringing its window forward no longer starts a session by itself; per-game forced triggering remains available.
- Improved launcher-to-game handover so confirmed games are not replaced by launchers. Better browser, chat, and creative-app recognition reduces false library entries; entry source is diagnostic only.
- Fixed 0% GPU readings in some DX12 games by including Graphics/Compute rendering load in game detection.
- Improved channel coordination and event handling between render detection and thread-relationship capture. Original capture errors and unfinished-cleanup recovery records are retained.
- Bounded repeated League of Legends client credential lookups while preserving established sessions and restoration records when further lookup is unavailable.

### Session power and original-value restoration

- Added a session power-plan guard that continually verifies the active plan and managed parameters and corrects external changes. Persistent conflicts slow responses to at most a 15-second interval instead of switching the preference to No switching. Reselect a plan if an older version changed that preference.
- Improved independent recovery guardian startup under Job constraints and on Modern Standby devices by verifying WMI origin and cross-session identity without requiring the desktop shell in the same Job. Startup-failure retries remain bounded.
- Fixed game and background GPU priority restoration. Readback failures keep recovery records, and later external writes are preserved during restoration.
- Supported original NTFS QWORD values, preserving their type, full value, and later external changes during restoration.
- Fixed service-restoration waits, transitional start/stop states, and failure-budget handling. Unfinished restoration retains its records and ownership, and unverified states are not reported as successful.

### Interface and feature changes

- Combined performance and background settings, grouped controls by whether a PC restart is required, and simplified descriptions and status messages.
- Removed AI tuning suggestions and prompt generation. Removed the Steam/WeGame platform filter from the game library while retaining game search and platform scanning.
- Reworked several interface animations and added Original Extreme, bringing the selection to 17 while preserving saved choices.
- Updated the anti-cheat page with monochrome brand marks for ACE, Easy Anti-Cheat, and BattlEye.

### Logging and runtime overhead

- Consolidated repeated thread diagnostics while retaining state changes, field evidence, and restoration results. Fixed truncated paths/CPU ranges, false pending-timer failures, and successful corrections logged as warnings. Added failure stages and error codes for thread restoration, anti-cheat, NVIDIA, and independent recovery guardians.
- The scheduling dialog now shows and logs pre-placement queueing, CPU use, and migration readings from the latest session. These are for comparison only and remain until the app exits.
- Stopped continuous main-window redraws while unfocused in the background. Failed overview GPU queries retain backoff across pages, and confirmed launchers reduce repeated probes to 30, 60, and 120-second intervals.
- Reduced thread and background-range query access and verified process, thread, and GPU permissions separately. Service enumeration is skipped without anti-cheat targets. Unowned handles are released promptly, while acquired objects retain handles and ownership until restoration completes.

## Software architecture

The main application handles configuration, game detection, and session coordination. CPU, background, power, and system modules act when their conditions are met. After an abnormal exit, applied changes are restored from the restoration records on the next start. Configuration, restoration records, and logs are stored locally and separately.

## Game optimization execution flow

Detect the game → establish a session and configuration snapshot → apply eligible actions → verify and log → restore after play. Each feature follows its own switch and capability requirements.

Initial discovery, acquisition, write failures, and restoration retries have execution limits. Acquired fields and the in-game power-plan guard follow their ongoing verification rules; uncertain identity or legal ranges pause the affected writes. Once a recovery budget is exhausted, active attempts stop while restoration records and held resources are retained. Logs report the actual restoration state.

## Feedback and support

The chat groups are currently full. You can also find me on Douyin by searching for ID **44601770838** to report problems or share your experience.

Include **the Pavise version, Windows version, CPU and GPU, game name, relevant settings, and reproduction steps** in a report. Action logs show what the session did; diagnostic logs provide technical details.

For frame drops, stutter, or latency, compare the same scene with and without Pavise, and mention recent Windows, game, or driver updates.

[Report an issue](https://github.com/dulaiduwang003/Pavise-Game/issues) · [User guide](https://pavise.club/en/docs/v3/) · [Changelog](https://pavise.club/en/changelog/)

Created and maintained by **[bdth](https://github.com/dulaiduwang003)**. Pavise remains free, and donations are entirely voluntary. Bug reports and constructive discussion are welcome.

[Support development](https://pavise.club/en/support/) · [Author's profile](https://github.com/dulaiduwang003)

<p align="center">
  <img src="docs/wechat.png" width="220" alt="Optional WeChat donation QR code">
  <img src="docs/alipay.png" width="220" alt="Optional Alipay donation QR code">
</p>

Licensed under [GNU GPL v3 only](LICENSE) (`GPL-3.0-only`). See [NOTICE](NOTICE) for distribution and copyright information.
