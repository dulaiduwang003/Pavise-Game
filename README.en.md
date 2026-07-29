<div align="center">

<img src="docs/icon.png" width="96" height="96" alt="Aegis">

# Aegis

A small Windows tool that keeps the resource boundary between a game and the desktop clear

`C#` · `WinForms` · `single executable` · `中文 / English / 日本語`

[简体中文](README.md) · **English** · [日本語](README.ja.md)

</div>

## In plain English: will it actually improve performance?

Aegis does not magically create extra performance or make your CPU and GPU run beyond their normal limits.

What it tries to do is simpler: **while you are gaming, it gives the game a better chance to use performance that would otherwise be taken by background apps, update tasks, user-mode anti-cheat processes, and other resource contention.** The game gets priority for CPU time, storage I/O, and scheduling while unused background programs are asked to step aside. When the game closes, Aegis tries to restore the previous state.

If your PC runs many background programs or regularly has CPU and disk contention, Aegis is more likely to improve stutter, frame-time consistency, and 1% lows; average FPS may improve a little as a result. If the system is already clean or the game is fully GPU-bound, the difference will usually be small. Overly aggressive settings can also cause compatibility problems with games, voice-chat apps, or anti-cheat software — Competitive mode is the most aggressive tier by default, and choosing it means accepting that tradeoff.

In one sentence: **Aegis does not create new performance; it helps keep the performance your PC already has from being disturbed while you play.** Measure the result by comparing the same scene in the same game with Aegis on and off.

## What it does

Aegis starts from an EXE or LNK that you explicitly add — not just games. Any process you want protected or boosted can be added. As long as that process is running, Aegis recognizes it; it does not need a visible window or the foreground, so alt-tabbing to the desktop or another app never drops it out of protection.

Some programs — self-extracting launchers in particular — run their real long-lived binary from outside the install directory, often a temp folder. Aegis can still recognize it by matching the entry executable's base name plus a common bitness/version suffix (like `xxx64` or `xxx_x64`), instead of requiring the process to stay under the configured directory.

I originally wrote it to deal with programs that kept using resources after a game closed. Target detection, CPU partitioning, anti-cheat controls, crash recovery, and the UI were added over time. It is still a local utility. It installs no service, includes no frame collector, and uploads no runtime data.

Automatic scheduling does not inject into the target process, edit its memory, or modify its files. Priority, I/O, page priority, CPU Sets, and hard affinity are read back where the API supports it. Windows provides no reliable process-level readback for Power Throttling, so that setting is judged by the write result only. The League column has exactly one file action, separately confirmed: reversible quarantine, which moves the optional layers and restores them in place from a manifest. The column offers no irreversible deletion. Use an in-game benchmark or your usual monitoring tool to measure performance.

## How it works

A process name alone is not enough to activate protection. Aegis combines the selected entry, in-root process relationships, bitness/version-suffix fallback matching, visible windows, foreground state, and render-oriented layouts. Anti-cheat, updater, crash-reporting, and external platform roles are removed before a session is accepted, even if the user explicitly picked that process. Once a session is confirmed, it stays protected across focus changes — switching to the desktop is never mistaken for the target having exited.

The selected preset controls what happens next.

- **Standard** applies a light Eco policy to current-session processes without a user-facing window and escalates only sustained heavy workloads; the foreground window and apps you're actively using keep their exemption.
- **Competitive** establishes the strictest CPU and priority boundary on its first pass — foreground and visible-window apps are no longer automatically exempt. Game families, anti-cheat, the whitelist, other user sessions, and genuine Windows core services remain hard safety boundaries.
- **Custom** lets you choose background control, service pauses, network changes, notification quieting, and refresh-rate handling separately — including, as an independent toggle, whether to adopt Competitive's suppression scope and power-plan intensity without switching preset.

The selected target process can receive High priority, higher I/O priority, GPU scheduling priority, and CPU Sets suited to the current processor. Background processes can receive lower priority or be moved to background cores. A failed write or mismatched readback is not reported as success for settings that support readback.

Anti-cheat processes, a live match's session host (such as the CN client), and their install directories are unconditionally exempt at every suppression intensity — this boundary does not change with preset or toggles, so suppression can never be the reason a match disconnects.

Recovery records include PID, creation time, image name, queryable original scheduling values, and original CPU Sets. The original Power Throttling state cannot be read reliably, so recovery returns its policy control to Windows instead of claiming an exact restore. If Aegis exits unexpectedly, the next launch continues from the remaining recovery records.

## Main features

- Target library based on EXE and LNK entries with drag and drop and local scanning — not limited to games
- Heuristic selection of the most likely target process so a resident launcher does not hold protection mode open
- Bitness/version-suffix fallback matching so a self-extracting launcher's real binary is still recognized outside the install directory
- Focus-independent protection: a confirmed session never loses protection or boost from losing the foreground or being minimized
- Vendor-neutral CPU Sets: hybrid CPUs follow OS-reported efficiency classes and retain full SMT, homogeneous CPUs with 6 cores or fewer are never hard-partitioned, and only CPUs with 8 or more cores reserve a small background partition
- Competitive placement can avoid physical cores with sustained abnormal DPC or interrupt activity
- Boost and suppression use both API results and readback where Windows supports it
- The League of Legends column provides WeGame-assisted launch, an LCU readiness gate, verified-path cleanup, true headless matches, and an independent recovery watchdog
- Anti-cheat controls are grouped by vendor with only the more conservative ACE group enabled by default
- Power plan, network throttling, MMCSS, Game DVR, notifications, and selected services can be restored
- HAGS and VBS are explicit persistent system settings and are not tied to automatic sessions
- Local session reports record duration and verified scheduling results
- Chinese, English, and Japanese interfaces can be switched at runtime

## Safety boundaries

Anti-cheat processes, a live match's session host and its install directory, Windows core services (session setup, authentication, service management, the compositor, audio processing, and similar), other login sessions, and Aegis itself are never treated as a suppression target at any preset or intensity — this boundary does not change with settings.

Below Competitive-grade intensity, Aegis does not treat the foreground process, applications with a visible main window, or their same-name processes and descendants as normal background targets. This keeps browsers, IDEs, and other front-end tools from protecting only their UI process. Additional exceptions come from the user-managed whitelist. Competitive-grade intensity (Competitive mode, or an explicit Custom toggle) drops the foreground/window exemption. Game families, anti-cheat, the whitelist, other user sessions, and Windows core services remain hard safety boundaries.

Anti-cheat suppression itself is intentionally aggressive. A protected program may reject a handle or immediately rewrite its state. Aegis logs that outcome and keeps recovery data instead of presenting a false success.

Logging off or shutting down Windows restores the changes that persist across a reboot (power plan, registry-backed switches), because those do not clear themselves. On exit the teardown is ordered by risk: process state first (priority, affinity, EcoQoS, core partitioning), then the slow service and display restores. If the exit budget runs out, what survives is the half a reboot cannot undo on its own.

## League of Legends column

The column lets WeGame perform normal authentication and launch. Aegis waits until the LCU login state succeeds and the current-summoner endpoint is available, then terminates only verified WeGame and optional-component processes. When a match starts, it closes the lobby UX through the client's native LCU endpoint. An independent watchdog reacquires local credentials and restores the lobby after the match. Manual restore bypasses headless mode for the rest of that session.

The irreversible Cross cleaner that used to live in Settings has been removed. `Cross` now goes through the same reversible quarantine as every other optional layer, so no unrecoverable deletion path remains.

Reversible quarantine handles Cross, DiagnosticAssistant, FeedBack, NetworkAssist, TQM, and TenioDL. It moves them into a hidden same-volume warehouse after writing a manifest, never deletes or overwrites, refuses to run while League or WeGame is active, and preserves both sides of restore conflicts. If a client update re-downloads a component so restore can no longer overwrite it, the batch record can be discarded once every item is verified back in place, and quarantine becomes available again.

The runtime path performs no process injection, memory editing, or game-core file modification. It never targets `League of Legends.exe`, Riot core/authentication/patching components, or ACE. LCU credentials remain short-lived in memory and are never logged, persisted, or passed to a child process. Game-directory changes occur only after separate confirmation of reversible quarantine. With the column disabled Aegis does not scan disks, enumerate processes, or contact the client API, and WeGame is launched as the signed-in user rather than as administrator, so the game and its anti-cheat are not elevated with it.

## HAGS and VBS

The HAGS switch changes the system GPU scheduling setting and takes effect after a reboot. The previous value is saved first.

The VBS switch changes DeviceGuard registry values and `hypervisorlaunchtype`. It also requires a reboot. Disabling it affects Memory Integrity, WSL2, Docker, Hyper-V, and Windows Sandbox, so this is a tradeoff the user needs to make deliberately.

Both are persistent system settings rather than automatic session changes. Failed recovery keeps the snapshot for retry.

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

The project uses the .NET Framework C# compiler included with Windows. There are no packages to restore and Visual Studio is not required.

```cmd
build.cmd
```

The script generates the icon and then builds `Aegis.exe` with an administrator manifest, product metadata, and file version `1.5.0.0`.

Source builds are not Authenticode-signed by default, and an unsigned personal open-source release can still be distributed. A publisher may use its own trusted code-signing certificate to verify publisher identity or improve the SmartScreen experience.

## Running and data storage

- Double-click `Aegis.exe` and it will remain in the notification area
- Changing other processes and system settings requires administrator rights
- One-click recovery is available in the maintenance section of Settings
- Startup uses a scheduled task
- The app performs one GitHub version check after launch and uploads no machine data

The default data directory is `%AppData%\Aegis`.

- `Aegis.profiles.dat` stores target profiles
- `Aegis.whitelist.txt` stores user exclusions
- `Aegis.reports.log` stores session reports
- `Aegis.log` stores runtime logs
- `HKCU\Software\Aegis` stores interface and feature settings

Place an empty `Aegis.portable` file beside the executable to use that writable directory for data instead.

## Source layout

- `src/Core` contains target detection, scheduling, suppression, and recovery
- `src/Platform` contains Windows APIs, settings, paths, and service wrappers
- `src/Ui` contains the WinForms interface and owner-drawn controls
- `src/Tests` contains the built-in self-tests
- `scripts` contains the application smoke test

The implementation uses Windows APIs including `SetPriorityClass`, `SetProcessDefaultCpuSets`, `NtSetInformationProcess`, `SetProcessInformation`, and `D3DKMTSetProcessSchedulingPriorityClass`. Process creation time is part of identity validation so a reused PID cannot redirect recovery to a new process.

## Validation scope

The built-in suite currently contains `63` tests covering CPU topology and core-count tiering, CPU Sets and hard-affinity recovery, PID reuse, real child-process boosting, staged suppression, the bitness/version fallback-matching boundary, focus-independent session protection, foreground and user-window protection, event-driven scan budgeting, session boundaries, League credential parsing, strict cleanup boundaries, reversible quarantine round-trips and conflict protection, session reports, release metadata, and off-screen UI rendering. A missing platform capability is reported as `SKIP` rather than `PASS`.

The same-core contention test deliberately places two compute processes on one core and suspends the contender. It only shows that throughput recovers when CPU time is released. It is not evidence of real-game FPS or 1% Low gains.

HAGS, VBS, power plans, MMCSS, network throttling, service pauses, and compatibility with real anti-cheat products do not currently have end-to-end automated tests. They require validation on the target system.

## Buy the author a coffee

If Aegis is useful to you, you can buy me a coffee.

<div align="center">
<img src="docs/wechat.png" width="240" alt="WeChat support code">
&nbsp;&nbsp;
<img src="docs/alipay.png" width="240" alt="Alipay support code">
</div>

## Author and license

Author  bdth

Email  2074055628@qq.com

Released under the [MIT License](LICENSE).

This is a personal open-source project provided as is. Performance and compatibility are not guaranteed. Anti-cheat suppression, VBS changes, service pauses, and cache deletion can all have side effects. Use it only on computers you control and review the relevant risk first.
