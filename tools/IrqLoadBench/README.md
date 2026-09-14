# IRQ session-load regression bench

Run `./tools/IrqLoadBench/Run.ps1` from PowerShell. The runner compiles current
production sources with an explicit test allowlist into a unique temporary
directory, not `Pavise.exe`. It runs three repetitions of lifecycle, weighted
load, persistence, display-provenance and existing IRQ tests, then renders the
actual dialog with synthetic 12/64-thread fixtures at 100–300% scaling in both
languages and themes. Logs, PNGs and input hashes remain in the reported folder.

No application entry point, real game, ETW capture, PDH sampling, process tuning
or registry mutation is run. Settings are process-local and all ledger fixtures
are inside the isolated run folder. CPU topology/boot reads are read-only.

V5 retains the V3 session/driver and V4 load rows (`L|windowTicks` and
`C|cpu|averagePercent|observedTicks|sampleCount`). It adds:

- `B|cpu|busyTicks`: valid time with sampled utilization >=80%; absent means unknown.
- `K|driverBase64|cpu|count|totalNs|maxNs|over500|over1ms|badDuration|1|buckets`:
  driver x CPU DPC distributions, independent of the bounded event timeline.
- `M|gameIdBase64|configurationBase64|sceneBase64`: stable game identity,
  policy/topology/refresh fingerprint and legacy scene metadata (preserved for compatibility).
- `Q|deviceIdBase64|configurationBase64`: policy, exact mask, priority value
  (including absence) and package version. Only matching readable snapshots
  at both ends of the capture are retained.
- `F`: present counts, active seconds, P99/P99.9, identity reliability and
  PresentDpcAlignment clues, restricted to the same IRQ epoch.

V3/V4 remain readable and are upgraded on append; absent new fields remain
unknown. Corrupt/future-format files are preserved. Existing V2 retirement is
unchanged. Load averages and busy time share the same weighted capture window.
The source is disposed when the epoch is sealed or invalidated. The dialog uses
the device's most recent related valid session, with a menu for other histories.
Game masks, driver placement and loads update together, never from the desktop.

## Manual adjustment and verification

Exact pinning supports one processor group only. The writer never substitutes
a proximity policy. Affinity, priority, rollback and restore outcomes are separate.
No-op writes preserve boot receipts. Selected restore reuses the affinity backups
and selected priority receipt. Failed restores retain recovery information;
automatic pinning remains disabled.

Independent `irq-adjustment-<id>.xml` write-ahead records store identities,
old/target configuration, changes/outcomes, retained baseline samples and
verification states. Evicting old observations does not evict these baselines.
Configuration written, reboot pending, observation pending, observed placement
and performance are distinct states.

Comparisons require matching stable game identity, app configuration, topology,
game mask, driver/package version and captured device settings. Scene labels
are no longer collected or required. Map, mode and graphics differences are
not checked; the displayed changes are observational references. Loss, unmapped
events, incomplete per-core evidence or >2x duration differences exclude a sample
with an explanation. Earlier boots can supply performance baselines; current
placement requires current-boot, post-write evidence. Shared/framework data
never establishes an individual device's placement or recommendation.

Performance requires >=3 complete sessions on each side, >=80% present and target
load coverage, and reliable present-stream identity. Metrics are median game-core
slow-DPC rate, frame P99/P99.9, long-frame frequency and target CPU load. An
improvement needs separated (>10%) reductions in slow DPC, P99 and long frames,
without P99.9 regression or a target-load increase beyond five percentage points.
This is observational, not a causal or statistical significance claim. Current
Event 184 has PID-only identity and therefore cannot pass the performance gate;
its numbers and time overlap remain clues. Long frames exceed twice the session
median interval.

Candidate plans combine up to five distinct sessions no later than the selected
reference. They match game/app identity, game mask, topology, boot, driver version
and the current device configuration. Three sessions are required for the
multi-session label. Every physical-core sibling must pass >=80% load coverage,
known busy time, <=60% average load and <=20% time at >=80% busy in every accepted
session. Game cores and SMT siblings are excluded. Ranking uses worst-session
average load plus busy share, with observed placement only breaking ties.
Shared/framework attribution, multi-queue/storage devices, unreadable or changed
configuration and multiple processor groups get explanations instead of generic
core suggestions. Two-second samples are trends, not microsecond preemption
evidence. MessageNumberLimit remains a configuration limit, not an allocation count.

Plans also report game-core slow DPCs/min, summed DPC ms/min and the driver's
game-core DPC-time share. These are duration-weighted observations, not time spent
blocking a particular game thread. Building or inspecting a plan does not capture
new events or change device settings.

The bench also injects ETW-shaped payloads into production parsing/folding and
tests transaction failures, individual priority receipts, enumeration failures,
baseline persistence, unlabeled sessions, history menu clicks and multi-group gating.
The UI matrix includes Chinese/English, both themes, 12/64 threads and scaling
at 100%, 125%, 150%, 200% and 300%. `dev.cmd test` runs the broader isolated suite;
`build.cmd -b dev build/Pavise.dev.exe` builds without launching the tuner.

These are correctness/layout tests, not evidence of game FPS improvement or
measured runtime overhead of Windows counters.

The device page filters the existing inventory snapshot by name, service, ID and
localized device category. Selection follows device identity; filtering out the
selected device disables its actions until it is visible again. The dialog opens
on core selection, with observation details on a separate tab and a fixed target
summary/apply footer. Candidate buttons select a physical core without applying
settings. History switching preserves the manual target. Advanced priority
options are collapsed initially and scroll into view when expanded.

Adjustment history preserves the latest operation separately from the current
adjustment. Verified no-ops and failures before any device write retain the
original write time, boot stamp and comparison baseline. The optional
`noDeviceWrite` field records this proof; legacy failures and legacy priority
no-ops without it remain unknown. Prepared writes, attempted writes (including
successful rollback to the original device configuration) and restores stop
history lookup. Both the device list and dialog use the same selection rule.

Restore results are recorded per device and per setting from a strict snapshot
of the affinity/priority receipts. A failure on one device does not change another
device's successful result. Retrying a remaining priority receipt retains the
earlier affinity success and original restore cutoff. If finalizing the last
device fails, its receipt index remains available for retry. Unreadable receipts
stop the operation before any device restoration.

The additional history and restore tests cover these cases, including persisted
history and dialog layout. The bench uses a compiler response file so long
checkout paths do not exceed the Windows command-line length limit.

Hardware validation still needs device-specific reboot/restore behavior,
MSI-X/RSS/storage completion behavior, multi-group readout and controlled game
sessions. Measure ETW, per-core aggregation, PDH, device snapshots and present
capture overhead on the target machine. No fixed performance gain is promised.

Windows references: [Interrupt affinity and KAFFINITY](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority)
and [Processor groups](https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups).
