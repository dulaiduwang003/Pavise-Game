# CPU / memory experiment protocol — 2026-09-08

Scope: isolated synthetic executables, no game access, injection, hooks, driver installation, elevation, UI, global power or registry changes. All binaries are Windows GUI-subsystem executables without a UI; every subprocess uses CREATE_NO_WINDOW. Existing user edits are preserved.

## Experiment 1: hardware prefetch controls

Perform a read-only capability audit first. A measured hardware comparison requires a verified, model-aware privileged MSR backend, independent read-back, preservation of unrelated/reserved bits, and verified restoration. P and E core registers and scopes must be handled separately. If unavailable, status is NOT_TESTED_NO_PRIVILEGED_BACKEND. Do not substitute software prefetch, assume default state is enabled, or report a hardware benefit from unchanged-state memory tests.

## Experiment 2: core placement / shared-resource contention

Discover physical core masks, efficiency classes, and L2 sharing through GetLogicalProcessorInformationEx. Require a single supported processor group, at least six P cores, an SMT sibling for the selected foreground P core, and four E-core L2 groups with at least four E cores in the selected packed group. Fail explicitly if the topology does not match this protocol.

Foreground stays on one P-core logical CPU in every arm. Background has four normal-priority threads. These are continuous synthetic competitors, with their own completed-work counters. Background placements:

- alone: no background workers;
- unrestricted: all process-allowed CPUs (only background placement is unrestricted);
- smt_overlap: one worker on the foreground SMT sibling, three on separate P cores; an engineered adverse control;
- p_separate: four separate P physical cores excluding foreground;
- e_pack: four E cores within one observed shared L2;
- e_spread: one E core from each of four observed L2 groups.

Foreground workloads: integer recurrence (compute), dependent random pointer chase over 8 MiB (cache8), and 128 MiB (dram128). Nodes occupy 64 bytes. The random cycle is validated before timing. Background types: integer recurrence or sequential reads of four private 64 MiB buffers. Buffers are allocated, initialized and touched before timing. No game workload or FPS is measured. Logical load throughput is not a hardware DRAM bandwidth counter.

Fixed main matrix: 3 foreground workloads × [1 alone + 2 background types × 5 placements] × 6 rounds = **198 arms**. Each arm: 0.7 s foreground warmup after all backgrounds become ready, then 2.5 s measurement. Each consecutive pair of rounds uses a seeded random order and its reverse; seeds and schedule are saved before the first main arm. Pointer layout seed is fixed. Pilot checks are excluded from the main results and do not choose workloads or change success criteria.

Primary comparisons: e_spread versus unrestricted and e_spread versus p_separate, matched by foreground, background and round. Report other placements descriptively. Primary outcome: foreground completed steps per elapsed second. Tail outcome: p99 fixed-work batch duration (not frame time or 1% low). Record competitor work per elapsed second, system CPU activity and thread affinity read-back/restoration. Timed background counters have a boundary granularity of one 32,768-step compute batch / 4,096-load stream batch per worker.

Strict candidate criterion (exploratory, not proof for games): median paired foreground improvement ≥3%, at least 5/6 positive pairs, median paired background throughput retention ≥95%, and median paired p99 batch-duration ratio ≤1.05. All six primary workload/background combinations and both comparators must be reported, including failures; no selective headline of the best comparison. SMT-overlap recovery must never be presented as ordinary-user uplift. No PMU, clock or CPU temperature read-back is available: mechanisms and thermal confounding remain limitations. Moving work onto slower cores may trade background performance for foreground time, which is not free acceleration.

Validation: verify all 198 arms completed exactly once; duration/sample totals and checksum/cycle checks; masks and restoration; no measured process windows observed; all owned children exit; source hashes, production source/binary hashes, Pavise registry snapshot and power scheme unchanged. Keep raw batch timings, schedule, capability audit, source hashes, per-arm JSON, and analysis output. Report unsupported experiments honestly.

Hardware references: [Intel Atom prefetch-control specification (Gracemont/Raptor Lake E cores)](https://cdrdv2-public.intel.com/795247/357930-Hardware-Prefetch-Controls-for-Intel-Atom-Cores.pdf), [Windows processor relationships](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-processor_relationship).
