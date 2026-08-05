# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.6.3] - 2026-08-05

### Fixed

- Xbox / Microsoft Store games (Forza Horizon 5 among them) could not be added to the library at
  all. They install under `WindowsApps` / `XboxGames`, where the ACL denies reading the executable
  even to administrators, which broke both entry paths: the file dialog's own existence check opens
  the file and surfaced a system-level "no permission to open this file" error before Pavise saw
  anything, and "add from running process" failed the PE-header check because the content could not
  be read. A file that demonstrably exists but cannot be read is now accepted on the strength of its
  existence; non-existent paths and non-executables are still rejected. Runtime detection was never
  affected — process paths come from `QueryFullProcessImageName`, which does not read the file.

### Added

- System audit page (hardware & system group). A read-only aggregation of four groups: write
  capability (NVIDIA driver interface, CPU Sets partitioning, EcoQoS), local measurements (CPU
  topology, whole-machine load, interrupt distribution), persistent system settings (HAGS, VBS,
  Game Mode, MPO, power plan), and a verdict list. Every verdict carries an evidence grade —
  measured on this machine / measured on the bench rig / mechanism-clear / unverified — and
  features that tested as useless simply do not appear as recommendations.
- The audit page's NVIDIA write probe: a write-readback-restore pass against a dedicated probe
  profile, verifying the driver actually accepts the three deep-tuning writes rather than trusting
  the write call's return value. All three verified effective on the dev machine's RTX 3090.

### Removed

- The v1.6.2 interrupt-core avoidance. It reliably picked the right core every round; what changed
  on re-evaluation was the magnitude argument. A single interrupt costs microseconds, so a steady
  2–3% interrupt share spread over tens of thousands of tiny events cannot produce a perceptible
  stutter, while surrendering a physical core is a certain cost (12.5% of the P-cores on a hybrid
  part). Trading a certain cost for a benefit that most likely drowns in measurement noise is not a
  trade. The pre-existing 4% DPC-storm threshold turns out to be the right design — only genuine
  interrupt storms (high-report-rate mice, broken drivers, 10%+ shares) justify giving up a core —
  and it stays unchanged. The `--irq-map` diagnostic stays; the audit page embeds its quick form.

## [1.6.2] - 2026-08-05

### Added

- Interrupt-core avoidance (off by default; requires strict CPU partitioning or competitive mode,
  and at least 8 physical cores). Device interrupts and DPCs concentrate on a few cores, and a game
  thread scheduled there waits behind interrupt servicing. Once a session is active, the interrupt
  load of every core is measured once, and if one physical core is a clear outlier it is taken out
  of the game partition. At most one core is ever given up, and a DPC storm already being avoided
  takes precedence over the steady-state pick.
- `--irq-map`, a diagnostic that reports the DPC + interrupt share of every core, which core would
  be picked, and whether that core is even inside the game partition.

### Changed

- Interrupt avoidance does **not** target CPU 0. Measurement on the development machine (Intel
  hybrid, 24 physical cores / 32 threads) found the opposite of the folklore: interrupts sit
  steadily on physical core 4/5 at 1.7%–2.9%, while the core carrying CPU 0 measures 0.05%–0.10%,
  i.e. 2%–4% of the dirtiest core. MSI-X spreads interrupts across cores, so which core carries them
  is a property of the machine — masking threads 0/1 here would give up a clean core and leave the
  genuinely dirty one inside the game partition. The avoidance target is therefore always measured.
- The existing DPC avoidance could not reach real loads. It only triggered on storms of 4% or more,
  but the dirtiest core on the development machine peaks at 2.9% and never qualified. Interrupt time
  accumulates per clock tick, so a 3-second window resolves only to 0.52% and anything below that
  quantises to zero — the old check was mostly reading quantisation noise. The new steady-state check
  measures over a 30-second window (0.05% resolution), which is also why avoidance only takes effect
  about half a minute into a session.

- Licence: the project moves off the GNU GPL v3 to the Pavise License, a source-available licence
  that keeps the source public and free to use, copy, modify and redistribute — for any purpose,
  personal or organisational — but prohibits taking money for distributing it in any form:
  no sold copies, no paid downloads or unlocks, no bundling into paid products, no preinstalls on
  machines sold for money. Voluntary donations remain fine as long as payment is never a condition
  of obtaining the software, a feature or support. The "Pavise" name and icon are outside the
  grant, so modified builds must ship under their own name. Commercial use requires prior written
  authorisation from the author. The About page now reports the licence accordingly.

### Removed

- The League column's competitive graphics feature is gone, both of its groups: the quality
  clamp (shadows, anti-aliasing, environment/effect/character quality, HUD animation, decorative
  effects, light beams) and the presentation mode (exclusive fullscreen, vsync). It rewrote ten
  known keys in the game's own `Game\Config\game.cfg`, which is the same file the in-game settings
  screen writes — anything it did can be done from the game's own settings, and the original-value
  snapshots it kept in the registry only existed to undo edits the user could undo themselves. Installs that already
  applied it keep the competitive values; change them in the game's settings screen. Any leftover
  snapshot under `HKCU\Software\Aegis` is inert.
- NVIDIA low latency mode. Its Ultra half never worked: the driver does not recognise setting id
  `0x0005F543` at all, returning `SETTING_NOT_FOUND` for both reads and writes, which is what the
  session log had been reporting. The only thing that ever landed was the maximum pre-rendered
  frames value written alongside it, i.e. plain "On" rather than the "Ultra" the switch advertised.

### Fixed

- Self-protecting processes (HIPS-style security software) that refuse every policy write no longer
  leave dead restore snapshots behind. When the three core writes are all refused and readback shows
  the process was never actually modified, the entry is reclassified as handle-protected, the name
  goes onto a persistent skip roster consulted by later sessions, and nothing is queued for restore.
  Crash recovery likewise drops journal lines whose snapshot already matches the live process, so
  stale HIPS records stop surviving restarts. "Restore recorded scheduling" clears the roster.
- Game-exit churn: when the game process disappears, deactivation now waits out a 15-second grace
  window (with a 5-second transition scan cadence) before tearing the session down, so launcher
  shells and instant restarts no longer trigger a full restore-and-reapply cycle within seconds.
- The power-plan retry storm: activation failures now propagate back to the caller, retries back
  off from 30 seconds up to 5 minutes instead of firing every audit, and the parameter-tuning log
  line is emitted once per policy change instead of on every failed attempt.
- Session log polish: render-lane sampling failure names the pid and explains the launcher case,
  the refresh-rate guard says so when the game already switched the rate back itself, deactivation
  counts are labelled as session-cumulative, and NVIDIA DRS write failures include the NVAPI status
  code.
- One-time whitelist cleanup: builds from earlier today merged eleven third-party exemptions
  (OBS, DingTalk, Zoom and friends) into the user whitelist file; the preset has since been reduced
  to system-core-only, and on next start those eleven names are removed once from existing files.
  Anything the user re-adds afterwards stays.
- Library-root exemption ignores drive-root profile paths (a profile whose root resolved to `D:\`
  would have exempted every process on the drive from background suppression).
- Startup-task migration refuses volatile locations (Temp, WeChat received-files cache, INetCache,
  recycle bin): an exe run from such a folder no longer captures the autostart task, since the file
  is likely to be cleaned up and break autostart until the next self-heal.
- With two library games running at once (say Genshin Impact idling while League of Legends is the
  detected session), competitive mode treated the second game as an ordinary background process —
  idle priority, EcoQoS, efficiency cores — because the sweep only exempted the active game's
  install root. Background eligibility now checks the process path against every library game's
  root, so being in the game library is itself protection. Only one game still gets boost, per-game
  driver settings and frame evidence: the detected session does not change.

- The League install-location scan no longer takes over the whole window on startup. The scan
  overlay was parented to the form and covered every page; it now covers only the League column
  page. Background rescans were also both loud and expensive: even with a valid cached install the
  service re-ran full discovery every two minutes, and because a TCLS-layout install has no
  wegame.exe the WeGame hunt fell through to probing common paths on every drive each time — a
  multi-second stall whenever a disk had spun down. Background passes now stay silent while the
  cached root is still valid and skip the all-drives probe entirely; the full hunt with the overlay
  is reserved for user-initiated actions or a genuinely unknown install location. When the install
  location is unknown or stale (uninstalled, moved to another disk), the hunt no longer starts on
  app launch at all — not everyone plays League. It waits until something League-shaped actually
  happens: the column page is opened, the column switch is turned on, a column action runs, or a
  League/WeGame process appears. Until then the column simply reports that no install is known. The
  scan overlay
  now carries a cancel button — cancelling aborts the drive walk between probes and holds further
  automatic discovery for ten minutes, until a user action or a League process appearing re-arms it.
  While a scan is running the mode switcher is locked, so a mode change can never race a discovery
  pass.

- The per-game NVIDIA summary line reported settings by the user's switch rather than by what was
  actually written, so a setting that had just failed still appeared in the success line as long as
  any other setting in the same batch succeeded.

- Switching monitors or changing display scaling left the window enlarged with its contents crammed
  into the top-left corner and the rest of the frame bare. The process declares PerMonitorV2, so
  Windows resizes the window on a DPI change, but the scale factor was computed once at startup and
  never updated, leaving all 900-odd scaled control coordinates and every cached font at the old
  scale. Recovering used to require restarting Aegis. The panel now checks the DPI of the monitor it
  is on each time it is shown, and rebuilds itself when the scale no longer matches.

  It deliberately does not listen for WM_DPICHANGED. Reacting automatically raises a question that has
  no cheap answer — when is it safe to move the window? A rebuild resizes it; crossing onto a monitor
  with a different scale produces another DPI change, and the two monitors ping-pong. An early attempt
  that also wrote the window position rebuilt the whole window ten times in 45 seconds and, because a
  game entering fullscreen changes the effective DPI too, repeatedly threw the running game out of
  fullscreen. Guarding that path needs deferral conditions, a cooldown and a compensating retry — a
  state machine protecting what is only a cosmetic problem, and one whose defects are invisible in the
  log until someone reproduces them. Rebuilding only when the panel is opened means it happens solely
  at a moment the user has already switched away from the game, so that class of risk does not exist
  rather than being fenced off. The cost is that changing scale while the panel is open requires
  closing and reopening it.

### Added

- Write-failure fuses on every UI-backed switch: two consecutive failed writes turn the switch off,
  persist the decision and say so in the log; re-enabling the switch resets the counter and resumes
  attempts. Covers the nine environment steps (notifications, download pause, refresh-rate guard,
  foreground scheduling, service pause, MMCSS, Game DVR, visual effects, Windows Update pause), the
  power plan, and the per-game NVIDIA driver tweaks (max performance, frame limiter, low latency —
  a failed session save counts against every key it carried). Restore direction is never fused.
  "Restore recorded scheduling" resets all fuses and counters; the switches stay off until the user
  re-enables them.
- Competitive power policy now also writes the hidden governor knobs when the machine exposes them
  (probed read-first, skipped silently otherwise): explicit 100% maximum processor state, rocket
  performance-increase policy, single-step decrease policy, full latency-sensitivity-hint
  performance, and unparked efficiency cores on hybrid CPUs.
- Evidence mode asks for confirmation before turning on, stating that collection itself has a cost,
  may slightly affect frame rate, and is meant for testing and diagnosis.
- Background GPU demotion, off by default, as a new switch in the custom policy dialog. When enabled,
  background processes suppressed at the restrained tier or above also have their GPU scheduling
  priority class lowered — restrained maps to below-normal, isolated to idle — so their rendering
  and compute submissions yield to the game when the GPU is saturated. The original class is read
  before the first write, carried in the suppression journal (legacy journal lines still parse),
  verified by readback on the existing reconcile cadence, and restored together with everything else
  on release or crash recovery. Anti-cheat-reason suppression never touches GPU state. Processes
  without a GPU context are skipped; if one creates a context mid-session, the next reconcile pass
  snapshots and demotes it. Streaming, recording or conferencing software that must keep working
  while a game runs should be whitelisted before turning this on, since idle-class GPU work is
  starved while the game keeps the GPU busy; the preset whitelist deliberately stays
  system-core-only, exemptions are the user's call.
- Frame evidence no longer counts frames rendered while the game is unfocused. Games cap their frame
  rate in the background (League drops to roughly 30 fps), and those slow frames all landed in the
  tail statistics, so the 1% and 0.1% lows measured the background frame limiter instead of real
  stutter, and every alt-tabbed minute diluted the session average. The Present callback now polls
  the foreground process at most once per 100 ms and drops unfocused intervals plus the single
  interval straddling each focus change; the evidence line reports how much unfocused time was
  excluded, and a session with fewer than 30 focused frames says so instead of reporting polluted
  numbers.
- Competitive power tuning now writes the processor energy performance preference (EPP, and the
  efficiency-class variant on hybrid CPUs) instead of relying on a pinned 100% minimum processor
  state. A 100% floor keeps clocks up even with no load, which soaks heat and makes a laptop reach
  its thermal or power limit sooner, and it does not make the render thread reach turbo any faster —
  EPP is what governs how eagerly the CPU ramps. The floor drops to 20% only when the machine
  actually exposes EPP in its power scheme; where it does not, the previous 100% behaviour is kept
  verbatim so nothing regresses on CPUs without HWP/CPPC. Both settings are written into the private
  duplicated scheme Aegis already owns, so restoring is still just switching schemes. This also fixes
  an inconsistency in the old competitive profile: on battery it pinned the frequency floor to 100%
  while leaving the energy preference at the balanced default of 50.
- Evidence mode now records which thread submits frames. The Present thread id comes from the ETW
  event header, so this needs no handle on the game, no injection and no memory access. The session
  record reports the dominant thread's share, how many threads submit, and how stable the dominant
  thread is across time windows — the evidence needed to tell a single frame-critical path from a
  game that submits evenly from many threads. Loading pauses no longer dilute the stability figure,
  because windows without frames are not counted.
- Evidence mode also probes once per session whether Aegis can obtain the thread handle that
  thread-level scheduling would require, recording the outcome and the Win32 error. The probe opens
  and immediately closes the handle; it reads no thread state and writes nothing. Anti-cheat that
  denies this is expected and is recorded as a plain fact, not retried or worked around. Both
  additions are gated behind evidence mode, which stays off by default.

### Removed

- The NVIDIA low-latency toggle is gone. It wrote ULTRA_LATENCY (0x0005F543), and this driver does not
  know that setting id at all — reads and writes both return SETTING_NOT_FOUND, confirmed by asking the
  driver to enumerate the ids it supports. The only part that ever landed was the "maximum pre-rendered
  frames = 1" written alongside it, which is the plain low-latency mode rather than the Ultra tier the
  switch claimed. Rather than hunt for the correct id for a feature whose benefit was never measured
  here, the toggle and both settings it wrote are removed. The feature was never released.
- The success line for NVIDIA tuning used to be built from the user's switches, so a single successful
  write reported every requested item as applied — including one that had just logged a failure on the
  line above. It now reports only the items that actually landed.

### Changed

- The Settings page had grown into a dump for three unrelated kinds of switch: per-game GPU tweaks,
  reboot-level kernel changes, and app preferences. Scrolling for the autostart toggle put VBS and
  MPO one mis-click away. It is now split by risk. A new **Graphics** page holds the per-game GPU
  items (high-performance preference, FSO, NVIDIA power mode and frame cap) plus the windowed-game
  optimization; a new **System environment** page holds the six changes that need a reboot and
  survive uninstalling Aegis (HAGS, VBS, MPO, GPU/NIC/USB interrupt affinity), behind a banner that
  says so. Settings keeps only app preferences and the maintenance tools. No switch changed what it
  does, and no stored setting key moved.
- The navigation rail now supports more than one group heading; Graphics and System environment sit
  under a new "hardware and system" group.

### Internal

- Page code is one file per page under `src/Ui/Pages`, replacing `PanelForm.Pages.cs` and
  `PanelForm.V14Pages.cs`, whose names had stopped matching their contents. Each page now declares
  its own controls instead of sharing one field block in `PanelForm.cs`, which drops that file from
  1138 to about 700 lines.
- Page switching, the UI heartbeat and the sleep/wake path used to each carry their own chain of
  `if (page == ...)` branches that had to be edited in lockstep. They now dispatch through one page
  hook table, so adding a page touches a single registration. `OnUiTick` also stopped re-implementing
  `RefreshLightweightUiState` inline.
- Page identity is a `PageId` enum instead of bare integers that were duplicated across the nav
  arrays, the screenshot harness and the page array ordering. Misleading names are gone:
  `pageTame` was the policy page while `tameList` belonged to the anti-cheat page, and `pageWhite`
  was the game library, not the whitelist.

## [1.5] - 2026-07-29

### Fixed

- Background suppression reported "write did not fully take effect" for large batches of processes.
  EcoQoS is applied asynchronously by the kernel, so reading the state back immediately after the
  write could still observe the old value; a measured 40% of writes were misjudged this way. The
  readback now retries within a bounded window, and the failing step is named in the log.
- Residue detection promoted processes that had backgrounded themselves. Idle priority with low IO
  and page priority is also what `PROCESS_MODE_BACKGROUND_BEGIN` produces, so Aegis recorded a
  fabricated "original" state and, on restore, raised such a process to normal priority and cleared
  the EcoQoS it had set for itself. Residue is now only assumed when the core placement matches too.
- A single stale boost entry caused the running game's priority, core partition and GPU state to be
  torn down and re-applied on every scan cycle. Only mismatched entries are restored now, and a core
  partition that the machine genuinely cannot provide is recorded as handled instead of being
  rewritten every audit without backoff.
- A process name that failed to decode in the suppression journal silently became an empty string,
  which made every later identity check report a mismatch; the entry was discarded while the process
  stayed suppressed with nothing left to restore it. Such a line is now rejected, retained verbatim
  and logged.
- LCU credentials scraped from client logs are now only used after verifying that the process owning
  the loopback port passes the same ownership check as the WMI and lockfile sources.
- A WeGame root that normalizes to a bare volume root is rejected, so an entire drive can no longer
  be treated as the cleanup scope and terminate unrelated processes that share a generic name.
- Watchdog readiness is no longer inferred from named objects alone.
- Session report rows wrapped mid-record because CJK characters are double-width in the monospace
  font. Records are rendered on two lines; the on-disk format is unchanged.

### Changed

- The interface ships Simplified Chinese only. English and Japanese strings were removed, reducing
  the build from about 610 KB to about 500 KB. The localization mechanism and every text key are
  retained, so restoring a language only requires adding its strings back.

### Added

- A dedicated League of Legends column with a ROG-style live command deck.
- WeGame launch bridging: authentication and launch remain untouched until the local League
  Client API confirms a healthy signed-in lobby, after which only verified WeGame, Cross, coach,
  recorder, feedback, network-helper, and download-helper process paths are closed.
- True in-game headless mode through the client's native `kill-ux` endpoint. The LeagueClient
  backend and game remain alive, while `launch-ux` and `ux-show` restore the lobby after the match.
- A detached recovery watchdog that carries no LCU token in its command line or on disk and can
  restore the lobby even when the main Aegis process exits during a match.
- Direct deletion of the Cross add-on layer (AI coach, iCreate recorder) behind a separate
  confirmation. The client re-downloads these components on every update, so no copy is kept;
  deletion refuses to run while any client, WeGame, or anti-cheat process is alive and never
  touches the game core, login chain, or updater.
- Built-in tests for LCU credential parsing, strict process-path boundaries, containment against
  sibling and out-of-root paths, and add-on deletion boundaries.

### Changed

- Removed the old irreversible Cross content cleaner from Settings. League add-on cleanup now
  lives only in the dedicated column behind a separate confirmation.
- LoL-specific runtime control intentionally does not duplicate Competitive-mode CPU, priority,
  EcoQoS, network, or ACE policy.
- With the column disabled the runtime no longer discovers installations, enumerates processes, or
  contacts the League Client API. A failed installation discovery now backs off exponentially to
  ten minutes instead of repeating a full drive, registry, and process sweep every cycle.
- Download helpers (TenioDL, WeGameUpdate) are excluded from automatic cleanup so that an
  in-progress download is not hard-terminated every few seconds. Manual cleanup still includes them.
- WeGame is launched as the signed-in user through the shell token rather than inheriting the
  administrator token, so the game and its anti-cheat no longer run elevated as a side effect.
- The detached recovery watchdog no longer starts WeGame on its own. It exits when the client
  backend is gone and carries a hard watch timeout.
- Runtime and quarantine messages are translated; they previously appeared in Chinese regardless
  of the selected language.

### Fixed

- Installation discovery re-ran a full drive, registry, and process sweep on every cycle whenever
  League was not found, because the retry interval was bypassed by a condition that is true exactly
  when the interval matters. On a machine without League installed this ran roughly every 1.5
  seconds indefinitely, including when the column was switched off, which is the default.
- Process cleanup opened every process on the system with terminate rights in order to read its
  image path, including the game and anti-cheat. Cleanup is now two-phase: paths are read with
  query-only access, and terminate rights are requested solely for confirmed targets, whose path is
  verified a second time on the new handle.
- The lobby interface was restored whenever the LeagueClient backend was running without a UX
  process, which also matches ordinary client startup and shutdown. Aegis could force the window
  open during startup, or reopen a client the user had just closed. Headless state is now journaled
  to disk and only an interface Aegis actually closed is restored.
- Exiting could block the interface for up to 35 seconds while waiting for the runtime worker to
  finish a cycle that can legitimately take longer than that.
- The snapshot read by the interface performed filesystem probes while holding the state lock,
  stalling both the interface and the worker when the installation lived on a slow or disconnected
  volume.
- A directory containing a file reparse point was measured as a safe add-on candidate, so the
  operation would have followed the link. Reparse points at any depth now reject the candidate.

The remaining entries in this section come from a defect sweep of the whole product, not just the
League column. They are grouped by subsystem.

#### Background suppression and adaptive isolation

- The pressure controller advanced its CPU/IO baseline on every observation, including windows
  shorter than its own one-second minimum, and reported level `None` for them. Because sweeps are
  also driven by process start/stop events with a 200 ms coalescing window, ordinary desktop
  process churn kept the measured window under a second indefinitely, so heat never accumulated
  and adaptive isolation never engaged in the default preset. Worse, callers treat the returned
  level as the target level, so a sub-second sample actively released a process that was already
  isolated. The baseline is now preserved on short windows and the accumulated level is reported.
- The "leftover suppression" heuristic normalised priority, affinity, IO and page priority but not
  CPU Sets, so a process still pinned to the background partition was snapshotted as if that
  partition were its own placement and re-pinned by the restore.
- `Reapply` and `ApplyQueued` re-checked the entry under the lock, released it, and only then wrote
  the throttle. A whitelist release running on the interface thread could interleave, leaving a
  process throttled with neither an in-memory snapshot nor a journal line - unrecoverable until it
  restarted. The write now happens inside the same critical section as the check.
- The anti-cheat page acquired the suppression lock without a bound 18 times per interface tick,
  while a sweep can hold that lock across a whole batch of process queries.
- A batch result lookup could not distinguish "no result recorded" from "the write failed", so
  handle-protected processes were reported as failed writes on every sweep.

#### Crash recovery and restore journals

- The suppression journal never stored the captured Power Throttling snapshot, so crash recovery
  wrote "system managed" back and permanently stripped the EcoQoS opt-in of applications that had
  chosen it themselves. The journal format now carries both fields and still parses legacy lines.
- `HealFromCrash` deliberately retains entries it could not restore, as the input for a later
  retry - but nothing loaded that file back into the live instance, so the first journal write of
  the new session erased them. The constructor now re-adopts them, matching what the freeze guard
  already did.
- Re-boosting a process whose journal entry survived a crash overwrote the true pre-Aegis values
  with the already-boosted ones, and the matching release then deleted the only good record.
- The detached freeze watchdog kept rewriting the journal after a successor instance had taken
  ownership of it, so it could resume a process the successor believed frozen, or drop the
  successor's own rows. It now only acts on the entries that existed when its owner died.
- Services were stopped before the ownership record was persisted, leaving a window in which a
  crash lost the undo record entirely.

#### Exit and shutdown

- `Deactivate` restored the slow, low-stakes environment tweaks - service restarts worth up to
  ~19 seconds, a display mode switch, a synchronous broadcast to every top-level window - before
  the high-stakes process state. Exit only budgets 8 seconds and then terminates the worker
  regardless, so a truncated exit lost exactly the part that matters. Process state is now
  restored first.
- There was no logoff or shutdown handler at all, so an OS shutdown ran no restore whatsoever.
- Exit did not wait for interface-initiated file operations, so quitting during an add-on file
  operation killed the process mid-operation.
- `RestoreEnv` cleared each feature's active flag regardless of whether that feature's restore had
  actually succeeded, so the residue check reported a clean state and the retry never ran.

#### Windows interop

- `CACHE_RELATIONSHIP.GroupCount` is zero in the ordinary single-processor-group case, and the
  parser required it to be positive, so every L3 cache record was discarded. Asymmetric-cache
  processors (7950X3D class) were therefore never detected and never got their cache-aware masks.
- `GetProcessInformation(ProcessPowerThrottling)` is not supported on Windows 10 and always failed,
  so the exact-QoS restore path silently degraded on every Windows 10 machine - and the self-test
  covering it skipped for the same reason, which is why this was invisible. It now falls back to
  `NtQueryInformationProcess`, and that test runs for real.
- A WQL query did not escape backslashes, so the device restart it drove never matched any device.

#### Files and configuration

- The atomic-write fallback copied the temporary file over the target whenever anything in the try
  block threw - including a failure of the write itself, and including a stale temporary file left
  by an earlier crash - and then reported success. Every configuration file and restore journal in
  the product goes through this path. The fallback now runs only when the temporary file is known
  to be complete.
- A failed whitelist read was indistinguishable from an empty whitelist, and the next edit
  overwrote the good file.
- A Defender exclusion was written to the system before ownership was recorded, and the
  PowerShell-timeout path returned without rolling back, orphaning an exclusion Aegis could no
  longer remove.

#### Interface

- Descriptions were clipped in English and Japanese on nine cards and labels. Chinese fits, which
  is why this survived a previous round that claimed to have fixed the same class of defect. Card
  height is now derived from the text's measured height rather than hardcoded.
- The library page ran one full system process enumeration per library entry, on the interface
  thread, every 1.2 seconds.
- Releasing the mouse outside a settings card still toggled it, because the mouse is captured on
  button-down.
- "Restore defaults" left five persisted settings untouched and then logged success.

### Security

- LCU credentials are accepted only for loopback HTTPS, held in memory, and never written to
  logs, snapshots, manifests, or child-process arguments.
- Process cleanup is individually path-verified. It never uses tree-wide termination and never
  targets LeagueClient core, the game executable, RiotClientServices, ACE, or game files.

## [1.4.4] - 2026-07-25

This release completed the original general-purpose optimization feature line before the
League-specific work introduced in 1.5.

### Security

- Background suppression exempted anti-cheat processes using an exact-name catalog, while
  detection used a broader substring match. Where the two disagreed, a process the application
  itself classified as anti-cheat could still be suppressed to idle priority, core-locked and
  EcoQoS — and in Extreme mode suspended outright. A throttled anti-cheat can time out its
  heartbeat and disconnect the player. Both paths now share the broader check, and the freeze
  path carries a second, independent guard.
- Directory and policy names were concatenated into PowerShell script text. PowerShell also
  treats typographic quotation marks (U+2018/U+2019 and related) as single-quote delimiters, so
  doubling ASCII apostrophes was not sufficient escaping. Because Aegis runs with an
  administrator manifest, a directory created with a crafted name could lead to arbitrary command
  execution with administrator rights once the user added it to the library and enabled a feature
  that referenced it. Arguments are now passed through environment variables, and the script body
  is delivered via `-EncodedCommand` without a temporary file — which also removes the window in
  which a user-writable temporary script could be replaced before the elevated process ran it.

### Fixed

- A failed read of `Aegis.profiles.dat` was indistinguishable from an empty library. The game
  library appeared empty, and adding a single game then overwrote the intact file — with no
  backup, because the backup branch only runs when a repair is detected. A read failure is now
  recorded and blocks saving until the next launch.
- Suppression restored a hardcoded "system managed" power-throttling policy instead of the
  process's captured original, permanently stripping the EcoQoS opt-in of applications that had
  chosen it themselves. The original mask is now snapshotted, persisted in the crash journal
  (backward-compatibly), and written back.
- The per-game GPU preference value was replaced wholesale rather than merged, discarding the
  other fields Windows stores in the same value — including the per-game windowed-optimization
  setting. Both the apply and restore paths now merge field-by-field.
- Interrupt-affinity restore only covered devices that still enumerated, so a device that had
  become unavailable was skipped silently while the operation reported success, leaving
  unreachable snapshots. The modified device list is now persisted and used as the restore scope.
- `CrashGuard.ClearBoost` wiped the entire boost journal, destroying entries deliberately retained
  from a previous crashed session for a later retry. Per-process release already maintained the
  journal precisely; the blanket wipe was removed.
- `HagsTweak.Disable` wrote a hardcoded value with no snapshot when no backup existed, which could
  create a registry value the user never had and that the application could not remove again.
- Truncate-in-place writes for the whitelist, game list and suppression recovery journal were
  replaced with atomic temp-and-replace writes. A torn whitelist previously survived the
  "file exists" repair check and left the application running with no whitelist at all.
- Directory containment used an unanchored prefix comparison, so a game root of `D:\Games\Apex`
  also exempted `D:\Games\ApexBackup`. Comparison is now anchored on a path separator.
- The background pressure controller had no minimum sampling window. Because sweeps are also
  driven by process start/stop events, a ~200 ms window inflated the measured rate enough to
  escalate an ordinary application to full isolation within about a second.
- Process identity verification treated an unreadable image name as confirmation rather than
  rejection, and a failed priority query was snapshotted as Normal — which would permanently
  demote a process that had been running above Normal.
- The shared font cache was disposed by paint handlers, so the title-bar mode button and the game
  library list raised an exception on every repaint after the first.
- Descriptions in the interface were truncated because the word-wrap branch required a card height
  no card in the product used; the truncated portion was frequently the risk disclaimer. Tray menu
  labels were drawn above centre. The overview status line never fit its container.
- Language switching discarded the in-progress flag for cache cleaning, allowing a second
  concurrent sweep. The tray menu displayed the stored Game DVR setting rather than the value the
  active preset actually applies, contradicting the policy page.
- Mid-session standby purging used the free/zero page lists to judge memory pressure. Windows
  deliberately keeps free physical memory in the standby list, so that test was true almost
  always; system memory load is now used instead. A full purge could also be triggered mid-game by
  toggling the setting, which the feature explicitly promises never to do.
- Panic restore could report success for a restore that had not run, because a timed-out earlier
  request satisfied the wait of a later one. The worker now records which request it served.

## [1.4.3] - 2026-07-25

### Added

- **GPU interrupt affinity tuning.** Steers GPU interrupt handling toward the cores the game runs
  on, reducing cross-core/cross-cache overhead. Written to the device's Interrupt Affinity Policy
  and reverted exactly on disable. Machines with more than one processor group use only the
  lighter `IrqPolicyAllCloseProcessors` policy, because the `AssignmentSetOverride` binary format
  is undocumented for that case. Takes effect after restarting the device or rebooting.
- **Game network priority.** Applies the same interrupt-affinity steering to physical network
  adapters, and tags traffic from executables in the game library with a QoS DSCP priority
  marking. Policies are refreshed on each enable and removed on disable.
- **Release notes viewer.** Trilingual notes ship inside the executable and are viewable offline
  from the About page, with an unread marker on the first launch of a new version.
- **Auto-hide on game start** (opt-in, off by default). Ten seconds after a game is detected the
  main window returns to the tray. It fires at most once per game session and re-arms only when
  the next session begins, so reopening the window during a match never gets it pulled away.
- **Window intro animation.** The main window fades in with a subtle rise instead of appearing
  abruptly.

### Changed

- Background-suppression exemptions no longer hard-code launcher platform names or probe a
  game-specific registry key. The launcher host chain is discovered generically by walking up the
  parent-process chain of the running renderer, so it applies to any platform and any title.
  Long-lived launcher shells that detach from the process tree (the platform hands the game to a
  transient intermediate process and then exits the chain) are covered by a generic
  launcher-category fallback that only applies while a session is live.
- The interrupt-affinity apply/read-back/rollback logic is shared by the GPU and network features
  through a single engine rather than duplicated.

### Fixed

- Tray context-menu text rendered roughly 6-7 pixels above center on every row, because the
  padding used to increase row height was not honored when laying out the text. Text is now
  vertically centered against the full row height.
- `ReversibleReg` could not correctly handle `RegistryValueKind.Binary` values: its generic path
  compared `ToString()` results, which compares type names rather than content for byte arrays,
  so a binary write could report success without matching and a restore could not be verified.
- GPU enumeration counted software display adapters registered by remote-control software as real
  hardware. Enumeration is now restricted to devices on the PCI bus.

## [1.4.2] - 2026-07-24

### Added

- **Any-process targeting.** Any executable the user explicitly adds — not only games — can be
  recognized, protected, and boosted. The target activates as soon as it is running; it no longer
  needs a visible window or the foreground.
- **Fallback entry matching.** Self-extracting/bootstrap launchers that run their real long-lived
  binary outside the configured install directory (commonly a temp folder) are still recognized,
  by matching the entry executable's base name plus a bitness/version-style suffix (`app` →
  `app64`, `app_x64`, `app-v2`). Continuation into an unrelated word (`app` → `app_updater`) is
  rejected to avoid false matches with unrelated third-party processes.
- **Focus-independent ("sticky") session protection.** Once a session is confirmed, it stays
  protected and boosted across window-focus changes — alt-tabbing to the desktop or another
  application is never mistaken for the target having exited.
- **Competitive-grade suppression scope.** Competitive mode (and, independently, a new "adopt
  Competitive-grade scope" toggle in Custom mode) drops the foreground/visible-window exemption
  entirely, so background suppression covers everything except genuine Windows core services.
- **Precise core-system-service exemption.** Replaced the previous "anything under `C:\Windows`"
  exemption with an exact allow-list (session/logon, authentication, service control, the desktop
  compositor, audio processing) that only applies at Competitive-grade intensity, matched by
  process name *and* install path together.
- `--detect-live` and `--live-repro` diagnostic runtime modes for reproducing detection/suppression
  behavior against real running processes without touching the user's live configuration.

### Changed

- Background-suppression intensity on Competitive (and Custom with strict-core) no longer
  downgrades to `BelowNormal` on machines without a safe CPU-Set partition; it now stays at
  `Idle + EcoQoS`, matching what Task Manager reports as "Efficiency mode" and applying stronger
  suppression instead of a silently weaker one.
- WeGame (the CN League of Legends session host) is exempt from suppression whenever a LoL
  session is live — client, lobby, or match — not only once anti-cheat has finished loading,
  closing a brief disconnect-risk window during match start.

### Fixed

- Alt-tabbing away from a game in Competitive mode no longer causes the game process itself to be
  background-suppressed, which previously produced disconnects in anti-cheat-protected titles.
- A background-suppression exemption path could previously be reasoned about only via a
  string-comparison guess of "is this the user-selected target"; it is now carried as an explicit
  value from detection through the session-stickiness layer, so a fallback-matched target whose
  name happens to resemble a launcher is not wrongly denied a persistent anchor.
- The bitness/version-suffix fallback match previously accepted any continuation after the
  required digit/underscore/hyphen boundary, which could let an unrelated third-party process
  with a colliding name prefix receive full trust as the user's selected target. The suffix shape
  is now restricted to genuine bitness/version patterns.

### Security

- Anti-cheat processes are exempt from background suppression unconditionally, and this exemption
  is now matched by name *and* a small set of known substrings, closing a gap where an anti-cheat
  variant absent from the built-in catalog could otherwise be suppressed.

## [1.0] - 2026-07-24

Initial public release under this repository.
