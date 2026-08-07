<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Pavise">

# Pavise

A small Windows tool that hands your system's resources to the game while you play

`C#` · `WinForms` · `Simplified Chinese UI`

[简体中文](README.md) · **English** · [日本語](README.ja.md)


</div>

## Will this actually make my games run better?

Pavise does not conjure performance out of nowhere. It cannot push your CPU or GPU past what they can already do.

What it does is closer to this: **while you play, it takes back the performance that background apps, update tasks and anti-cheat were using, and gives it to the game.** The game gets first claim on CPU, disk and scheduling; idle background programs step aside. When the game exits, everything goes back.

So if your PC has a lot running in the background and the CPU or disk is often busy, Pavise is likely to help with stutter, frame-time consistency and 1% lows — average FPS may tick up as a side effect. If your system is already clean, or the game is entirely GPU-bound, expect little or nothing. Push the settings too hard and you can break things instead: games, voice chat or anti-cheat may misbehave. Competitive mode is the most aggressive tier by default, and picking it means accepting that.

In one line: **Pavise doesn't create performance, it stops the performance you already have from being disturbed.** Judge it by running the same scene in the same game with it on and off.

## What it does

Add a game's EXE or shortcut to the target library (any program you want protected works, not just games) and Pavise will recognise it: as long as it's running it stays protected, and switching to the desktop or minimising never drops it.

Some launchers run the real program from somewhere else entirely — a temp folder, for instance. Pavise finds it by matching the file name plus the usual 64-bit and version suffixes, so the process doesn't have to live in the folder you pointed at.

It's a purely local tool: no service installed, nothing uploaded; it doesn't inject into the game or touch its memory or files. Every system setting and process state it changes is recorded, and wherever the API allows it the value is read back after writing — a value that doesn't read back as expected is not counted as a success. Measure any performance change yourself, with the game's own benchmark or your usual monitoring tool.

## How it works

Recognising a game takes more than a process name. Pavise weighs the entry you added, how processes inside the install folder relate to each other, window state and foreground state together. Launchers, updaters, crash reporters and anti-cheat are ruled out by role first — even if that's what you picked, it won't be treated as the game itself. For games that start through a launcher (launcher runs first, then hands off to the real game), Pavise remembers the real one after seeing it once and recognises it directly next time; if that remembered path stops being valid it's dropped automatically. A confirmed session isn't lost just because you tab away.

Once it knows what's running, the preset decides what happens:

- **Standard** — windowless background gets a light touch first; only the ones that keep hogging resources get pushed harder. Whatever you're actively using is left alone.
- **Competitive** — everything outside the game gets pushed down immediately, windows or not. The only things left alone are the current foreground app together with its child processes, the game family, anti-cheat, network accelerators, your whitelist, other user accounts and Windows core services.
- **Custom** — pick background control, service pauses, network, notifications and refresh rate one by one. Competitive's suppression scope and power intensity are also available separately, without switching preset.

The game gets high priority, higher I/O and GPU scheduling priority, and the right cores for your CPU. Background processes get demoted or moved to other cores depending on the preset. Every write is read back where possible, and a failed write is not counted as a success.

Anti-cheat and the host of a live match (such as a regional client) are exempt at every intensity, regardless of preset or toggles — suppressing them only gets you disconnected.

Recovery works from records: for every process it touches, Pavise stores the state from before (validated against PID and creation time so it can't restore onto the wrong process) and puts it back on exit. If Pavise crashes, the next launch picks up where it left off.

## Main features

- Target library: EXE / LNK / Steam shortcuts, drag and drop, plus one-click scanning for installed games (reads Steam, Epic, GOG, Ubisoft, Riot, WeGame, Battle.net, Xbox and Microsoft Store records; network accelerators are never suggested as games)
- Finds the real game automatically so a launcher can't hold protection open; covers real binaries that run from another folder, same-folder launcher processes (Mount & Blade II, for one) and launcher hand-off (League of Legends)
- Protection survives tabbing away and minimising
- CPU partitioning is vendor-neutral: hybrid, X3D and multi-processor-group systems are all handled; 6 cores or fewer are never force-partitioned, and only 8 cores and up reserve a small background partition
- System audit page: read-only check of what this machine supports, what's measured live and what's already set, with every verdict labelled by evidence grade (measured here / measured on a bench / mechanism is clear / not verified); NVIDIA settings can be write-tested with one click
- A newer build takes over a running older one: the old instance goes through its own exit path and fully restores before quitting — it is not killed
- USB interrupt steering: the interrupt storm from a 4K/8K polling mouse can be moved off the game's cores
- League of Legends column: WeGame-assisted launch, precise cleanup, truly headless matches, independent recovery watchdog
- Anti-cheat controls grouped by vendor, three intensity tiers each, all defaulting to the gentlest
- Power plan, network, Game DVR and notifications are all restorable
- NVIDIA tuning: maximum-performance power and a frame cap, original values snapshotted, off restores them
- Standby-memory cleanup before a match (off by default) and an MPO troubleshooting switch
- Each session's outcome goes to the runtime log: how long you played, how many background processes were suppressed, how much CPU they used in total
- The UI ships in Simplified Chinese only; the multi-language machinery is still in the code

## Safety boundaries

Anti-cheat, the host of a live match and its folder, Windows core services (logon, authentication, the desktop compositor, audio and the like), other login sessions and Pavise itself are never suppressed at any preset or intensity — no switch changes this.

Below competitive intensity, foreground apps and windowed apps — along with their same-name processes and children — are never suppressed, so multi-process programs like browsers and IDEs don't end up with only their UI shell protected. Competitive intensity (Competitive mode, or the matching Custom toggle) removes the "has a window" exemption, but the current foreground app and its children are still left alone; network accelerators stay exempt too. Anything else you want spared goes in the whitelist.

Anti-cheat suppression is aggressive by nature. Some protected programs will refuse the change or put it straight back. That is recorded as a failure with the recovery data kept, and is not counted as a success.

Logging off or shutting down restores the changes that would otherwise survive a reboot (power plan, registry-backed switches) first. If there isn't enough time on exit, what gets saved is the part a reboot can't fix by itself.

## League of Legends column

WeGame handles login and launch, nothing more. Once the lobby is confirmed ready, Pavise shuts down WeGame and the regional add-on processes using verified install paths. When a match starts, the lobby window is closed through the client's own interface and an independent watchdog brings it back afterwards. Manual restore is always available and switches off the automatic flow for that session.

Add-on cleanup is a direct delete behind a separate confirmation: it removes components such as the AI coach and iCreate recording, which the client re-downloads on its next update. The game itself, the login path and the updater are never in scope, and the operation is refused while League, WeGame or anti-cheat is running.

None of this injects into processes, edits memory, or touches game core files or anti-cheat. Login credentials live briefly in memory and are never written to a log or to disk. With the column switched off, Pavise doesn't scan disks or contact the client at all. WeGame is launched as the signed-in user, not as administrator, so the game and its anti-cheat aren't elevated along with it.

## HAGS and VBS

Both change low-level system settings, both need a reboot, and the original value is snapshotted first. Turning VBS off means WSL2, Docker, Hyper-V and Windows Sandbox stop working — that tradeoff is yours to make.

They sit on the "System environment" page along with MPO and the GPU/NIC/USB interrupt affinity switches. Everything on that page needs a reboot and does **not** revert when you uninstall Pavise, which is why it's kept apart from the scheduling settings that undo themselves when the game exits.

## Interface

<div align="center">
<img src="docs/overview-v14.png" width="49%" alt="Overview">
<img src="docs/library-v14.png" width="49%" alt="Target library">
<img src="docs/policy-v14.png" width="49%" alt="Policy">
<img src="docs/anticheat-v14.png" width="49%" alt="Anti-cheat controls">
<br>
<img src="docs/reports-v14.png" width="49%" alt="Session reports">
<img src="docs/settings-v14.png" width="49%" alt="Settings and recovery">
</div>

## Build

The project builds with the .NET Framework C# compiler that ships with Windows. No Visual Studio, no packages to restore.

```cmd
build.cmd
```

For development there's a single command that stops the old instance, rebuilds and launches:

```cmd
dev.cmd        rem stop old instance -> build Pavise.dev.exe -> launch
dev.cmd test   rem build with self-tests, run them, print a summary
```

The script generates the icon first, then builds `Pavise.exe` with an administrator manifest, product name, company name and the current file version. The version number comes from `App.Version` in `src/Program.cs`.

Source builds are not Authenticode-signed. An unsigned personal release is fine to distribute; a publisher who wants verified identity or a better SmartScreen experience can sign with their own trusted certificate.

## Running and data storage

- Double-click `Pavise.exe` and it goes to the notification area
- Changing other processes and system settings requires administrator rights
- One-click recovery lives in the maintenance section of Settings
- Startup uses a scheduled task
- One GitHub version check after launch; no machine data is uploaded

The default data directory is `%AppData%\Pavise`:

- `Pavise.profiles.dat` — target profiles
- `Pavise.whitelist.txt` — your whitelist
- `Pavise.log` — runtime log
- `HKCU\Software\Pavise` in the registry — interface and feature switches

Put an empty `Pavise.portable` file next to the executable, and if that directory is writable the data goes there instead.

## Source layout

- `src/Core` — target detection, scheduling, suppression and recovery
- `src/Platform` — Windows APIs, settings, paths and service wrappers
- `src/Ui` — WinForms interface and owner-drawn controls; `src/Ui/Pages` is one file per page, `src/Ui/Controls` holds the custom controls
- `tests` — the built-in self-tests (compiled in only for `build.cmd xxx.exe --selftest`; release builds contain no test code)
- `scripts` — application smoke test

The implementation uses Windows APIs including `SetPriorityClass`, `SetProcessDefaultCpuSets`, `NtSetInformationProcess`, `SetProcessInformation` and `D3DKMTSetProcessSchedulingPriorityClass`. Process creation time is part of identity validation, so a reused PID cannot redirect a change onto a different process.

## Validation scope

The built-in suite currently contains `120` tests, covering target detection and session protection, suppression and recovery (including PID reuse and crash wake-up), CPU topology and partitioning, profile storage format compatibility and unknown-version protection, game scanning and accelerator filtering, the boundaries of the launcher-learning mechanism, system audit thresholds, League column boundaries and UI rendering. A missing platform capability is recorded as `SKIP`, never as `PASS`.

The same-core contention test deliberately puts two compute processes on one core and suspends the contender. It only shows that throughput recovers once CPU time is released — it is not evidence of real-game FPS or 1% Low gains.

### Synthetic measurement of what suppression buys

`--contention-lab` is a multi-round paired A/B bench. The victim is a single-threaded fixed-work frame loop (no throttling, no vsync, so frame time directly reflects how much CPU time it got); the contenders are as many fully loaded processes as there are logical processors. Each round alternates between "left alone" and "isolated suppression", using the real suppression core rather than a simulation.

Six rounds on a 6-core / 12-thread laptop, 2026-08-02:

| Metric | Left alone | Isolated suppression |
|---|---|---|
| Median frame time | 0.65 ms | 0.65 ms |
| 1% worst frames | 3.4 → 14.2 ms (degrading each round) | 1.09 ~ 1.30 ms (stable throughout) |

All six rounds improved. Median improvement in the 1% worst frames was **90.7%**; median frame time improved **0.0%**. That matches the design expectation — suppression reduces stutter rather than raising average FPS — and it's the first time that claim has local data behind it.

Worth calling out separately: in the left-alone segments the tail frames kept getting worse over time (3.4 ms climbing to 14.2 ms), while the suppressed segments stayed near 1.1 ms for the same duration. Suppression didn't just lower the tail, it made the tail immune to that creeping degradation.

This is a synthetic load. The victim is CPU-only — no GPU, no VRAM, no disk I/O — and its threading is far simpler than a real game's. It proves that isolated suppression blocks the damage CPU contention does to the frame-time tail. It does not let you extrapolate an FPS gain in a real game.

### The gain comes from lowering priority, not from core partitioning

The same bench, restructured as a three-arm comparison (left alone / priority only / priority + partitioning), over five rounds:

| Comparison | Improvement in 1% worst frames (median across rounds) |
|---|---|
| Priority only vs left alone | 89.5% |
| Priority + partitioning vs left alone | 90.4% |
| Additional contribution from partitioning | −2.0% |

**Essentially all of the gain comes from lowering priority. Core partitioning added no measurable contribution.**

One caveat has to be stated alongside it: on this 6-physical-core machine `HasSafeBackgroundPartition()` returns false — per the established `CpuPartitionPolicy.BackgroundCoreCount` policy, 6 cores or fewer get no background partition, so the core-pinning branch of the `Isolated` tier never actually ran here. The table therefore proves "on a machine that doesn't partition, lowering priority already captures the entire gain", not "partitioning is useless". Partitioning itself needs separate validation on a machine with 8 or more cores.

### Freezing is the only tier that improves the median frame

Extending the bench to four arms (left alone / priority only / priority + partitioning / frozen), two independent runs gave the same result:

| Tier | Median frame | Frames | 1% worst frames |
|---|---|---|---|
| Left alone | 0.65 ms | ~18000 | 1.5 ~ 18.3 ms |
| Priority only | 0.65 ms | ~22000 | 0.9 ~ 1.5 ms |
| Priority + partitioning | 0.65 ms | ~22000 | 0.9 ~ 1.4 ms |
| **Frozen** | **0.59 ms** | **~24800** | 0.8 ~ 1.0 ms |

The first three tiers all sit at exactly 0.65 ms; only freezing pushes it to 0.59 ms (−9.6%), and it did so in every round without exception. The physical explanation is direct: lowering priority and partitioning only change the queueing order, and the suppressed processes still burn CPU cycles. Freezing stops them completely, so throughput changes too (+11% frames).

Freezing's extra gain on tail frames is less consistent (a further 15% ~ 21% median improvement, but with wide variation between rounds), because the isolated tier has already pushed the tail down close to the noise floor and there isn't much room left.

Every round of freezing was verified to have actually taken effect: after applying the tier, the suppressed processes' CPU time was sampled twice in a row and only counted if it had stopped growing. Without that check, "thought it was frozen but it wasn't" data could easily have been taken as a conclusion.

### The interrupts are not on CPU 0

A common piece of community advice is to strip threads 0 and 1 out of a game's CPU affinity, on the theory that the system is pinned to the first core. `--irq-map` measures each core's DPC + interrupt time share via `NtQuerySystemInformation`. On the development machine (Intel hybrid, 24 physical cores / 32 threads), three rounds with a 30-second window:

| Physical core | Round 1 | Round 2 | Round 3 |
|---|---|---|---|
| **4/5** | **1.77%** | **1.72%** | **2.40%** |
| 16 | 0.21% | 0.89% | 0.47% |
| **0/1 (hosts CPU 0)** | **0.00%** | **0.00%** | **0.05%** |

**Interrupts sit consistently on physical core 4/5; the core hosting CPU 0 carries 2% ~ 4% of that.** Every round picked the same core — the target is clear and stable. Modern devices spread interrupts via MSI-X, so which core carries them depends on what's installed in that machine. Assume by position and you dodge the wrong core: on this machine, masking off threads 0/1 gives up a clean core while the genuinely dirty 4/5 stays inside the game's partition.

The measurement has one trap worth knowing about: interrupt time accumulates per clock-interrupt tick, and a 3-second window holds only about 192 ticks, giving a **resolution of 0.52%** — anything below that quantises to zero. The first run's "almost every core reads 0.0000%" with a 3-second window was quantisation noise, not a genuinely clean machine. Accurate measurement needs a 30-second window (0.05% resolution).

**A feature built on this data — measure the cores, move the game off the interrupt core — shipped in v1.6.2 and was removed a day later.** Not because the implementation was wrong: it picked the correct core every single round. It was removed because sizing up the magnitude changed the conclusion. A single interrupt disturbance is microseconds; a 2% ~ 3% share spreads across tens of thousands of tens-of-microseconds events, and no single one eats a noticeable slice of a frame's budget. Giving up a physical core, meanwhile, is a certain cost (12.5% of the P-cores on a hybrid chip). Trading a certain cost for a gain that most likely lands inside measurement noise doesn't add up. `DpcSampler`'s original 4% storm threshold turns out to be the right design: only a genuine interrupt storm (4K/8K mice, misbehaving drivers, 10%+ territory) is worth surrendering a core for. Merely being a bit dirty is not.

The `--irq-map` diagnostic stays: it tells you whether this machine's interrupt distribution is abnormal (above 5%, investigate the device or steer it away with interrupt affinity), and whether the folk remedy of masking threads 0/1 dodges the right core on your machine or the wrong one. The system audit page has a quick version built in.

The three NVIDIA tuning writes (maximum-performance power, frame cap, pre-rendered frame limit) were write-read-restore tested on the development machine's RTX 3090 via `--nv-probe` and **all three genuinely take effect**. The low-latency mode setting ID that was removed is rejected by the driver (NVAPI -160), which confirms the decision to drop it in 1.6.1. The audit page offers the same one-click test so any machine can verify for itself.

HAGS, VBS, MPO and compatibility with real anti-cheat products have no end-to-end automated tests. They need validating on the target machine.

## Buy the author a coffee

If Pavise is useful to you, you can buy me a coffee.

<div align="center">
<img src="docs/wechat.png" width="240" alt="WeChat support code">
&nbsp;&nbsp;
<img src="docs/alipay.png" width="240" alt="Alipay support code">
</div>

## Author and license

Author: bdth

Email: 2074055628@qq.com

WeChat: Ssssssstyle (bug reports, feature requests, or questions about using it)

Released under the [Pavise License](LICENSE): the source is public and free to use and modify, but **selling it is prohibited**.

This is a personal project provided as is. Performance and compatibility are not guaranteed. Anti-cheat suppression, VBS changes, service pauses and cache deletion can all have side effects. Use it only on computers you control, and read up on the relevant risk first.

### What you may do

Free of charge, no need to ask me:

- Use it for any purpose — personally, or inside a company, internet cafe or esports venue
- Copy it and distribute it to anyone free of charge
- Modify the source, build your own version, and distribute that free of charge
- Read, study and reference the source code

### No sale

**Taking money in any form for distributing Pavise or a modified version is not allowed.** This includes, without limitation:

- Selling copies, activation keys, accounts or download access
- Supplying it as part of, as a bonus to, or as added value for a paid product, paid service or subscription
- Paywalls, paid downloads, paid unlocks, donation gates
- Preinstalling it on complete machines, devices or system images that are sold for money
- Trading it for revenue sharing, or using it to drive traffic towards other paid offerings

Voluntary donations are fine — provided that paying or not paying never affects whether you get the software, any of its features, or any support.

Commercial use requires prior written authorisation. Email 2074055628@qq.com.

### When you distribute it

- Retain the licence, the copyright notice and the author information in full
- Tell recipients that this may not be sold and that they are bound by the same licence
- When distributing a modified version, state who changed it and what was changed

**The "Pavise" name and icon are not covered by the licence grant.** A modified version must be released under your own name and icon, not as Pavise — I cannot verify what a modified build does, and sharing the name would send the responsibility to the wrong place.

The latest version and source updates are always provided free in the WeChat and QQ groups (QQ group 1051472054; WeChat Ssssssstyle, mention Pavise). **If you paid for it, you were scammed** — ask for a refund, then get it for free from the groups.
