<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Pavise">

# Pavise

A small Windows tool that hands system resources to the game while you play

`C#` · `WinForms` · `Simplified Chinese UI`

[简体中文](README.md) · **English** · [日本語](README.ja.md)

<br>

<img src="docs/benchmark-v171.png" width="100%" alt="Benchmark">

</div>

## Performance

Test conditions for the chart above: an i7-9750H laptop with WeChat, Clash, QQ and a music player running in the background, scored against a clean system as 100. Averaged over the three scenarios, the machine scores 59 without Pavise, 60 in Normal mode, 96 in Competitive and 99 in Extreme.

Pavise does not generate additional performance. It returns the share taken by background processes to the game. Machines with many background programs and noticeable CPU or disk contention benefit the most; a clean system, or a game entirely limited by the GPU, will see little change.

Note that Extreme mode scores only 71 in the heavy-load scenario, below Competitive's 93. Suppression strength does not correlate directly with performance, and the default preset is the mildest tier.

Verify the effect yourself by toggling it on and off in the same game and the same scene.

## Usage

Add a game's EXE or shortcut to the target library, or use the scan function to import games already installed through Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net or Xbox.

Protection applies for as long as the game runs; switching to the desktop or minimising does not end it. Games started through a launcher (League of Legends, for example) have their real executable remembered after the first confirmation and are recognised directly afterwards. Launchers, updaters, crash reporters and anti-cheat processes are not identified as games.

All changes are reverted from the recorded state when the game exits. If Pavise itself exits abnormally, the restore resumes on next launch.

## Modes

| Mode | Scope of suppression |
|---|---|
| Normal | Light suppression of windowless background processes, escalating for those that keep taking resources. Programs in active use are untouched |
| Competitive | Everything except the game is suppressed, windowed applications included. Only the current foreground program and its children are exempt |
| Extreme | Competitive plus shared service hosts, which are moved onto background cores |
| Custom | Background suppression, network and notifications selected individually |

In every mode, anti-cheat, the host process of a running game, Windows core services, network accelerators and other logged-in accounts are never suppressed. No switch in the application overrides this boundary.

## Per-game configuration

Select a game in the library and open its dedicated configuration page. Each game can override any of the 34 items covering mode, background suppression, cores, memory and power, system environment and graphics. Items left untouched follow the global settings, changes are saved immediately and apply from the game's next session.

- Effective values are frozen the instant a session activates; anything changed mid-session applies next time, so policy never shifts in the middle of a match
- The current core selection can be locked to a single game without affecting the others
- A set of overrides can be applied to other games, or cleared back to global in one click
- The overview page and tray icon show the mode actually in effect, labelled with the game it comes from
- When repeated driver write failures auto-disable a switch, that game's matching override is removed as well

Library entries can also be renamed. Only the display name changes; detection is unaffected.

## Game columns

Deep, game-specific integration on its own page, one tab per game. The first column is League of Legends (CN server, WeGame chain):

- Shell launch via WeGame: log in and start normally; once the LCU confirms the lobby is ready, WeGame, Cross and the other Tencent companion processes are terminated precisely
- True headless matches: after a match starts the lobby UI is closed through the client's native interface, and a watchdog chain relaunches and restores it when the match ends
- Live chain status: client install, LCU session, game phase, WEGAME, CROSS and CEF cells refresh in real time
- Direct add-on removal: AI coach, iCreate recording and similar layers can be deleted in one click; the client re-downloads them on update, and file operations are locked while the client is running

No injection, no memory writes, no touching of core game files. The League of Legends entry in the library is tagged with a column badge.

## Features

**Processes and cores**

- The game receives high priority, elevated disk IO and GPU scheduling priority and dedicated cores; background processes are demoted or migrated to other cores according to the mode
- CPU partitioning supports hybrid architectures, X3D and multiple processor groups. No core splitting on 6 cores or fewer
- Core allocation can be set core by core, with presets for all cores, SMT off, P-cores only and invert; changes take effect when you press Apply. Most people should leave it alone — the scheduler already puts game threads on P-cores
- Background affinity squeeze: already-suppressed background processes are pulled from every core down to a handful of physical ones. The target is their concurrent memory access eating bandwidth, which neither priority nor efficiency mode can touch. The landing zone is computed live from the cores you gave the game, and on multi-CCD parts it avoids the game's L3 entirely. Bench-measured 33 fps to 95 fps
- Smart frame protection: the thread determining the frame rate is identified and boosted separately. Bench-measured improvement of 77%–96% in 1% lows when the CPU is saturated
- Fallback boost: for games whose handles are blocked by kernel anti-cheat, the system assigns high priority at process creation instead, effective from the next launch
- Service yielding: selected service processes are steered onto background cores during a session — steered, not stopped

**Graphics**

- Background GPU yielding: background processes using the GPU have their GPU scheduling priority lowered as well
- NVIDIA tuning: maximum performance power mode, DLSS4 Transformer override, per-game ReBAR, Ansel injection disabled, battery frame cap removed. Original values are snapshotted and restored on disable
- On dual-GPU machines, suppressed background processes using the discrete GPU are moved to the integrated one

**Input and interrupts**

- Filter Keys, Sticky Keys and Toggle Keys are turned off. Microsoft defines them as ignoring brief keystrokes, so leaving them on necessarily costs latency
- System suspension of mouse and keyboard is blocked, which fixes the first input feeling vague after a short idle. It touches input devices only, not USB drives or audio interfaces
- Input queue sizes altered by other tools are repaired, and Enhance Pointer Precision (the system mouse acceleration curve) can be turned off
- Interrupt storms from 4K/8K polling mice are steered away from the game's cores. The cost is explained before you enable it: below 1000 Hz there is essentially nothing to gain
- Disk controller completion interrupts are moved to idle cores

**Memory and power**

- Memory cleaning is down to the one cheapest feature, off by default and only recommended for memory-constrained machines; see the next section
- Sessions switch to the Pavise-managed power plan by default, created on the first session with parameters written for this machine's processor and named with a PG tag and machine signature; any local plan can be selected instead, in which case Pavise only switches to it without touching its settings
- Power plan, network, Game DVR, notifications and indexing services are all restored after the game ends

**What you can see**

- Health check page: read-only inspection of the machine's capabilities, with each conclusion tagged by evidence level. System values broken by third-party tools can be repaired in place with one click, and the GPU's current power or thermal throttling state is shown. The interrupt row uses kernel ETW to trace DPC and ISR activity and names the top source directly
- Session report: play duration, number of suppressed processes and their CPU usage, and the share of time spent against a power or thermal limit
- The library supports forced takeover for things that cannot be detected, such as emulators and cloud gaming: the moment the process appears, the session begins
- Whitelist on its own page; drag items in and the scope is determined automatically

A purely local tool: no service is installed, no data is uploaded, and the game process, its memory and its files are never modified. Writes are read back and verified wherever possible; a value that does not read back as expected is not counted as success.

## Memory cleaning

One feature is left, off by default, and only recommended for memory-constrained machines. The other two were removed in 1.7.2; the reasons are at the end of this section.

That judgement is backed by measurement. The table below measures each of the system-level commands tools of this kind reach for, on a 64 GB machine with 40344 MB available and 16154 MB of system cache before the run:

| Command | Duration | Available memory | System cache |
|---|---|---|---|
| Empty all process working sets | 1894.9 ms | +5191 MB | +3364 MB |
| Purge standby list (low priority) | 13.3 ms | +144 MB | −47 MB |
| Purge standby list (entire) | 1380.6 ms | +382 MB | −18894 MB |
| Flush modified page list | 8019.7 ms | +5845 MB | +251 MB |
| Combine physical memory pages | 6152.1 ms | +2121 MB | +531 MB |

Purging the entire standby list discards 18894 MB of system cache to gain 382 MB of available memory. The standby list already counts as available memory, so purging it converts cached available memory into empty available memory — the total barely moves, while the cached content is gone and the game's next load has to read from disk again. Flushing the modified page list takes 8 seconds and combining physical pages 6.2, neither of which belongs on the path into a match.

**Kept: purge low-priority standby memory before a session**

Purges only the pages the system itself marks as least likely to be needed again. It takes 13 ms and never touches the standby list as a whole, so file cache that still has value survives. Runs once per session. Loading gets slightly smoother on a memory-constrained machine and is imperceptible on one with headroom.

**Removed: trim background working sets once the session is stable**

Calling `SetProcessWorkingSetSize` per process does not free memory; it only pushes pages out of the working set and onto the standby list, and the dirty ones have to be written to the page file first — disk IO landing squarely inside the session. The targets were already-suppressed background processes, exactly the ones the memory manager trims first when it is actually under pressure. They were never suspended, so they faulted their pages straight back in and the whole round of work was undone within seconds; the net effect was one extra round of reads and writes during the match.

**Removed: empty the standby list when it crosses a threshold**

The functional equivalent of ISLC (Intelligent Standby List Cleaner). The `standby list ≥ 1 GB and available memory ≤ 1 GB` pair of conditions was more conservative than ISLC's default, but once it fired the cost outweighed the gain: 1380 ms of system-wide stall plus the entire file cache discarded, in exchange for 382 MB of available memory. Worse, there was no cooldown behind the 5-second check — the trigger condition itself means available memory is already at the floor, so the cache refilled from disk and crossed the line again within seconds, producing a purge/read/refill cycle on low-end machines that is worse than the periodic micro-stutter it was meant to fix.

For background, see Mark Russinovich, "The Memory-Optimization Hoax" (Windows and .NET Magazine, January 2004).

## What it does not do

Most of the circulated registry tweaks for input latency act at the scale of a fraction of a millisecond, while the main source of latency is the render queue. Pavise reports these but does not change them, including Bluetooth mice and 125 Hz polling rates — the health check page points them out and leaves the decision to you.

MSI mode, low-latency mode, background frame caps, fullscreen-window optimisation, game file prewarming and masking off CPU 0/1 by position were all implemented and later removed, for reasons of no measurable effect, the risk of writing a device into an unbootable state, and misread semantics respectively — the driver's background frame cap actually applies to applications that have lost focus, so the game itself is what gets limited after you alt-tab. Anything removed has the values written by older versions restored automatically on upgrade.

## Screenshots

<div align="center">
<img src="docs/guide-overview.png" width="49%" alt="Overview">
<img src="docs/guide-library.png" width="49%" alt="Library">
<img src="docs/guide-policy.png" width="49%" alt="Policy">
<img src="docs/guide-core.png" width="49%" alt="Core allocation">
<img src="docs/guide-anticheat.png" width="49%" alt="Anti-cheat">
<img src="docs/guide-audit.png" width="49%" alt="Health check">
</div>

## Building

Uses the .NET Framework compiler included with Windows. Visual Studio is not required and there are no packages to restore.

```cmd
build.cmd      rem produces Pavise.exe
dev.cmd        rem kill old instance -> build -> launch
```

Source builds are unsigned, so SmartScreen will show a warning.

## Running it and where data is stored

Double-clicking `Pavise.exe` sends it to the tray. Adjusting other processes requires administrator rights. Start-on-boot is implemented through Task Scheduler. It checks GitHub once for a new version at startup and uploads no local data.

Data is stored in `%AppData%\Pavise` by default, covering target configuration, whitelist and log; UI and feature toggles live in the registry under `HKCU\Software\Pavise`. Placing an empty `Pavise.portable` file next to the executable switches storage to the program directory.

The settings page provides a one-click restore.

## Buy the author a coffee

<div align="center">
<img src="docs/wechat.png" width="220" alt="WeChat">
&nbsp;&nbsp;
<img src="docs/alipay.png" width="220" alt="Alipay">
</div>

## Author and licence

bdth ｜ 2074055628@qq.com ｜ WeChat: Ssssssstyle (bugs, suggestions and usage questions)

Released under the [Pavise Licence](LICENSE): the source is open and may be freely used, modified and distributed at no charge, but **selling it is prohibited**.

Taking money in any form for distributing Pavise or a modified version is not permitted. This includes selling copies, activation keys or download access; bundling it into a paid product or subscription; paywalls, paid unlocks and donation gates.

When distributing, keep the licence and author information intact, inform recipients that the software may not be sold, and when distributing a modified version, state who modified it and what was changed.

This is a personal project provided as-is, with no guarantee of results or compatibility. Anti-cheat suppression, VBS and cache clearing can all have side effects; use it on your own machine and understand the associated risks first.

The latest build and source updates are always available free of charge in the WeChat and QQ groups. QQ group 1 is 1051472054, group 2 is 1101249532, group 3 is 383761286; on WeChat, add Ssssssstyle and mention Pavise. **If you paid for this, you were scammed** — request a refund and obtain it for free from the group.
