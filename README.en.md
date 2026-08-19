<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Pavise">

# Pavise

A small Windows tool that hands system resources to the game while you play

`C#` · `WinForms` · `Chinese / English`

[简体中文](README.md) · **English** · [日本語](README.ja.md)

<br>

<img src="docs/benchmark-v171.png" width="100%" alt="Benchmark">

</div>

## Performance

The chart above is a four-tier bench run from the v1.7.1 era. Test conditions: an i7-9750H laptop with WeChat, Clash, QQ and a music player running in the background, scored against a clean system as 100. Averaged over the three scenarios, the machine scores 59 without Pavise, 60 in Normal mode and 96 in Competitive. Normal and Competitive in that chart are today's Smart and Focus; Extreme has been retired.

Pavise does not generate additional performance. It returns the share taken by background processes to the game. Machines with many background programs and noticeable CPU or disk contention benefit the most; a clean system, or a game entirely limited by the GPU, will see little change.

Extreme scored only 71 in the heavy-load scenario, below Competitive's 93 — that is why it was retired. Suppression strength does not correlate directly with performance, and the default preset is the mildest tier.

Verify the effect yourself by toggling it on and off in the same game and the same scene.

## Usage

Add a game's EXE or shortcut to the target library, or use the scan function to import games already installed through Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net or Xbox.

Protection applies for as long as the game runs; switching to the desktop or minimising does not end it. Games started through a launcher (League of Legends, for example) have their real executable remembered after the first confirmation and are recognised directly afterwards. Launchers, updaters, crash reporters and anti-cheat processes are not identified as games.

All changes are reverted from the recorded state when the game exits. If Pavise itself exits abnormally, the restore resumes on next launch.

## Modes

| Mode | Scope of suppression |
|---|---|
| Smart | Light suppression of windowless background work, escalating tier by tier for whatever keeps taking resources. Whatever you are using, and its family, is never touched |
| Focus | Everything outside the game is suppressed, windows included — even the app you alt-tab to, with only the whitelist exempt. Sustained heavy-load background processes additionally go into a job object with a hard CPU cap |
| Custom | Background suppression, cores, memory and power, system environment and graphics, each picked individually |

Smart escalates per process from that process's own heat and can reach the same isolation level as Focus, but the foreground family stays exempt throughout; Focus puts every eligible background process at that level at once.

In every mode, anti-cheat, the host of the running game, Windows core services, network accelerators and other logged-in accounts are never suppressed. This boundary is unaffected by any switch. The four automatic exemption categories — game platforms and launchers, network accelerators, anti-cheat, and the input/audio/peripheral chain — can be viewed directly on the Whitelist page.
## Per-game profiles

Select a game in the library and open its profile. Each game can override all 27 items across mode, background suppression, cores, memory and power, system environment and graphics. Items without an override follow the global setting; changes save immediately and apply to that game's next match.

- Effective values freeze the moment a match activates. Anything changed mid-match applies to the next one, so the policy never shifts within a match
- The current core selection can be pinned to one game without affecting the others
- A set of overrides can be copied to other games, or cleared back to global in one click
- The Overview page and the tray show the mode actually in effect, and which game's profile it came from
- When consecutive driver write failures auto-disable a switch, that game's matching override is cleared as well

Library entries can be renamed; only the display name changes, recognition is unaffected.

## Features

**Processes and cores**

- The game process gets high priority, raised disk I/O and GPU scheduling priority, and its own cores; background work is demoted or moved to other cores according to the mode
- CPU partitioning handles hybrid architectures, X3D and multiple processor groups. No core splitting on 6 cores or fewer
- Core allocation can be drawn per logical core, with presets for all cores, no hyper-threading, P-cores only, and invert; changes need Apply to take effect. Most people never need it — the scheduler already puts game threads on P-cores
- Heavy-suppression core shrinking: already-suppressed background work is pulled from the whole machine onto a few physical cores. It targets their concurrent memory access eating bandwidth, which neither priority nor efficiency mode can reach. The landing spot is computed live from the game cores you assigned, and on multi-CCD chips it steers clear of the entire L3 block holding them. Bench-verified 33 fps to 95 fps. On 6 cores or fewer it measurably hurts the foreground, so it is forced off there
- Smart frame guard: identifies the thread that decides the frame rate and boosts it alone. Bench-verified 77%–96% better 1% lows when the CPU is saturated
- The game yields once the frame thread is in charge: when the CPU is saturated and that thread really was boosted, the game process steps back to normal priority and only the thread stays high. Games whose frame thread cannot be identified are unaffected and keep high priority throughout. Can be turned off
- Focus resource cage: sustained heavy-load background processes go into a job object with a hard cap of ten percent of system CPU. A capped program feels clearly slower when you switch back to it. A guard process lifts the cap if Pavise exits unexpectedly
- Pavise yields during a match, giving up the game cores and lowering its own scheduling weight
- Fallback boost: for games whose handle the kernel anti-cheat blocks, the system grants priority at process creation instead; effective on the next launch
- Anti-cheat compatibility list: games that refuse writes are recorded, so the priority and I/O writes certain to fail are not retried, while GPU scheduling priority and the frame thread are still attempted. The list expires on a new version, and the Anti-Cheat page can view and clear it

**Graphics**

- Background GPU yielding: when a background process uses the GPU, its GPU scheduling priority is lowered as well
- NVIDIA per-game tuning: maximum performance power mode, low latency mode (on or ultra), Smooth Motion frame generation, unrestricted shader cache, DLSS override (latest, or a pinned J/K generation), per-game ReBAR, and Ansel injection off. Original values are snapshotted and restored when turned off
- AMD per-game tuning: Anti-Lag, AFMF fluid frames, and RSR upscaling. RSR explains that it changes the machine-wide render resolution before enabling
- GPU power limit: raised to the vendor's maximum during a match, restored from the snapshot on exit

**Keyboard, mouse and interrupts**

- Filter Keys, Sticky Keys and Toggle Keys off. Microsoft's own definition of these is to ignore brief keystrokes, so leaving them on always costs latency
- Selective suspend blocked for keyboard and mouse devices, curing the floaty first input after a short idle. Touches only keyboard and mouse — not USB drives or sound cards
- Input queue lengths altered by other tools are repaired, and Enhance Pointer Precision (the system mouse acceleration curve) is turned off
- Foreground time slice, three modes: system default, foreground weighted, report only. Foreground weighted is the old optimization scene's 0x26; it depends on the machine and is not a guaranteed win on the bench, so compare it yourself in the same scenario. Any other folk value is reported as an anomaly
- GPU interrupt affinity is moved to cores near the render thread. USB and storage controller interrupt affinity was retired in 1.8.1.0 — see below

**Memory and power**

- Only the cheapest memory cleanup item is kept. On by default, but it acts only when memory is tight, so machines with headroom never notice it. Details in the next section
- Matches switch to the Pavise managed power plan by default, created on the first match with parameters written for this processor and a name carrying the PG tag and a machine signature. You can also select any plan on the machine, in which case Pavise only switches to it and changes none of its parameters. It is set once, with no periodic polling, so it never fights other power software over the active plan
- The managed plan has two sets. Focus writes 100% minimum processor state, no core parking, the most performance-biased energy preference and a fully raised boost policy; Smart keeps downclocking headroom, and on desktops it writes the same performance-biased energy preference and turns off clock duty cycling on AC, with a middle value for laptops
- Disable processor idle during a match: off by default, effective only in Focus mode on AC power. It removes the wake-up latency of deep idle states, but **on a fair number of machines it is a net loss** — disabling idle also suppresses turbo, so clocks end up lower instead of higher, exactly what was measured on a test laptop. It warns before enabling, records this machine's turbo baseline at that moment, measures again once the first match stabilizes, and turns itself off if the result falls clearly below the baseline. That check is only a backstop; compare one match on against one match off yourself
- MMCSS multimedia scheduling: the share reserved for non-multimedia work drops from 20% to 10%, and the Games task's scheduling category and file I/O are raised to high. It stays applied while game mode is on and is restored when turned off or on exit
- Game DVR background recording stays off while game mode is on and is restored only when Pavise exits, because the system reads it the moment a game launches and it must be in place first
- Power plan, network and notifications are all restored after the game ends

**System environment**

Changes that need a reboot, or that stay on the machine, are collected on this page. Every one is reversible:

- Hardware-accelerated GPU scheduling (HAGS); greyed out on machines that do not support it
- VBS virtualization-based security off, with a risk confirmation before enabling
- Software mitigations for CPU speculative-execution vulnerabilities unloaded, reclaiming the fixed cost of every kernel transition. Only unlocked on machines where a reclaimable cost is actually measured
- Control Flow Guard off per game executable, touching only that mitigation bit; effective on the game's next launch
- Game mode guard, windowed game optimization, device power

**What you can see**

- Checkup page: a read-only inspection of what this machine can do, with an evidence grade on every verdict. System items broken by third-party tools can be fixed on the spot, and you can see whether the GPU is currently limited by its power or thermal ceiling. The interrupt row uses kernel ETW to capture DPC and ISR time and names the top source directly
- Session report: play time, how many processes were suppressed and their CPU share, the share of time spent against power and thermal limits, and the peak shared video memory the game spilled into system RAM
- The library supports forced takeover for emulators, cloud gaming and anything else that cannot be recognised: the moment the process starts, a match begins
- The whitelist has its own page; drop items in and the scope is decided automatically

A purely local tool: no service installed, no data uploaded, no injection into game processes, no modification of game memory or files. Every write is read back where possible, and a value that does not read back as expected is not counted as success.

## Memory cleanup

One item is kept. On by default, but it acts only when memory is tight. The other two were removed; the reasons are at the end of this section.

That decision is backed by measurements. The table below measures the system-level commands tools like this use, on a 64 GB machine with 40344 MB available and 16154 MB of system cache before each run:

| Command | Duration | Available memory | System cache |
|---|---|---|---|
| Empty all process working sets | 1894.9 ms | +5191 MB | +3364 MB |
| Purge standby list (low priority) | 13.3 ms | +144 MB | −47 MB |
| Purge standby list (entire) | 1380.6 ms | +382 MB | −18894 MB |
| Flush modified page list | 8019.7 ms | +5845 MB | +251 MB |
| Combine physical memory pages | 6152.1 ms | +2121 MB | +531 MB |

Purging the entire standby list throws away 18894 MB of system cache for 382 MB of available memory. The standby list already counts as available memory, so purging it converts cached available memory into empty available memory — the total barely moves, while the cached content is gone and the game load that follows has to read from disk again. Flushing the modified page list takes 8 seconds and combining pages 6.2 seconds; neither belongs on the match-start path.

**Kept: purge low-priority standby memory when memory is tight**

Only the cache pages the system judges least likely to be needed again, about 13 ms per run, leaving the rest of the standby list alone so file cache that still has value is not thrown out with it. Available memory is monitored during a match and the purge runs only when available physical memory drops below 15%, with a 45-second cooldown. Those two gates exist to avoid the trap the old ISLC-style full purge fell into (below): act once, at minimal cost, only when memory is genuinely tight. On by default; machines with headroom never reach the trigger.

**Removed: reclaim background working sets once the match stabilizes**

Calling `SetProcessWorkingSetSize` per process does not free memory. It only pushes pages from the working set to the standby list, and the dirty ones must be written to the page file first — that disk I/O lands squarely inside the match. The targets are already-suppressed background processes, exactly what the memory manager trims first under pressure; they are not suspended, so they fault the pages straight back and the whole round is undone seconds later, netting one extra read-write cycle during play.

**Removed: ISLC-style purge of the entire standby list**

Triggering a full standby purge on `standby list ≥ 1 GB and available ≤ 1 GB` costs more than it returns: 1380 ms of system-wide stall plus the entire file cache discarded, for 382 MB of available memory. Worse, there was no cooldown and a check every 5 seconds — the trigger condition itself means memory is nearly exhausted, so after the purge the cache refills from disk and crosses the line again within seconds, producing a purge-read-refill loop on low-end machines that is worse than the periodic stutter it was meant to fix. The kept item learned from both: only the low-priority portion (13 ms instead of 1380 ms), with a cooldown.

See Mark Russinovich, *The Memory-Optimization Hoax* (Windows and .NET Magazine, January 2004).

## What this does not do

The keyboard and mouse registry tweaks that circulate mostly act on the order of a fraction of a millisecond, while the main source of latency is the render queue. Pavise reports these without changing them, Bluetooth mice and 125 Hz polling rates included — the Checkup page names them and the decision is yours.

MSI mode, low-latency mode, background hard frame caps, game file preheating and blocking CPU 0/1 by position were all implemented and removed: measured as ineffective, risking damage to devices, or based on a misreading — the driver's background frame cap actually applies to applications that lose focus, so alt-tabbing out caps the game itself.

Driver-level frame rate caps (NVIDIA and AMD) were retired in 1.8.0.3: capping is more direct in the game's own settings or the GPU control panel, and a second cap at the driver layer easily fights the game's own limiter and VRR. On AMD it also had to go through Radeon Chill, which is mutually exclusive with Anti-Lag — trading one latency optimization for another.

1.8.1.0 pulled three more: USB and storage controller interrupt affinity, which on hybrid CPUs with few performance cores landed interrupts on low-clock efficiency cores and caused a floaty first click on high-polling mice after idle; the standalone in-match core-unparking override, which duplicated the parking settings in the managed power plan; and the per-game forced discrete GPU preference, since modern Windows already picks the discrete GPU for games. GPU interrupt affinity, which targets cores near the render thread, is kept.

The Competitive preset's automatic disabling of processor idle was pulled in the same version. It is now a manual switch, off by default, that measures peak frequency after enabling and turns itself off if the frequency drops.

Extreme mode and the League of Legends spotlight were retired in 1.8.0.2; Focus is the top tier.

Removed items automatically restore the values written by older versions on upgrade.

## Interface

<div align="center">
<img src="docs/guide-overview.png" width="49%" alt="Overview">
<img src="docs/guide-library.png" width="49%" alt="Library">
<img src="docs/guide-policy.png" width="49%" alt="Policy">
<img src="docs/guide-core.png" width="49%" alt="Cores">
<img src="docs/guide-anticheat.png" width="49%" alt="Anti-cheat">
<img src="docs/guide-audit.png" width="49%" alt="Checkup">
</div>

## Building

Uses the .NET Framework compiler shipped with Windows. No Visual Studio, no packages to restore.

```cmd
build.cmd      rem produces Pavise.exe
dev.cmd        rem kill the old instance, build, launch
```

Source builds are unsigned, so SmartScreen will warn.

## Running and data locations

Double-click `Pavise.exe` and it goes to the tray. Adjusting other processes requires administrator rights. Start-up is implemented as a scheduled task. The version is checked against GitHub once at launch; no local data is uploaded.

Data is stored in `%AppData%\Pavise` by default — target configuration, whitelist and run logs — while interface and feature switches live in the registry at `HKCU\Software\Pavise`. Place an empty `Pavise.portable` file next to the executable to store everything in the program directory instead.

The Settings page offers a one-click restore.

## Buy the author a coffee

<div align="center">
<img src="docs/wechat.png" width="220" alt="WeChat">
&nbsp;&nbsp;
<img src="docs/alipay.png" width="220" alt="Alipay">
</div>

## Author and licence

bdth ｜ 2074055628@qq.com ｜ WeChat Ssssssstyle (bugs, suggestions, usage questions)

This project uses the [Pavise Licence](LICENSE): the source is open, free to use, modify and distribute at no charge, and **selling it is forbidden**.

Taking money in any form for distributing Pavise or a modified version is not allowed — including selling copies, activation codes or download access, bundling it into a paid product or subscription, paywalls, paid unlocks and donation gates.

Keep the licence and author information intact when distributing, tell recipients that selling this software is forbidden, and state the modifier and the changes when distributing a modified version.

This is a personal project provided as is, with no guarantee of effect or compatibility. Anti-cheat suppression, VBS and cache clearing can all have side effects; use it only on your own machine and understand the risks first.

The latest release and source updates are always free in the WeChat and QQ groups. QQ groups 1051472054, 1101249532 and 383761286; WeChat Ssssssstyle, mention Pavise. **If you paid for this, you were scammed** — ask for a refund and get it free from the groups.
