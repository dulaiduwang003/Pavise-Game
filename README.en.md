<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Pavise">

# Pavise

Windows game resource scheduling and guard tool

`C#` · `WinForms` · `Interface: Chinese / English`

[简体中文](README.md) · **English** · [日本語](README.ja.md)

**v2.2.1.2 · [Release notes (Chinese)](docs/releases/v2.2.1.2.md)**

<br>

<img src="docs/screenshots/en/overview.png" width="100%" alt="Pavise overview">

</div>

## Performance

Pavise does not generate additional performance. It returns the share taken by background processes to the game. Machines with many background programs and noticeable CPU or disk contention benefit the most. A clean system, or a game entirely limited by the GPU, will see little change.

Suppression strength does not correlate with performance. The 1.x-era old Extreme mode (isolate everything) scored only 71 in the heavy-load scenario, below the then-Competitive's 93, and that is why it was removed. Today's Extreme tier shares only the name: its suppression scope and strength are identical to Esports, and the difference is solely how many optional optimizations are enabled. The default preset is the mildest tier.

The existing four-tier bench figures date from v1.7.1: an i7-9750H laptop with WeChat, Clash, QQ and a music player in the background, scored against a clean system as 100, averaging 59 without Pavise, 60 in Normal and 96 in Competitive (Normal and Competitive are today's Smart and Esports). The 2.x scheduling model differs from 1.7, so treat those numbers as an order-of-magnitude reference; they have not been re-run on 2.x.

Measure the effect by toggling it on and off in the same game and the same scene.

## Usage

Requires Windows 10 2004 (build 19041) or newer; Windows 11 24H2 is preferred. On earlier builds the efficiency mode and per-process timer isolation that background suppression relies on do not exist, so scheduling would have no effect: Pavise reports this and exits, restoring any system changes left by earlier versions.

Add a game's EXE or shortcut to the library, or use the scan function to import games already installed through Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net or Xbox.

Protection applies for as long as the game runs; switching to the desktop or minimising does not end it. Games started through a launcher (League of Legends, for example) have their real executable remembered after the first confirmation and are recognised directly afterwards. Launchers, updaters, crash reporters and anti-cheat processes are not identified as games.

Session changes are restored from their records when the game exits. After an abnormal exit, the next launch retries recovery and retains records of failures. Application GPU preferences, per-EXE compatibility settings and persistent system settings require separate restoration; input-language changes are not rolled back. Purged caches and manually deleted League add-ons cannot be recovered by restoring settings; the client may download add-ons again during an update or repair.

## Modes

| Mode | Suppression scope |
|---|---|
| Smart | Every background process that clears the protection boundary is isolated outright the moment the match starts, with no heat check and no tier-by-tier escalation. Whatever you are using, and its family, is exempt from suppression; under sustained CPU saturation (above 90% for over ten seconds) it temporarily escalates to the Esports profile, stepping back down after two stable minutes, at most three times per match |
| Esports | Widens suppression to non-game processes that pass the protection boundary, including windowed apps and apps used after alt-tab. The whitelist and built-in protection rules still apply |
| Extreme | Starts from Esports and enables eligible, non-excluded items in the Extreme catalogue, including some persistent settings. Hidden until unlocked in Settings and the computer is restarted. Suppression scope and strength match Esports; it does not enable every optional feature |
| Handheld | Background suppression identical to Esports, with the power side left to vendor tools: no power slider, and pure power-saving items stay enabled on AC. Requires a battery; for handhelds and thin-and-light laptops |
| Custom | Background suppression, cores, memory and power, system environment and graphics, each chosen individually |

The tiers mainly differ in process eligibility and additional policies. Eligible ordinary background processes are isolated directly using Idle CPU priority, very low disk I/O priority, low paging priority, EcoQoS and a timer-resolution cap. GPU yielding also lowers GPU scheduling priority. Windows dynamic priority boosts are preserved so a background process the game is waiting on can finish its work; these boosts are distinct from processor turbo, which background suppression does not directly disable.

This is the opposite of the 1.9-era approach, because the premise changed. Isolating everything really was a net loss back then: the scattered wake-ups of a hundred idle processes were squeezed onto two cores by affinity narrowing and queued behind each other, tripling the longest frame by 2.6×. From 2.0 on, background affinity is left alone by default and the entire narrowing-and-migration mechanism is gone; since 2.2 a heat-gated core squeeze is back as an experimental item, acting only on background processes that keep using CPU, while idle ones are still never touched. A cold process with no ready threads costs no CPU to begin with, and when it does wake it can run on any core without higher-priority work, so nothing queues and the cost of isolating everything disappears.

Ordinary background suppression always excludes anti-cheat, Windows core services, network accelerators, the input/audio/peripheral chain, hardware tools and other signed-in accounts. Separate, default-off switches on the Anti-Cheat page can suppress selected user-mode anti-cheat processes; they do not control kernel drivers.

Game family exemption is on by default: game platforms, launcher shells, resident processes inside the game folder and child processes spawned by the game are all released as a family. Turning it off releases only the game itself and the whitelist; everything else is suppressed as ordinary background.

## Per-game configuration

Select a game in the library and open its configuration. The current policy catalogue contains 34 per-game overrides across modes, background suppression, cores, memory and power, environment and graphics, including the mode, family policy and core mask. Unset overrides follow global settings, and changes save immediately. Per-EXE fullscreen-optimization and DPI compatibility settings are separate from these 34 items and are not automatically restored at match exit.

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

- The overview shows the active mode, whether it comes from the global setting or a specific game's configuration, and the last session's report
- **Session report**: play time, the number of suppressed processes and their CPU usage, the share of time spent power- or thermal-limited, and the peak shared video memory the game spilled into system RAM
- **Feature search**: type a keyword at the top to jump straight to a switch instead of remembering which page it lives on
- **Auto-hide**: the window collapses to the tray ten seconds after a game is detected, once per session
- **Interface**: light and dark themes, instant Chinese/English switching, and newly written logs following the selected language. Japanese is a documentation translation; the current app has no Japanese interface

### Library

- Add an EXE, a shortcut or the game's folder manually, or drop them onto the window; when a folder holds several candidate programs they are listed for you to pick. Scanning imports from Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net and Xbox
- **Forced takeover**: for emulators, cloud gaming and anything else that cannot be recognised, the match starts as soon as the process does
- **Auto-add**: newly recognised games are collected automatically. A path you removed goes on an ignore list and is never auto-added again until you add it back by hand
- **Suspected malware alert**: a process with no visible window outside the Windows and Program Files directories that uses a quarter or more of the logical CPUs for two minutes straight, or a suppressed process that lifts its own background core restriction, is logged and raised as a tray warning about a possible miner infection. Random-looking names use half the threshold; each name is alerted at most once a day
- **Render observation label**: records GPU 3D activity observed on an EXE. This alone does not identify the main game renderer or establish that related processes can safely be suppressed
- **Family background suppression**: per-game, off by default. While off, game platforms, launcher shells, resident processes in the game folder and child processes spawned by the game are released as a family
- **Per-game configuration**: 34 items in the current policy catalogue; unset overrides follow global settings, and entries can be renamed
- **WeGame shell removal**: games launched through WeGame (CrossFire, Assault Fire, Delta Force and the like) get the same shell-removal strip under their card as League of Legends. With shell launch on, WeGame, Cross and Tencent add-on processes are ended precisely 30 seconds into a match; if the game exits within 20 seconds, automatic removal is disabled for that game on this machine; if the shell keeps respawning, removal pauses for the match. Per-game switch, off by default
- **Voice is never suppressed**: processes with an active microphone capture session are detected through audio sessions and left alone during play in every tier, which is also what lets QQ and WeChat through while on a call (they are suppressed as usual otherwise); KOOK, YY, TeamSpeak, Mumble, Oopz and controller mappers such as DS4Windows and reWASD are also exempt by name. The Smart tier's adaptive escalation is now a separate switch, off by default

### Processes and cores

- **Game process boost**: the renderer receives high priority and higher disk I/O, memory-page and GPU scheduling priority, restored on exit
- **Candidate thread boost**: on by default and forced on in Extreme; in other tiers a game that gets slower can turn it off per game. CPU time identifies a busy candidate, not a frame-critical thread; a wrong choice can slow the game. Sustained CPU saturation withdraws the candidate boost and returns the game process to normal priority, regardless of candidate state
- **Smart yield**: sustained CPU saturation for ten seconds returns the game to normal priority and withdraws candidate thread boost, even if a candidate was already boosted. This is a risk control, not a universal FPS improvement
- **In-match self-yield**: Pavise moves off the game cores and lowers its own scheduling weight
- **Background suppression**: directly isolates eligible ordinary background processes through CPU, I/O and paging priority, EcoQoS and timer-resolution policy, while preserving Windows dynamic priority boosts
- **Background GPU priority demotion**: a background process using the GPU also has its GPU scheduling priority lowered
- **Heavy background core squeeze** (experimental): during a match, background processes that use more than half a core for 10 seconds straight are confined to the fewest cores the game does not use, avoiding the game's L3 block on multi-CCD parts and landing on efficiency cores on hybrid CPUs; idle background is left alone, the limit lifts 30 seconds after load drops, and everything is restored on exit. Off by default; not offered below 5 physical cores
- **Suppressed working-set trim** (Extreme only): enabled for Extreme sessions unless excluded in Extreme management. Attempts trimming only when available memory is both below 4 GiB and below one eighth of total memory. Subsequent page faults and slower first responses remain possible. Anti-cheat processes are excluded
- **Wider background suppression**: covers eligible non-game background processes even after alt-tab. Forced on in Esports, Extreme and Handheld; built-in protection and the whitelist still apply
- **Game core partitioning**: selects the cores available to the game, with support for hybrid CPUs, X3D and multiple processor groups. It neither confines all background processes to the remaining cores nor guarantees exclusivity. Background affinity changes require a separately enabled, eligible heavy-background or anti-cheat policy
- **Partition swap**: X3D machines can switch to the large-cache CCD
- **Manual core selection**: draw it per core, with presets for all cores, no SMT, P-cores only and inverse. Written on Apply
- **Whitelist**: drag an EXE in, scope determined automatically, never suppressed in any mode

### Anti-cheat coverage

Nine anti-cheat systems are listed individually, each stating which games it protects and which of its components are kernel drivers that must not be touched: ACE (Tencent), TenProtect, Vanguard (Riot), EasyAntiCheat (Epic), BattlEye, EA Javelin, nProtect GameGuard, FACEIT and NEAC (NetEase).

Chinese arenas are listed separately: Perfect World Arena, 5E and B5. Their anti-cheat lives inside the platform client, so Pavise protects those processes and never suppresses them. NetEase NEAC gains NeacClient and OWNeacClient, Tencent TP gains TP3Helper; HoYoverse ships kernel drivers only, which are recognised for log messages.

Three additional **protection-only** groups cover PunkBuster, Nexon Game Security (including BlackCipher), and Wellbia XIGNCODE3 / UNCHEATER. Names, name prefixes and dedicated directories exempt their processes from background suppression, including `.aes` components and helpers inside those directories. These groups have no suppression switches. Turning off the anti-cheat master switch does not disable background exemptions. [Recognition rules, sources and coverage limits (Chinese)](docs/anti-cheat-coverage.md).

The **compatibility list** records games that refuse writes, so priority and I/O writes certain to fail are not retried while GPU scheduling priority and the frame thread are still attempted. The list expires when the game updates.

**Anti-cheat suppression** (per-group switches on the Anti-Cheat page, off by default) offers no intensity choice; the profile is fixed and scan-safe: below-normal CPU priority, very low disk I/O (disk scanning is the main source of harm), efficiency-core capping, and confinement to the fewest cores the game does not use (the last physical core on 6- to 8-core machines, efficiency cores on hybrid CPUs, the other L3 block on multi-CCD parts; writes refused by the anti-cheat's own protection make Pavise drop the whole suppression for that run without retrying). It never drops the scanner to the lowest CPU priority, seals its timer resolution, or lowers its memory page priority — game threads can be suspended by the system during a scan and resume only when the scanner finishes, so starving it of CPU time only stretches a brief stutter into a multi-second freeze.

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

- **Match interrupt observation**: kernel ETW collects per-device DPC activity, accounted per match, identifying devices contending with the game's cores
- **Manual pinning**: pick the device and the target cores. The cost is spelled out before writing, and every device keeps a receipt for reverting
- **GPU interrupt affinity**: moved to cores near the render thread
- The page flags cases that are not worth touching: pinning a multi-message MSI-X device can reduce parallelism, and StorPort DPCs follow the CPU that issued the I/O, so pinning them usually does nothing

### Memory and power

- **Managed power plan**: created at the first match and configured for the processor. Selecting an existing plan only switches plans; explicitly enabling Disable CPU idle can still temporarily change that plan's idle settings. Windows active-plan notifications trigger reapplication if another app switches plans, with no periodic fallback after success. This is not an access-control lock against other writers
- **Storage kept awake in-match**: the managed plan zeroes the NVMe power-state latency tolerances and keeps the AHCI link Active, preventing the occasional hitch of an SSD waking from a low-power state; the NVMe side is relaxed on battery
- **Disable CPU idle**: only during a game; writes both the AC and DC values of the currently active power plan and restores them at match end. On battery it noticeably shortens battery life; not offered on AMD processors
- **Standby memory cleanup**: the entire standby list is purged only when both the list-size and true-free-memory thresholds are crossed. Technical detail below
- **MMCSS multimedia scheduling**: the share reserved for non-multimedia work drops from 20% to 10%, the Games task's scheduling category and file I/O are raised, and the lazy idle-check tier is disabled
- **Low-latency DWM composition** (Extreme only): requests MMCSS scheduling for DWM during play and can be excluded in Extreme management. Benefits depend on the presentation path and workload; this does not guarantee uninterrupted composition
- **Pause Windows Update, Delivery Optimization, nonessential services and automatic maintenance**, restoring owned changes afterwards. Service pausing uses a fixed allowlist. There is no current wireless-scan suppression control
- **Vendor performance mode during play**: switches Lenovo Legion and ASUS ROG laptops to their vendor performance profile through the vendor interface and switches back at match end; off by default, turn it on if you want it, and it is not forced by the Esports or Extreme tier
- **Per-game DPI awareness**: with display scaling above 100% the borderless window is sized in physical pixels instead of being stretched by the compositor
- **Turn off Game DVR and Xbox background recording**, and **keep the display awake during a match**
- **Low-latency audio** (Extreme only): attempts to open a silent stream with the device's minimum supported shared buffer, requesting a shorter engine period. Enabled for Extreme sessions unless excluded in Extreme management; the stream closes on exit
- **Power budget yield**: on laptops, hands shared power budget to the GPU when it is pinned against its limit and the CPU has headroom, reverting automatically when verification fails. Off by default; verification detail below

### System environment

Settings on this page persist and can be restored. Some require a restart; follow the instructions for each item:

- **Hardware-accelerated GPU scheduling (HAGS)**, **AMD Smart Access Memory**, **disable VBS**, **remove speculative-execution mitigations**
- **Game mode guard**, **windowed game optimisation**, **variable refresh rate optimisation** (lets DX11 exclusive-fullscreen games without native VRR use it)
- **Constant timer tick**, **global timer resolution**
- **Disable NIC power saving**, **disable NIC link power saving** (turns off 802.3az low-power idle, Green Ethernet and idle link-speed reduction so the link never sleeps, wakes or renegotiates between 1G and 100M; the adapter drops for a few seconds when written), **NIC interrupt-moderation experiment** (unchanged by default; explicit Off testing only on the single physical wired adapter selected by the public-IPv4 route probe, restored only after both NetCfg GUID and PnP instance identity match)
- **Accessibility key interception**, **disable keyboard and mouse selective suspend**
- **Disable memory compression and page combining** (Extreme tier only): removes the background CPU cost of the compression thread and page-combining scans; offered from 24GB of RAM

### System audit

A read-only check generates findings according to available hardware and samples, rather than a fixed count. Each finding states its evidence level: measured locally, bench-tested, mechanism clear, or unverified. Manual repairs cover abnormal time-slice settings, multimedia network throttling, input queues, wired-route metrics, FTH shims and explicitly disabled GPU MSI. GPU power/thermal limits and DPC/ISR sources are also reported.

The Settings page offers a full configuration wipe that reverts every persistent change Pavise ever made, including those left by retired features. The Uninstall Pavise button next to it does everything inside the app: it stops the runtime, restores every system change from its receipts, then removes the startup task, the managed power plan, settings, the data folder and leftovers from older versions, so the machine ends up as if Pavise had never been installed. Delete Pavise.exe itself afterward.

A local tool: no installed service, no uploaded machine data, no game-process injection and no game-memory edits. General scheduling does not modify game files; the League extension separately offers add-on directory deletion after user confirmation. Writes are read back where possible, and unexpected values are not counted as success.

### League of Legends enhancement

The current build includes an extension card for recognised League entries:

- **Shell launch / Clean now**: lets the client complete normal login, then exits identified WeGame, Cross and add-on processes after the LCU confirms the session is ready. Repeated respawning stops automatic cleanup for that match
- **Headless match / Restore UI**: closes the lobby UI through the client API and restores it after play. It records a recovery lease and confirms a separate recovery guard before closing the UI
- **Delete add-ons**: after the client exits and checks pass, manual confirmation deletes identified CN-client add-on directories such as AI Coach and iCreate. No backup is retained; Pavise cannot undo deletion, and the client may re-download them during update or repair

## How power budget yield verifies itself

This feature changes the processor energy performance preference, which affects whole-machine power distribution, so it verifies more thoroughly than the others.

Twenty seconds of observation to decide whether to yield, then fifteen more to verify after engaging. A failed verification reverts immediately and stops automatic attempts until the user re-enables it.

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

### Why only this one

The table below measures the system-level commands tools in this category use. Test machine: 64 GB RAM, 40344 MB available and 16154 MB system cache before measurement.

| Command | Duration | Available memory | System cache |
|---|---|---|---|
| Empty all process working sets | 1894.9 ms | +5191 MB | +3364 MB |
| Purge standby list (low priority) | 13.3 ms | +144 MB | −47 MB |
| Purge standby list (all) | 1380.6 ms | +382 MB | −18894 MB |
| Flush modified page list | 8019.7 ms | +5845 MB | +251 MB |
| Combine physical memory pages | 6152.1 ms | +2121 MB | +531 MB |

Purging the entire standby list discards 18894 MB of system cache to gain 382 MB of available memory. The standby list already counts as available memory, so purging it converts cached available memory into empty available memory: the total barely moves, the cached content is gone, and the game load that follows reads from disk again. Flushing the modified page list takes 8 seconds and combining physical pages 6.2 seconds, neither of which belongs on the path into a match.

These are measurements of the old implementation, not performance conclusions for the current policy. For background, see Mark Russinovich, "The Memory-Optimization Hoax" (Windows and .NET Magazine, January 2004).

## Historical features and research boundaries

The entries below describe retired implementations, not necessarily the status of similarly named features today. Current availability follows the feature list above; recovery code is not an activation path. Some historical changes are restored at startup, while others are reverted from their records through Clear all settings.

**Low-priority standby cleanup under memory pressure** (retired 2026-08-20)

The trigger used the available-memory ratio, and available memory already counts standby pages. Purging only moves pages from the standby list to the free list, so the ratio does not change, the condition never clears, and the 45-second cooldown merely set a tempo for repeat triggers. User logs show 14 triggers in 10 minutes at exactly 45-second intervals.

The effect did not hold up either: `MemoryPurgeLowPriorityStandbyList` only clears priority 0, which totals 1.4 MB on a 24 GB machine, releasing 0.0 to 0.1 MB per call. What it saves is a few hundred nanoseconds of page-reclaim work in the memory manager, which is noise against a frame's budget.

**Retired implementation: reclaiming background working sets after the match settles**

Calling `SetProcessWorkingSetSize` per process does not release memory. It moves pages from the working set to the standby list, and dirty pages among them must be written to the page file first, which puts that disk I/O inside the match. The targets are already-suppressed background processes, exactly what the memory manager trims first under pressure. They are not suspended, so they fault the pages back immediately, and the net effect is one extra round of reads and writes during the match.

**Driver-level frame rate cap** (retired 1.8.0.3, removed again in 2.1.3.3)

Capping frame rate is more direct in the game's own settings or the GPU control panel. A second cap at the driver level tends to fight the game's own limiter and VRR, and on AMD it has to go through Radeon Chill, which is mutually exclusive with Anti-Lag: one latency optimisation traded for another.

**USB and storage controller interrupt affinity** (retired 1.8.1.0)

On hybrid architectures with few performance cores it lands interrupts on low-clocked efficiency cores, which makes the first movement of a high-polling-rate mouse feel loose after idle. GPU interrupt affinity near the render cores is kept.

**Others**

Old automatic bulk MSI changes, background frame caps and position-based CPU 0/1 exclusion were removed. The current app still offers manual repair of explicitly disabled GPU `MSISupported=0`, driver low-latency controls. Cache warm-up is fully retired; legacy preferences are discarded on load. Background frame caps remain removed: the driver's background category usually means an unfocused app, which can cap the game itself after alt-tab.

Standalone core-unparking overrides have been replaced by managed-plan parking settings. Current GPU preferences are standby staging or persistent app preferences, neither of which migrates running processes. Disabling CPU idle remains manual, not forced by Extreme. The 1.8.0.2 removal concerned the old Extreme mode and League section; the current build includes reimplemented versions of both. IFEO fallback boosting and per-game CFG disabling have no new-write path, only historical recovery.

RSS receive-core steering, GPU clock locking, system-memory residency, match-only single-display mode, automatic interrupt orchestration, process freezing, refresh-rate guarding, MPO disabling and notification quieting have no current activation path. System-memory residency is distinct from the still-available VRAM reservation feature.

The heterogeneous-GPU, VRS, Thermal Exchange and Interrupt Fabric projects under `tools/` are research benches or probes, not general optimizations integrated into arbitrary games. Their throughput results must not be presented as game FPS gains.

## What is reported but not modified

Most of the widely shared keyboard and mouse registry tweaks act on the order of a few tenths of a millisecond, while latency comes mainly from the render queue. Pavise reports these items without modifying them, including Bluetooth mice and 125 Hz polling rates. The audit page points them out and the decision is yours.

## Interface

<div align="center">
<img src="docs/screenshots/en/library.png" width="49%" alt="Library">
<img src="docs/screenshots/en/policy.png" width="49%" alt="Optimization policies">
<img src="docs/screenshots/en/graphics.png" width="49%" alt="Graphics">
<img src="docs/screenshots/en/interrupt.png" width="49%" alt="Device interrupts">
<img src="docs/screenshots/en/audit.png" width="49%" alt="System audit">
</div>

A full illustrated walkthrough (Chinese, 21 screenshots) is in [docs/tutorial](docs/tutorial/README.md).

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
