# Device interrupt enhancement validation

2026-09-14 — multi-session candidate planning:

| Check | Result |
| --- | --- |
| IRQ bench, 3 repetitions | 75 groups passed, 6,483 assertions |
| Device page and dialog UI | 22,202 assertions passed |
| Full isolated regression | 95 suites passed, 0 failed |
| Development build | build/Pavise.2.2.2.3.exe, succeeded |
| Bench input hashes / git diff --check | Unchanged / passed |

New cases cover worst-session ranking, hot or undersampled earlier matches,
game/configuration/boot/driver/device mismatches, missing evidence, duplicate
timestamps, historical cutoffs, bounded five-session plans and device capability
gates. Multi-session screenshots were inspected in Chinese dark and English light.
Evidence is in `build/irq-plan-validation/`. No device writes or real captures
were performed. This validates decision rules and UI, not game performance gains.
Current executable SHA256:
`44BE2835EA1283A8CA6EE7A61A791FDA2FE002D496F86E4C6E91F01515A4BFE3`.

Earlier UI revision for 2.2.2.3, including removal of scene labels (superseded build):

| UI revision check | Result |
| --- | --- |
| IRQ bench, 3 repetitions | 72 groups passed, 6,201 assertions |
| Device page and core dialog interactions/layout | 21,894 assertions passed |
| Full isolated regression before label removal | 94 suites passed, 0 failed |
| Development build | build/Pavise.2.2.2.3.exe, succeeded |
| Final bench input hashes / git diff --check | Unchanged / passed |

Coverage includes device search without re-enumeration, selection by device ID,
hidden targets disabling actions, independent suggestions on configured devices,
core/detail tabs, candidate selection without applying, keyboard tab activation,
history switching with unchanged manual targets, unlabeled comparison samples,
legacy metadata compatibility and advanced-option visibility. Dialog checks use 12/64 threads, 100–300% scaling,
Chinese/English and both themes; device-page checks cover all scales and themes.
Chinese dark and English light screenshots were visually inspected. Logs and
screenshots are in `build/irq-ui-validation/`. No device settings or real capture
sessions were used. Executable SHA256:
`C5E58E2F95FAA7BDC7B6B8034D0B2C545848CFA7487DBD32B42FF9CA3F712139`.

Follow-up fixes against 20706c7 were independently validated after all source
changes were frozen:

| Follow-up check | Result |
| --- | --- |
| IRQ bench, 3 repetitions including history/restore regressions | 72 groups passed, 6,138 assertions |
| Dialog layout and history display | 4,866 assertions passed |
| Full isolated regression from tracked and new nonignored sources | 94 suites passed, 0 failed |
| Development build | build/Pavise.irqfix.exe, succeeded |
| Final bench input hashes / git diff --check | Unchanged / passed |

The regressions cover repeated no-ops preserving the active adjustment, failures
before writes, rollback/unknown boundaries, legacy history, per-device partial
restores, priority-only retries, receipt-read faults and finalization retry.
Chinese and English evidence-area screenshots were inspected. Logs and screenshots
are in `build/irq-fix-validation/`; the executable SHA256 is
`1A7D0996C034DEB34051630330E15252512894EA7ADD8F9E08FAC6B07716919B`.
These follow-up checks used no real device writes or capture sessions.

Baseline: dev / 7e8c1c2. The workspace was clean before a fast-forward to origin/dev.

| Check | Result |
| --- | --- |
| Isolated IRQ bench, 3 repetitions | 66 groups passed, 5,622 assertions |
| Actual dialog layout/history controls | 4,786 assertions passed |
| Topology / scale / language / theme | 12/64 threads; 100/125/150/200/300%; Chinese/English; dark/light |
| Full isolated regression | 92 suites passed, 0 failed |
| Development build | build/Pavise.dev.exe, succeeded |
| Final bench input hashes | All unchanged |
| git diff --check | Passed |

The development build produced no compiler warnings. The full self-test build
reported existing CS0649 warnings for unused test hooks; there were no errors.

The bench covers in-memory production ETW parsing/folding, exact-mask capability
gating, failed writes and rollback failures, separate priority results, selected
priority receipt restoration, per-device enumeration failure, V3/V4 migration,
V5 persistence/corruption handling, baseline retention, current-boot placement,
scene/configuration eligibility, frame-stream reliability and history menu clicks.
Screenshots of the Chinese dark and English light dialogs were inspected at
125%, including the scrolled evidence area. Layout assertions cover all listed
scales, languages, themes and topologies.

Local deliverables (relative to repository root):

- `build/Pavise.dev.exe`
- `build/irq-enhancement-validation/selftests.txt`
- `build/irq-enhancement-validation/console.log`
- `build/irq-enhancement-validation/verification.json`
- `build/irq-enhancement-validation/history-*.png`

Development executable SHA256:
`37B2B989F4B2960874822B1FED45EFDB434BC66A4861FF392EA2F9EE711CAB63`

No real device settings, game, ETW session or PDH capture were used for testing.
Correctness tests do not establish performance gains or probe overhead. Device
reboot/restore behavior, MSI-X/RSS/storage completion behavior and game performance
still require hardware measurements. Current PID-only present identity remains
insufficient for a performance-improvement claim. See README.md for the evidence
gates, supported scope and measurement limitations.
