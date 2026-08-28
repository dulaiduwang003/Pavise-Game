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

New V4 records add `L|windowTicks` plus up to 64
`C|cpu|averagePercent|observedTicks|sampleCount` rows. V3 histories remain readable
with missing load values and are upgraded on the next append. Averages are
weighted by valid observed time within the same IRQ capture epoch; they are
whole-system utilization, not renderer-only utilization or an exit snapshot.
The load source is disposed when that epoch is sealed or invalidated. The
dialog uses only the latest record, never the current desktop or preset.

These are correctness/layout tests, not evidence of game FPS improvement or
measured runtime overhead of Windows counters.
