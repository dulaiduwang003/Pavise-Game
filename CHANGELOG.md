# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

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
  the build from about 610 KB to about 507 KB. The localization mechanism and every text key are
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
