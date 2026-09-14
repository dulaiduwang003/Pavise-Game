# Minimal optimization-risk counterexamples

Run `powershell -NoProfile -File tools/OptimizationRiskBench/Run.ps1` from the repo
root, or append `-SpreadControlOnly` for the separate-logical-CPU control.
See RESULTS.md for the recorded run and its limitations.

`Run.ps1 -ExtrasOnly` runs real audio activation/readback. It opens only a silent
owned stream and releases it; it does not switch the default device or record audio.
Cache warm-up and its executable contention arm have been retired. The earlier
measurements and exact source snapshots remain historical evidence in RESULTS.md
and their original temporary result folders, not a currently available feature.
No compression, power, driver, MMCSS registry, security, or service settings change.

`Run.ps1 -StoreOnly` uses a real read handle that denies replacement of its own
temporary profile file. It checks preservation of the original, the production
nonfatal busy result, and successful retry on the same instance after releasing
the handle. A fresh store instance is also tried. This does not identify the holder
of any observed external lock. Fatal non-busy errors are covered by isolated self-tests.

This is not a game benchmark and cannot measure an FPS gain from removing features.
It compiles current production source with a dedicated entry point. It never starts
the Pavise runtime, writes production settings, changes power/security/driver settings,
touches another process's priority or memory, or purges the system standby list.

- `policy`: calls current production pure decision functions with explicitly synthetic values.
- `trim`: 12 ABBA-ordered observations with 256 MiB of private memory. Only the bench's
  own working set is trimmed. Retouch time and total page-fault deltas are recorded;
  page faults are not classified as hard/disk faults. No memory pressure is induced.
- `lane baseline` / `lane boost`: two owned threads share one allowed logical CPU.
  A frame-like loop does fixed work and sleeps; an independent noncritical worker runs
  continuously. Production `RenderLane.TryIdentify` and `LaneJudge` choose the worker.
  The boost arm applies only `THREAD_PRIORITY_HIGHEST` to that owned worker, with
  readback and restoration. A four-second measurement tests the consequences of a
  false identification in this constrained workload. It is not a complete RenderLane
  lifecycle test, a process-HIGH experiment, GPU rendering, or game frametime measurement.
- `lane baseline spread` / `lane boost spread`: same counterexample on two different
  allowed logical CPUs. `Run.ps1 -SpreadControlOnly` executes this ABBA control.
  Completed cycles inside the measurement window are recorded separately from complete
  cycle intervals; no percentile is invented when there are no interval samples.

Each process has a 45-second watchdog; worker loops are bounded by the controller.
Only its own threads are affinity constrained. Exiting removes all process-local
effects. There is no automatic deletion of results. Compile with .NET Framework csc,
`-define:PAVISE_PERFLAB`, `-main:PaviseApp.OptimizationRiskBench`, the same references
as build.cmd, all src/*.cs, RiskBench.cs and ExtraBench.cs. Use the accompanying runner for ABBA
ordering, baseline CPU sampling, and raw evidence.

## Continued negative-optimization review

`Run-Review.ps1` runs a separate console entry against current production source.
It supports `-Arm priority`, `power-inputs`, `auto-gpu-filter`, and `read-cost`.
It never starts the Pavise runtime or writes power plans, drivers, real settings,
user libraries, or another application's affinity/priority. Results and exact bench
source snapshots are retained in a unique `Pavise-NegativeReview-*` temporary folder.

- `priority`: two ABBA blocks per layout; two-second bounded windows; two owned
  processes exchange requests through shared memory. The requester deliberately
  busy-waits for a normal-priority helper. Compare Normal with production's guarded
  target (now Normal for a restricted CPU domain), on the same logical CPU and on distinct logical CPUs. This is a constrained
  priority-inversion counterexample, not a representation of every game or a game FPS
  measurement. Affinity/priority readback and restoration are mandatory. Empty
  completion windows retain NaN percentiles, never invented two-second frame times.
- `power-inputs`: production state machine with synthetic GPU/power/CPU sequences,
  except the cold first CPU sample which calls the actual production sampler. No
  laptop eligibility or real EPP changes run. Five deterministic repetitions per case.
- `auto-gpu-filter`: focused composition of the production EXE-path visibility
  gate and eligibility/enrollment functions. Windows visibility, identity,
  hybrid hardware, executable existence, and GPU use are explicit synthetic
  assumptions; preference control and journal are entirely in-memory. This does not
  execute the live discovery loop or prove an actual cross-GPU performance loss.
- `read-cost`: read-only production process snapshots (fresh and immediately reused)
  and the background shared-VRAM PDH query, with scan-side enqueue time separated
  from time to background completion. Cold and warm wall times are kept separate. The tight
  sampling schedule is not the production cadence and does not measure game latency.

`Summarize-Review.ps1 -RunDirectory <all-arms-folder>` validates expected row counts,
unique arm keys, isolation and restoration, then recomputes every priority percentile
from retained completed-request samples before summarizing.

The post-fix runner asserts nonzero scheduling progress and Normal readback in
restricted-domain arms, rejection of missing CPU/GPU verification evidence, and no
low-power writes for either visible PIDs or hidden PIDs sharing a visible EXE.
Historical `high` runs remain readable by the summarizer; new runs use `guarded`.
Expected current rows: 16 priority, 20 power decisions, 10 GPU gates, 40 read costs.
The full self-test runner additionally covers fake EPP apply/restore dispatch,
failed restoration receipt retention, late-generation rejection, late window
visibility, and VRAM single-flight / reset / seal / shutdown boundaries.
