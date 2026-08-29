<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Pavise">

# Pavise

A small Windows tool that hands system resources to the game while you play

`C#` · `WinForms` · `Chinese / English`

[简体中文](README.md) · **English** · [日本語](README.ja.md)

**v2.1.3.1 · [Release notes (Chinese)](docs/releases/v2.1.3.1.md)**

<br>

<img src="docs/benchmark-v171.png" width="100%" alt="Benchmark">

</div>

## Performance

The chart above is a four-tier bench run from the v1.7.1 era. Test conditions: an i7-9750H laptop with WeChat, Clash, QQ and a music player running in the background, scored against a clean system as 100. Averaged over the three scenarios, the machine scores 59 without Pavise, 60 in Normal mode and 96 in Competitive. Normal and Competitive in that chart are today's Smart and Focus; Extreme has been retired.

Pavise does not generate additional performance. It returns the share taken by background processes to the game. Machines with many background programs and noticeable CPU or disk contention benefit the most; a clean system, or a game entirely limited by the GPU, will see little change.

Extreme scored only 71 in the heavy-load scenario, below Competitive's 93 — that is why it was retired. Suppression strength does not correlate directly with performance, and the default preset is the mildest tier.

Verify the effect yourself by toggling it on and off in the same game and the same scene.

## Experimental: VidMm video memory residency

This optional policy is implemented under Optimization Policies and is off by default. Pavise queries the actual game renderer's video-memory budget and usage. It attempts a conservative reservation only after sustained budget pressure, with a verified adapter and no existing reservation, then reads the result back.

It does not add or lock VRAM, install a driver, or inject into the game. Restricted access, integrated-only systems, ambiguous adapters or unconfirmed writes cause the operation to be skipped or stopped; the actual rendering path on hybrid systems still needs hardware testing. Session exit releases Pavise's reservation; incomplete recovery keeps its record.

This is not a general frame-rate guarantee. Benefits and driver compatibility still require matched real-game testing; a successful API call does not prove smoother gameplay.

## Usage

Add a game's EXE or shortcut to the target library, or use the scan function to import games already installed through Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net or Xbox.

Protection applies for as long as the game runs; switching to the desktop or minimising does not end it. Games started through a launcher (League of Legends, for example) have their real executable remembered after the first confirmation and are recognised directly afterwards. Launchers, updaters, crash reporters and anti-cheat processes are not identified as games.

Recoverable temporary session changes are restored from their records when the game exits. After an abnormal Pavise exit, the next launch retries recovery and keeps records of failures. Application GPU preferences remain saved, input-language changes are not rolled back, and purged cache contents cannot be restored.

## Modes

| Mode | Scope of suppression |
|---|---|
| Smart | Every background process that clears the protection boundary is isolated outright the moment the match starts — no heat check, no tier-by-tier escalation. Whatever you are using, and its family, is never touched |
| Focus | The isolation net widens to everything outside the game, windows included — even the app you alt-tab to, with only the whitelist exempt |
| Custom | Background suppression, cores, memory and power, system environment and graphics, each picked individually |

The two modes differ in which processes are eligible to be touched, not in how hard they are clamped: anything past the boundary is isolated outright, cold processes included, rather than waiting for it to burn a dozen seconds of CPU first. Isolation writes the lowest priority class, the lowest disk I/O and page priority, EcoQoS with a capped timer resolution, and disabled turbo boost; with GPU yielding on, GPU scheduling priority drops to idle as well.

This is the opposite of what 1.9 did, because the premise changed. Blanket isolation really was a net loss back then — the scattered timer wakeups of a hundred idle processes got packed onto two cores by affinity narrowing and queued against each other, multiplying the worst frame by 2.6. Since 2.0 the background is never given a new affinity mask; narrowing and core relocation were pulled entirely. A cold process with no ready threads costs no CPU to begin with, and when it does wake it can run on any core with no higher-priority work pending — no queue, and the cost of blanket isolation goes with it.

In every mode, anti-cheat, the host of the running game, Windows core services, network accelerators and other logged-in accounts are never suppressed. This boundary is unaffected by any switch. Game family exemption is on by default, leaving game platforms and launchers, game-folder processes and game child processes untouched; turn it off to exempt only the game itself and the whitelist. The exemption categories can be viewed directly on the Whitelist page.
## Per-game profiles

Select a game in the library and open its profile. Each game can override policies for mode, background suppression, cores, memory and power, system environment and graphics. Items without an override follow the global setting; changes save immediately and most apply to that game's next match.

- Most per-game values are selected when a match activates and apply changes next match. The optional-service pause and CPU idle switches are handled within the current session; turning them off restores their changes
- The current core selection can be pinned to one game without affecting the others
- A set of overrides can be cleared back to global in one click
- The Overview page and the tray show the mode actually in effect, and which game's profile it came from
- When consecutive driver write failures auto-disable a switch, that game's matching override is cleared as well

Library entries can be renamed; only the display name changes, recognition is unaffected.

## Features

**Processes and cores**

- The game process gets high priority, raised disk I/O and GPU scheduling priority, and its own cores; background work is demoted according to the mode. The background is never given a new affinity mask — no narrowing, no relocation
- CPU partitioning handles hybrid architectures, X3D and multiple processor groups. No core splitting on 6 cores or fewer
- Core allocation can be drawn per logical core, with presets for all cores, no hyper-threading, P-cores only, and invert; changes need Apply to take effect. Most people never need it — the scheduler already puts game threads on P-cores
- Smart frame guard: identifies the thread that decides the frame rate and boosts it alone. Bench-verified 77%–96% better 1% lows when the CPU is saturated
- Smart yield: when the CPU stays saturated for ten seconds and the frame thread did not manage to take over on its own, the game process steps back to normal priority — whole-process high priority measurably worsens tail frames in that state. It returns to high priority as soon as the frame thread takes over or the CPU frees up
- Pavise yields during a match, giving up the game cores and lowering its own scheduling weight
- Anti-cheat compatibility list: games that refuse writes are recorded, so the priority and I/O writes certain to fail are not retried, while GPU scheduling priority and the frame thread are still attempted. The list expires on a new version, and the Anti-Cheat page can view and clear it

**Graphics**

- Graphics now has Common / NVIDIA / AMD / Intel tabs. Common → Application GPU preferences lets you select EXEs or running background apps and save the Windows power-saving GPU preference. Adding requires a confirmed integrated/discrete pair; Intel Arc B580 is not classified as integrated. It [applies next app launch](https://support.microsoft.com/en-US/Windows/Hardware/Display-Graphics/optimizations-for-windowed-games-in-windows-11), without moving or restarting running apps or guaranteeing exclusive game GPU use. Preferences persist after a game or Pavise exits. Removing entries or resetting all configuration restores only Pavise's changes, preserving existing power-saving settings and detected external changes. Uncertain writes or restoration retain recovery records
- Intel → Global low latency during games is off by default, requires a warning about its global scope, and supports per-game enable overrides. Only basic On is used on DX9/DX11 paths that official IGCL explicitly reports as supporting live changes. No Boost, XeSS frame generation, overclocking or frame cap is enabled, and higher frame rates are not guaranteed. On multi-GPU systems this changes the selected Intel adapter, which is not necessarily the game's rendering GPU. Existing On/Boost settings are skipped; only confirmed owned changes are restored on exit. Uncertain writes do not grant restoration rights. Availability depends on the installed driver; see the [Intel IGCL definitions](https://github.com/intel/drivers.gpu.control-library/blob/b6c462933502e13d1537dd5024949a51be30e63d/include/igcl_api.h)
- Background GPU yielding: when a background process uses the GPU, its GPU scheduling priority is lowered as well
- NVIDIA per-game tuning: maximum performance power mode, low latency mode (on or ultra), Smooth Motion frame generation, unrestricted shader cache, DLSS override (latest, or a pinned J/K generation), and per-game ReBAR. Original values are snapshotted and restored when turned off
- GPU power limit: raised to the vendor's maximum during a match, restored from the snapshot on exit

**Keyboard, mouse and interrupts**

- Switch to English once at game entry: in Optimization Policies → Session Extras, off by default, with per-game overrides. During a short entry window it requests an available independent English keyboard layout once for the actual foreground game. Switching back to Chinese or returning to the game does not cause another request; exit does not restore the input method. Enabling mid-game applies next launch, and restarting Pavise skips games already running. Uses the [Windows input-language request](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-inputlangchangerequest), without simulated keys or changes to the default input method or Windows layout-sharing mode. It is not tied to an IME brand, but missing layouts, permissions or application rejection cause a skip; compatibility with every IME and game is not guaranteed
- Filter Keys, Sticky Keys and Toggle Keys off. Microsoft's own definition of these is to ignore brief keystrokes, so leaving them on always costs latency
- Selective suspend blocked for keyboard and mouse devices, curing the floaty first input after a short idle. Touches only keyboard and mouse — not USB drives or sound cards
- Input queue lengths altered by other tools are repaired, and Enhance Pointer Precision (the system mouse acceleration curve) is turned off
- Foreground time slice, three modes: system default, foreground weighted, report only. Foreground weighted is the old optimization scene's 0x26; it depends on the machine and is not a guaranteed win on the bench, so compare it yourself in the same scenario. Any other folk value is reported as an anomaly
- GPU interrupt affinity is moved to cores near the render thread. USB and storage controller interrupt affinity was retired in 1.8.1.0 — see below

**Memory and power**

- Matches switch to the Pavise managed power plan by default, created on the first match with parameters written for this processor and a name carrying the PG tag and a machine signature. You can also select any plan on the machine, in which case Pavise only switches to it and changes none of its parameters. It is set once, with no periodic polling, so it never fights other power software over the active plan
- The managed plan has two sets. Focus writes 100% minimum processor state, no core parking, the most performance-biased energy preference and a fully raised boost policy; Smart keeps downclocking headroom, and on desktops it writes the same performance-biased energy preference and turns off clock duty cycling on AC, with a middle value for laptops
- Disable CPU idle: in Optimization Policies → Session Extras, off by default, with a warning before each enable. Applies only during a game, on confirmed AC power, with Pavise's managed power plan active. Only the AC setting is changed; turning it off, leaving the game or switching to battery restores Pavise's change. User-selected plans are not modified. Power use, heat and fan noise may increase; some systems may lose turbo frequency or frame rate. Compare results yourself; there is no automatic performance verdict
- Standby memory cleaner (ISLC rules): in Optimization Policies → Session Extras, off by default with confirmation before enabling. Both the list-size and true-free-memory conditions must pass before the entire standby list is purged. Parameters are global; each game can override the switch. Game and background working sets are not trimmed. Details below
- Power budget yield: on a laptop the CPU and GPU draw from one shared power and thermal budget, so when the GPU is pinned against its limit and the CPU has headroom, the energy preference moves to a still-performance-leaning middle value to let the budget flow to the GPU. Off by default, and eligible only on a laptop, on AC, in Focus mode, with Pavise's managed power plan active and RAPL wattage readable; the decision is made once per match. Twenty seconds of observation to decide, fifteen more to verify after engaging, then an immediate revert and a stop to automatic attempts if it frees less than 3W or GPU utilization drops by more than 3%. An eligible user can confirm and enable the switch again to retry. On the author's own i7-9750H, EPP across its full range moved neither package power nor actual frequency, so machines like it will always fail verification: that is the intended behavior
- MMCSS multimedia scheduling: the share reserved for non-multimedia work drops from 20% to 10%, and the Games task's scheduling category and file I/O are raised to high. It stays applied while game mode is on and is restored when turned off or on exit
- Game DVR background recording stays off while game mode is on and is restored only when Pavise exits, because the system reads it the moment a game launches and it must be in place first
- Power plan, network and notifications are all restored after the game ends

**System environment**

Changes that need a reboot, or that stay on the machine, are collected on this page. Every one is reversible:

- Hardware-accelerated GPU scheduling (HAGS). Its effect on average frame rate is within the margin of error; what matters is that it is a prerequisite for driver-side frame generation and similar features, which are unavailable while it is off. Greyed out on machines that do not support it
- VBS virtualization-based security off, with a risk confirmation before enabling
- Software mitigations for CPU speculative-execution vulnerabilities unloaded, reclaiming the fixed cost of every kernel transition. Only unlocked on machines where a reclaimable cost is actually measured
- Game mode guard, windowed game optimization, device power

**What you can see**

- Checkup page: a read-only inspection of what this machine can do, with an evidence grade on every verdict. System items broken by third-party tools can be fixed on the spot, and you can see whether the GPU is currently limited by its power or thermal ceiling. The interrupt row uses kernel ETW to capture DPC and ISR time and names the top source directly
- Session report: play time, how many processes were suppressed and their CPU share, the share of time spent against power and thermal limits, and the peak shared video memory the game spilled into system RAM
- The library supports forced takeover for emulators, cloud gaming and anything else that cannot be recognised: the moment the process starts, a match begins
- The whitelist has its own page; drop items in and the scope is decided automatically

A purely local tool: no service installed, no data uploaded, no injection into game processes, no modification of game memory or files. Every write is read back where possible, and a value that does not read back as expected is not counted as success.

## Memory cleanup

**Optional: standby memory cleaner (ISLC rules), off by default.** Enable it in Optimization Policies → Session Extras. The adjacent parameter dialog saves the list threshold, free-memory threshold and polling interval together. Pavise defaults to 1024 MB, 1024 MB and 4000 ms; MB means 1024² bytes. Thresholds accept 0–1048576 MB and polling accepts 250–300000 ms. These are Pavise defaults, not a claim about every ISLC release's factory settings.

The condition is `List ≥ list threshold AND Free < free threshold`. List counts standby plus the system working set; Free counts only free and zero pages. Task Manager's Available includes standby and cannot substitute for Free. See [the ISLC author's clarification](https://www.wagnardsoft.com/forums/viewtopic.php?p=6381) and [Microsoft's counter definitions](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information).

Polling runs only during a detected game session. The sole mutation is `MemoryPurgeStandbyList`, covering all standby priorities. It never trims process/system working sets, flushes modified pages, combines pages, or changes memory compression, page files or timer resolution. A separate worker prevents overlapping or queued catch-up polls, with no hidden extra cooldown. Two consecutive query/purge failures disable the strategy; enabling it again requires confirmation.

Disabling the effective strategy for the current game or ending the game/Pavise cancels pending purges. A call already inside Windows cannot be interrupted, and shutdown must wait for it; discarded cache cannot be restored. The global switch is a default: a per-game On overrides global Off. Stop that game's cleanup by disabling its override or turning off the game guard. Purging may increase disk reads, loading time or stutter. Improved frame rate and zero risk on every system are not guaranteed. Avoid running another automatic cleaner alongside it. Parameters remain global. This implements ISLC's core memory rules, not its startup management, exclusion-list interface or optional timer controls.

**The following measurements describe retired implementations, not a performance test of the new strategy.**

The table below measures the system-level commands tools like this use, on a 64 GB machine with 40344 MB available and 16154 MB of system cache before each run:

| Command | Duration | Available memory | System cache |
|---|---|---|---|
| Empty all process working sets | 1894.9 ms | +5191 MB | +3364 MB |
| Purge standby list (low priority) | 13.3 ms | +144 MB | −47 MB |
| Purge standby list (entire) | 1380.6 ms | +382 MB | −18894 MB |
| Flush modified page list | 8019.7 ms | +5845 MB | +251 MB |
| Combine physical memory pages | 6152.1 ms | +2121 MB | +531 MB |

Purging the entire standby list throws away 18894 MB of system cache for 382 MB of available memory. The standby list already counts as available memory, so purging it converts cached available memory into empty available memory — the total barely moves, while the cached content is gone and the game load that follows has to read from disk again. Flushing the modified page list takes 8 seconds and combining pages 6.2 seconds; neither belongs on the match-start path.

**Removed: purge low-priority standby memory when memory is tight**

This one was treated as the version that had learned from the others' mistakes. Measurements on 2026-08-20 proved it fell into the same trap, just less visibly.

The trigger is a ratio of available memory, and available memory already counts standby pages — purging merely moves pages from the standby list to the free list, so the ratio does not change and the condition can never clear. The 45-second cooldown only set a tempo for the endless repetition. Real user logs show 14 triggers in 10 minutes, spaced exactly 45 seconds apart, never stopping.

More to the point, it barely did anything: `MemoryPurgeLowPriorityStandbyList` only clears priority tier 0, which totals 1.4 MB on a 24 GB machine — 0.0 to 0.1 MB actually freed per run, at a call cost of 0.2 ms. What it saves is a few hundred nanoseconds of page-reclaim work in the memory manager, which is noise against a frame's budget.

**Removed: reclaim background working sets once the match stabilizes**

Calling `SetProcessWorkingSetSize` per process does not free memory. It only pushes pages from the working set to the standby list, and the dirty ones must be written to the page file first — that disk I/O lands squarely inside the match. The targets are already-suppressed background processes, exactly what the memory manager trims first under pressure; they are not suspended, so they fault the pages straight back and the whole round is undone seconds later, netting one extra read-write cycle during play.

**Removed: the old full-standby purge based on Available**

The old implementation checked `standby list ≥ 1 GB and available ≤ 1 GB` every five seconds. It used Available rather than ISLC's Free, so it did not reproduce ISLC's trigger. In the historical measurement above, one full purge took about 1380 ms, removed about 18894 MB of system cache and increased Available by only about 382 MB. That version and the low-priority variant remain retired. The new strategy uses true Free in its dual threshold, but discarded cache can still cause extra reads; a successful call is not proof of smoother gameplay.

See Mark Russinovich, *The Memory-Optimization Hoax* (Windows and .NET Magazine, January 2004).

## What this does not do

The keyboard and mouse registry tweaks that circulate mostly act on the order of a fraction of a millisecond, while the main source of latency is the render queue. Pavise reports these without changing them, Bluetooth mice and 125 Hz polling rates included — the Checkup page names them and the decision is yours.

MSI mode, low-latency mode, background hard frame caps, game file preheating and blocking CPU 0/1 by position were all implemented and removed: measured as ineffective, risking damage to devices, or based on a misreading — the driver's background frame cap actually applies to applications that lose focus, so alt-tabbing out caps the game itself.

Driver-level frame rate caps (NVIDIA and AMD) were retired in 1.8.0.3: capping is more direct in the game's own settings or the GPU control panel, and a second cap at the driver layer easily fights the game's own limiter and VRR. On AMD it also had to go through Radeon Chill, which is mutually exclusive with Anti-Lag — trading one latency optimization for another.

1.8.1.0 pulled three more: USB and storage controller interrupt affinity, which on hybrid CPUs with few performance cores landed interrupts on low-clock efficiency cores and caused a floaty first click on high-polling mice after idle; the standalone in-match core-unparking override, which duplicated the parking settings in the managed power plan; and the per-game forced discrete GPU preference, since modern Windows already picks the discrete GPU for games. GPU interrupt affinity, which targets cores near the render thread, is kept.

The Competitive preset's automatic disabling of processor idle was pulled in the same version. The current manual policy is off by default and requires risk confirmation before enabling. Presets do not enable it automatically, and the old frequency stress test is not used.

Extreme mode and the League of Legends spotlight were retired in 1.8.0.2; Focus is the top tier.

Fallback boost (IFEO) and per-game CFG disabling have been removed, including global and per-game controls, startup staging and in-session writes. No compatibility handling is added for retired fields. Libraries containing a retired per-game field follow the existing failure flow: restore system changes, automatically clear old data, show a dialog and exit. Failed cleanup retains recovery records and reports the reason.

Recovery for historical changes is retained. Normal startup and upgrades do not restore them automatically; use the cleanup button on the Settings page. Incompatible libraries or read/write failures use the automatic cleanup-and-exit flow described above.

## Interface

<div align="center">
<img src="docs/guide-overview.png" width="49%" alt="Overview">
<img src="docs/guide-library.png" width="49%" alt="Library">
<img src="docs/guide-policy.png" width="49%" alt="Policy">
<img src="docs/guide-core.png" width="49%" alt="Cores">
<img src="docs/guide-anticheat.png" width="49%" alt="Anti-cheat">
<img src="docs/guide-audit.png" width="49%" alt="Checkup">
</div>

## Running and data locations

Double-click `Pavise.exe` and it goes to the tray. The build is unsigned, so SmartScreen may warn — choose to run anyway. Adjusting other processes requires administrator rights. Start-up is implemented as a scheduled task. The version is checked against the official update source once at launch; no local data is uploaded.

Data is stored in `%AppData%\Pavise` by default — target configuration, whitelist and run logs — while interface and feature switches live in the registry at `HKCU\Software\Pavise`. Place an empty `Pavise.portable` file next to the executable to store everything in the program directory instead.

The Settings page offers a one-click restore.

## Buy the author a coffee

<div align="center">
<img src="docs/wechat.png" width="220" alt="WeChat">
&nbsp;&nbsp;
<img src="docs/alipay.png" width="220" alt="Alipay">
</div>

## Author and licence

bdth ｜ 2074055628@qq.com ｜ Douyin 44601770838 (bugs, suggestions, usage questions)

This project uses the [Pavise Licence](LICENSE): free to use, free to redistribute unchanged, reverse engineering forbidden, and **selling it is forbidden**.

Taking money in any form for distributing Pavise or a modified version is not allowed — including selling copies, activation codes or download access, bundling it into a paid product or subscription, paywalls, paid unlocks and donation gates.

Keep the licence and author information intact when distributing, tell recipients that selling this software is forbidden, and state the modifier and the changes when distributing a modified version.

This is a personal project provided as is, with no guarantee of effect or compatibility. Anti-cheat suppression, VBS and cache clearing can all have side effects; use it only on your own machine and understand the risks first.

The latest release is always free in the QQ groups: 1051472054, 1101249532 and 383761286. **If you paid for this, you were scammed** — ask for a refund and get it free from the groups.
