<div align="center">

<a href="https://pavise.club/en/">
  <img src="docs/icon.png" width="88" height="88" alt="Pavise logo">
</a>

# PAVISE

**English** · [简体中文](README.zh-CN.md)

### Keep resources for your game.

**A free, open-source runtime resource orchestrator for Windows games.**<br>
Intelligent background suppression during play helps your existing hardware devote more of its performance to the current game.

**Source version 3.3.1.2 · Intelligent background suppression · Per-game configuration · Action and diagnostic logs**

[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0-d6b451?style=flat-square&labelColor=171a21)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases/latest)
[![GitHub downloads](https://img.shields.io/github/downloads/dulaiduwang003/Pavise-Game/total?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game/releases)
[![GitHub stars](https://img.shields.io/github/stars/dulaiduwang003/Pavise-Game?style=flat-square&labelColor=171a21&color=d6b451)](https://github.com/dulaiduwang003/Pavise-Game)

**[Website](https://pavise.club/en/) · [Download and updates](https://github.com/dulaiduwang003/Pavise-Game/releases/latest) · [User guide](https://pavise.club/en/docs/v3/) · [Report an issue](https://github.com/dulaiduwang003/Pavise-Game/issues)**

</div>

> This repository provides **Pavise source code, build instructions, and runnable regression tests**. The current source version is 3.3.1.2. Available downloads and published release notes are listed on the [releases page](https://github.com/dulaiduwang003/Pavise-Game/releases/latest).

## Why runtime resource orchestration?

Pavise is a free, open-source **runtime resource orchestrator for Windows games**. Built around intelligent background suppression during play, it coordinates CPU placement, critical threads, performance priorities, and power policies for the current game.

**Pavise focuses on how resources are allocated and used while a game is running.** Game-thread placement, CPU selection, power states, and competing workloads all shape the gaming experience. Per-game configuration, execution checks, and restoration after play give players control over runtime scheduling and help them get more from the hardware they already own.

Our goal is to make **professional Windows gaming resource management freely available to every player, for a smoother and more consistent gaming experience.** Players should not need expensive optimization software to make good use of their own hardware. That is why Pavise remains free and open source: its implementation can be read, its behavior can be checked, and its tools are available to everyone.

## At its core: intelligent background suppression

**Pavise reduces competition from background activity during play, leaving more of your hardware's existing performance available to the game.** Browsers, launchers, updaters, and other background applications share CPU, disk, and GPU resources with your game. Pavise manages eligible background workloads according to the selected mode and current load.

Once the guard is enabled and a game is detected, Pavise can, according to your settings:

- **Make background work yield:** lower eligible background CPU, disk I/O, memory-page, and GPU scheduling priorities and request efficiency mode. CPU placement can also move manageable background work away from game cores.
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
3. Select the features or configuration preset you want, choose Smart or Competitive mode and the background governance level, then enable the guard and start your game.
4. After leaving the game, check the restoration status. Export the session logs if troubleshooting is needed.

Before upgrading, exit the game and close the old version normally. In-app updates are under **About → Check for updates** and preserve your personal data.

## Build from source

Use the .NET Framework C# compiler and Windows PowerShell included with Windows. Visual Studio is not required.

```bat
build.cmd -b prod
```

The output is `build\Pavise.exe`. The build checks the implementation and version identity.

## Tests and benchmarks

This repository includes runnable regression tests for the current source, covering intelligent background governance, configuration reads, session and configuration identity, bounded retries, restoration ownership, timers, and security software detection. Run the published suites with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Run-PublicTests.ps1
```

Tests use in-memory settings, controlled interfaces, or their own processes. Test windows stay hidden, and the Pavise application entry point is not started. The independent CPU and memory benchmark also provides capability probes, measurement checks, and controlled background CPU-placement experiments. See the [test documentation](tests/README.md) for requirements, isolation boundaries, and measurement limits, and the [source guide](docs/SOURCE-GUIDE.md) for code entry points.

Development experiments on Microsoft background governance, Job limits, recovery, CPU topology, runtime overhead, and independent benchmarks are collected in [Experiments and validation](https://github.com/dulaiduwang003/Pavise-Game/blob/pavise3x/docs/experiments/README.md). Each topic includes methods, data, and test conditions. The linked records cover measurements from November 7–10, 2026 on the 3.3.0.8 development snapshot.

## Complete feature list

The following lists the features and sub-capabilities in the current 3.3.1.2 source. Availability depends on Windows, hardware, drivers, and the game. See the [user guide](https://pavise.club/en/docs/v3/) for instructions.

<details>
<summary>View the complete feature list</summary>

| Group | Features |
| --- | --- |
| **Guard and overview** | Guard switch; Smart and Competitive modes; configuration presets (Author's setup, Extreme, Custom); game and guard status; hardware and utilization monitoring; live CPU, GPU, and memory readings; session counters for suppressed processes and paused services; security software compatibility notice, details, and dismissal. |
| **Intelligent background suppression (core)** | Background governance; Off, Normal, and Aggressive levels; dynamic scope expansion under load; built-in Microsoft background targets (diagnostic services, search indexing, Widgets, and their WebView2 processes); Job CPU quotas; whole-core background limits; background CPU, disk I/O, memory-page, and GPU priority reduction; efficiency mode (EcoQoS); limits on dynamic background thread boosts; Delivery Optimization download yielding; automatic maintenance limits during play; game and essential dependency protection; releasing or retaining governance when switching away; restoration after play. |
| **Game library and detection** | Game executable detection; adding an EXE or a running game; selecting minimized or tray games; Steam and WeGame scanning; automatic library additions; ignore list and undo; search; renaming games; replacing or restoring covers; removing games. |
| **Per-game tuning** | Per-game configuration; forced optimization trigger; game-family background governance; disabling fullscreen optimizations; NVIDIA ReBAR; per-game anti-cheat suppression (only for games with ACE anti-cheat; Gentle, Balanced, or Strong); advanced tuning grouped into CPU and Other with saved values shown inline; game core range and isolation; a separate render-thread scheduling switch; six scheduling strategies; optional clear core; GPU and disk I/O resource priority in the scheduling dialog. |
| **CPU placement and isolation** | All-core, P-core, E-core, CCD, and cache-domain selection; manual logical processor selection; keeping one thread per core or including SMT siblings; whole-core background avoidance; draft CPU selections with Save and Reload saved; CPU-range restoration. |
| **Render threads and resource priority** | Game priority, Render priority, Dedicated rendering, Multicore coordination, Dependency coordination, and Enhanced dedicated rendering; per-game strategy selection; optional clear core for every strategy except Dependency coordination; bounded role discovery; verified placement and priority changes; ongoing correction and owned restoration; GPU and disk I/O priority saved with the strategy. Game memory-page priority has been removed. |
| **In-game power** | No-switch, managed, or existing power plans; managed power and dynamic parameters; separate AC and DC settings; CPU-topology matching; core parking, EPP, and boost coordination; plan and parameter verification; an in-game power-plan guard; original-plan restoration; laptop vendor performance modes; Lenovo SmartFan and ASUS G-Helper integration; original vendor-mode restoration; handheld compatibility; keeping the system awake during play. |
| **Timers and multimedia** | Timer tick configuration; global timer resolution; high-resolution timer requests during play; timer readback and post-restart verification; timer-state restoration; multimedia scheduling. |
| **Memory and caches** | Game-cache prewarming; standby-list cleanup; Default, Conservative, Aggressive, and Custom cleanup policies; list-size and available-memory thresholds; polling interval; manual deep cleanup; reclaiming Pavise's own memory during play; available physical-memory change display; larger NTFS cache pool. |
| **Graphics and presentation** | Hardware-accelerated GPU scheduling (HAGS); optimizations for windowed games; Windows and NVIDIA windowed VRR options; AMD Smart Access Memory; NVIDIA low-latency mode, maximum-performance preference, and shader-cache size; OpenGL driver threading; disabling Intel Endurance Gaming limits; disabling background game recording. |
| **System performance** | System performance bundle; VBS, Hypervisor, and memory integrity management; disabling configurable CPU software mitigations with basic or extended scope; disabling NTFS last-access updates; game protection-overhead settings (CFG, EAF, EAF+, IAF, CallerCheck, and 32-bit SimExec); game-volume short-name settings; Windows app background limits; pausing Windows Update; English input-layout switching for games and restoration afterward. |
| **Foreground time slices** | Original-setting restoration; short variable 3:1, 2:1, or 1:1; short fixed 1:1; long variable 3:1, 2:1, or 1:1; long fixed 1:1; applying or restoring without restarting; restart-status notice. |
| **Storage and audio** | Storage power-management bundle; keeping storage awake; disabling storage-controller runtime power management and D3Cold; keeping audio awake on AC power; original storage and audio setting restoration. |
| **Performance laboratory** | EPP power yielding; locking GPU clocks to the maximum; maximum GPU power limit; low-buffer audio stream; CPU idle policy (original policy, higher idle threshold, or idle disabled); laboratory-state checks and restoration. |
| **Anti-cheat controls** | ACE suppression enabled per game in Library advanced tuning and applied only during that game's matches; Gentle, Balanced, and Strong levels; hard affinity of ACE to auxiliary cores; original-setting restoration; EasyAntiCheat, BattlEye, and other anti-cheat software are protected, never suppressed; game and launch-chain dependency protection. |
| **Whitelist and dependency protection** | Adding running processes, services, EXEs, or files by drag and drop; path and family protection; name search; scope inspection; removal; restoring platform and preset lists; protection for Windows system processes, gaming network accelerators, GPU drivers, hardware power and cooling tools, anti-cheat and game-protection components (marked per-game suppression, protection only, or record only), input/audio/peripheral driver chains, and streaming tools; viewing and clearing the process access restriction history. |
| **League of Legends and WeGame** | Dedicated LoL enhancements; auxiliary-process cleanup and restoration; immediate cleanup or restore; WeGame UI-component handling; headless LoL client; lobby restoration; LoL add-on inspection and removal; overlay-cleanup notices; login and update dependency protection. |
| **System checkup** | Sampling; cancellation and rerun; findings, evidence levels, and action links; restoration records; one-click repair. |
| **Checkup: platform and CPU** | Platform capabilities; GPU inventory; NVIDIA interfaces; CPU Sets partitioning; EcoQoS; Windows version; 3D V-Cache driver; Windows Game Mode; CPU and background load; CPU topology; throttling events from the last seven days; top three background consumers; resident RGB software detection. |
| **Checkup: memory** | Capacity, usage, and remaining memory; page file; memory speed; module count and channel layout; memory compression; page combining. |
| **Checkup: display and graphics** | Primary and multiple-display refresh rates; NVIDIA throttle flags; ReBAR; HAGS; MPO; Game DVR. |
| **Checkup: power and system** | Current power plan; energy-saving mode; AC or battery status; core parking; input-idle demotion; Windows-version guidance for Ryzen branch-prediction improvements; foreground time-slice inspection and settings link. |
| **Checkup: input chain** | Keyboard and mouse connection types, including Bluetooth; Filter Keys; accessibility hotkey interception; third-party ThreadDpcEnable changes. |
| **Checkup: storage and network** | DirectStorage and BypassIO; reasons passthrough is blocked; network links; default route; network throttling settings. |
| **Checkup: GPU interrupts and drivers** | GPU interrupt inspection; ISR/DPC placement and P99 sampling; interrupt placement; post-restart verification; rollback on regression; original GPU interrupt configuration restoration; separate GPU MSI repair and restoration; driver latency diagnosis; maximum duration, P99, and device clues. |
| **Checkup: repair and restoration** | Memory compression; network throttling; wired-network preference; Fault Tolerant Heap (FTH); separate repair and restore actions. |
| **General and maintenance** | Startup at sign-in; automatic hiding when a game begins; exit grace period (0, 2, 5, 8, 15, or 30 seconds); shader-cache cleanup; settings reset; restoration-record protection; independent recovery guard after an abnormal exit. |
| **Window and appearance** | Language selection; light and dark themes; separate Smart and Competitive colors; background intensity; visual customization; separate mode effects; immediate effect application or defaults; window dragging and minimizing; tray open, hide, and exit. |
| **17 interface animations** | Original Smart, Solar Eclipse, Plasma Heart, Warp Gate, Firebird, Abyssal Jellyfish, Quantum Binary, Astral Whale, Starship Engine, Singularity Bloom, Photon Cube, Event Horizon, Supernova, Celestial Orrery, Chronoglass, Original Esports, and Original Extreme. |
| **Logs** | Log-writing switch; runtime logs; action and diagnostic views; refresh and clear; copying selections; separate action and diagnostic exports; output folder and filename selection; scheduling role identification, actual CPU placement, priority verification, and restoration results. |
| **Updates, about, and support** | Version information and release notes; website and source links; voluntary donations; in-app update checks; signature verification; download, verification, and installation progress; cancelling checks or downloads; rollback on failure; manual updates; GitHub and Quark alternatives; preserving personal data during upgrades; restore and uninstall. |

</details>

Open a game's Advanced tuning page in the library to configure render-thread scheduling. The first-level switch is off by default for each game. Enabling it shows a notice that results vary by game and hardware, and performance may drop or stutter; compare the same scene yourself. On first enable, Game priority is used if no valid strategy has been saved; existing valid selections are retained. The dialog offers six strategies, with optional clear core and GPU or disk I/O priority:

| Strategy | Potential use |
| --- | --- |
| Game priority | The game competes with other applications for CPU time, and both the process and render thread need higher scheduling priority. |
| Render priority | A ready render thread queues behind competing CPU work, affecting response. |
| Dedicated rendering | Frequent render-thread migration adds cache-rebuilding overhead and frame-time variation. |
| Multicore coordination | Critical tasks compete on the same core while other busy work needs room to run in parallel. |
| Dependency coordination | Rendering waits for related tasks; boosting rendering alone does not shorten that wait. |
| Enhanced dedicated rendering | A fixed render core still faces ordinary-thread contention and response-sensitive scheduling delays. |

These are possible use cases, rather than detected bottlenecks or promised gains. Strategy and resource settings are saved together for the next game session without changing the switch. Disabling the switch immediately withdraws write authorization and restores owned changes. An explicit enable can start during the current session only if this target and strategy have not already had an enabled discovery phase; disabling and re-enabling the same phase keeps its spent budget and waits for the next session. Retired comparison rules stay off until a formal strategy is saved and explicitly enabled. Identity or range uncertainty pauses the affected writes. Resource priority requires render-thread scheduling and covers GPU and disk I/O only; game memory-page priority is no longer adjusted or restored. Read-only scheduling diagnostics can still record its original state.

Game priority combines three actions: set the game process to High, pin the identified render thread to an automatically chosen CPU, and raise that thread's priority. An existing Realtime process category is retained. Process priority can apply before render-thread discovery; if no suitable CPU can be selected, pinning is skipped while eligible priority changes can still apply. Protected games receive only the actions that can be verified. Only this strategy raises the whole game's CPU priority, independently of the GPU and disk I/O resource switches. Its original category is recorded separately and restored when disabled, at game exit, or when moving to another strategy, while preserving later external changes. Voice chat, streaming, recording, and other applications may slow down when the CPU is saturated.

Enhanced dedicated rendering gives a confirmed presentation thread stronger priority on a fixed CPU without changing the process category. It requires verified topology, legal range and binding, plus room for Pavise's control thread to run outside the target CPU. With clear core enabled, the latest thread snapshot must also have complete coverage. If coverage or operating conditions fail, Pavise withdraws its added priority; recovery uses the same session and remaining budget. An exhausted initial boost budget stops further acquisition, restoring priority before releasing the binding. Existing High or Realtime processes and threads with special priorities are not newly acquired by this strategy. Clear core affects covered game threads; system tasks may still use the CPU.

Dependency coordination prioritizes a small, stable set of tasks related to presentation. It keeps existing CPU ranges, does not pin threads, and does not use clear core. Independent observations must confirm the same presentation identity and associated members; high CPU use alone is insufficient. It does not restrict selection by CPU vendor, core type, SMT, core count, or processor groups. Unverifiable identity or priority permissions prevent writes, and an incomplete group is rolled back. Once acquired, only those threads' priorities are maintained. Disabling, game exit, or role exit restores owned changes. A bounded recheck after a member exits must retain the same presentation identity and reconfirm membership with fresh evidence; dependency clues do not establish the complete frame-critical path or coverage of every busy thread.

Discovery budgets are per managed game session and rule phase. Game priority, Render priority, Dedicated rendering, and Enhanced dedicated rendering allow at most two full captures; Multicore coordination and Dependency coordination allow three. Each capture is about 3.2 seconds. Consecutive matches in the same game process do not refill these budgets. When a critical role exits, the old plan is restored first, including clear-core limits on other threads. After restoration is settled, remaining capture budget permits at most one replacement-role recheck with fresh identity and original values. An exhausted budget or previously used recheck stops new acquisition. Unfinished restoration retains records, handles, and guardians and prevents new observation or acquisition. Role changes do not refill initial-acquisition, new-thread, or failure budgets, and a surviving thread changing duties does not automatically trigger reselection.

## Background suppression: mode and intensity

**Usage mode and suppression intensity are separate settings.** Smart and Competitive determine when and how broadly background work is managed. The Background governance setting controls suppression intensity. Games, manually allowlisted applications, and essential dependencies stay protected.

| Mode | Behavior |
| --- | --- |
| **Smart** | Adjusts governance scope according to load while the game is in the foreground, expands it under sustained high load, and contracts it as load falls. Switching away releases background governance while retaining the session's power plan and affinity. |
| **Competitive** | Keeps governance and affinity active throughout the game session, including when you switch away. Bringing an ordinary application to the foreground does not automatically exempt it. |

| Background governance level | Behavior |
| --- | --- |
| **Off** | Disables background governance, including the built-in Microsoft targets. |
| **Normal** | Lowers eligible background resource priorities and enables efficiency mode. Each built-in Microsoft target uses the minimum nonzero Windows Job CPU quota of 0.01% (`CpuRate=1`) and at most two background physical cores. |
| **Aggressive** | Also limits temporary background thread boosts. Built-in targets use the same minimum nonzero CPU quota of 0.01% and at most one background physical core, with lower CPU and I/O priorities. |

The built-in list covers DiagTrack, DPS, WdiServiceHost, WdiSystemHost, diagsvc, WSearch, verified indexing workers, Widgets, and their WebView2 processes. These targets are governed even at low utilization. Preset system allowlists do not exempt them; manual process allowlists and family protection still apply. Service allowlists exempt only ordinary background control, including the whole shared host, and do not affect Microsoft background governance. A shared service host is admitted only when every running service it hosts is on the list. Only running services are considered, and original settings are restored after play.

For ACE suppression, acquired resource fields and affinity are continually verified and corrected without a cumulative drift or maintenance-failure limit. Failures back off to at most 30 seconds with rate-limited logs. Unknown identity, original values, or legal ranges pause writes. Initial acquisition, new threads, and restoration retain bounded retries. Disabling, changing intensity, or ending a session revokes old write authorization before restoring owned values, preserving later external changes.

Clear core reclaims a normal game thread's record only after verifying its exit and the updated restoration ledger. New threads can use the released capacity without refilling failure budgets. Live threads, unknown identities, and records with pending restoration are retained. Capacity limits and scan intervals can still leave coverage gaps.

Game CPU selections and isolation remain drafts until Save is clicked. Their separate enable switch is in Advanced tuning. Leaving the editing page or switching games does not save the draft. Refreshes, theme changes, and scaling preserve it; a failed save keeps it, and Reload saved loads the stored configuration. Selecting all logical CPUs without isolation adds no extra game CPU-range constraint, avoiding the corresponding Job, guardian, and periodic audit. Existing game ranges and pending restoration obligations remain respected, including checks for render placement and clear core.

Automatic render placement and clear core stay within the game's allowed CPU range and avoid physical cores reserved by other features. With game core range disabled, Pavise adds no selection limit but still respects the game's existing process range. Ordinary strategy saves and CPU-range changes apply in the next session; explicit enable follows the discovery-phase rules above. Disabling immediately stops correction and restores owned changes. Pinning a thread is not whole-core exclusivity; system interrupts and protected processes remain under Windows control.

## Configuration presets and power

Author's setup and Extreme are batches of feature settings, selected separately from Smart or Competitive mode. Both presets set **CPU idle policy to the original policy and foreground time slices to restore the original configuration**. Upgrading preserves existing choices; the new preset definitions apply when you reapply a preset.

Persistent preset settings such as VBS and CPU software mitigations change the corresponding system protections. Follow each feature's instructions and restart notices.

Smart and Competitive share power parameters. With managed power, laptops retain the previous policy and desktops use CPU-matched presets. If a high-performance template is missing, the original plan can be checked and copied as a candidate. Supported handhelds leave power management to Windows and vendor software.

While a game session owns a power plan, the guard keeps verifying the active plan and managed parameters and writes back external changes. Repeated external edits do not change the user's choice to No switching. Checks and writes are serial; repeated conflicts slow the response interval to at most 15 seconds, with rate-limited logs. Initial acquisition, plan creation, write failures, monitor-registration failures, and restoration retain finite retry budgets. Restoration preserves later external changes. Reselect the power plan if an older version changed it to No switching.

## 3.3.1.2 release notes

- Reduced runtime overhead and improved UI scaling; added service whitelisting for ordinary background control only.

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

The main application handles configuration, game detection, and session coordination. CPU, background, power, and system modules act when their conditions are met. An independent recovery guard handles required restoration after abnormal exits. Configuration, restoration records, and logs are stored locally and separately.

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
