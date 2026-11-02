"""Recompute paired synthetic loop results. Never labels them as game FPS."""
import csv
import hashlib
import json
import math
import pathlib
import statistics
import sys

root = pathlib.Path(sys.argv[1])
manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8-sig"))
verification = json.loads((root / "verification.json").read_text(encoding="utf-8-sig"))
assert (root / "completed.txt").read_text(encoding="utf-8-sig").strip() == "PASS"
with (root / "samples.csv").open(encoding="utf-8-sig", newline="") as stream:
    rows = list(csv.DictReader(stream))
assert len(rows) == manifest["Rounds"] * manifest.get("CasesPerRound", 17) == verification["Samples"]
assert verification["ActivePlanUnchanged"]
if (root / "Benchmark.exe").exists():
    assert hashlib.sha256((root / "Benchmark.exe").read_bytes()).hexdigest().upper() == manifest["BenchSha256"]
drift_limit = manifest.get("BaselineDriftLimitPercent", 10)
if "ProcessPriority" in manifest:
    assert all(row["process_priority"] == "128" for row in rows)
    assert hashlib.sha256((root / "PresentLaneLab.cpp").read_bytes()).hexdigest().upper() == manifest["SourceSha256"]
raw_hashes = {}
for row in rows:
    label = f'{int(row["round"]):02d}-{row["case"]}-{int(row["order"]):02d}-{row["arm"]}'
    raw = root / (label + ".csv")
    raw_hashes[raw.name] = hashlib.sha256(raw.read_bytes()).hexdigest()
    with raw.open(newline="") as stream:
        original = list(csv.DictReader(stream))
    assert len(original) == 1
    assert all(original[0][key] == value for key, value in row.items()
               if key not in ("round", "case", "order"))
    assert row["trace_rc"] == "0" and row["events_lost"] == "0"
    assert row["restored"] == "1" and row["failed"] == "0"
    assert row["present_winner"] == row["frame_tid"] and int(row["present_events"]) >= 8
    if row["arm"] == "wake-group":
        assert row["wake_rc"] == "0" and row["wake_lost"] == "0"
        assert row["wake_winner"] == row["worker_tid"] and int(row["wake_events"]) >= 16
    assert abs(float(row["loops_per_second"]) - int(row["samples"]) / float(row["seconds"])) < .01

groups = {}
for row in rows:
    groups.setdefault((row["case"], int(row["round"])), []).append(row)
output = {}
for (case, round_number), group in groups.items():
    baselines = [row for row in group if row["arm"] == "baseline"]
    assert len(baselines) == 2
    baseline_rate = statistics.geometric_mean(float(row["loops_per_second"]) for row in baselines)
    baseline_p99 = statistics.geometric_mean(float(row["p99_ms"]) for row in baselines)
    drift = abs(float(baselines[1]["loops_per_second"]) / float(baselines[0]["loops_per_second"]) - 1)
    for row in group:
        if row["arm"] == "baseline":
            continue
        p99 = float(row["p99_ms"])
        result = {
            "round": round_number,
            "loop_rate_change_percent": (float(row["loops_per_second"]) / baseline_rate - 1) * 100,
            "p99_change_percent": (p99 / baseline_p99 - 1) * 100 if math.isfinite(p99) else None,
            "baseline_drift_percent": drift * 100,
            "quantitative_pair_valid": drift * 100 <= drift_limit,
            "completed_cycles": int(row["samples"]),
            "cpu_candidate_was_present_thread": row["cpu_winner"] == row["frame_tid"],
        }
        output.setdefault(case, {}).setdefault(row["arm"], []).append(result)
summary = {}
for case, arms in output.items():
    summary[case] = {}
    for arm, values in arms.items():
        valid = [value for value in values if value["quantitative_pair_valid"]]
        complete = len(valid) == len(values)
        p99s = [value["p99_change_percent"] for value in valid if value["p99_change_percent"] is not None]
        summary[case][arm] = {
            "median_loop_rate_change_percent": statistics.median(value["loop_rate_change_percent"] for value in valid) if complete else None,
            "median_p99_change_percent": statistics.median(p99s) if complete and p99s else None,
            "quantitative_result_valid": complete,
            "valid_pairs": len(valid),
            "zero_progress_windows": sum(value["completed_cycles"] == 0 for value in values),
            "rounds": len(values),
            "max_baseline_drift_percent": max(value["baseline_drift_percent"] for value in values),
        }
report = {
    "kind": "Synthetic runtime Present loop; not game FPS or displayed frame rate",
    "samples": len(rows),
    "present_attribution_matches": sum(row["present_winner"] == row["frame_tid"] for row in rows),
    "cpu_attribution_matches": sum(row["cpu_winner"] == row["frame_tid"] for row in rows),
    "wake_attribution_matches": sum(row["arm"] == "wake-group" and row["wake_winner"] == row["worker_tid"] for row in rows),
    "baseline_drift_limit_percent": drift_limit,
    "summary": summary,
    "pairs": output,
    "raw_sha256": raw_hashes,
}
(root / "analysis.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
print(json.dumps({key: value for key, value in report.items() if key not in ("pairs", "raw_sha256")}, indent=2, allow_nan=False))
