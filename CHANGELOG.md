# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.4.4] - 2026-07-25

**Final feature release.** Feature development ends here. Aegis will continue to receive
maintenance — keeping pace with Windows updates and anti-cheat vendor changes, and fixing
defects — but no new features are planned.

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
