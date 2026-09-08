"""Windows-only hidden, isolated CPU benchmark runner. Standard library only."""
import argparse
import ctypes
from ctypes import wintypes
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import time
import winreg

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
NO_WINDOW = subprocess.CREATE_NO_WINDOW
OWNED = set()
WINDOW_EVENTS = []
LAUNCHES = []
USER32 = ctypes.WinDLL("user32", use_last_error=True)
ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
USER32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
USER32.IsWindowVisible.argtypes = [wintypes.HWND]
USER32.EnumWindows.argtypes = [ENUMPROC, wintypes.LPARAM]
ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002 | 0x8000)


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def check_windows():
    @ENUMPROC
    def visit(hwnd, unused):
        pid = wintypes.DWORD()
        USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in OWNED and USER32.IsWindowVisible(hwnd):
            event = {"pid": pid.value, "hwnd": int(hwnd), "utc": utc()}
            if not any(x["pid"] == event["pid"] and x["hwnd"] == event["hwnd"] for x in WINDOW_EVENTS):
                WINDOW_EVENTS.append(event)
        return True
    USER32.EnumWindows(visit, 0)


def hidden(args, timeout=120):
    """No shell, no console, no inherited interactive stdin, bounded owned child."""
    start = time.monotonic()
    p = subprocess.Popen([str(a) for a in args], cwd=str(REPO), stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=NO_WINDOW)
    OWNED.add(p.pid)
    record = {"pid": p.pid, "program": Path(str(args[0])).name, "startUtc": utc(), "createNoWindow": True}
    LAUNCHES.append(record)
    try:
        # communicate drains redirected pipes while checking for unexpected windows.
        while True:
            check_windows()
            if WINDOW_EVENTS:
                raise RuntimeError("Owned process displayed a visible window; stop this run")
            try:
                stdout, stderr = p.communicate(timeout=0.2)
                break
            except subprocess.TimeoutExpired:
                if time.monotonic() - start > timeout:
                    raise TimeoutError("Owned child exceeded timeout: " + str(args[0]))
        record.update(exitCode=p.returncode, seconds=time.monotonic() - start, endUtc=utc())
        if p.returncode:
            raise RuntimeError("Child failed (%s): %s\n%s\n%s" % (p.returncode, args[0], stdout.decode("utf-8", "replace"), stderr.decode("utf-8", "replace")))
        return stdout.decode("utf-8-sig", "replace").strip()
    finally:
        if p.poll() is None:
            p.kill()
            p.communicate(timeout=10)
            record["killedByRunner"] = True
        OWNED.discard(p.pid)


def registry_digest():
    values = []
    def walk(key, prefix):
        subkeys, nvalues, _ = winreg.QueryInfoKey(key)
        for i in range(nvalues):
            name, value, kind = winreg.EnumValue(key, i)
            values.append((prefix, name, kind, repr(value)))
        for i in range(subkeys):
            name = winreg.EnumKey(key, i)
            with winreg.OpenKey(key, name, 0, winreg.KEY_READ) as child:
                walk(child, prefix + "\\" + name)
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Pavise", 0, winreg.KEY_READ) as key:
            walk(key, "Pavise")
    except FileNotFoundError:
        return {"present": False}
    return {"present": True, "valueCount": len(values), "sha256": hashlib.sha256(json.dumps(sorted(values)).encode()).hexdigest()}


def protected_snapshot():
    files = list((REPO / "src").rglob("*.cs")) + list((REPO / "tests").glob("*.cs"))
    files += [p for p in [REPO / "Pavise.exe", REPO / "version.json", REPO / "tools/Publish-VersionManifest.ps1"] if p.exists()]
    return {"files": {str(p.relative_to(REPO)): sha(p) for p in sorted(files)},
            "registry": registry_digest(),
            "powerScheme": hidden([os.environ["WINDIR"] + r"\System32\powercfg.exe", "/getactivescheme"])}


def metadata():
    script = r"""
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$cpu = Get-CimInstance Win32_Processor | Select-Object Name, ProcessorId, NumberOfCores, NumberOfLogicalProcessors
$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, TotalVisibleMemorySize, FreePhysicalMemory
$board = Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product
$drivers = @(Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -match 'Pawn|WinRing|^msr$|inpout|iocbios|XTU' } | Select-Object Name, State, StartMode)
[pscustomobject]@{ cpu=$cpu; os=$os; board=$board; candidateMsrDrivers=$drivers } | ConvertTo-Json -Depth 5 -Compress
"""
    return json.loads(hidden(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script]))


def bits(mask):
    return [i for i in range(64) if mask & (1 << i)]


def topology_plan(top):
    if any(c["group"] != 0 for c in top["cores"] + top["caches"]):
        raise RuntimeError("Protocol supports only a single processor group")
    allowed = top["allowedMask"]
    classes = sorted(set(c["efficiency"] for c in top["cores"]))
    if len(classes) != 2:
        raise RuntimeError("Need two verified P/E efficiency classes; got " + repr(classes))
    pcores = sorted([c for c in top["cores"] if c["efficiency"] == classes[-1] and c["mask"] & allowed == c["mask"]], key=lambda c: c["mask"])
    ecores = sorted([c for c in top["cores"] if c["efficiency"] == classes[0] and c["mask"] & allowed == c["mask"]], key=lambda c: c["mask"])
    if len(pcores) < 6 or any(len(bits(c["mask"])) != 1 for c in ecores):
        raise RuntimeError("Insufficient physical P/E topology")
    fgcore = pcores[1]
    if len(bits(fgcore["mask"])) != 2:
        raise RuntimeError("Foreground P core has no verified SMT sibling")
    fg, sibling = bits(fgcore["mask"])
    pothers = [bits(c["mask"])[0] for c in pcores if c != fgcore][:4]
    emask = sum(c["mask"] for c in ecores)
    l2 = sorted({c["mask"] & emask for c in top["caches"] if c["level"] == 2 and c["mask"] & emask}, key=lambda m: m)
    if len(l2) < 4 or len(bits(l2[0])) < 4 or any(a & b for i, a in enumerate(l2) for b in l2[i + 1:]):
        raise RuntimeError("Need four separate E-core L2 groups")
    placements = {"alone": [], "unrestricted": [allowed] * 4,
                  "smt_overlap": [1 << i for i in [sibling] + pothers[:3]],
                  "p_separate": [1 << i for i in pothers],
                  "e_pack": [1 << i for i in bits(l2[0])[:4]],
                  "e_spread": [1 << bits(m)[0] for m in l2[:4]]}
    return {"foregroundLogicalCpu": fg, "foregroundMask": 1 << fg,
            "foregroundPhysicalCoreMask": fgcore["mask"], "pLogicalCpus": [bits(c["mask"]) for c in pcores],
            "eL2Groups": [bits(m) for m in l2], "placements": placements}


def schedule(plan, pilot):
    cells = [(fg, "none", "alone") for fg in ["compute", "cache8", "dram128"]]
    cells += [(fg, bg, placement) for fg in ["compute", "cache8", "dram128"]
              for bg in ["compute", "stream"] for placement in ["unrestricted", "smt_overlap", "p_separate", "e_pack", "e_spread"]]
    orders = []
    if pilot:
        # Cover every placement and all workloads/backgrounds, without estimating gains.
        orders = [[("compute", "none", "alone"), ("cache8", "compute", "unrestricted"),
                   ("dram128", "stream", "smt_overlap"), ("compute", "stream", "p_separate"),
                   ("cache8", "stream", "e_pack"), ("dram128", "compute", "e_spread")]]
    else:
        for pair in range(3):
            order = cells[:]
            random.Random(20260908 + pair).shuffle(order)
            orders.extend([order, list(reversed(order))])
    out = []
    for round_index, order in enumerate(orders, 1):
        for fg, bg, placement in order:
            out.append({"id": "%03d_r%d_%s_%s_%s" % (len(out) + 1, round_index, fg, bg, placement),
                        "round": round_index, "foreground": fg, "background": bg, "placement": placement,
                        "foregroundMask": plan["foregroundMask"], "backgroundMasks": plan["placements"][placement],
                        "warmupSeconds": 0.2 if pilot else 0.7, "measureSeconds": 0.35 if pilot else 2.5,
                        "seed": 9750139})
    return out


def verify_arm(result):
    if result["error"] or result["samples"] <= 0 or result["seconds"] < result["config"]["measureSeconds"]:
        raise RuntimeError("Invalid arm: " + repr(result))
    if result["priorityClass"] != "Normal":
        raise RuntimeError("Unexpected process priority")
    if result["observedForegroundMask"] != result["requestedForegroundMask"] or result["restoredForegroundMask"] != result["originalForegroundMask"]:
        raise RuntimeError("Foreground affinity verification failed")
    for worker in result["workers"]:
        if worker["error"] or worker["observedGroup"] != 0 or worker["observedMask"] != worker["requestedMask"] or worker["restoredMask"] != worker["originalMask"] or worker["measuredSteps"] <= 0:
            raise RuntimeError("Background worker verification failed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["probe", "pilot", "run"], required=True)
    parser.add_argument("--run-dir")
    args = parser.parse_args()
    root = Path(args.run_dir).resolve() if args.run_dir else HERE / "results" / (dt.datetime.now().strftime("%Y%m%d-%H%M%S") + "-" + args.mode)
    if root.exists():
        raise RuntimeError("Refusing to overwrite an existing run directory")
    root.mkdir(parents=True)
    status = {"mode": args.mode, "startUtc": utc(), "status": "running", "completed": 0, "runDirectory": str(root)}
    save(root / "status.json", status)
    before = None
    try:
        before = protected_snapshot()
        save(root / "protected-before.json", before)
        csc = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        exe = root / "CpuMemoryBench.exe"
        compile_log = hidden([csc, "/nologo", "/target:winexe", "/platform:x64", "/unsafe", "/optimize+", "/r:System.Web.Extensions.dll", "/out:" + str(exe), HERE / "CpuMemoryBench.cs"])
        (root / "compile.txt").write_text(compile_log, encoding="utf-8")
        source_hashes = {p.name: sha(p) for p in [HERE / "CpuMemoryBench.cs", HERE / "Run.py", HERE / "TEST-PROTOCOL.md"]}
        save(root / "source-hashes.json", {"sources": source_hashes, "executable": sha(exe)})
        hidden([exe, "probe", root / "topology.json"])
        top = read(root / "topology.json")
        machine = metadata()
        save(root / "machine.json", machine)
        save(root / "prefetch-capability.json", {
            "status": "NOT_TESTED_NO_PRIVILEGED_BACKEND", "elevated": top["elevated"],
            "candidateMsrDrivers": machine["candidateMsrDrivers"], "hardwarePrefetchState": "unknown_not_read",
            "hardwareRegisterReads": 0, "hardwareRegisterWrites": 0,
            "reason": "No verified model-aware privileged MSR backend is available to this bench. No elevation or driver installation attempted.",
            "memoryFixturesArePrefetchAB": False})
        plan = topology_plan(top)
        save(root / "placement-plan.json", plan)
        configs = [] if args.mode == "probe" else schedule(plan, args.mode == "pilot")
        save(root / "schedule.json", configs)
        status["total"] = len(configs)
        for config in configs:
            arm = root / "arms" / config["id"]
            arm.mkdir(parents=True)
            save(arm / "config.json", config)
            status.update(current=config["id"], updatedUtc=utc())
            save(root / "status.json", status)
            hidden([exe, "run", arm / "config.json", arm], timeout=100)
            verify_arm(read(arm / "result.json"))
            status["completed"] += 1
            save(root / "status.json", status)
        if any(sha(HERE / name) != digest for name, digest in source_hashes.items()):
            raise RuntimeError("Frozen bench source changed during run")
        status["status"] = "complete"
    except Exception as exc:
        status.update(status="failed", error=repr(exc))
        raise
    finally:
        try:
            after = protected_snapshot()
            save(root / "protected-after.json", after)
            isolation = {"productionFilesUnchanged": before is not None and before["files"] == after["files"],
                         "registryUnchanged": before is not None and before["registry"] == after["registry"],
                         "activePowerSchemeUnchanged": before is not None and before["powerScheme"] == after["powerScheme"],
                         "visibleOwnedWindowEvents": WINDOW_EVENTS, "remainingOwnedPids": sorted(OWNED),
                         "windowObservationIntervalSeconds": 0.2, "allLaunchesCreateNoWindow": all(x["createNoWindow"] for x in LAUNCHES)}
            save(root / "isolation.json", isolation)
            if not all(isolation[k] for k in ["productionFilesUnchanged", "registryUnchanged", "activePowerSchemeUnchanged"]) or WINDOW_EVENTS or OWNED:
                status.update(status="failed", isolationFailure=True)
        finally:
            save(root / "launches.json", LAUNCHES)
            status["endUtc"] = utc()
            save(root / "status.json", status)
            print(json.dumps(status, ensure_ascii=False), flush=True)
    if status["status"] != "complete":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
