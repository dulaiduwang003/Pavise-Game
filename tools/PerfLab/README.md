# Pavise PerfLab

PerfLab is an isolated A/B performance test for the real Pavise engine. It creates:

- a visible renderer with calibrated `DwmFlush` presentation telemetry and an
  explicit `gdi_timer` fallback when `DwmFlush` cannot be calibrated;
- several bounded background CPU/memory workers;
- short-lived child bursts launched by a launcher-like renderer anchor itself,
  exercising the one-shot launcher transition budget and process notifications;
- an Pavise engine host using an in-memory settings store and a private data directory.

The `overhead` lane observes the synthetic workload without changing its scheduling.
The `policy` lane boosts only the synthetic renderer and suppresses only workers whose
full image path matches the private PerfLab copy. Neither lane modifies the user's
`HKCU\Software\Pavise` settings or uses normal user processes as test targets.
The controller requests elevation once because a non-elevated process cannot validate
the real priority/IO/GPU boost path.

Build:

```powershell
& .\tools\PerfLab\build.ps1
```

Run ten paired rounds:

```powershell
$bin = "$env:TEMP\Pavise-PerfLab-bin"
$out = "$env:TEMP\Pavise-PerfLab-results"
$args = @(
  "--run", "--engine", "$bin\Pavise.PerfEngine.exe",
  "--lane", "overhead", "--rounds", "10", "--warmup", "10",
  "--seconds", "20", "--cooldown", "10", "--workers", "6",
  "--out", $out
)
Start-Process "$bin\Pavise.PerfLab.exe" -ArgumentList $args -Verb RunAs -Wait
Get-Content "$out\summary.txt"
```

`trials.csv` contains every raw trial, including the Pavise engine's CPU, I/O,
resource footprint, policy writes, and GameMode full-snapshot count. `summary.txt`
uses medians and reports separate renderer, engine-overhead, scan-budget, and
suppression gates. The scan budget is tied to the production 20-second
event-backed reconciliation interval, so short-lived process bursts cannot
silently turn it back into a four-second global sweep.

The measurement boundary uses a two-phase handshake. The controller requests
start, the engine captures its CPU/I/O/scan/policy baselines, and only its ACK
releases the renderer and load workers. At the other boundary the renderer
signals completion before report I/O and window teardown. Readiness/warmup
observations and shutdown restoration are therefore outside the engine window.
`apply_operations` is also reported as a measurement-window delta.

Each active arm publishes a nonce-bound v1 roster only after every long-lived
worker has started. The roster is written through a temporary file and atomic
rename, then announced to the engine through a trial-unique event. The engine
rejects duplicate/missing members, a mismatched nonce or image path, another
session, PID reuse, and any creation-time mismatch. It retains a handle to each
exact process object for the complete arm, so short child bursts using the same
executable cannot replace a roster member.

Each v4 engine report records elapsed measurement time, the expected roughly
1 Hz sample count, actual-sample density, and both aggregate suppression and
identity-bound worker coverage. Worker coverage includes the expected roster
size, minimum and final covered count, full-roster samples, and total coverage
samples. Both engine and controller require at least 80% sampling density; one
observation cannot claim coverage of a 20-second window. The policy gate
requires the final observation to cover every long-lived worker and at least
90% of samples to have full-roster coverage. Five of six workers plus any
number of short bursts therefore cannot pass.

Runs shorter than 20 seconds remain useful as smoke tests, but their
`duration_gate` is `INSUFFICIENT`; they cannot produce an overall pass because
they do not span one production reconciliation interval.

The renderer gate treats the nearest-rank p90 as a diagnostic because ten pairs
make it the second-worst trial. It fails on a repeatable regression (more than
20% of pairs exceed +5%), median average/p99 regressions, or a paired one-sided
95% upper bound that cannot exclude ten additional long frames per 1,000. Each
A/B pair is the statistical unit, so correlated frames inside a trial do not
create fake precision. The long-frame bucket is selected at least 30% above the
measured compositor cadence from 8.33 through 66.67 ms; this avoids placing the
threshold directly on a 40 Hz or 30 Hz frame median, where harmless phase noise
would otherwise look like a large miss-rate change.

If the point estimates are inside every practical limit but ten pairs do not
provide enough power for the paired long-frame upper bound, the renderer gate
reports `INCONCLUSIVE`, not `FAIL`. Increase `--rounds` or repeat under unchanged
conditions before drawing a conclusion.

`presentation_mode` is recorded for every arm. A run is environment-invalid if
the A/B arms do not use one common mode. The `gdi_timer` fallback remains a
visible GDI renderer and must sustain at least 20 measured frames per second
with bounded frame time; it is never labeled as compositor telemetry.

The output directory must either be empty or already contain PerfLab's
`.pavise-perflab-owner` marker. PerfLab creates the marker on first use and
refuses to overwrite a non-empty unowned directory.

The `overhead` lane keeps the synthetic renderer and workers at the same
scheduling policy in both arms, so Pavise cannot hide its observation cost behind
policy gains. Run a second output directory with `--lane policy` to exercise the
real GameMode sweep, boost, whitelist and suppression paths. That lane is scoped
to the private synthetic worker path and leaves normal user processes untouched.

`--duty <ms>` overrides the per-worker duty cycle (milliseconds of spin per 100 ms
window). The default of 10-12 ms is a light regression load: six workers occupy
roughly 11% of a six-core machine, so nothing is actually contended.

## What this harness can and cannot measure

PerfLab is a **regression guard**, not a benefit meter. It answers "does Pavise
make things worse, and what does it cost to run", not "how much does suppression
gain".

Measured on a 6-core / 240 Hz laptop, an isolated copy of the synthetic renderer
holds ~20.2 ms per frame whether it runs alone, alongside eight workers at 70%
duty (~5.6 cores of load), or with a child process spawned every 750 ms. The
frame time moves by less than 1 ms across all three. The renderer is therefore
insensitive to CPU contention by construction, and a policy-lane A/B cannot show
a suppression gain no matter how the load is configured. Reading a null result
there as "suppression does not help" is a misreading of the instrument.

## Known limitation: results drift after roughly ten minutes

Every observed run shows the renderer stepping to a distinctly faster frame time
around 590 seconds into the run - round 8 of a default ten-round run - and
staying there. It reproduced in four independent runs at the same offset.

Ruled out so far, each by a dedicated isolation probe: display idle timeout (the
controller now holds `ES_DISPLAY_REQUIRED` for the whole run and the step still
appears), CPU contention from the workers, child-process spawn cost, `DwmFlush`
pacing, and display refresh changes (a constant 240 Hz was logged across a full
13-minute probe). None of those reproduce the step outside PerfLab, and the
isolated probes stay flat for 13 minutes.

The paired A/B design absorbs it - both arms of a round sit next to each other -
but rounds before and after the step measure different system states. Until the
cause is found, prefer `--rounds 7` for a clean single-state run, and treat any
comparison that spans the step with suspicion.
