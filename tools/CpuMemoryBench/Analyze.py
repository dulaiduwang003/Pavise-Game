"""Independently validate raw batches and summarize EVERY preregistered cell."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics as stats

HERE = Path(__file__).resolve().parent


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as file:
        for block in iter(lambda: file.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def percentile(values, p):
    # Nearest-rank percentile, no interpolation.
    return values[max(0, math.ceil(len(values) * p) - 1)]


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def write_csv(path, rows):
    with Path(path).open("w", newline="", encoding="utf-8-sig") as file:
        writer = csv.DictWriter(file, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def analyze(root):
    status = read(root / "status.json")
    schedule = read(root / "schedule.json")
    assert status["status"] == "complete", "Run did not complete"
    assert status["completed"] == len(schedule) > 0
    ids = [c["id"] for c in schedule]
    assert len(set(ids)) == len(ids)
    files = list((root / "arms").glob("*/result.json"))
    assert len(files) == len(schedule)
    isolation = read(root / "isolation.json")
    assert all(isolation[k] for k in ["productionFilesUnchanged", "registryUnchanged", "activePowerSchemeUnchanged", "allLaunchesCreateNoWindow"])
    assert not isolation["visibleOwnedWindowEvents"] and not isolation["remainingOwnedPids"]
    hashes = read(root / "source-hashes.json")
    assert sha(root / "CpuMemoryBench.exe") == hashes["executable"]
    source_dir = root / "sources" if (root / "sources").is_dir() else HERE
    assert all(sha(source_dir / name) == digest for name, digest in hashes["sources"].items()), "Frozen sources differ"
    arms, manifest = [], {}
    for config in schedule:
        directory = root / "arms" / config["id"]
        raw = read(directory / "result.json")
        assert read(directory / "config.json") == raw["config"] == config
        assert raw["error"] is None and raw["priorityClass"] == "Normal"
        assert raw["requestedForegroundMask"] == raw["observedForegroundMask"] == config["foregroundMask"]
        assert raw["restoredForegroundMask"] == raw["originalForegroundMask"] != 0
        with (directory / "samples.csv").open() as file:
            ticks = [int(row["batch_ticks"]) for row in csv.DictReader(file)]
        assert len(ticks) == raw["samples"] > 0 and min(ticks) > 0
        seconds = sum(ticks) / raw["stopwatchFrequency"]
        assert math.isclose(seconds, raw["seconds"], rel_tol=1e-12)
        assert config["measureSeconds"] <= seconds < config["measureSeconds"] + max(ticks) / raw["stopwatchFrequency"] + 1e-6
        steps = len(ticks) * (32768 if config["foreground"] == "compute" else 2048)
        assert raw["steps"] == steps
        assert math.isclose(steps / seconds, raw["stepsPerSecond"], rel_tol=1e-12)
        assert len(raw["workers"]) == len(config["backgroundMasks"])
        for worker, mask in zip(raw["workers"], config["backgroundMasks"]):
            assert worker["error"] is None and worker["observedGroup"] == 0
            assert mask == worker["observedMask"] == worker["requestedMask"]
            assert worker["originalMask"] == worker["restoredMask"] != 0
            assert worker["measuredSteps"] > 0
        assert 0 <= raw["systemCpuPercent"] <= 100
        if config["foreground"] != "compute":
            limit = (8 if config["foreground"] == "cache8" else 128) * 1024 * 1024 // 8
            assert 0 <= raw["checksum"] < limit and raw["checksum"] % 8 == 0
        milliseconds = sorted(t * 1000 / raw["stopwatchFrequency"] for t in ticks)
        arms.append({"id": config["id"], "round": config["round"], "foreground": config["foreground"],
                     "background": config["background"], "placement": config["placement"],
                     "seconds": seconds, "samples": len(ticks), "foregroundStepsPerSecond": steps / seconds,
                     "backgroundStepsPerSecond": sum(w["measuredSteps"] for w in raw["workers"]) / seconds,
                     "backgroundUnit": "8-byte logical loads/s" if config["background"] == "stream" else "recurrence steps/s",
                     "p50BatchMs": percentile(milliseconds, .5), "p95BatchMs": percentile(milliseconds, .95),
                     "p99BatchMs": percentile(milliseconds, .99), "maxBatchMs": milliseconds[-1],
                     "systemCpuPercent": raw["systemCpuPercent"], "processCpuSeconds": raw["processCpuSeconds"],
                     "foregroundCpuSeconds": raw["foregroundCpuSeconds"], "checksum": raw["checksum"]})
        for name in ["config.json", "result.json", "samples.csv"]:
            file = directory / name
            manifest[str(file.relative_to(root))] = sha(file)
    groups = {}
    for arm in arms:
        groups.setdefault((arm["foreground"], arm["background"], arm["placement"]), []).append(arm)
    summaries = []
    for (fg, bg, placement), rows in sorted(groups.items()):
        summaries.append({"foreground": fg, "background": bg, "placement": placement, "n": len(rows),
                          "foregroundMedian": stats.median(r["foregroundStepsPerSecond"] for r in rows),
                          "foregroundMin": min(r["foregroundStepsPerSecond"] for r in rows),
                          "foregroundMax": max(r["foregroundStepsPerSecond"] for r in rows),
                          "backgroundMedian": stats.median(r["backgroundStepsPerSecond"] for r in rows),
                          "p99BatchMsMedian": stats.median(r["p99BatchMs"] for r in rows),
                          "systemCpuPercentMedian": stats.median(r["systemCpuPercent"] for r in rows)})
    comparisons = []
    if status["mode"] == "run":
        assert len(arms) == 198 and len(groups) == 33
        assert all(len(rows) == 6 and {r["round"] for r in rows} == set(range(1, 7)) for rows in groups.values())
        pairs = [("e_spread", "unrestricted", True), ("e_spread", "p_separate", True),
                 ("p_separate", "unrestricted", False), ("e_pack", "unrestricted", False),
                 ("e_spread", "e_pack", False), ("smt_overlap", "unrestricted", False)]
        for fg in ["compute", "cache8", "dram128"]:
            for bg in ["compute", "stream"]:
                for treatment, control, primary in pairs:
                    a = {r["round"]: r for r in groups[(fg, bg, treatment)]}
                    b = {r["round"]: r for r in groups[(fg, bg, control)]}
                    paired = [{"round": i,
                               "foregroundRatio": a[i]["foregroundStepsPerSecond"] / b[i]["foregroundStepsPerSecond"],
                               "backgroundRatio": a[i]["backgroundStepsPerSecond"] / b[i]["backgroundStepsPerSecond"],
                               "p99Ratio": a[i]["p99BatchMs"] / b[i]["p99BatchMs"]} for i in range(1, 7)]
                    fg_ratio = stats.median(p["foregroundRatio"] for p in paired)
                    bg_ratio = stats.median(p["backgroundRatio"] for p in paired)
                    p99_ratio = stats.median(p["p99Ratio"] for p in paired)
                    wins = sum(p["foregroundRatio"] > 1 for p in paired)
                    comparisons.append({"foreground": fg, "background": bg, "treatment": treatment, "control": control,
                                        "primary": primary, "foregroundGainPercent": (fg_ratio - 1) * 100,
                                        "backgroundGainPercent": (bg_ratio - 1) * 100, "p99ChangePercent": (p99_ratio - 1) * 100,
                                        "foregroundPositivePairs": wins,
                                        "foregroundMinPairPercent": (min(p["foregroundRatio"] for p in paired) - 1) * 100,
                                        "foregroundMaxPairPercent": (max(p["foregroundRatio"] for p in paired) - 1) * 100,
                                        "passesExploratoryCandidateCriterion": fg_ratio >= 1.03 and wins >= 5 and bg_ratio >= .95 and p99_ratio <= 1.05,
                                        "pairs": paired})
    output = root / "analysis"
    output.mkdir(exist_ok=True)
    write_csv(output / "arms.csv", arms)
    write_csv(output / "cells.csv", summaries)
    write_json(output / "comparisons.json", comparisons)
    if comparisons:
        write_csv(output / "comparisons.csv", [{k: v for k, v in c.items() if k != "pairs"} for c in comparisons])
    write_json(output / "raw-sha256.json", manifest)
    summary = {"runDirectory": str(root), "validatedArms": len(arms), "validatedRawBatches": sum(r["samples"] for r in arms),
               "cells": len(groups), "allIsolationChecksPassed": True, "prefetch": read(root / "prefetch-capability.json"),
               "analyzerSha256": sha(Path(__file__)),
               "primaryComparisons": sum(c["primary"] for c in comparisons),
               "primaryCandidatePasses": sum(c["primary"] and c["passesExploratoryCandidateCriterion"] for c in comparisons),
               "minimumArmSeconds": min(r["seconds"] for r in arms), "maximumArmSeconds": max(r["seconds"] for r in arms),
               "maximumBatchMs": max(r["maxBatchMs"] for r in arms)}
    write_json(output / "validation.json", summary)
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    if comparisons:
        for c in comparisons:
            if c["primary"]:
                print("{foreground:8} {background:7} {treatment:8}/{control:12}: FG {foregroundGainPercent:+7.2f}% BG {backgroundGainPercent:+7.2f}% p99 {p99ChangePercent:+7.2f}% wins {foregroundPositivePairs}/6 pass {passesExploratoryCandidateCriterion}".format(**c))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("run_dir", type=Path)
    analyze(parser.parse_args().run_dir.resolve())
