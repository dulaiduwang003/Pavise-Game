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

- All three memory-cleaning features are off by default and are only recommended for memory-constrained machines; see the next section
- The session switches to the Ultimate Performance power plan, creating one if the machine lacks it and reusing the existing one otherwise
- Power plan, network, Game DVR, notifications and indexing services are all restored after the game ends

**What you can see**

- Health check page: read-only inspection of the machine's capabilities, with each conclusion tagged by evidence level. Supports one-click verification of whether NVIDIA writes took effect, live measurement of mouse polling rate, and the GPU's current power or thermal throttling state. The interrupt row uses kernel ETW to trace DPC and ISR activity and names the top source directly
- Session report: play duration, number of suppressed processes and their CPU usage, and the share of time spent against a power or thermal limit
- The library supports forced takeover for things that cannot be detected, such as emulators and cloud gaming: the moment the process appears, the session begins
- Whitelist on its own page; drag items in and the scope is determined automatically

A purely local tool: no service is installed, no data is uploaded, and the game process, its memory and its files are never modified. Writes are read back and verified wherever possible; a value that does not read back as expected is not counted as success.

## Memory cleaning

All three memory features are off by default. **Every one of them can introduce occasional stutter**, and they exist for memory-constrained low-end machines. On a machine with plenty of RAM they are all cost and no benefit, and should be left off.

That judgement is backed by measurement. The table below measures each of the system-level commands tools of this kind reach for, on a 64 GB machine with 40344 MB available and 16154 MB of system cache before the run:

| Command | Duration | Available memory | System cache |
|---|---|---|---|
| Empty all process working sets | 1894.9 ms | +5191 MB | +3364 MB |
| Purge standby list (low priority) | 13.3 ms | +144 MB | −47 MB |
| Purge standby list (entire) | 1380.6 ms | +382 MB | −18894 MB |
| Flush modified page list | 8019.7 ms | +5845 MB | +251 MB |
| Combine physical memory pages | 6152.1 ms | +2121 MB | +531 MB |

Purging the entire standby list discards 18894 MB of system cache to gain 382 MB of available memory. The standby list already counts as available memory, so purging it converts cached available memory into empty available memory — the total barely moves, while the cached content is gone and the game's next load has to read from disk again. Flushing the modified page list takes 8 seconds and combining physical pages 6.2, neither of which belongs on the path into a match.

What each of the three actually does, and what it costs:

**Purge low-priority standby memory before a session**

Purges only the pages the system itself marks as least likely to be needed again. It takes 13 ms and never touches the standby list as a whole, so file cache that still has value survives. Runs once per session. Loading gets slightly smoother on a memory-constrained machine and is imperceptible on one with headroom. This is the cheapest of the three.

**Trim background working sets once the session is stable**

Runs 30 seconds in, past the loading phase, returning physical memory held by already-suppressed background processes to the system. It uses none of the global commands in the table above; instead it calls `SetProcessWorkingSetSize` on each of those processes individually, leaving the game, the current foreground app, the whitelist and anti-cheat alone. The memory returns to the standby list where the game can claim it at any time, and no background process is closed or loses data.

The cost is a noticeable pause the first time you switch back to one of those programs, because it has to page itself back in. It runs at low priority in batches, for at most 3 seconds, and stops the moment the session ends.

**Empty the standby list when it crosses a threshold**

The functional equivalent of ISLC (Intelligent Standby List Cleaner). During a session it reads the actual size of the standby list every 5 seconds and only purges when the list is at or above 1 GB *and* available memory is at or below 1 GB.

It does not target low memory — the table above already shows that emptying the whole list barely adds any — but the periodic micro-stutter caused by memory-manager lock contention when the standby list grows too large. The cost is the entire file cache, after which every program has to go back to disk for its files. Machines with plenty of RAM essentially never hit the threshold.

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
