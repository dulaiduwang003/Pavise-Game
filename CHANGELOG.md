# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

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
