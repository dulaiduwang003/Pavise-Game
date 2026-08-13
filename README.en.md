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

- The game receives high priority, elevated disk IO and GPU scheduling priority and dedicated cores; background processes are demoted or migrated to other cores according to the mode
- CPU partitioning supports hybrid architectures, X3D and multiple processor groups. No core splitting on 6 cores or fewer
- Smart frame protection: the thread determining the frame rate is identified and boosted separately. Bench-measured improvement of 77%–96% in 1% lows when the CPU is saturated
- Background GPU yielding: background processes using the GPU have their GPU scheduling priority lowered as well
- NVIDIA tuning: maximum performance power mode, DLSS4 Transformer override, per-game ReBAR, Ansel injection disabled, battery frame cap removed. Original values are snapshotted and restored on disable
- Input latency: Filter Keys and Sticky Keys are turned off (Microsoft defines them as ignoring brief keystrokes, so enabling them introduces delay), system suspension of mouse and keyboard is blocked, and input queue sizes altered by other tools are repaired
- Interrupt storms from 4K/8K polling mice are steered away from the game's cores
- Power plan, network, Game DVR, notifications and indexing services are all restored after the game ends
- Health check page: read-only inspection of the machine's capabilities, with each conclusion tagged by evidence level. Supports one-click verification of whether NVIDIA writes took effect, live measurement of mouse polling rate, and the GPU's current power or thermal throttling state
- Session report: play duration, number of suppressed processes and their CPU usage, and the share of time spent against a power or thermal limit
- Whitelist on its own page; drag items in and the scope is determined automatically

A purely local tool: no service is installed, no data is uploaded, and the game process, its memory and its files are never modified. Writes are read back and verified wherever possible; a value that does not read back as expected is not counted as success.

## What it does not do

Most of the circulated registry tweaks for input latency act at the scale of a fraction of a millisecond, while the main source of latency is the render queue. Pavise reports these but does not change them, including Bluetooth mice, 125 Hz polling rates and Enhance Pointer Precision.

MSI mode, low-latency mode, background frame caps and masking off CPU 0/1 by position were all implemented and later removed, for reasons of no measurable effect, the risk of writing a device into an unbootable state, and misread semantics respectively — the driver's background frame cap actually applies to applications that have lost focus, so the game itself is what gets limited after you alt-tab.

The same applies to memory cleaning. Purging the entire standby list adds only 382 MB of available memory at the cost of discarding 18.9 GB of file cache, which the game then has to re-read from disk. The default therefore purges only the pages the system marks as least likely to be needed again, taking 13 ms.

## Screenshots

<div align="center">
<img src="docs/overview-v14.png" width="49%" alt="Overview">
<img src="docs/library-v14.png" width="49%" alt="Library">
<img src="docs/policy-v14.png" width="49%" alt="Policy">
<img src="docs/anticheat-v14.png" width="49%" alt="Anti-cheat">
</div>

## Building

Uses the .NET Framework compiler included with Windows. Visual Studio is not required and there are no packages to restore.

```cmd
build.cmd      rem produces Pavise.exe
dev.cmd        rem kill old instance -> build -> launch
dev.cmd test   rem run the built-in self-tests (currently 175)
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

Taking money in any form for distributing Pavise or a modified version is not permitted. This includes selling copies, activation keys or download access; bundling it into a paid product or subscription; paywalls, paid unlocks and donation gates; preinstalling it on machines or system images sold for money; and using it to obtain traffic revenue. Voluntary donations are excluded, provided that paying or not paying has no effect on obtaining the software or support. Commercial use requires prior written authorisation by email.

When distributing, keep the licence and author information intact, inform recipients that the software may not be sold, and when distributing a modified version, state who modified it and what was changed.

This is a personal project provided as-is, with no guarantee of results or compatibility. Anti-cheat suppression, VBS and cache clearing can all have side effects; use it on your own machine and understand the associated risks first.

The latest build and source updates are always available free of charge in the WeChat and QQ groups. QQ group 1 is 1051472054, group 2 is 1101249532, group 3 is 383761286; on WeChat, add Ssssssstyle and mention Pavise. **If you paid for this, you were scammed** — request a refund and obtain it for free from the group.
